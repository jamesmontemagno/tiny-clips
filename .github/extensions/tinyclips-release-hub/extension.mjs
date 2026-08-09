import { createServer } from "node:http";
import { randomBytes } from "node:crypto";
import { createCanvas, CanvasError, joinSession } from "@github/copilot-sdk/extension";
import {
    generateReleaseNotes,
    executeReleaseAction,
    executeWorkflowDispatch,
    getActionsWorkflowDetails,
    getReleaseSnapshot,
    getWorkflowRunStatus,
    dispatchWorkflow,
} from "./release-data.mjs";
import { getSettings, setDemoMode } from "./settings.mjs";
import { renderHtml } from "./renderer.mjs";

const servers = new Map();

function sendJson(res, statusCode, value) {
    res.writeHead(statusCode, {
        "Content-Type": "application/json; charset=utf-8",
        "Cache-Control": "no-store",
    });
    res.end(JSON.stringify(value));
}

async function readJsonBody(req) {
    const chunks = [];
    let size = 0;

    for await (const chunk of req) {
        size += chunk.length;
        if (size > 64 * 1024) {
            throw new Error("Request body is too large.");
        }
        chunks.push(chunk);
    }

    if (chunks.length === 0) {
        return {};
    }
    return JSON.parse(Buffer.concat(chunks).toString("utf8"));
}

async function startServer(instanceId) {
    const token = randomBytes(24).toString("hex");
    const server = createServer(async (req, res) => {
        const requestUrl = new URL(req.url ?? "/", "http://127.0.0.1");

        try {
            if (req.method === "GET" && requestUrl.pathname === "/") {
                res.writeHead(200, {
                    "Content-Type": "text/html; charset=utf-8",
                    "Cache-Control": "no-store",
                    "Content-Security-Policy": "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; img-src 'self' data:",
                });
                res.end(renderHtml({ instanceId, token }));
                return;
            }

            if (req.method === "GET" && requestUrl.pathname === "/api/status") {
                sendJson(res, 200, await getReleaseSnapshot());
                return;
            }
            if (req.method === "GET" && requestUrl.pathname === "/api/settings") {
                sendJson(res, 200, await getSettings());
                return;
            }

            if (req.method === "POST" && requestUrl.pathname === "/api/action") {
                if (req.headers["x-release-hub-token"] !== token) {
                    sendJson(res, 403, { error: "Invalid release hub token." });
                    return;
                }

                const body = await readJsonBody(req);
                if (body.action === "workflow_status") {
                    const result = await getWorkflowRunStatus(body.platform, body.kind, body.since, body.tag);
                    sendJson(res, 200, { result });
                    return;
                }
                if (body.action === "workflow_details") {
                    const result = await getActionsWorkflowDetails(body.workflowId);
                    sendJson(res, 200, { result, snapshot: await getReleaseSnapshot() });
                    return;
                }
                if (body.action === "dispatch_workflow") {
                    const result = await executeWorkflowDispatch(body);
                    sendJson(res, 200, { result, snapshot: await getReleaseSnapshot() });
                    return;
                }
                if (body.action === "update_settings") {
                    const result = await setDemoMode(body.demoMode);
                    sendJson(res, 200, { result, snapshot: await getReleaseSnapshot() });
                    return;
                }
                const result = body.action === "generate_notes"
                    ? await generateReleaseNotes(body.platform, body.version)
                    : await executeReleaseAction(body.action, body);
                sendJson(res, 200, { result, snapshot: await getReleaseSnapshot() });
                return;
            }

            sendJson(res, 404, { error: "Not found." });
        } catch (error) {
            sendJson(res, 400, {
                error: error instanceof Error ? error.message : String(error),
            });
        }
    });

    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    const address = server.address();
    const port = typeof address === "object" && address ? address.port : 0;
    return { server, url: `http://127.0.0.1:${port}/` };
}

const platformSchema = { type: "string", enum: ["mac", "windows"] };

