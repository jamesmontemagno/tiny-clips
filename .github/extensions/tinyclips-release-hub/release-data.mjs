import { execFile } from "node:child_process";
import { readFile } from "node:fs/promises";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

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
    const [branch, status, head, originMain, originUrl, aheadOfMain, behindMain, macTags, windowsTags, remoteMacTags, remoteWindowsTags, macVersion, releases, macWorkflow, windowsWorkflow, wingetWorkflow, wingetPublished] =
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
        const latestRelease = releases.releases.find((release) => TAG_PATTERNS[platform].test(release.tagName)) ?? null;
        const releasePackageVersion = platform === "windows" ? windowsTagToPackageVersion(latestRelease?.tagName) : null;
        const wingetStatus = platform === "windows" && wingetPublished.available && wingetPublished.version && releasePackageVersion
            ? (comparePackageVersions(wingetPublished.version, releasePackageVersion) === 0 ? "current" : "outdated")
            : "unavailable";
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
        const releaseResult = await tryRun("gh", ["release", "view", input.tag, "--json", "isDraft"]);
        if (!releaseResult.ok || JSON.parse(releaseResult.output).isDraft) {
            throw new Error(`Published GitHub release ${input.tag} was not found.`);
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
