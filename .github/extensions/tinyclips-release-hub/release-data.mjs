import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";
import { getSettings } from "./settings.mjs";

const execFileAsync = promisify(execFile);
const TAG_PATTERNS = {
    mac: /^v\d+\.\d+\.\d+(?:\.\d+)?-mac$/,
    windows: /^v\d+\.\d+\.\d+(?:\.\d+)?-windows$/,
};
const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");

async function run(command, args, options = {}) {
    const result = await execFileAsync(command, args, {
        cwd: options.cwd ?? await getRepoRoot(),
        encoding: "utf8",
        windowsHide: true,
        maxBuffer: 4 * 1024 * 1024,
        env: { ...process.env, GH_PAGER: "cat", PAGER: "cat" },
    });
    return result.stdout.trim();
}

async function tryRun(command, args) {
    try {
        return { ok: true, output: await run(command, args) };
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        return { ok: false, output: "", error: message.split(/\r?\n/)[0] };
    }
}

async function getRepoRoot() {
    return repoRoot;
}

function platformConfig(platform) {
    if (platform === "mac") {
        return {
            changelog: "CHANGELOG.md",
            unreleasedHeading: "## Unreleased",
            workflow: "release.yml",
            workflowInput: "version",
        };
    }
    if (platform === "windows") {
        return {
            changelog: "windows/CHANGELOG.md",
            unreleasedHeading: "## [Unreleased]",
            workflow: "windows-release.yml",
            workflowInput: "tag",
        };
    }
    throw new Error(`Unsupported platform: ${platform}`);
}

