import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const versionPattern = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/;

export async function bumpAgentVersion(root, version) {
    if (!versionPattern.test(version)) {
        throw new Error(`Invalid agent version: ${version}`);
    }
    const projectPath = resolve(root, "agent/src/TinyClips.Cli/TinyClips.Cli.csproj");
    const pluginPath = resolve(root, "plugin.json");
    const registryPath = resolve(root, "agent/src/TinyClips.Cli/.mcp/server.json");
    const [project, pluginText, registryText] = await Promise.all([
        readFile(projectPath, "utf8"), readFile(pluginPath, "utf8"), readFile(registryPath, "utf8"),
    ]);
    const plugin = JSON.parse(pluginText);
    const registry = JSON.parse(registryText);
    const projectMatches = [...project.matchAll(/<PackageVersion>([^<]+)<\/PackageVersion>/g)];
    if (projectMatches.length !== 1 || typeof plugin.version !== "string" ||
        typeof registry.version !== "string" || !Array.isArray(registry.packages) ||
        registry.packages.length !== 1 || registry.packages[0].identifier !== "TinyClips" ||
        typeof registry.packages[0].version !== "string") {
        throw new Error("Agent version metadata has an unexpected shape; no files were changed.");
    }
    const replaceVersions = (text, expected) => {
        let count = 0;
        const updated = text.replace(/("version"\s*:\s*")([^"]+)(")/g, (_, prefix, old, suffix) => {
            count += 1;
            return `${prefix}${version}${suffix}`;
        });
        if (count !== expected) {
            throw new Error("Agent version metadata has unexpected version fields; no files were changed.");
        }
        return updated;
    };
    const updatedProject = project.replace(/<PackageVersion>[^<]+<\/PackageVersion>/, `<PackageVersion>${version}</PackageVersion>`);
    const updatedPlugin = replaceVersions(pluginText, 1);
    const updatedRegistry = replaceVersions(registryText, 2);
    await Promise.all([
        writeFile(projectPath, updatedProject),
        writeFile(pluginPath, updatedPlugin),
        writeFile(registryPath, updatedRegistry),
    ]);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
    const version = process.argv[2];
    if (!version || process.argv.length !== 3) {
        throw new Error("Usage: node agent/scripts/bump-version.mjs <version>");
    }
    await bumpAgentVersion(process.cwd(), version);
}
