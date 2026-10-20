import { createServer } from "node:http";
import { randomBytes, timingSafeEqual } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { PulseError } from "./model.mjs";
import { extensionRoot } from "./state.mjs";
import { renderHtml } from "./renderer.mjs";

export async function perform(context, name, input = {}) {
    switch (name) {
        case "refresh_status":
            if (Object.keys(input).length) throw new PulseError("invalid_input", "Refresh takes no input.");
            return context.data.snapshot(true);
        case "get_item_detail": return context.data.detail(input);
        case "get_momentum": return context.data.momentum(input);
        case "get_view_state":
            if (Object.keys(input).length) throw new PulseError("invalid_input", "Reading preferences takes no input.");
            return context.preferences.read();
        case "set_view_state": return context.preferences.write(input);
        case "update_assignees": return context.data.updateAssignees(input);
        case "set_issue_state": return context.data.setIssueState(input);
        default: throw new PulseError("invalid_input", "Unknown Project Pulse action.");
    }
}

const assets = new Map([
    ["/app.mjs", ["assets\\app.mjs", "text/javascript"]],
    ["/styles.css", ["assets\\styles.css", "text/css"]],
    ["/model.mjs", ["model.mjs", "text/javascript"]],
    ["/momentum.mjs", ["assets\\momentum.mjs", "text/javascript"]],
    ["/celebration.mjs", ["assets\\celebration.mjs", "text/javascript"]],
]);

export async function startServer(getContext, log = () => {}) {
    const nonce = randomBytes(32).toString("hex");
    let origin;
    const server = createServer(async (request, response) => {
        function send(status, value, type = "application/json") {
            response.writeHead(status, {
                "Content-Type": `${type}; charset=utf-8`, "Cache-Control": "no-store",
                "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer",
                "Content-Security-Policy": "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self'; connect-src 'self'; img-src 'self' https://avatars.githubusercontent.com; base-uri 'none'; form-action 'none'",
            });
            response.end(type === "application/json" ? JSON.stringify(value) : value);
        }
        try {
            if (request.headers.host !== new URL(origin).host) {
                send(403, { code: "forbidden", error: "Invalid request host." }); return;
            }
            const url = new URL(request.url, origin);
            if (request.method === "GET" && url.pathname === "/"
                && [...url.searchParams.keys()].every(key => key === "scoutTheme")
                && (!url.search || ["light", "dark"].includes(url.searchParams.get("scoutTheme")))) {
                send(200, renderHtml(nonce), "text/html"); return;
            }
            if (request.headers.origin && request.headers.origin !== origin) {
                send(403, { code: "forbidden", error: "Cross-origin requests are not allowed." }); return;
            }
            if (request.headers["sec-fetch-site"] === "cross-site") {
                send(403, { code: "forbidden", error: "Cross-site requests are not allowed." }); return;
            }
            if (request.method === "GET" && assets.has(url.pathname) && !url.search) {
                const [relative, type] = assets.get(url.pathname);
                send(200, await readFile(join(extensionRoot, ...relative.split("\\")), "utf8"), type); return;
            }
            if (request.method === "GET" && url.pathname === "/api/snapshot" && !url.search) {
                send(200, await getContext().data.snapshot()); return;
            }
            if (request.method === "GET" && url.pathname === "/api/preferences" && !url.search) {
                send(200, await getContext().preferences.read()); return;
            }
            if (request.method === "POST" && url.pathname === "/api/action" && !url.search) {
                const supplied = request.headers["x-pulse-nonce"];
                if (typeof supplied !== "string" || Buffer.byteLength(supplied) !== Buffer.byteLength(nonce)
                    || !timingSafeEqual(Buffer.from(supplied), Buffer.from(nonce))) {
                    send(403, { code: "forbidden", error: "Invalid request nonce. Reopen the canvas." }); return;
                }
                if (request.headers["content-type"] !== "application/json") {
                    send(415, { error: "Send an application/json request." }); return;
                }
                let size = 0;
                const chunks = [];
                for await (const chunk of request) {
                    size += chunk.length;
                    if (size > 16384) throw new PulseError("invalid_input", "The request body exceeds 16 KiB.");
                    chunks.push(chunk);
                }
                let body;
                try { body = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
                catch { throw new PulseError("invalid_input", "The request body is not valid JSON."); }
                if (!body || typeof body !== "object" || Array.isArray(body)
                    || Object.keys(body).some(key => !["action", "input"].includes(key))
                    || typeof body.action !== "string" || !body.input || typeof body.input !== "object" || Array.isArray(body.input)) {
                    throw new PulseError("invalid_input", "Provide an action name and an input object.");
                }
                send(200, await perform(getContext(), body.action, body.input)); return;
            }
            send(404, { error: "Not found." });
        } catch (error) {
            log(error.message);
            const status = error.code === "conflict" ? 409 : error.code === "permission_denied" ? 403
                : ["rate_limit", "retry_later"].includes(error.code) ? 429 : error instanceof PulseError ? 400 : 500;
            send(status, { code: error.code ?? "internal_error", error: error instanceof PulseError ? error.message : "Project Pulse encountered an unexpected error. Check extension diagnostics.", retryAt: error.retryAt ?? null });
        }
    });
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    origin = `http://127.0.0.1:${server.address().port}`;
    return {
        url: `${origin}/`,
        close: () => new Promise((resolve, reject) => {
            server.close(error => error ? reject(error) : resolve());
            server.closeIdleConnections();
        }),
    };
}
