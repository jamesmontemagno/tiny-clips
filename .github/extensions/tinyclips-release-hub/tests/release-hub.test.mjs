import assert from "node:assert/strict";
import test from "node:test";
import { generateReleaseNotes, parseReleaseTagVersion } from "../release-data.mjs";
import { renderHtml } from "../renderer.mjs";

test("parses app and agent release tag versions", () => {
    assert.deepEqual(parseReleaseTagVersion("v2.4.1-mac"), [2, 4, 1, 0]);
    assert.deepEqual(parseReleaseTagVersion("v2.4.1.3-windows"), [2, 4, 1, 3]);
    assert.deepEqual(parseReleaseTagVersion("agent-v0.2.0-preview.1"), [0, 2, 0, 0]);
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
