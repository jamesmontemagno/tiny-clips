import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import { assignmentSchema, issueStateSchema, itemSchema, viewSchema, momentumSchema, PulseError } from "./model.mjs";
import { readConfig, createPreferences } from "./state.mjs";
import { createProjectHub } from "./project-hub.mjs";
import { startServer, perform } from "./server.mjs";

const instances = new Map();
let context;
let signature;
let session;
const diagnostics = [];
function log(message) {
    if (session) session.log({ level: "error", message });
    else diagnostics.push(message);
}
async function configure() {
    const config = await readConfig();
    const next = JSON.stringify(config);
    if (signature !== next) {
        context = { config, preferences: createPreferences(config), data: createProjectHub(config, { log }) };
        signature = next;
    }
    await context.preferences.read();
    return context;
}
function handled(handler) {
    return async ctx => {
        try { return await handler(ctx); }
        catch (error) {
            log(error.message);
            throw new CanvasError(error instanceof PulseError ? error.code : "project_pulse_error", error.message);
        }
    };
}
const action = (name, description, inputSchema) => ({
    name, description, ...(inputSchema ? { inputSchema } : {}),
    handler: handled(async ctx => perform(await configure(), name, ctx.input ?? {})),
});

session = await joinSession({
    canvases: [createCanvas({
        id: "tinyclips-project-pulse",
        displayName: "TinyClips Project Pulse",
        description: "Follow TinyClips team activity, pull requests, assignments, and the macOS and Windows 2.0 launch.",
        actions: [
            action("refresh_status", "Refresh GitHub activity, ownership, launch progress, and explicit coverage/error information."),
            action("get_momentum", "Gather daily commits, PR opens/merges and issue opens/completions across configured repositories on demand (7, 30 or 90 days).", momentumSchema),
            action("get_item_detail", "Read current issue or pull request details, including its expectedState for confirmed edits.", itemSchema),
            action("get_view_state", "Read repository-scoped personal view preferences."),
            action("set_view_state", "Save personal filters, the selected view, or automatic refresh interval without changing shared configuration.", viewSchema),
            action("update_assignees", "Apply confirmed issue/PR assignee changes; expectedState must come from current item details.", assignmentSchema),
            action("set_issue_state", "Confirm closing/reopening an issue; closing requires a reason and current expectedState.", issueStateSchema),
        ],
        open: handled(async ctx => {
            await configure();
            let entry = instances.get(ctx.instanceId);
            if (!entry) {
                entry = startServer(() => context, log);
                instances.set(ctx.instanceId, entry);
            }
            try {
                return { title: "TinyClips Project Pulse", status: "Team activity and the road to 2.0", url: (await entry).url };
            } catch (error) {
                if (instances.get(ctx.instanceId) === entry) instances.delete(ctx.instanceId);
                throw error;
            }
        }),
        onClose: handled(async ctx => {
            const entry = instances.get(ctx.instanceId);
            if (entry) {
                await (await entry).close();
                instances.delete(ctx.instanceId);
            }
        }),
    })],
});
for (const message of diagnostics.splice(0)) log(message);
