import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { chmodSync, copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { Script, runInNewContext } from "node:vm";
import { generateReleaseNotes, nextPatchTag, parseReleaseTagVersion } from "../release-data.mjs";
import { renderHtml } from "../renderer.mjs";
import { bumpAgentVersion } from "../../../../agent/scripts/bump-version.mjs";

test("parses app and agent release tag versions", () => {
    assert.deepEqual(parseReleaseTagVersion("v2.4.1-mac"), [2, 4, 1, 0]);
    assert.deepEqual(parseReleaseTagVersion("v2.4.1.3-windows"), [2, 4, 1, 3]);
    assert.deepEqual(parseReleaseTagVersion("agent-v0.2.0-preview.1"), [0, 2, 0, 0]);
});

test("suggests macOS release revisions without changing the app version", () => {
    assert.equal(nextPatchTag("mac", "v2.1.0.0-mac", "2.1.0"), "v2.1.0.1-mac");
    assert.equal(nextPatchTag("mac", "v2.1.0.5-mac", "2.1.0"), "v2.1.0.6-mac");
    assert.equal(nextPatchTag("mac", "v2.1.0-mac", "2.1.0"), "v2.1.0.1-mac");
    assert.equal(nextPatchTag("mac", "v2.1.0.5-mac", "2.2.0"), "v2.2.0.0-mac");
    assert.equal(nextPatchTag("mac", null, "2.1.0"), "v2.1.0.0-mac");
    assert.equal(nextPatchTag("mac", "v1.5.2-mac", "1.5"), "v1.5.3-mac");
    assert.equal(nextPatchTag("windows", "v2.1.0-windows", null), "v2.1.1-windows");
    assert.equal(nextPatchTag("agent", "agent-v0.2.0"), "agent-v0.2.1");
});

test("generates macOS notes from the requested released section", async () => {
    const result = await generateReleaseNotes("mac", "v2.1.0.0-mac");

    assert.match(result.notes, /Studio annotations can be emoji/);
    assert.doesNotMatch(result.notes, /The Release Hub suggests the next fourth component/);
    assert.ok(result.itemCount > 0);
});

test("generates agent release notes from the Agent CLI changelog", async () => {
    const result = await generateReleaseNotes("agent", "0.1.0");

    assert.equal(result.source, "agent/CHANGELOG.md");
    assert.match(result.notes, /dnx TinyClips status/);
    assert.match(result.notes, /NuGet Trusted Publishing/);
    assert.match(result.notes, /cross-platform \.NET 10 Agent CLI/i);
});

test("renders the Agent tab and guarded publish action", () => {
    const html = renderHtml({ instanceId: "test", token: "test" });

    assert.match(html, /data-platform="agent"/);
    assert.match(html, /publish_agent_release/);
    assert.match(html, /create_agent_release_pr/);
    assert.match(html, /NuGet Trusted Publishing/);
    const script = html.match(/<script>([\s\S]*?)<\/script>/)?.[1];
    assert.ok(script);
    assert.doesNotThrow(() => new Script(script));
});

test("Agent dashboard gates publishing on the selected tag and shows its PR", () => {
    const html = renderHtml({ instanceId: "test", token: "test" });
    const script = html.match(/<script>([\s\S]*?)<\/script>/)[1];
    const beforeListeners = script.slice(0, script.indexOf('    document.querySelectorAll("[role=tab]").forEach((tab) => {'));
    const dashboard = { innerHTML: "", querySelectorAll: () => [] };
    const document = { getElementById: (id) => id === "dashboard" ? dashboard : null };
    const context = { document, window: { location: { search: "?platform=agent" } }, URLSearchParams };
    runInNewContext(`${beforeListeners}
      globalThis.renderAgent = (data, tag) => {
        snapshot = data;
        releaseTags.agent = tag;
        renderAgentDashboard();
        return dashboard.innerHTML;
      };`, context);
    const snapshot = {
        git: { canPrepareRelease: true, atMainTip: true, branch: "main" },
        settings: { demoMode: false },
        platforms: { agent: {
            suggestedTag: "agent-v0.2.1",
            packageVersion: "0.2.0",
            packageVersionsConsistent: true,
            packageVersions: { cli: "0.2.0" },
            agentReleasePullRequests: { available: true, pullRequests: [] },
            remoteTags: [],
        } },
    };
    const mismatch = context.renderAgent(snapshot, "agent-v0.3.0");
    assert.match(mismatch, /data-action="create_agent_release_pr" type="button">Create PR/);
    assert.match(mismatch, /data-action="publish_agent_release" type="button" disabled/);
    const match = context.renderAgent(snapshot, "agent-v0.2.0");
    assert.match(match, /data-action="publish_agent_release" type="button">Publish tag/);
    snapshot.platforms.agent.agentReleasePullRequests.pullRequests.push({
        headRefName: "release/agent-v0.3.0", number: 42, state: "OPEN", url: "https://example.invalid/pr/42",
    });
    const pending = context.renderAgent(snapshot, "agent-v0.3.0");
    assert.match(pending, /Release PR #42 is open/);
    assert.match(pending, /data-action="create_agent_release_pr" type="button" disabled/);
});

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");

function createAgentFixture(t) {
    const root = mkdtempSync(join(tmpdir(), "tinyclips-agent-release-test-"));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    for (const relative of [
        "agent/scripts/bump-version.mjs",
        "agent/src/TinyClips.Cli/TinyClips.Cli.csproj",
        "agent/src/TinyClips.Cli/.mcp/server.json",
        "plugin.json",
        ".github/extensions/tinyclips-release-hub/release-data.mjs",
        ".github/extensions/tinyclips-release-hub/settings.mjs",
    ]) {
        const target = join(root, relative);
        mkdirSync(dirname(target), { recursive: true });
        copyFileSync(join(repoRoot, relative), target);
    }
    return root;
}

test("bumps the four Agent metadata versions, including prereleases", async (t) => {
    const root = createAgentFixture(t);
    await bumpAgentVersion(root, "0.3.0-preview.1");
    const project = readFileSync(join(root, "agent/src/TinyClips.Cli/TinyClips.Cli.csproj"), "utf8");
    const plugin = JSON.parse(readFileSync(join(root, "plugin.json"), "utf8"));
    const registry = JSON.parse(readFileSync(join(root, "agent/src/TinyClips.Cli/.mcp/server.json"), "utf8"));
    assert.match(project, /<PackageVersion>0\.3\.0-preview\.1<\/PackageVersion>/);
    assert.equal(plugin.version, "0.3.0-preview.1");
    assert.equal(registry.version, plugin.version);
    assert.equal(registry.packages[0].version, plugin.version);
});

test("rejects invalid versions and unexpected metadata without changing files", async (t) => {
    const root = createAgentFixture(t);
    const project = join(root, "agent/src/TinyClips.Cli/TinyClips.Cli.csproj");
    const plugin = join(root, "plugin.json");
    const registry = join(root, "agent/src/TinyClips.Cli/.mcp/server.json");
    const originals = [project, plugin, registry].map((path) => readFileSync(path, "utf8"));
    await assert.rejects(bumpAgentVersion(root, "01.3.0"), /Invalid agent version/);
    assert.deepEqual([project, plugin, registry].map((path) => readFileSync(path, "utf8")), originals);
    writeFileSync(registry, '{"version":"0.2.0","packages":[]}');
    await assert.rejects(bumpAgentVersion(root, "0.3.0"), /unexpected shape/);
    assert.equal(readFileSync(project, "utf8"), originals[0]);
    assert.equal(readFileSync(plugin, "utf8"), originals[1]);
});

test("creates an Agent bump PR on an isolated branch without publishing a tag", { skip: process.platform === "win32" && "The fake gh executable uses a Unix shebang." }, async (t) => {
    const root = createAgentFixture(t);
    const remote = join(root, "remote.git");
    const bin = join(root, "bin");
    const state = join(root, "pr.json");
    mkdirSync(bin);
    const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8", env: { ...process.env, GIT_PAGER: "cat" } }).trim();
    git("init", "--quiet", "--initial-branch=main");
    writeFileSync(join(root, ".git/info/exclude"), "bin/\npr.json\nremote.git/\n");
    git("config", "user.name", "Release Tests");
    git("config", "user.email", "release-tests@example.invalid");
    git("config", "commit.gpgsign", "false");
    git("add", "agent", "plugin.json", ".github");
    git("commit", "--quiet", "-m", "Initial fixture\n\nCo-authored-by: copilot-swe-agent[bot] <198982749+Copilot@users.noreply.github.com>");
    git("init", "--bare", "--quiet", remote);
    git("remote", "add", "origin", remote);
    git("push", "--quiet", "-u", "origin", "main");
    const head = git("rev-parse", "HEAD");
    const gh = join(bin, "gh");
    writeFileSync(gh, `#!/usr/bin/env node
const fs = require("node:fs");
const args = process.argv.slice(2);
const state = process.env.RELEASE_TEST_PR;
if (args[0] === "pr" && args[1] === "list") {
  process.stdout.write(fs.existsSync(state) ? "[" + fs.readFileSync(state, "utf8") + "]" : "[]");
} else if (args[0] === "pr" && args[1] === "create") {
  fs.writeFileSync(state, JSON.stringify({number: 42, url: "https://example.invalid/pr/42", state: "OPEN", isDraft: false}));
} else { process.exitCode = 1; }
`);
    chmodSync(gh, 0o755);
    const oldPath = process.env.PATH;
    const oldState = process.env.RELEASE_TEST_PR;
    process.env.PATH = `${bin}${process.platform === "win32" ? ";" : ":"}${oldPath}`;
    process.env.RELEASE_TEST_PR = state;
    t.after(() => {
        process.env.PATH = oldPath;
        if (oldState === undefined) delete process.env.RELEASE_TEST_PR;
        else process.env.RELEASE_TEST_PR = oldState;
    });
    const { performReleaseAction } = await import(pathToFileURL(join(root, ".github/extensions/tinyclips-release-hub/release-data.mjs")).href);
    const input = { tag: "agent-v0.4.0", confirmation: "CREATE PR agent-v0.4.0" };
    await assert.rejects(performReleaseAction("create_agent_release_pr", { ...input, confirmation: "wrong" }), /Confirmation/);
    const result = await performReleaseAction("create_agent_release_pr", input);
    assert.equal(result.pullRequest.number, 42);
    assert.equal(git("branch", "--show-current"), "main");
    assert.equal(git("rev-parse", "HEAD"), head);
    assert.equal(git("status", "--porcelain"), "");
    assert.equal(git("--git-dir", remote, "tag", "--list"), "");
    const branch = "refs/heads/release/agent-v0.4.0";
    assert.match(git("--git-dir", remote, "show", `${branch}:agent/src/TinyClips.Cli/TinyClips.Cli.csproj`), /<PackageVersion>0\.4\.0<\/PackageVersion>/);
    const registry = JSON.parse(git("--git-dir", remote, "show", `${branch}:agent/src/TinyClips.Cli/.mcp/server.json`));
    const plugin = JSON.parse(git("--git-dir", remote, "show", `${branch}:plugin.json`));
    assert.equal(plugin.version, "0.4.0");
    assert.equal(registry.version, plugin.version);
    assert.equal(registry.packages[0].version, plugin.version);
    assert.equal((await performReleaseAction("create_agent_release_pr", input)).pullRequest.number, 42);
});
const runners = [
    { name: "Bash", command: "bash", extension: "sh" },
    { name: "PowerShell", command: process.platform === "win32" ? "powershell" : "pwsh", extension: "ps1" },
];
let powerShellAvailable = true;
if (process.platform !== "win32") {
    try {
        execFileSync("pwsh", ["-NoProfile", "-Command", "exit 0"], { stdio: "ignore" });
    } catch {
        powerShellAvailable = false;
    }
}

function createReleaseFixture(t, platform, notes, runner) {
    const root = mkdtempSync(join(tmpdir(), "tinyclips-release-test-"));
    t.after(() => rmSync(root, { recursive: true, force: true }));
    const scriptDir = join(root, ".github", "skills", "tag-new-release");
    mkdirSync(scriptDir, { recursive: true });
    const script = join(scriptDir, `tag-new-release.${runner.extension}`);
    copyFileSync(join(repoRoot, ".github", "skills", "tag-new-release", `tag-new-release.${runner.extension}`), script);
    mkdirSync(join(root, "mac", "TinyClips"), { recursive: true });
    writeFileSync(join(root, "mac", "TinyClips", "Info.plist"), '<?xml version="1.0"?><plist version="1.0"><dict><key>CFBundleShortVersionString</key><string>2.1.0</string></dict></plist>');
    mkdirSync(join(root, "windows"));
    mkdirSync(join(root, "agent"));
    const heading = platform === "windows" ? "## [Unreleased]" : "## Unreleased";
    const changelog = join(root,
        platform === "mac" ? "CHANGELOG.md"
            : platform === "windows" ? "windows/CHANGELOG.md"
                : "agent/CHANGELOG.md");
    const original = `# Changelog\n\n${heading}\n\n${notes}\n\n## Previous release\n\n### Fixed\n- Previously released fix.\n`;
    writeFileSync(changelog, original);
    const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8", env: { ...process.env, GIT_PAGER: "cat" } }).trim();
    git("init", "--quiet");
    git("config", "user.name", "Release Tests");
    git("config", "user.email", "release-tests@example.invalid");
    git("config", "commit.gpgsign", "false");
    git("config", "tag.gpgsign", "false");
    git("add", ".");
    git("commit", "--quiet", "-m", "Initial fixture\n\nCo-authored-by: copilot-swe-agent[bot] <198982749+Copilot@users.noreply.github.com>");
    const head = git("rev-parse", "HEAD");
    const tag = platform === "mac" ? "v2.1.0.1-mac"
        : platform === "windows" ? "v2.1.1-windows"
            : "agent-v0.3.0";
    const args = runner.extension === "sh"
        ? [script, "--platform", platform, "--version", tag]
        : ["-NoProfile", "-File", script, "-Platform", platform, "-Version", tag];
    const run = (dryRun = false, allowEmpty = false) => execFileSync(runner.command, [
        ...args,
        ...(allowEmpty ? [runner.extension === "sh" ? "--allow-empty" : "-AllowEmpty"] : []),
        ...(dryRun ? [runner.extension === "sh" ? "--dry-run" : "-DryRun"] : []),
    ], { cwd: root, encoding: "utf8", stdio: "pipe" });
    return { root, git, run, changelog, original, head, tag };
}

for (const platform of ["mac", "windows", "agent"]) {
    test(`generates maintenance release notes for ${platform} when Unreleased has no bullets`, async (t) => {
        const fixture = createReleaseFixture(t, platform, "### Fixed\n", runners[0]);
        const moduleDir = join(fixture.root, ".github", "extensions", "tinyclips-release-hub");
        mkdirSync(moduleDir, { recursive: true });
        for (const file of ["release-data.mjs", "settings.mjs"]) {
            copyFileSync(join(repoRoot, ".github", "extensions", "tinyclips-release-hub", file), join(moduleDir, file));
        }
        const { generateReleaseNotes: generate } = await import(pathToFileURL(join(moduleDir, "release-data.mjs")).href);

        const result = await generate(platform, fixture.tag);
        assert.match(result.notes, /Maintenance release\./);
        assert.equal(result.itemCount, 0);
    });
}

for (const runner of runners) {
    for (const platform of ["mac", "windows", "agent"]) {
        const skip = runner.extension === "sh" && platform === "mac" && process.platform !== "darwin"
            ? "The macOS Bash script requires PlistBuddy."
            : runner.extension === "ps1" && !powerShellAvailable
                ? "PowerShell is not installed."
                : false;
        for (const notes of ["", "### Fixed\n"]) {
            test(`${runner.name} ${platform} rejects ${notes ? "heading-only" : "empty"} notes without changing Git or the changelog`, { skip }, (t) => {
                const fixture = createReleaseFixture(t, platform, notes, runner);

                assert.throws(() => fixture.run(), (error) => {
                    assert.match(error.stderr.toString(), /contains no release notes/);
                    return true;
                });
                assert.equal(readFileSync(fixture.changelog, "utf8"), fixture.original);
                assert.equal(fixture.git("status", "--porcelain"), "");
                assert.equal(fixture.git("rev-parse", "HEAD"), fixture.head);
                assert.equal(fixture.git("tag", "--list"), "");
            });
        }

        test(`${runner.name} ${platform} prepares a maintenance release without changing the changelog when explicitly allowed`, { skip }, (t) => {
            const fixture = createReleaseFixture(t, platform, "### Fixed\n", runner);

            const preview = fixture.run(true, true);
            assert.match(preview, /Maintenance release\./);
            assert.match(preview, /Changelog: unchanged/);
            assert.equal(readFileSync(fixture.changelog, "utf8"), fixture.original);
            assert.equal(fixture.git("status", "--porcelain"), "");
            assert.equal(fixture.git("rev-parse", "HEAD"), fixture.head);
            assert.equal(fixture.git("tag", "--list"), "");

            fixture.run(false, true);
            assert.equal(fixture.git("rev-list", "--count", `${fixture.head}..HEAD`), "1");
            assert.equal(fixture.git("diff", "--name-only", `${fixture.head}..HEAD`), "");
            assert.equal(fixture.git("cat-file", "-t", `refs/tags/${fixture.tag}`), "tag");
            assert.equal(
                fixture.git("for-each-ref", "--format=%(contents)", `refs/tags/${fixture.tag}`),
                `Release ${fixture.tag}\n\nMaintenance release.`,
            );
            assert.equal(readFileSync(fixture.changelog, "utf8"), fixture.original);
            assert.equal(fixture.git("status", "--porcelain"), "");
        });

        test(`${runner.name} ${platform} previews without mutation and prepares an annotated release with only Unreleased notes`, { skip }, (t) => {
            const fixture = createReleaseFixture(t, platform, "### Fixed\n- New release fix.", runner);

            const preview = fixture.run(true);
            assert.match(preview, /Fixed:\s+- New release fix\./);
            assert.doesNotMatch(preview, /Previously released fix/);
            assert.equal(readFileSync(fixture.changelog, "utf8"), fixture.original);
            assert.equal(fixture.git("status", "--porcelain"), "");
            assert.equal(fixture.git("rev-parse", "HEAD"), fixture.head);
            assert.equal(fixture.git("tag", "--list"), "");

            fixture.run();
            assert.equal(fixture.git("cat-file", "-t", `refs/tags/${fixture.tag}`), "tag");
            assert.equal(fixture.git("rev-list", "-n", "1", fixture.tag), fixture.git("rev-parse", "HEAD"));
            assert.match(fixture.git("for-each-ref", "--format=%(contents)", `refs/tags/${fixture.tag}`), /Fixed:\s+- New release fix\./);
            assert.doesNotMatch(fixture.git("for-each-ref", "--format=%(contents)", `refs/tags/${fixture.tag}`), /Previously released fix/);
            assert.match(readFileSync(fixture.changelog, "utf8"), /## (?:(?:v2\.1\.0\.1-mac)|(?:\[v2\.1\.1-windows\])|(?:0\.3\.0)) - \d{4}-\d{2}-\d{2}/);
            assert.match(readFileSync(fixture.changelog, "utf8"), /Previously released fix/);
            assert.equal(fixture.git("status", "--porcelain"), "");
        });
    }
}
