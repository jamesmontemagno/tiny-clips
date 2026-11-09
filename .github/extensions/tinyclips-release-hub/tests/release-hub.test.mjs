import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFileSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { generateReleaseNotes, nextPatchTag, parseReleaseTagVersion } from "../release-data.mjs";
import { renderHtml } from "../renderer.mjs";

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
});

test("generates macOS notes from the requested released section", async () => {
    const result = await generateReleaseNotes("mac", "v2.1.0.0-mac");

    assert.match(result.notes, /Studio annotations can be emoji/);
    assert.doesNotMatch(result.notes, /The Release Hub suggests the next fourth component/);
    assert.ok(result.itemCount > 0);
});

test("generates agent release notes from the shared changelog", async () => {
    const result = await generateReleaseNotes("agent", "0.1.0");

    assert.equal(result.source, "CHANGELOG.md");
    assert.match(result.notes, /dnx TinyClips status/);
    assert.match(result.notes, /NuGet Trusted Publishing/);
});

test("renders the Agent tab and guarded publish action", () => {
    const html = renderHtml({ instanceId: "test", token: "test" });

    assert.match(html, /data-platform="agent"/);
    assert.match(html, /publish_agent_release/);
    assert.match(html, /NuGet Trusted Publishing/);
});

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../../..");
const runners = [
    { name: "Bash", command: "bash", extension: "sh" },
    { name: "PowerShell", command: process.platform === "win32" ? "powershell" : "pwsh", extension: "ps1" },
];

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
    const heading = platform === "mac" ? "## Unreleased" : "## [Unreleased]";
    const changelog = join(root, platform === "mac" ? "CHANGELOG.md" : "windows/CHANGELOG.md");
    const original = `# Changelog\n\n${heading}\n\n${notes}\n\n## Previous release\n\n### Fixed\n- Previously released fix.\n`;
    writeFileSync(changelog, original);
    const git = (...args) => execFileSync("git", args, { cwd: root, encoding: "utf8", env: { ...process.env, GIT_PAGER: "cat" } }).trim();
    git("init", "--quiet");
    git("config", "user.name", "Release Tests");
    git("config", "user.email", "release-tests@example.invalid");
    git("config", "commit.gpgsign", "false");
    git("config", "tag.gpgsign", "false");
    git("add", ".");
    git("commit", "--quiet", "-m", "Initial fixture");
    const head = git("rev-parse", "HEAD");
    const tag = platform === "mac" ? "v2.1.0.1-mac" : "v2.1.1-windows";
    const args = runner.extension === "sh"
        ? [script, "--platform", platform, "--version", tag]
        : ["-NoProfile", "-File", script, "-Platform", platform, "-Version", tag];
    const run = (dryRun = false) => execFileSync(runner.command, [
        ...args,
        ...(dryRun ? [runner.extension === "sh" ? "--dry-run" : "-DryRun"] : []),
    ], { cwd: root, encoding: "utf8", stdio: "pipe" });
    return { root, git, run, changelog, original, head, tag };
}

for (const platform of ["mac", "windows"]) {
    test(`rejects ${platform} release-note previews when Unreleased has no bullets`, async (t) => {
        const fixture = createReleaseFixture(t, platform, "### Fixed\n", runners[0]);
        const moduleDir = join(fixture.root, ".github", "extensions", "tinyclips-release-hub");
        mkdirSync(moduleDir, { recursive: true });
        for (const file of ["release-data.mjs", "settings.mjs"]) {
            copyFileSync(join(repoRoot, ".github", "extensions", "tinyclips-release-hub", file), join(moduleDir, file));
        }
        const { generateReleaseNotes: generate } = await import(pathToFileURL(join(moduleDir, "release-data.mjs")).href);

        await assert.rejects(generate(platform, fixture.tag), /No release notes found.*Add bullet points/);
    });
}

for (const runner of runners) {
    for (const platform of ["mac", "windows"]) {
        const skip = runner.extension === "sh" && platform === "mac" && process.platform !== "darwin"
            ? "The macOS Bash script requires PlistBuddy."
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
            assert.match(readFileSync(fixture.changelog, "utf8"), /## (?:v2\.1\.0\.1-mac|\[v2\.1\.1-windows\]) - \d{4}-\d{2}-\d{2}/);
            assert.match(readFileSync(fixture.changelog, "utf8"), /Previously released fix/);
            assert.equal(fixture.git("status", "--porcelain"), "");
        });
    }
}
