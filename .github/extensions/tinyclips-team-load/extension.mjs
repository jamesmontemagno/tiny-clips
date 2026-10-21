import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import { TeamLoadError } from "./model.mjs";
import { readConfig, createTrackingStore } from "./state.mjs";
import { createGitHub } from "./github.mjs";
import { createHub } from "./hub.mjs";
import { startServer, perform } from "./server.mjs";

const instances = new Map();
const diagnostics = [];
let session;
let context;
let signature;

function log(message) {
    if (session) session.log({ level: "error", message });
    else diagnostics.push(message);
}

async function configure() {
    const config = await readConfig();
    const next = JSON.stringify(config);
    if (signature !== next) {
        const sendPrompt = async prompt => {
            if (!session?.send) throw new Error("The hosting Copilot session is unavailable.");
            return session.send({ prompt });
        };
        context = { config, hub: createHub(config, { store: createTrackingStore(config), github: createGitHub({ log }), sendPrompt, log }) };
        signature = next;
    }
    await context.hub.loadTracking();
    return context;
}

function handled(handler) {
    return async ctx => {
        try { return await handler(ctx); }
        catch (error) {
            log(error.message);
            throw new CanvasError(error instanceof TeamLoadError ? error.code : "team_load_error", error.message);
        }
    };
}

const repoRef = { repository: { type: "string", pattern: "^[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$" }, number: { type: "integer", minimum: 1 } };
const action = (name, description, inputSchema) => ({
    name, description, ...(inputSchema ? { inputSchema } : {}),
    handler: handled(async ctx => perform(await configure(), name, ctx.input ?? {})),
});

session = await joinSession({
    canvases: [createCanvas({
        id: "tinyclips-team-load",
        displayName: "TinyClips Team Load",
        description: "See what each TinyClips teammate and Copilot is working on right now, how active the team has been over time, and assign or reassign issues to whoever has capacity.",
        actions: [
            action("get_state", "Read tracked people/repos and the open issues and PRs that make up each person's current load."),
            action("refresh", "Refresh live data from GitHub.", { type: "object", properties: { force: { type: "boolean" } }, additionalProperties: false }),
            action("set_tracking", "Choose which GitHub users and repositories the canvas tracks (persisted).", {
                type: "object", required: ["people", "repos"], additionalProperties: false,
                properties: { people: { type: "array", items: { type: "string" } }, repos: { type: "array", items: { type: "string" } } },
            }),
            action("assign", "Assign an issue to a tracked person, or to Copilot with login 'copilot'. Without `additional` the issue must be unowned; with `additional: true` the person is added alongside existing assignees.", { type: "object", required: ["repository", "number", "login"], additionalProperties: false, properties: { ...repoRef, login: { type: "string" }, additional: { type: "boolean" } } }),
            action("unassign", "Remove an assignee from an issue (also used to undo an assign).", { type: "object", required: ["repository", "number", "login"], additionalProperties: false, properties: { ...repoRef, login: { type: "string" } } }),
            action("reassign", "Move an open issue from one assignee to another, preserving other assignees. The new owner is verified before the old one is removed. Copilot uses login 'copilot'.", { type: "object", required: ["repository", "number", "from", "login"], additionalProperties: false, properties: { ...repoRef, from: { type: "string" }, login: { type: "string" } } }),
            action("start_session", "Assign an issue to the current user and start a focused work session for it in this conversation. Use `additional: true` for an issue that already has other assignees.", { type: "object", required: ["repository", "number"], additionalProperties: false, properties: { ...repoRef, title: { type: "string" }, additional: { type: "boolean" } } }),
            action("get_item", "Read an issue or pull request with its rendered body and recent comments.", { type: "object", required: ["repository", "number"], additionalProperties: false, properties: repoRef }),
            action("get_momentum", "Read daily activity (commits, PRs opened/merged, issues opened/completed) across the tracked repositories for the whole team, one person, or everyone. `days` defaults to 7. History is cached for an hour; pass `force: true` to refetch it now.", {
                type: "object", additionalProperties: false,
                properties: { days: { type: "integer", enum: [7, 30, 90] }, person: { type: "string" }, everyone: { type: "boolean" }, repository: { type: "string", pattern: "^[A-Za-z0-9-]+/[A-Za-z0-9_.-]+$" }, force: { type: "boolean" } },
            }),
        ],
        open: handled(async ctx => {
            await configure();
            let entry = instances.get(ctx.instanceId);
            if (!entry) {
                entry = startServer(() => context, log);
                instances.set(ctx.instanceId, entry);
            }
            try {
                return { title: "Team load", status: "Who's on what, and who has room", url: (await entry).url };
            } catch (error) {
                if (instances.get(ctx.instanceId) === entry) instances.delete(ctx.instanceId);
                throw error;
            }
        }),
        onClose: handled(async ctx => {
            const entry = instances.get(ctx.instanceId);
            if (entry) {
                instances.delete(ctx.instanceId);
                await (await entry).close();
            }
        }),
    })],
});
for (const message of diagnostics.splice(0)) log(message);