await joinSession({
    canvases: [
        createCanvas({
            id: "tinyclips-release-hub",
            displayName: "TinyClips Release Hub",
            description: "Manage macOS and Windows release readiness, notes, tags, workflows, and winget submission.",
            inputSchema: {
                type: "object",
                properties: {
                    platform: platformSchema,
                },
                additionalProperties: false,
            },
            actions: [
                {
                    name: "refresh_status",
                    description: "Refresh TinyClips release readiness, tags, releases, and workflow status.",
                    handler: async () => getReleaseSnapshot(),
                },
                {
                    name: "get_settings",
                    description: "Return the persistent Release Hub settings.",
                    handler: async () => getSettings(),
                },
                {
                    name: "set_demo_mode",
                    description: "Enable or disable persistent Demo Mode, which simulates all state-changing release actions.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            enabled: { type: "boolean" },
                        },
                        required: ["enabled"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => setDemoMode(ctx.input.enabled),
                },
                {
                    name: "generate_release_notes",
                    description: "Generate a release-notes preview from a platform changelog.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            platform: platformSchema,
                            version: { type: "string" },
                        },
                        required: ["platform"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => generateReleaseNotes(ctx.input.platform, ctx.input.version),
                },
                {
                    name: "get_workflow_status",
                    description: "Query the recent GitHub Actions run triggered for a release or winget submission.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            platform: platformSchema,
                            kind: { type: "string", enum: ["release", "winget"] },
                            since: { type: "string", minLength: 1 },
                            tag: { type: "string" },
                        },
                        required: ["platform", "kind", "since"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => getWorkflowRunStatus(
                        ctx.input.platform,
                        ctx.input.kind,
                        ctx.input.since,
                        ctx.input.tag,
                    ),
                },
                {
                    name: "get_actions_workflow_details",
                    description: "Inspect a GitHub Actions workflow, its manual-dispatch inputs, and its recent runs.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            workflowId: { type: "integer", minimum: 1 },
                        },
                        required: ["workflowId"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => getActionsWorkflowDetails(ctx.input.workflowId),
                },
                {
                    name: "dispatch_workflow",
                    description: "Manually dispatch a GitHub Actions workflow after exact typed confirmation.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            workflowId: { type: "integer", minimum: 1 },
                            ref: { type: "string", minLength: 1 },
                            inputs: {
                                type: "object",
                                additionalProperties: {
                                    oneOf: [{ type: "string" }, { type: "number" }, { type: "boolean" }],
                                },
                            },
                            confirmation: { type: "string", minLength: 1 },
                        },
                        required: ["workflowId", "confirmation"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => executeWorkflowDispatch(ctx.input),
                },
                {
                    name: "prepare_release",
                    description: "Create the changelog commit and annotated tag after exact typed confirmation.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            platform: platformSchema,
                            version: { type: "string", minLength: 1 },
                            confirmation: { type: "string", minLength: 1 },
                        },
                        required: ["platform", "version", "confirmation"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => performReleaseAction("prepare_release", ctx.input),
                },
                {
                    name: "undo_prepare",
                    description: "Delete an unpushed local release tag and restore its single release commit after exact typed confirmation.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            platform: platformSchema,
                            tag: { type: "string", minLength: 1 },
                            confirmation: { type: "string", minLength: 1 },
                        },
                        required: ["platform", "tag", "confirmation"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => performReleaseAction("undo_prepare", ctx.input),
                },
                {
                    name: "push_release",
                    description: "Fast-forward main with the prepared release commit and push its tag after exact typed confirmation.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            tag: { type: "string", minLength: 1 },
                            confirmation: { type: "string", minLength: 1 },
                        },
                        required: ["tag", "confirmation"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => performReleaseAction("push_release", ctx.input),
                },
                {
                    name: "run_release_workflow",
                    description: "Dispatch the platform GitHub release workflow after exact typed confirmation.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            platform: platformSchema,
                            tag: { type: "string", minLength: 1 },
                            confirmation: { type: "string", minLength: 1 },
                        },
                        required: ["platform", "tag", "confirmation"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => performReleaseAction("run_release_workflow", ctx.input),
                },
                {
                    name: "submit_winget",
                    description: "Dispatch the manually gated winget submission for the latest published Windows release when winget is behind.",
                    inputSchema: {
                        type: "object",
                        properties: {
                            tag: { type: "string", minLength: 1 },
                            confirmation: { type: "string", minLength: 1 },
                        },
                        required: ["tag", "confirmation"],
                        additionalProperties: false,
                    },
                    handler: async (ctx) => performReleaseAction("submit_winget", ctx.input),
                },
            ],
            open: async (ctx) => {
                let entry = servers.get(ctx.instanceId);
                if (!entry) {
                    entry = await startServer(ctx.instanceId);
                    servers.set(ctx.instanceId, entry);
                }
                return {
                    title: "TinyClips Release Hub",
                    status: "Release status and operations",
                    url: entry.url,
                };
            },
            onClose: async (ctx) => {
                const entry = servers.get(ctx.instanceId);
                if (entry) {
                    servers.delete(ctx.instanceId);
                    await new Promise((resolve) => entry.server.close(resolve));
                }
            },
        }),
    ],
}).catch((error) => {
    throw new CanvasError("release_hub_start_failed", error instanceof Error ? error.message : String(error));
});
