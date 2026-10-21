import { createServer } from "node:http";
import { randomBytes, timingSafeEqual } from "node:crypto";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { TeamLoadError } from "./model.mjs";
import { extensionRoot } from "./state.mjs";
import { renderHtml } from "./renderer.mjs";

const empty = (input, name) => {
    if (Object.keys(input).length) throw new TeamLoadError("invalid_input", `${name} takes no input.`);
};

export async function perform(context, name, input = {}) {
    const hub = context.hub;
    switch (name) {
        case "get_state": empty(input, "get_state"); await hub.loadTracking(); return hub.state();
        case "refresh": {
            const extra = Object.keys(input).filter(key => key !== "force");
            if (extra.length || (input.force !== undefined && typeof input.force !== "boolean")) throw new TeamLoadError("invalid_input", "refresh accepts only { force: boolean }.");
            return hub.refresh({ force: input.force ?? false });
        }
        case "set_tracking": return hub.setTracking(input);
        case "assign": return hub.assign(input);
        case "unassign": return hub.unassign(input);
        case "reassign": return hub.reassign(input);
        case "start_session": return hub.startSession(input);
        case "get_item": return hub.detail(input);
        case "get_momentum": return hub.momentum(input);
        default: throw new TeamLoadError("invalid_input", "Unknown Team Load action.");
    }
}

const assets = new Map([
    ["/app.mjs", ["assets/app.mjs", "text/javascript"]],
    ["/markdown.mjs", ["assets/markdown.mjs", "text/javascript"]],
    ["/icons.mjs", ["assets/icons.mjs", "text/javascript"]],
    ["/momentum.mjs", ["assets/momentum.mjs", "text/javascript"]],
    ["/styles.css", ["assets/styles.css", "text/css"]],
    ["/model.mjs", ["model.mjs", "text/javascript"]],
]);

const IMG_SRC = "'self' data: https://avatars.githubusercontent.com https://*.githubusercontent.com https://github.com https://user-images.githubusercontent.com";
export const CSP = `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src ${IMG_SRC}; media-src https://*.githubusercontent.com https://github.com; base-uri 'none'; form-action 'none'`;

export function statusFor(error) {
    if (error.code === "conflict") return 409;
    if (error.code === "permission_denied") return 403;
    if (error.code === "not_found") return 404;
    if (error.code === "rate_limit") return 429;
    return error instanceof TeamLoadError ? 400 : 500;
}

export async function startServer(getContext, log = () => {}) {
    const nonce = randomBytes(32).toString("hex");
    let origin;
    const server = createServer(async (request, response) => {
        function send(status, value, type = "application/json") {
            response.writeHead(status, {
                "Content-Type": `${type}; charset=utf-8`, "Cache-Control": "no-store",
                "X-Content-Type-Options": "nosniff", "Referrer-Policy": "no-referrer", "Content-Security-Policy": CSP,
            });
            response.end(type === "application/json" ? JSON.stringify(value) : value);
        }
        try {
            if (request.headers.host !== new URL(origin).host) { send(403, { code: "forbidden", error: "Invalid request host." }); return; }
            const url = new URL(request.url, origin);
            if (request.method === "GET" && url.pathname === "/"
                && [...url.searchParams.keys()].every(key => key === "scoutTheme")
                && (!url.search || ["light", "dark"].includes(url.searchParams.get("scoutTheme")))) {
                send(200, renderHtml(nonce, url.searchParams.get("scoutTheme")), "text/html"); return;
            }
            if (request.headers.origin && request.headers.origin !== origin) { send(403, { code: "forbidden", error: "Cross-origin requests are not allowed." }); return; }
            if (request.headers["sec-fetch-site"] === "cross-site") { send(403, { code: "forbidden", error: "Cross-site requests are not allowed." }); return; }
            if (request.method === "GET" && assets.has(url.pathname) && !url.search) {
                const [relative, type] = assets.get(url.pathname);
                send(200, await readFile(join(extensionRoot, ...relative.split("/")), "utf8"), type); return;
            }
            if (request.method === "POST" && url.pathname === "/api/action" && !url.search) {
                const supplied = request.headers["x-team-load-nonce"];
                if (typeof supplied !== "string" || Buffer.byteLength(supplied) !== Buffer.byteLength(nonce)
                    || !timingSafeEqual(Buffer.from(supplied), Buffer.from(nonce))) {
                    send(403, { code: "forbidden", error: "Invalid request nonce. Reopen the canvas." }); return;
                }
                if (request.headers["content-type"] !== "application/json") { send(415, { code: "invalid_input", error: "Send an application/json request." }); return; }
                let size = 0;
                const chunks = [];
                for await (const chunk of request) {
                    size += chunk.length;
                    if (size > 16384) throw new TeamLoadError("invalid_input", "The request body exceeds 16 KiB.");
                    chunks.push(chunk);
                }
                let body;
                try { body = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
                catch { throw new TeamLoadError("invalid_input", "The request body is not valid JSON."); }
                if (!body || typeof body !== "object" || Array.isArray(body)
                    || Object.keys(body).some(key => !["action", "input"].includes(key))
                    || typeof body.action !== "string" || !body.input || typeof body.input !== "object" || Array.isArray(body.input)) {
                    throw new TeamLoadError("invalid_input", "Provide an action name and an input object.");
                }
                send(200, await perform(getContext(), body.action, body.input)); return;
            }
            send(404, { code: "not_found", error: "Not found." });
        } catch (error) {
            log(error.message);
            send(statusFor(error), { code: error.code ?? "internal_error", error: error instanceof TeamLoadError ? error.message : "Team Load hit an unexpected error. Check extension diagnostics.", retryAt: error.retryAt ?? null });
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
