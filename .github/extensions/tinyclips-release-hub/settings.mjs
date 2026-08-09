import { mkdir, readFile, writeFile } from "node:fs/promises";
import { homedir } from "node:os";
import { join } from "node:path";

const defaults = { demoMode: false };
const copilotHome = process.env.COPILOT_HOME || join(homedir(), ".copilot");
const settingsDirectory = join(copilotHome, "extensions", "tinyclips-release-hub", "artifacts");
const settingsPath = join(settingsDirectory, "settings.json");

export async function getSettings() {
    try {
        const parsed = JSON.parse(await readFile(settingsPath, "utf8"));
        return { demoMode: parsed?.demoMode === true };
    } catch (error) {
        if (error && typeof error === "object" && error.code === "ENOENT") {
            return { ...defaults };
        }
        throw error;
    }
}

export async function setDemoMode(demoMode) {
    if (typeof demoMode !== "boolean") {
        throw new Error("Demo mode must be enabled or disabled.");
    }
    await mkdir(settingsDirectory, { recursive: true });
    const settings = { demoMode };
    await writeFile(settingsPath, `${JSON.stringify(settings, null, 2)}\n`, "utf8");
    return settings;
}