function parseVersion(tag) {
    const match = tag.match(/^v(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?-(?:mac|windows)$/);
    return match ? match.slice(1).map((value) => Number(value ?? 0)) : [0, 0, 0, 0];
}

function compareTags(left, right) {
    const a = parseVersion(left);
    const b = parseVersion(right);
    for (let index = 0; index < a.length; index += 1) {
        if (a[index] !== b[index]) {
            return b[index] - a[index];
        }
    }
    return 0;
}

function nextPatchTag(platform, latestTag, appVersion) {
    if (platform === "mac" && appVersion) {
        const prefix = `v${appVersion}.`;
        const matchingPatch = latestTag?.startsWith(prefix) ? parseVersion(latestTag)[2] : -1;
        return `${prefix}${matchingPatch + 1}-mac`;
    }
    const [major, minor, patch] = parseVersion(latestTag ?? "");
    return `v${major}.${minor}.${patch + 1}-windows`;
}

function extractSection(markdown, heading) {
    const lines = markdown.split(/\r?\n/);
    const start = lines.findIndex((line) => line.trim() === heading);
    if (start < 0) {
        return "";
    }
    const body = [];
    for (let index = start + 1; index < lines.length; index += 1) {
        if (lines[index].startsWith("## ")) {
            break;
        }
        body.push(lines[index]);
    }
    return body.join("\n").trim();
}

function releaseHeadingMatches(line, platform, version) {
    if (platform === "mac") {
        return line === `## ${version}` || line.startsWith(`## ${version} -`);
    }
    return line === `## [${version}]` || line.startsWith(`## [${version}] -`);
}

function extractReleaseSection(markdown, platform, version) {
    const lines = markdown.split(/\r?\n/);
    const start = lines.findIndex((line) => releaseHeadingMatches(line.trim(), platform, version));
    if (start < 0) {
        return "";
    }
    const body = [];
    for (let index = start + 1; index < lines.length; index += 1) {
        if (lines[index].startsWith("## ")) {
            break;
        }
        body.push(lines[index]);
    }
    return body.join("\n").trim();
}

function countNotes(markdown) {
    return markdown.split(/\r?\n/).filter((line) => line.trim().startsWith("- ")).length;
}

function repositoryWebUrl(remoteUrl) {
    const githubPath = remoteUrl.match(/github\.com(?::|\/)([^/]+\/[^/]+?)(?:\.git)?$/)?.[1];
    return githubPath ? `https://github.com/${githubPath.replace(/\.git$/, "")}` : null;
}

async function readMacVersion(root) {
    const plist = await readFile(`${root}/mac/TinyClips/Info.plist`, "utf8");
    const match = plist.match(/<key>CFBundleShortVersionString<\/key>\s*<string>([^<]+)<\/string>/);
    return match?.[1] ?? "unknown";
}

async function getTags(platform) {
    const output = await run("git", ["tag", "--list", `v*-${platform}`]);
    return output.split(/\r?\n/).filter((tag) => TAG_PATTERNS[platform].test(tag)).sort(compareTags);
}

async function getRemoteTags(platform) {
    const result = await tryRun("git", [
        "ls-remote", "--tags", "origin", `refs/tags/v*-${platform}`,
    ]);
    if (!result.ok) {
        return { available: false, tags: [], error: result.error };
    }
    const tags = result.output
        .split(/\r?\n/)
        .map((line) => line.match(/refs\/tags\/(.+?)(?:\^\{\})?$/)?.[1])
        .filter((tag) => tag && TAG_PATTERNS[platform].test(tag));
    return { available: true, tags: [...new Set(tags)].sort(compareTags) };
}

async function getWorkflow(workflow) {
    const result = await tryRun("gh", [
        "run", "list", "--workflow", workflow, "--limit", "8",
        "--json", "status,conclusion,displayTitle,createdAt,url,event,headBranch",
    ]);
    if (!result.ok) {
        return { available: false, error: result.error };
    }
    const runs = JSON.parse(result.output || "[]");
    return { available: true, run: runs[0] ?? null, runs };
}

async function getActionsSnapshot() {
    const [workflowResult, runResult] = await Promise.all([
        tryRun("gh", ["workflow", "list", "--all", "--json", "id,name,path,state", "--limit", "100"]),
        tryRun("gh", [
            "run", "list", "--all", "--limit", "50",
            "--json", "conclusion,createdAt,databaseId,displayTitle,event,headBranch,name,number,status,url,workflowDatabaseId,workflowName",
        ]),
    ]);
    if (!workflowResult.ok || !runResult.ok) {
        return {
            available: false,
            workflows: [],
            runs: [],
            error: workflowResult.error ?? runResult.error ?? "Could not query GitHub Actions.",
        };
    }

    const runs = JSON.parse(runResult.output || "[]");
    const latestRunsByWorkflowId = new Map();
    for (const run of runs) {
        const workflowId = String(run.workflowDatabaseId ?? "");
        if (workflowId && !latestRunsByWorkflowId.has(workflowId)) {
            latestRunsByWorkflowId.set(workflowId, run);
        }
    }
    const workflows = JSON.parse(workflowResult.output || "[]")
        .sort((left, right) => left.name.localeCompare(right.name))
        .map((workflow) => ({
            ...workflow,
            latestRun: latestRunsByWorkflowId.get(String(workflow.id)) ?? null,
        }));
    return { available: true, workflows, runs, error: null };
}

function parseWorkflowDispatch(yaml) {
    const lines = yaml.replace(/\r/g, "").split("\n");
    const onBlock = findYamlBlock(lines, "on");
    if (!onBlock) {
        return { supported: false, inputs: [] };
    }
    if (onBlock.inlineValue.includes("workflow_dispatch")) {
        return { supported: true, inputs: [] };
    }

    const dispatchBlock = findChildBlock(lines, onBlock.startIndex, onBlock.indent, "workflow_dispatch");
    if (!dispatchBlock) {
        return { supported: false, inputs: [] };
    }
    const inputsBlock = findChildBlock(lines, dispatchBlock.startIndex, dispatchBlock.indent, "inputs");
    return {
        supported: true,
        inputs: inputsBlock ? parseWorkflowInputs(lines, inputsBlock.startIndex, inputsBlock.indent) : [],
    };
}

function findYamlBlock(lines, key) {
    const matcher = new RegExp(`^\\s*["']?${escapeRegExp(key)}["']?\\s*:\\s*(.*)$`);
    for (let index = 0; index < lines.length; index += 1) {
        const match = lines[index].match(matcher);
        if (match) {
            return { startIndex: index, indent: countIndent(lines[index]), inlineValue: match[1] ?? "" };
        }
    }
    return null;
}

function findChildBlock(lines, parentIndex, parentIndent, key) {
    const matcher = new RegExp(`^\\s*["']?${escapeRegExp(key)}["']?\\s*:\\s*(.*)$`);
    for (let index = parentIndex + 1; index < lines.length; index += 1) {
        const line = lines[index];
        if (!line.trim() || line.trim().startsWith("#")) {
            continue;
        }
        const indent = countIndent(line);
        if (indent <= parentIndent) {
            break;
        }
        const match = line.match(matcher);
        if (match) {
            return { startIndex: index, indent };
        }
    }
    return null;
}

function parseWorkflowInputs(lines, inputsIndex, inputsIndent) {
    const inputs = [];
    let currentInput = null;
    let options = null;
    for (let index = inputsIndex + 1; index < lines.length; index += 1) {
        const line = lines[index];
        const trimmed = line.trim();
        if (!trimmed || trimmed.startsWith("#")) {
            continue;
        }
        const indent = countIndent(line);
        if (indent <= inputsIndent) {
            break;
        }
        if (indent === inputsIndent + 2 && /^[A-Za-z0-9_.-]+\s*:/.test(trimmed)) {
            if (currentInput) {
                inputs.push(currentInput);
            }
            currentInput = {
                name: trimmed.slice(0, trimmed.indexOf(":")).trim(),
                description: "",
                required: false,
                default: "",
                type: "string",
                options: [],
            };
            options = null;
            continue;
        }
        if (!currentInput) {
            continue;
        }
        if (trimmed === "options:") {
            options = currentInput.options;
            continue;
        }
        if (options && trimmed.startsWith("- ")) {
            options.push(unquote(trimmed.slice(2).trim()));
            continue;
        }
        options = null;
        const separator = trimmed.indexOf(":");
        if (separator < 0) {
            continue;
        }
        const property = trimmed.slice(0, separator).trim();
        const value = trimmed.slice(separator + 1).trim();
        if (property === "description") currentInput.description = unquote(value);
        if (property === "required") currentInput.required = value === "true";
        if (property === "default") currentInput.default = unquote(value);
        if (property === "type") currentInput.type = unquote(value) || "string";
    }
    if (currentInput) {
        inputs.push(currentInput);
    }
    return inputs;
}

function countIndent(line) {
    return line.length - line.trimStart().length;
}

function unquote(value) {
    return (value.startsWith('"') && value.endsWith('"')) || (value.startsWith("'") && value.endsWith("'"))
        ? value.slice(1, -1)
        : value;
}

function escapeRegExp(value) {
    return value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

export async function getActionsWorkflowDetails(workflowId) {
    const actions = await getActionsSnapshot();
    if (!actions.available) {
        throw new Error(actions.error);
    }
    const workflow = actions.workflows.find((candidate) => String(candidate.id) === String(workflowId));
    if (!workflow) {
        throw new Error(`Workflow ${workflowId} was not found.`);
    }
    const yaml = await run("gh", ["workflow", "view", String(workflow.id), "--yaml", "--ref", "main"]);
    return {
        workflow,
        dispatch: parseWorkflowDispatch(yaml),
        recentRuns: actions.runs.filter((run) => String(run.workflowDatabaseId) === String(workflow.id)).slice(0, 10),
    };
}

function sanitizeWorkflowInputs(inputs, dispatch) {
    if (!inputs || typeof inputs !== "object" || Array.isArray(inputs)) {
        return {};
    }
    const declaredInputs = new Map(dispatch.inputs.map((input) => [input.name, input]));
    const sanitized = {};
    for (const [name, value] of Object.entries(inputs)) {
        const declared = declaredInputs.get(name);
        if (!declared) {
            throw new Error(`Workflow input "${name}" is not declared.`);
        }
        if (!["string", "number", "boolean"].includes(typeof value)) {
            throw new Error(`Workflow input "${name}" must be a string, number, or boolean.`);
        }
        const normalized = String(value).trim();
        if (declared.options.length > 0 && !declared.options.includes(normalized)) {
            throw new Error(`Workflow input "${name}" must be one of: ${declared.options.join(", ")}.`);
        }
        sanitized[name] = normalized;
    }
    for (const declared of dispatch.inputs) {
        if (declared.required && !sanitized[declared.name]) {
            throw new Error(`Workflow input "${declared.name}" is required.`);
        }
    }
    return sanitized;
}

export async function dispatchWorkflow(input) {
    const details = await getActionsWorkflowDetails(input.workflowId);
    if (!details.dispatch.supported) {
        throw new Error(`Workflow "${details.workflow.name}" does not support manual dispatch.`);
    }

    assertConfirmation(input.confirmation, `RUN ${details.workflow.id}`);
    const ref = typeof input.ref === "string" && input.ref.trim() ? input.ref.trim() : "main";
    const inputs = sanitizeWorkflowInputs(input.inputs, details.dispatch);
    const args = ["workflow", "run", String(details.workflow.id), "--ref", ref];
    for (const [name, value] of Object.entries(inputs)) {
        args.push("-f", `${name}=${value}`);
    }
    const output = await run("gh", args);
    return {
        action: "dispatch_workflow",
        workflow: details.workflow,
        ref,
        inputs,
        output: output || `Dispatched ${details.workflow.name}.`,
    };
}

function simulateReleaseAction(action, input) {
    if (action === "prepare_release") {
        assertTag(input.platform, input.version);
        assertConfirmation(input.confirmation, `PREPARE ${input.version}`);
        return { action, tag: input.version, demo: true, output: `Simulated preparation for ${input.version}.` };
    }
    if (action === "undo_prepare") {
        assertTag(input.platform, input.tag);
        assertConfirmation(input.confirmation, `UNDO ${input.tag}`);
        return { action, tag: input.tag, demo: true, output: `Simulated undo for ${input.tag}.` };
    }
    if (action === "push_release") {
        const platform = input.tag.endsWith("-mac") ? "mac" : "windows";
        assertTag(platform, input.tag);
        assertConfirmation(input.confirmation, `PUSH ${input.tag}`);
        return { action, tag: input.tag, demo: true, output: `Simulated push for ${input.tag}.` };
    }
    if (action === "run_release_workflow") {
        assertTag(input.platform, input.tag);
        assertConfirmation(input.confirmation, `RUN ${input.tag}`);
        return { action, tag: input.tag, demo: true, output: `Simulated release workflow for ${input.tag}.` };
    }
    if (action === "submit_winget") {
        assertTag("windows", input.tag);
        assertConfirmation(input.confirmation, `SUBMIT ${input.tag}`);
        return { action, tag: input.tag, demo: true, output: `Simulated winget submission for ${input.tag}.` };
    }
    throw new Error(`Unknown release action: ${action}`);
}

export async function executeReleaseAction(action, input) {
    return (await getSettings()).demoMode
        ? simulateReleaseAction(action, input)
        : performReleaseAction(action, input);
}

export async function executeWorkflowDispatch(input) {
    const details = await getActionsWorkflowDetails(input.workflowId);
    if (!details.dispatch.supported) {
        throw new Error(`Workflow "${details.workflow.name}" does not support manual dispatch.`);
    }
    assertConfirmation(input.confirmation, `RUN ${details.workflow.id}`);
    const ref = typeof input.ref === "string" && input.ref.trim() ? input.ref.trim() : "main";
    const inputs = sanitizeWorkflowInputs(input.inputs, details.dispatch);
    if ((await getSettings()).demoMode) {
        return {
            action: "dispatch_workflow",
            workflow: details.workflow,
            ref,
            inputs,
            demo: true,
            output: `Simulated dispatch for ${details.workflow.name}.`,
        };
    }
    return dispatchWorkflow(input);
}

export async function getWorkflowRunStatus(platform, kind, since, tag) {
    const workflow = kind === "winget"
        ? "winget-submit.yml"
        : platformConfig(platform).workflow;
    const result = await getWorkflow(workflow);
    if (!result.available) {
        throw new Error(result.error || `Could not query ${workflow}.`);
    }

    const threshold = Number.isFinite(Date.parse(since))
        ? Date.parse(since) - 60_000
        : Date.now() - 60_000;
    const recentRuns = result.runs.filter((run) => Date.parse(run.createdAt) >= threshold);
    const run = (tag ? recentRuns.find((candidate) => candidate.headBranch === tag) : null)
        ?? recentRuns[0]
        ?? null;
    return { workflow, run };
}

async function getReleases() {
    const result = await tryRun("gh", [
        "release", "list", "--limit", "20",
        "--json", "tagName,name,isDraft,isPrerelease,publishedAt",
    ]);
    if (!result.ok) {
        return { available: false, releases: [], error: result.error };
    }
    return { available: true, releases: JSON.parse(result.output || "[]") };
}

function comparePackageVersions(left, right) {
    const a = left.split(".").map(Number);
    const b = right.split(".").map(Number);
    const length = Math.max(a.length, b.length);
    for (let index = 0; index < length; index += 1) {
        const difference = (b[index] ?? 0) - (a[index] ?? 0);
        if (difference !== 0) {
            return difference;
        }
    }
    return 0;
}

function windowsTagToPackageVersion(tag) {
    const match = tag?.match(/^v(\d+\.\d+\.\d+)(?:\.(\d+))?-windows$/);
    return match ? `${match[1]}.${match[2] ?? "0"}` : null;
}

async function getWingetPublishedVersion() {
    const url = "https://api.github.com/repos/microsoft/winget-pkgs/contents/manifests/r/Refractored/TinyClips";
    try {
        const response = await fetch(url, {
            headers: {
                Accept: "application/vnd.github+json",
                "User-Agent": "tinyclips-release-hub",
                "X-GitHub-Api-Version": "2022-11-28",
            },
            signal: AbortSignal.timeout(10_000),
        });
        if (!response.ok) {
            throw new Error(`GitHub API returned ${response.status}.`);
        }
        const entries = await response.json();
        if (!Array.isArray(entries)) {
            throw new Error("Unexpected winget package response.");
        }
        const versions = entries
            .filter((entry) => entry.type === "dir" && /^\d+\.\d+\.\d+\.\d+$/.test(entry.name))
            .sort((left, right) => comparePackageVersions(left.name, right.name));
        const latest = versions[0];
        return latest
            ? { available: true, version: latest.name, url: latest.html_url }
            : { available: true, version: null, url: null };
    } catch (error) {
        return {
            available: false,
            version: null,
            url: null,
            error: error instanceof Error ? error.message : String(error),
        };
    }
}

export async function generateReleaseNotes(platform, version) {
    const config = platformConfig(platform);
    const root = await getRepoRoot();
    const markdown = await readFile(`${root}/${config.changelog}`, "utf8");
    const body = version
        ? extractReleaseSection(markdown, platform, version) || extractSection(markdown, config.unreleasedHeading)
        : extractSection(markdown, config.unreleasedHeading);
    const tag = version || "NEXT_VERSION";
    const install = platform === "mac"
        ? [
            "## Install",
            "Install Tiny Clips for macOS with Homebrew:",
            "",
            "```text",
            "brew install --cask tiny-clips",
            "```",
            "",
            "Already installed? Update with `brew upgrade --cask tiny-clips`.",
            "",
            "## Release validation",
            "- Apple notarization and stapling completed successfully.",
            "- Signature verification passed (`spctl` and `codesign`).",
            "- Sparkle appcast and Homebrew cask metadata will be updated.",
        ]
        : [
            "## Install",
            "Install Tiny Clips for Windows from any Windows 11 terminal:",
            "",
            "```text",
            "winget install Refractored.TinyClips",
            "```",
            "",
            "Update later with `winget upgrade Refractored.TinyClips`.",
            "",
            "## Release validation",
            "- Azure Artifact Signing, SignTool verification, and WACK run in the release workflow.",
            "- x64 and ARM64 MSIX packages are published before the separate winget submission.",
        ];
    const notes = [body || "- Maintenance release.", "", ...install].join("\n").trim();
    return { platform, version: tag, source: config.changelog, notes, itemCount: countNotes(body) };
}

export async function getReleaseSnapshot() {
    const root = await getRepoRoot();
    const [branch, status, head, originMain, originUrl, aheadOfMain, behindMain, macTags, windowsTags, remoteMacTags, remoteWindowsTags, macVersion, releases, macWorkflow, windowsWorkflow, wingetWorkflow, wingetPublished, actions, settings] =
        await Promise.all([
            run("git", ["branch", "--show-current"]),
            run("git", ["status", "--porcelain"]),
            run("git", ["rev-parse", "HEAD"]),
            run("git", ["rev-parse", "origin/main"]),
            run("git", ["remote", "get-url", "origin"]),
            run("git", ["rev-list", "--count", "origin/main..HEAD"]),
            run("git", ["rev-list", "--count", "HEAD..origin/main"]),
            getTags("mac"),
            getTags("windows"),
            getRemoteTags("mac"),
            getRemoteTags("windows"),
            readMacVersion(root),
            getReleases(),
            getWorkflow("release.yml"),
            getWorkflow("windows-release.yml"),
            getWorkflow("winget-submit.yml"),
            getWingetPublishedVersion(),
            getActionsSnapshot(),
            getSettings(),
        ]);

    const buildPlatform = async (platform, tags, remoteTagResult, workflow) => {
        const config = platformConfig(platform);
        const markdown = await readFile(`${root}/${config.changelog}`, "utf8");
        const latestTag = tags[0] ?? null;
        const remoteTagSet = new Set(remoteTagResult.tags);
        const latestRemoteTag = remoteTagResult.tags[0] ?? null;
        const pendingTag = remoteTagResult.available
            ? tags.find((tag) => !remoteTagSet.has(tag)) ?? null
            : null;
        const suggestedTag = pendingTag ?? nextPatchTag(
            platform,
            latestRemoteTag ?? latestTag,
            platform === "mac" ? macVersion : null,
        );
        const latestRelease = releases.releases.find((release) =>
            TAG_PATTERNS[platform].test(release.tagName) && !release.isDraft && !release.isPrerelease,
        ) ?? null;
        const releasePackageVersion = platform === "windows" ? windowsTagToPackageVersion(latestRelease?.tagName) : null;
        let wingetStatus = "unavailable";
        if (platform === "windows" && wingetPublished.available && releasePackageVersion) {
            if (!wingetPublished.version) {
                wingetStatus = "missing";
            } else {
                const versionComparison = comparePackageVersions(wingetPublished.version, releasePackageVersion);
                wingetStatus = versionComparison === 0
                    ? "current"
                    : (versionComparison > 0 ? "behind" : "ahead");
            }
        }
        const wingetSubmissionTag = platform === "windows" &&
            (wingetStatus === "missing" || wingetStatus === "behind")
            ? latestRelease?.tagName ?? null
            : null;
        return {
            id: platform,
            label: platform === "mac" ? "macOS" : "Windows",
            latestTag,
            latestRemoteTag,
            remoteTags: remoteTagResult.tags.slice(0, 20),
            pendingTag,
            latestRelease,
            suggestedTag,
            appVersion: platform === "mac" ? macVersion : null,
            changelog: config.changelog,
            unreleasedNotes: extractSection(markdown, config.unreleasedHeading),
            unreleasedCount: countNotes(extractSection(markdown, config.unreleasedHeading)),
            workflow,
            wingetWorkflow: platform === "windows" ? wingetWorkflow : null,
            wingetPublished: platform === "windows"
                ? { ...wingetPublished, releasePackageVersion, status: wingetStatus }
                : null,
            wingetSubmissionTag,
        };
    };

    return {
        generatedAt: new Date().toISOString(),
        repository: root,
        repositoryUrl: repositoryWebUrl(originUrl),
        git: {
            branch,
            clean: status.length === 0,
            changeCount: status ? status.split(/\r?\n/).length : 0,
            atMainTip: head === originMain,
            aheadOfMain: Number(aheadOfMain),
            behindMain: Number(behindMain),
            canPrepareRelease: status.length === 0 && head === originMain,
            canPushRelease: status.length === 0 && Number(behindMain) === 0 && Number(aheadOfMain) <= 1,
        },
        githubAvailable: releases.available,
        githubError: releases.error ?? null,
        actions,
        settings,
        platforms: {
            mac: await buildPlatform("mac", macTags, remoteMacTags, macWorkflow),
            windows: await buildPlatform("windows", windowsTags, remoteWindowsTags, windowsWorkflow),
        },
    };
}

function assertTag(platform, tag) {
    if (!TAG_PATTERNS[platform].test(tag)) {
        throw new Error(`Invalid ${platform} release tag: ${tag}`);
    }
}

function assertConfirmation(actual, expected) {
    if (actual !== expected) {
        throw new Error(`Confirmation must exactly match: ${expected}`);
    }
}

export async function performReleaseAction(action, input) {
    if (action === "prepare_release") {
        assertTag(input.platform, input.version);
        assertConfirmation(input.confirmation, `PREPARE ${input.version}`);
        await run("git", ["fetch", "--quiet", "origin", "main"]);
        const [head, originMain, status] = await Promise.all([
            run("git", ["rev-parse", "HEAD"]),
            run("git", ["rev-parse", "origin/main"]),
            run("git", ["status", "--porcelain"]),
        ]);
        if (status) {
            throw new Error("Release preparation requires a clean working tree.");
        }
        if (head !== originMain) {
            throw new Error("Release preparation requires a session based exactly on the latest origin/main.");
        }
        const root = await getRepoRoot();
        if (process.platform === "win32") {
            const output = await run("powershell", [
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", `${root}\\.github\\skills\\tag-new-release\\tag-new-release.ps1`,
                "-Platform", input.platform, "-Version", input.version,
            ]);
            return { action, tag: input.version, output };
        }
        const output = await run("bash", [
            `${root}/.github/skills/tag-new-release/tag-new-release.sh`,
            "--platform", input.platform, "--version", input.version,
        ]);
        return { action, tag: input.version, output };
    }

    if (action === "undo_prepare") {
        assertTag(input.platform, input.tag);
        assertConfirmation(input.confirmation, `UNDO ${input.tag}`);
        await run("git", ["fetch", "--quiet", "origin", "main"]);
        const config = platformConfig(input.platform);
        const [head, originMain, status, aheadOfMain, behindMain, tagCommit, tagType, subject, changedFiles, remoteTag] =
            await Promise.all([
                run("git", ["rev-parse", "HEAD"]),
                run("git", ["rev-parse", "origin/main"]),
                run("git", ["status", "--porcelain"]),
                run("git", ["rev-list", "--count", "origin/main..HEAD"]),
                run("git", ["rev-list", "--count", "HEAD..origin/main"]),
                run("git", ["rev-list", "-n", "1", input.tag]),
                run("git", ["cat-file", "-t", `refs/tags/${input.tag}`]),
                run("git", ["log", "-1", "--format=%s"]),
                run("git", ["diff", "--name-only", "origin/main..HEAD"]),
                run("git", ["ls-remote", "--tags", "origin", `refs/tags/${input.tag}`]),
            ]);
        const files = changedFiles.split(/\r?\n/).filter(Boolean);
        if (status) {
            throw new Error("Undo preparation requires a clean working tree.");
        }
        if (remoteTag) {
            throw new Error(`Cannot undo ${input.tag} because the tag already exists on origin.`);
        }
        if (Number(behindMain) !== 0 || Number(aheadOfMain) !== 1) {
            throw new Error("Undo preparation requires exactly one local release commit ahead of origin/main.");
        }
        if (tagCommit !== head || tagType !== "tag") {
            throw new Error(`Tag ${input.tag} is not the annotated tag for the current release commit.`);
        }
        if (subject !== `Mark ${input.tag} release`) {
            throw new Error(`Current commit is not the expected ${input.tag} release commit.`);
        }
        if (files.length !== 1 || files[0].replaceAll("\\", "/") !== config.changelog) {
            throw new Error(`Release commit must only change ${config.changelog}.`);
        }
        await run("git", ["tag", "-d", input.tag]);
        const output = await run("git", ["reset", "--hard", originMain]);
        return { action, tag: input.tag, output };
    }

    if (action === "push_release") {
        const platform = input.tag.endsWith("-mac") ? "mac" : "windows";
        assertTag(platform, input.tag);
        assertConfirmation(input.confirmation, `PUSH ${input.tag}`);
        await run("git", ["fetch", "--quiet", "origin", "main"]);
        const [head, originMain, status, aheadOfMain, behindMain, tagCommit] = await Promise.all([
            run("git", ["rev-parse", "HEAD"]),
            run("git", ["rev-parse", "origin/main"]),
            run("git", ["status", "--porcelain"]),
            run("git", ["rev-list", "--count", "origin/main..HEAD"]),
            run("git", ["rev-list", "--count", "HEAD..origin/main"]),
            run("git", ["rev-list", "-n", "1", input.tag]),
        ]);
        if (status) {
            throw new Error("Release push requires a clean working tree.");
        }
        if (tagCommit !== head) {
            throw new Error(`Tag ${input.tag} does not point at the current release commit.`);
        }
        if (Number(behindMain) !== 0 || Number(aheadOfMain) > 1) {
            throw new Error("Release push requires at most one release commit ahead of the latest origin/main.");
        }
        if (Number(aheadOfMain) === 1) {
            await run("git", ["push", "origin", "HEAD:refs/heads/main"]);
        } else if (head !== originMain) {
            throw new Error("Current release commit does not match origin/main.");
        }
        const output = await run("git", ["push", "origin", input.tag]);
        return {
            action,
            tag: input.tag,
            output: output || `Pushed ${input.tag}.`,
            tracking: { platform, kind: "release" },
        };
    }

    if (action === "run_release_workflow") {
        assertTag(input.platform, input.tag);
        assertConfirmation(input.confirmation, `RUN ${input.tag}`);
        const config = platformConfig(input.platform);
        const output = await run("gh", [
            "workflow", "run", config.workflow, "-f", `${config.workflowInput}=${input.tag}`,
        ]);
        return {
            action,
            tag: input.tag,
            output: output || `Dispatched ${config.workflow}.`,
            tracking: { platform: input.platform, kind: "release" },
        };
    }

    if (action === "submit_winget") {
        assertTag("windows", input.tag);
        assertConfirmation(input.confirmation, `SUBMIT ${input.tag}`);
        const [releaseResult, releases, wingetPublished] = await Promise.all([
            tryRun("gh", ["release", "view", input.tag, "--json", "isDraft,isPrerelease"]),
            getReleases(),
            getWingetPublishedVersion(),
        ]);
        if (!releaseResult.ok || JSON.parse(releaseResult.output).isDraft) {
            throw new Error(`Published GitHub release ${input.tag} was not found.`);
        }
        if (JSON.parse(releaseResult.output).isPrerelease) {
            throw new Error(`GitHub release ${input.tag} is a prerelease and cannot be submitted to winget.`);
        }
        const latestPublishedWindowsRelease = releases.releases.find((release) =>
            TAG_PATTERNS.windows.test(release.tagName) && !release.isDraft && !release.isPrerelease,
        );
        if (!latestPublishedWindowsRelease || latestPublishedWindowsRelease.tagName !== input.tag) {
            throw new Error(`Winget submissions must target the latest published Windows release: ${latestPublishedWindowsRelease?.tagName ?? "none found"}.`);
        }
        if (!wingetPublished.available) {
            throw new Error(`Could not verify the published winget version: ${wingetPublished.error ?? "unknown error"}`);
        }
        const packageVersion = windowsTagToPackageVersion(input.tag);
        if (wingetPublished.version && packageVersion &&
            comparePackageVersions(wingetPublished.version, packageVersion) <= 0) {
            throw new Error(`Winget already provides ${wingetPublished.version}, which is not behind ${packageVersion}.`);
        }
        const output = await run("gh", [
            "workflow", "run", "winget-submit.yml", "-f", `tag=${input.tag}`,
        ]);
        return {
            action,
            tag: input.tag,
            output: output || "Dispatched winget submission.",
            tracking: { platform: "windows", kind: "winget" },
        };
    }

    throw new Error(`Unknown release action: ${action}`);
}
