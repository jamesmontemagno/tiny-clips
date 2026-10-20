import { createHash, randomUUID } from "node:crypto";
import { mkdir, readFile, rename, writeFile, unlink } from "node:fs/promises";
import { homedir } from "node:os";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { PulseError, defaults, repositoriesOf, validateConfig, validateView } from "./model.mjs";

export const extensionRoot = dirname(fileURLToPath(import.meta.url));
export const repoRoot = join(extensionRoot, "..", "..", "..");

export async function readConfig() {
    try {
        return validateConfig(JSON.parse(await readFile(join(extensionRoot, "config.json"), "utf8")));
    } catch (error) {
        if (error instanceof PulseError) throw error;
        throw new PulseError("invalid_config", `Cannot read Project Pulse config.json: ${error.message}`);
    }
}

export function createPreferences(config, home = process.env.COPILOT_HOME || join(homedir(), ".copilot")) {
    const identity = `github.com/${config.repository.toLowerCase()}`;
    const key = createHash("sha256").update(identity).digest("hex");
    const directory = join(home, "extension-data", "tinyclips-project-pulse");
    const path = join(directory, `${key}.json`);
    let queue = Promise.resolve();
    function configuredView(value) {
        const view = validateView(value);
        if (view.repository !== "all") {
            const repository = repositoriesOf(config).find(name => name.toLowerCase() === view.repository.toLowerCase());
            if (!repository) throw new PulseError("invalid_input", "Choose one of the configured TinyClips repositories.");
            view.repository = repository;
        }
        return view;
    }
    async function read() {
        try {
            const data = JSON.parse(await readFile(path, "utf8"));
            const expected = Object.keys(defaults).filter(key =>
                (data.version >= 2 || key !== "repository") && (data.version >= 3 || key !== "refreshMinutes"));
            if (data.format !== "tinyclips-project-pulse-preferences" || ![1, 2, 3].includes(data.version) || data.repository !== identity
                || !data.view || Object.keys(data.view).length !== expected.length || expected.some(key => !Object.hasOwn(data.view, key))) {
                throw new Error("Unknown preferences format, version, or repository identity.");
            }
            return configuredView(data.view);
        } catch (error) {
            if (error.code === "ENOENT") return { ...defaults };
            throw new PulseError("storage_error", `Cannot read preferences at ${path}. Repair or move the file before retrying. ${error.message}`);
        }
    }
    function write(patch) {
        configuredView(patch);
        const operation = queue.then(async () => {
            const view = configuredView({ ...await read(), ...patch });
            const temporary = join(directory, `${key}.${randomUUID()}.tmp`);
            let created = false;
            try {
                await mkdir(directory, { recursive: true });
                await writeFile(temporary, `${JSON.stringify({ format: "tinyclips-project-pulse-preferences", version: 3, repository: identity, view }, null, 2)}\n`, { flag: "wx" });
                created = true;
                await rename(temporary, path);
                created = false;
            } catch (error) {
                if (created) {
                    try { await unlink(temporary); }
                    catch (cleanup) { throw new PulseError("storage_error", `Preference save failed: ${error.message}. Temporary-file cleanup also failed: ${cleanup.message}`); }
                }
                throw new PulseError("storage_error", `Cannot save preferences at ${path}: ${error.message}`);
            }
            return view;
        });
        queue = operation.then(() => undefined, () => undefined);
        return operation;
    }
    return { read, write, path };
}
