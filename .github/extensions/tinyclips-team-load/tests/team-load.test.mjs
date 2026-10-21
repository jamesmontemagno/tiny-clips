import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile, rm, mkdir } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import {
    AGENT, TeamLoadError, canonLogin, cleanTitle, computeLoad, loadOf, tone, meterScale, sortRoster, sortForAssign,
    ago, parseItemUrl, normalizeTracking, validateItemRef, validateConfig,
    MOMENTUM_DAYS, MOMENTUM_METRICS, dailyMomentum, momentumStart, validateMomentumInput,
} from "../model.mjs";
import { readConfig, createTrackingStore } from "../state.mjs";
import { createGitHub, normalizeIssue, normalizePull } from "../github.mjs";
import { createHub } from "../hub.mjs";
import { startServer, perform, statusFor, CSP } from "../server.mjs";
import { renderHtml } from "../renderer.mjs";

const config = await readConfig();
const T = Date.parse("2026-10-20T12:00:00Z");
const iso = hoursAgo => new Date(T - hoursAgo * 3600_000).toISOString();
const page = (nodes, next = null) => ({ nodes, pageInfo: { hasNextPage: !!next, endCursor: next } });

// ---------- model ----------

test("canonLogin folds Copilot identities and lowercases", () => {
    assert.equal(canonLogin("Copilot"), AGENT);
    assert.equal(canonLogin("copilot-swe-agent"), AGENT);
    assert.equal(canonLogin("copilot-swe-agent[bot]"), AGENT);
    assert.equal(canonLogin("BurkeHolland"), "burkeholland");
    assert.equal(canonLogin(""), null);
    assert.equal(canonLogin(undefined), null);
});

test("cleanTitle strips feature and launch prefixes", () => {
    assert.equal(cleanTitle("[Feature]: Add thing"), "Add thing");
    assert.equal(cleanTitle("Feature: Count down"), "Count down");
    assert.equal(cleanTitle("[2.0 Launch] Ship it"), "Ship it");
});

test("computeLoad assigns issues, PRs, reviews and collects unowned issues", () => {
    const items = [
        { kind: "issue", repository: "o/a", number: 1, title: "Owned", url: "u1", updatedAt: iso(5), labels: [], assignees: ["burkeholland", "jamesmontemagno"] },
        { kind: "issue", repository: "o/a", number: 2, title: "Free", url: "u2", updatedAt: iso(1), labels: [{ name: "bug", color: "d73a4a" }], assignees: [] },
        { kind: "issue", repository: "o/a", number: 3, title: "Copilot's", url: "u3", updatedAt: iso(2), labels: [], assignees: ["copilot"] },
        { kind: "issue", repository: "o/a", number: 4, title: "Outsider", url: "u4", updatedAt: iso(2), labels: [], assignees: ["someone"] },
        { kind: "pr", repository: "o/a", number: 10, title: "PR", url: "u10", updatedAt: iso(3), labels: [], assignees: [], author: "burkeholland", reviewers: ["jamesmontemagno", "burkeholland"], isDraft: true },
        { kind: "pr", repository: "o/a", number: 11, title: "Agent PR", url: "u11", updatedAt: iso(4), labels: [], assignees: ["burkeholland"], author: "copilot", reviewers: [] },
    ];
    const { people, unowned } = computeLoad({ items, people: ["burkeholland", "jamesmontemagno", AGENT], profiles: { burkeholland: { name: "Burke Holland", avatarUrl: "https://a/1" } } });
    const by = Object.fromEntries(people.map(p => [p.login, p]));
    assert.deepEqual(by.burkeholland.items.map(i => `${i.kind}${i.number}`), ["pr10", "pr11", "issue1"]);
    assert.equal(by.burkeholland.name, "Burke Holland");
    assert.equal(by.burkeholland.items.find(i => i.number === 10).isDraft, true, "PR author wins over review request");
    assert.deepEqual(by.jamesmontemagno.items.map(i => `${i.kind}${i.number}`), ["review10", "issue1"]);
    assert.deepEqual(by.copilot.items.map(i => `${i.kind}${i.number}`), ["issue3", "pr11"]);
    assert.equal(by.copilot.agent, true);
    assert.equal(by.copilot.avatarUrl, null);
    assert.deepEqual(unowned.map(i => i.number), [2]);
    assert.equal(loadOf(items, "jamesmontemagno"), 2);
    assert.equal(loadOf(items, "someone"), 1);
});

test("tone, scale and sorting", () => {
    assert.equal(tone(0).key, "free");
    assert.equal(tone(3).key, "room");
    assert.equal(tone(4).key, "busy");
    assert.equal(tone(6).label, "At limit");
    assert.deepEqual(tone(10), { key: "over", label: "4 over limit" });
    assert.equal(tone(2, { agent: true }).label, "Agent · 2 running");
    assert.equal(meterScale([1, 2]), 10);
    assert.equal(meterScale([12]), 12);
    assert.equal(meterScale([40]), 16);
    const people = [
        { login: "a", name: "Ann", agent: false, items: [1] },
        { login: AGENT, name: "Copilot", agent: true, items: [1, 2, 3, 4, 5, 6, 7, 8] },
        { login: "b", name: "Bob", agent: false, items: [1, 2, 3] },
        { login: "c", name: "Cy", agent: false, items: [] },
    ];
    assert.deepEqual(sortRoster(people).map(p => p.login), ["b", "a", "c", AGENT]);
    const { humans, agent } = sortForAssign(people);
    assert.deepEqual(humans.map(p => p.login), ["c", "a", "b"]);
    assert.equal(agent.login, AGENT);
});

test("ago formats relative times", () => {
    assert.equal(ago(iso(0.2), T), "now");
    assert.equal(ago(iso(5), T), "5h");
    assert.equal(ago(iso(72), T), "3d");
    assert.equal(ago("nope", T), "");
});

test("parseItemUrl only accepts github.com issue and PR URLs", () => {
    assert.deepEqual(parseItemUrl("https://github.com/jamesmontemagno/tiny-clips/issues/424"), { repository: "jamesmontemagno/tiny-clips", number: 424 });
    assert.deepEqual(parseItemUrl("https://github.com/o/r/pull/7#discussion"), { repository: "o/r", number: 7 });
    assert.equal(parseItemUrl("https://evil.com/o/r/issues/1"), null);
    assert.equal(parseItemUrl("https://github.com/o/r/blob/main/x"), null);
    assert.equal(parseItemUrl("javascript:alert(1)"), null);
});

test("normalizeTracking and validateItemRef validate input", () => {
    assert.deepEqual(normalizeTracking({ people: ["@BurkeHolland", "burkeholland", "Copilot"], repos: ["o/r", "O/R"] }), { people: ["burkeholland", AGENT], repos: ["o/r"] });
    assert.throws(() => normalizeTracking({ people: ["bad login!"], repos: [] }), TeamLoadError);
    assert.throws(() => normalizeTracking({ people: [], repos: ["not-a-repo"] }), TeamLoadError);
    assert.throws(() => normalizeTracking({ people: Array.from({ length: 30 }, (_, i) => `u${i}`), repos: [] }), TeamLoadError);
    assert.deepEqual(validateItemRef({ repository: "o/r", number: 3, login: "@Copilot" }, { login: true }), { repository: "o/r", number: 3, login: AGENT });
    assert.throws(() => validateItemRef({ repository: "o/r", number: 0 }), TeamLoadError);
    assert.throws(() => validateItemRef({ repository: "o/r", number: 1, extra: true }), TeamLoadError);
    assert.throws(() => validateItemRef({ repository: "o/r", number: 1, login: "a b" }, { login: true }), TeamLoadError);
});

test("bundled config is valid and offers tiny-clips-mac", () => {
    assert.ok(config.availableRepositories.includes("jamesmontemagno/tiny-clips-mac"));
    assert.ok(config.people.includes(AGENT));
    assert.throws(() => validateConfig({ ...config, format: "nope" }), TeamLoadError);
});

test("validateItemRef accepts additional only where it is allowed", () => {
    const ref = { repository: "o/r", number: 3, login: "@Burke", additional: true };
    assert.deepEqual(validateItemRef(ref, { login: true, additional: true }), { repository: "o/r", number: 3, login: "burke", additional: true });
    assert.throws(() => validateItemRef(ref, { login: true }), error => error.code === "invalid_input", "unassign must not accept additional");
    assert.throws(() => validateItemRef({ ...ref, additional: "yes" }, { login: true, additional: true }), error => error.code === "invalid_input");
});

test("reassignment validates and canonicalizes both assignees", () => {
    const ref = { repository: "o/r", number: 5, from: "@Burke", login: "@Copilot" };
    assert.deepEqual(validateItemRef(ref, { login: true, from: true }), { ...ref, from: "burke", login: AGENT });
    for (const bad of [{ ...ref, from: "" }, { ...ref, from: "a b" }, { ...ref, from: undefined }, { ...ref, from: "@copilot" }, { ...ref, additional: true }]) {
        assert.throws(() => validateItemRef(bad, { login: true, from: true }), error => error.code === "invalid_input");
    }
    assert.throws(() => validateItemRef(ref, { login: true }), error => error.code === "invalid_input");
});

test("validateMomentumInput applies defaults and rejects bad input", () => {
    assert.deepEqual(validateMomentumInput({}), { days: 7, person: null, everyone: false, repository: null, force: false });
    assert.deepEqual(validateMomentumInput({ days: 7, person: "@Copilot", repository: "o/r", force: true }), { days: 7, person: AGENT, everyone: false, repository: "o/r", force: true });
    assert.deepEqual(validateMomentumInput({ days: 90, everyone: true }).everyone, true);
    assert.deepEqual([...MOMENTUM_DAYS], [7, 30, 90]);
    for (const bad of [{ days: 8 }, { days: "7" }, { person: "a b" }, { person: "x", everyone: true }, { everyone: "yes" }, { repository: "nope" }, { force: 1 }, { extra: 1 }, []]) {
        assert.throws(() => validateMomentumInput(bad), error => error.code === "invalid_input", JSON.stringify(bad));
    }
});

test("dailyMomentum buckets UTC days, de-dupes, and scopes by person, team and everyone", () => {
    const now = Date.parse("2026-11-10T15:00:00Z");
    const at = (day, time = "10:00:00") => `2026-11-${day}T${time}Z`;
    const events = [
        { type: "commit", at: at("10", "00:00:00"), key: "a", actors: ["BurkeHolland"] },
        { type: "commit", at: at("10", "00:00:00"), key: "a", actors: ["burkeholland"] },
        { type: "commit", at: at("09", "23:59:59"), key: "b", actors: ["stranger"] },
        { type: "commit", at: at("04", "00:00:00"), key: "edge", actors: ["burkeholland"] },
        { type: "commit", at: at("03", "23:59:59"), key: "before", actors: ["burkeholland"] },
        { type: "prOpened", at: at("05"), key: "o/r#1", actors: ["Copilot"] },
        { type: "prMerged", at: at("11"), key: "o/r#1", actors: ["copilot"] },
        { type: "issueCompleted", at: at("06"), key: "o/r#2", actors: ["burkeholland", "copilot"] },
        { type: "issueOpened", at: at("07"), key: "o/r#3", actors: [] },
        { type: "mystery", at: at("07"), key: "x", actors: ["burkeholland"] },
        { type: "commit", at: "not a date", key: "bad", actors: ["burkeholland"] },
    ];
    const team = ["burkeholland", AGENT];
    const weekly = dailyMomentum(events, { days: 7, team }, now);
    assert.equal(weekly.series.length, 7);
    assert.equal(weekly.series[0].date, "2026-11-04");
    assert.equal(weekly.series.at(-1).date, "2026-11-10");
    assert.deepEqual(weekly.totals, { commits: 2, prsOpened: 1, prsMerged: 0, issuesOpened: 0, issuesCompleted: 1 });
    assert.equal(weekly.series[0].commits, 1, "the first instant of the first UTC day is inside the window");
    assert.equal(weekly.series.at(-1).commits, 1, "duplicates are counted once");
    assert.equal(weekly.unattributed, 1);
    assert.equal(MOMENTUM_METRICS.length, 5);
    for (const metric of MOMENTUM_METRICS) assert.equal(weekly.series.reduce((sum, day) => sum + day[metric.key], 0), weekly.totals[metric.key]);
    assert.deepEqual(dailyMomentum(events, { days: 7, everyone: true, team }, now).totals, { commits: 3, prsOpened: 1, prsMerged: 0, issuesOpened: 1, issuesCompleted: 1 });
    assert.deepEqual(dailyMomentum(events, { days: 7, person: AGENT, team }, now).totals, { commits: 0, prsOpened: 1, prsMerged: 0, issuesOpened: 0, issuesCompleted: 1 });
    assert.equal(dailyMomentum(events, { days: 30, person: "burkeholland", team }, now).totals.commits, 3, "a longer window reaches the earlier commit");
    assert.equal(momentumStart(7, now), Date.parse("2026-11-04T00:00:00Z"));
    assert.deepEqual(dailyMomentum([], { days: 7 }, now).totals, { commits: 0, prsOpened: 0, prsMerged: 0, issuesOpened: 0, issuesCompleted: 0 });
});

// ---------- github ----------

function fakeGh(handlers = {}) {
    const calls = [];
    const run = async (args, options) => {
        calls.push(args);
        const query = args.find(a => typeof a === "string" && a.startsWith("query="))?.slice(6) ?? "";
        const vars = Object.fromEntries(args.filter((a, i) => (args[i - 1] === "-f" || args[i - 1] === "-F") && !a.startsWith("query=")).map(a => a.split("=")));
        for (const [match, handler] of Object.entries(handlers)) {
            if (args.join(" ").includes(match) || query.includes(match)) return handler({ args, vars, options, calls });
        }
        throw new Error(`unexpected gh call: ${args.join(" ").slice(0, 120)}`);
    };
    return { run, calls };
}

const issueNode = (number, assignees = [], extra = {}) => ({ number, title: `Issue ${number}`, url: `https://github.com/o/r/issues/${number}`, updatedAt: iso(number), labels: { nodes: [{ name: "bug", color: "zzz" }] }, assignees: { nodes: assignees.map(login => ({ login })) }, ...extra });
const prNode = (number, extra = {}) => ({ number, title: `PR ${number}`, url: `https://github.com/o/r/pull/${number}`, updatedAt: iso(number), isDraft: false, author: { login: "copilot-swe-agent" }, labels: { nodes: [] }, assignees: { nodes: [] }, reviewRequests: { nodes: [{ requestedReviewer: { login: "JamesMontemagno" } }, { requestedReviewer: null }] }, ...extra });

test("normalizers canonicalize people and sanitize label colors", () => {
    const issue = normalizeIssue(issueNode(1, ["Copilot", "Burke"]), "o/r");
    assert.deepEqual(issue.assignees, [AGENT, "burke"]);
    assert.equal(issue.labels[0].color, "8c959f");
    const pr = normalizePull(prNode(2), "o/r");
    assert.equal(pr.author, AGENT);
    assert.deepEqual(pr.reviewers, ["jamesmontemagno"]);
});

test("fetchRepo paginates issues and PRs", async () => {
    const { run, calls } = fakeGh({
        "issues(states:OPEN": ({ vars }) => ({ data: { repository: { issues: vars.after ? page([issueNode(2)]) : page([issueNode(1)], "c1") } } }),
        "pullRequests(states:OPEN": () => ({ data: { repository: { pullRequests: page([prNode(3)]) } } }),
    });
    const items = await createGitHub({ run }).fetchRepo("o/r");
    assert.deepEqual(items.map(i => `${i.kind}${i.number}`), ["issue1", "issue2", "pr3"]);
    assert.equal(calls.length, 3);
    assert.ok(calls.every(args => args.includes("--hostname") && args.includes("github.com")));
});

test("fetchRepo reports coverage limits instead of silently truncating", async () => {
    const { run } = fakeGh({
        "issues(states:OPEN": () => ({ data: { repository: { issues: page([issueNode(1)], "more") } } }),
        "pullRequests(states:OPEN": () => ({ data: { repository: { pullRequests: page([]) } } }),
    });
    await assert.rejects(createGitHub({ run }).fetchRepo("o/r"), error => error.code === "coverage_limit");
});

test("profile requests are coalesced and cached; orgs are rejected", async () => {
    let hits = 0;
    const { run } = fakeGh({
        "users/burke": async () => { hits++; await new Promise(r => setTimeout(r, 5)); return { login: "Burke", name: "Burke H", avatar_url: "https://a/b", type: "User" }; },
        "users/acme": () => ({ login: "acme", type: "Organization" }),
    });
    const gh = createGitHub({ run });
    const [a, b] = await Promise.all([gh.profile("burke"), gh.profile("Burke")]);
    assert.equal(hits, 1);
    assert.deepEqual(a, b);
    await gh.profile("burke");
    assert.equal(hits, 1);
    assert.equal((await gh.profile("copilot")).agent, true);
    await assert.rejects(gh.profile("acme"), error => error.code === "not_found");
});

test("graphql errors surface as TeamLoadErrors", async () => {
    const { run } = fakeGh({ "nameWithOwner url": () => ({ errors: [{ type: "NOT_FOUND", message: "Could not resolve" }] }) });
    await assert.rejects(createGitHub({ run }).repoInfo("o/missing"), error => error.code === "not_found");
});

function issueServer(initial = []) {
    let assignees = [...initial];
    let state = "open";
    let isPr = false;
    const { run, calls } = fakeGh({
        "repos/o/r/issues/5": () => ({ state, pull_request: isPr ? {} : undefined, assignees: assignees.map(login => ({ login })) }),
        "issue edit": ({ args }) => {
            const login = args[args.length - 1] === "@copilot" ? "Copilot" : args[args.length - 1];
            if (args.includes("--add-assignee")) assignees.push(login);
            else assignees = assignees.filter(l => l !== login);
            return "";
        },
    });
    return { run, calls, set: next => { ({ state = state, isPr = isPr } = next); } };
}

test("assign uses gh issue edit, maps Copilot to @copilot, and verifies", async () => {
    const server = issueServer();
    const gh = createGitHub({ run: server.run });
    assert.deepEqual(await gh.assign({ repository: "o/r", number: 5, login: AGENT }), { assignees: [AGENT], added: true });
    const edit = server.calls.find(args => args[0] === "issue");
    assert.deepEqual(edit, ["issue", "edit", "5", "--repo", "o/r", "--add-assignee", "@copilot"]);
    assert.deepEqual(await gh.unassign({ repository: "o/r", number: 5, login: AGENT }), { assignees: [] });
    assert.ok(server.calls.some(args => args.includes("--remove-assignee") && args.includes("@copilot")));
});

test("assign with additional adds beside existing assignees and is a no-op for an existing one", async () => {
    const server = issueServer(["jamesmontemagno"]);
    const gh = createGitHub({ run: server.run });
    assert.deepEqual(await gh.assign({ repository: "o/r", number: 5, login: AGENT, additional: true }), { assignees: ["jamesmontemagno", AGENT], added: true });
    const writes = () => server.calls.filter(args => args[0] === "issue").length;
    assert.equal(writes(), 1);
    assert.deepEqual(await gh.assign({ repository: "o/r", number: 5, login: "jamesmontemagno", additional: true }), { assignees: ["jamesmontemagno", AGENT], added: false });
    assert.equal(writes(), 1, "no write when the person is already assigned");
    await assert.rejects(gh.assign({ repository: "o/r", number: 5, login: "burkeholland" }), error => error.code === "conflict", "the strict triage guard still applies without additional");
    const closed = issueServer(["jamesmontemagno"]);
    closed.set({ state: "closed" });
    await assert.rejects(createGitHub({ run: closed.run }).assign({ repository: "o/r", number: 5, login: AGENT, additional: true }), error => error.code === "conflict");
    const pr = issueServer(["jamesmontemagno"]);
    pr.set({ isPr: true });
    await assert.rejects(createGitHub({ run: pr.run }).assign({ repository: "o/r", number: 5, login: AGENT, additional: true }), error => error.code === "invalid_input");
});

test("unassign removes only the requested assignee and is idempotent", async () => {
    const server = issueServer(["jamesmontemagno", "Copilot"]);
    const gh = createGitHub({ run: server.run });
    assert.deepEqual(await gh.unassign({ repository: "o/r", number: 5, login: "jamesmontemagno" }), { assignees: [AGENT] });
    assert.deepEqual(await gh.unassign({ repository: "o/r", number: 5, login: "jamesmontemagno" }), { assignees: [AGENT] });
    assert.equal(server.calls.filter(args => args.includes("--remove-assignee")).length, 1);
});

test("reassign verifies the new owner before removing the source and preserves other assignees", async () => {
    const server = issueServer(["jamesmontemagno", "helper"]);
    const result = await createGitHub({ run: server.run }).reassign({ repository: "o/r", number: 5, from: "jamesmontemagno", login: AGENT });
    assert.deepEqual(result, { assignees: ["helper", AGENT], added: true });
    const writes = server.calls.filter(args => args[0] === "issue");
    assert.deepEqual(writes.map(args => args.slice(-2)), [["--add-assignee", "@copilot"], ["--remove-assignee", "jamesmontemagno"]]);
    const addIndex = server.calls.indexOf(writes[0]), removeIndex = server.calls.indexOf(writes[1]);
    assert.ok(server.calls.slice(addIndex + 1, removeIndex).some(args => args[0] === "api"), "a fresh read verifies the new owner before removal");
});

test("reassign to an existing co-assignee only removes the source, including Copilot", async () => {
    const server = issueServer(["Copilot", "burkeholland", "helper"]);
    const result = await createGitHub({ run: server.run }).reassign({ repository: "o/r", number: 5, from: AGENT, login: "burkeholland" });
    assert.deepEqual(result, { assignees: ["burkeholland", "helper"], added: false });
    assert.deepEqual(server.calls.filter(args => args[0] === "issue").map(args => args.slice(-2)), [["--remove-assignee", "@copilot"]]);
});

test("reassign rejects stale sources, closed issues, PRs and same-person drops without writes", async () => {
    for (const scenario of [
        { initial: [], from: "burkeholland", login: AGENT, code: "conflict" },
        { initial: ["burkeholland"], state: "closed", from: "burkeholland", login: AGENT, code: "conflict" },
        { initial: ["burkeholland"], isPr: true, from: "burkeholland", login: AGENT, code: "invalid_input" },
        { initial: ["burkeholland"], from: "burkeholland", login: "burkeholland", code: "invalid_input" },
    ]) {
        const server = issueServer(scenario.initial);
        server.set({ state: scenario.state ?? "open", isPr: scenario.isPr ?? false });
        await assert.rejects(createGitHub({ run: server.run }).reassign({ repository: "o/r", number: 5, from: scenario.from, login: scenario.login }), error => error.code === scenario.code);
        assert.equal(server.calls.filter(args => args[0] === "issue").length, 0);
    }
});

test("reassign never removes the source when adding the new owner fails or is ignored", async () => {
    for (const fail of [false, true]) {
        const server = issueServer(["burkeholland"]);
        const run = async (args, options) => {
            if (args.includes("--add-assignee")) {
                if (fail) throw new TeamLoadError("permission_denied", "Copilot is unavailable.");
                return "";
            }
            return server.run(args, options);
        };
        await assert.rejects(createGitHub({ run }).reassign({ repository: "o/r", number: 5, from: "burkeholland", login: AGENT }), TeamLoadError);
        assert.ok(!server.calls.some(args => args.includes("--remove-assignee")));
        assert.deepEqual((await createGitHub({ run: server.run }).readIssue({ repository: "o/r", number: 5 })).assignees, ["burkeholland"]);
    }
});

test("reassign reports partial writes explicitly when removing the source fails", async () => {
    const server = issueServer(["burkeholland"]);
    const run = async (args, options) => {
        if (args.includes("--remove-assignee")) throw new TeamLoadError("permission_denied", "Removal denied.");
        return server.run(args, options);
    };
    const gh = createGitHub({ run });
    await assert.rejects(gh.reassign({ repository: "o/r", number: 5, from: "burkeholland", login: AGENT }), error => error.code === "write_uncertain" && /may still be assigned/.test(error.message) && /Removal denied/.test(error.message));
    assert.deepEqual((await gh.readIssue({ repository: "o/r", number: 5 })).assignees, ["burkeholland", AGENT]);
});

test("assign refuses conflicts: already assigned, closed, or a PR", async () => {
    const taken = issueServer(["jamesmontemagno"]);
    await assert.rejects(createGitHub({ run: taken.run }).assign({ repository: "o/r", number: 5, login: "burkeholland" }), error => error.code === "conflict" && /@jamesmontemagno/.test(error.message));
    assert.ok(!taken.calls.some(args => args[0] === "issue"), "no write after a conflict");
    const closed = issueServer();
    closed.set({ state: "closed" });
    await assert.rejects(createGitHub({ run: closed.run }).assign({ repository: "o/r", number: 5, login: "burkeholland" }), error => error.code === "conflict");
    const pr = issueServer();
    pr.set({ isPr: true });
    await assert.rejects(createGitHub({ run: pr.run }).assign({ repository: "o/r", number: 5, login: "burkeholland" }), error => error.code === "invalid_input");
});

test("assign reports when GitHub silently ignores the assignee", async () => {
    const { run } = fakeGh({
        "repos/o/r/issues/5": () => ({ state: "open", assignees: [] }),
        "issue edit": () => "",
    });
    await assert.rejects(createGitHub({ run }).assign({ repository: "o/r", number: 5, login: AGENT }), error => error.code === "github_error" && /Copilot/.test(error.message));
});

test("writes are never retried; reads retry once on transient errors", async () => {
    let edits = 0, reads = 0;
    const flaky = fakeGh({
        "repos/o/r/issues/5": () => ({ state: "open", assignees: [] }),
        "issue edit": () => { edits++; throw new Error("HTTP 502"); },
    });
    await assert.rejects(createGitHub({ run: flaky.run }).assign({ repository: "o/r", number: 5, login: "x" }));
    assert.equal(edits, 1);
    const read = fakeGh({ "users/x": () => { reads++; if (reads === 1) throw new Error("HTTP 502"); return { login: "x", type: "User" }; } });
    assert.equal((await createGitHub({ run: read.run }).profile("x")).login, "x");
    assert.equal(reads, 2);
});

const commitNode = (oid, hoursAgo, login = "Burke") => ({ oid, committedDate: iso(hoursAgo), author: { user: login ? { login } : null } });
const pullHistoryNode = (number, extra = {}) => ({ number, createdAt: iso(300), updatedAt: iso(number), mergedAt: null, author: { login: "Burke" }, ...extra });
const issueHistoryNode = (number, extra = {}) => ({ number, createdAt: iso(300), updatedAt: iso(number), closedAt: null, state: "OPEN", stateReason: null, author: { login: "Burke" }, assignees: { nodes: [] }, ...extra });
const commitsPage = (nodes, next = null) => ({ data: { repository: { defaultBranchRef: { target: { history: page(nodes, next) } } } } });

test("history turns commits, PRs and issues into attributed events and pages back only to the window start", async () => {
    // 7 days ending 2026-10-20 reaches back to 2026-10-14T00:00Z, i.e. 156 hours before T.
    const since = new Date(momentumStart(7, T)).toISOString();
    const { run, calls } = fakeGh({
        "history(first:100": ({ vars }) => commitsPage(vars.after ? [commitNode("c3", 20, "Copilot")] : [commitNode("c1", 1), commitNode("c2", 2, null)], vars.after ? null : "cc1"),
        "pullRequests(first:100": ({ vars }) => ({ data: { repository: { pullRequests: vars.after
            ? page([pullHistoryNode(60), pullHistoryNode(400)], "never-requested")
            : page([pullHistoryNode(3, { mergedAt: iso(2), author: { login: "copilot-swe-agent" } })], "p1") } } }),
        "issues(first:100": () => ({ data: { repository: { issues: page([
            issueHistoryNode(7, { state: "CLOSED", stateReason: "COMPLETED", closedAt: iso(5), assignees: { nodes: [{ login: "Burke" }, { login: "copilot-swe-agent" }] } }),
            issueHistoryNode(8, { state: "CLOSED", stateReason: "NOT_PLANNED", closedAt: iso(6) }),
            issueHistoryNode(9),
        ]) } } }),
    });
    const result = await createGitHub({ run, now: () => T }).history("o/r", { days: 7 });
    assert.equal(result.truncated, false);
    const events = Object.fromEntries(result.events.map(event => [`${event.type}:${event.key}`, event]));
    assert.deepEqual(events["commit:o/r@c1"].actors, ["burke"]);
    assert.deepEqual(events["commit:o/r@c2"].actors, [], "a commit without a GitHub account has no actor");
    assert.deepEqual(events["commit:o/r@c3"].actors, [AGENT]);
    assert.deepEqual(events["prOpened:o/r#3"].actors, [AGENT]);
    assert.equal(events["prMerged:o/r#3"].at, iso(2));
    assert.equal(events["prOpened:o/r#60"].at, iso(300), "a PR updated in the window is kept even if it was opened before it");
    assert.equal(events["prOpened:o/r#400"], undefined, "a PR last updated before the window is dropped");
    assert.deepEqual(events["issueCompleted:o/r#7"].actors, ["burke", AGENT], "completion is credited to the assignees");
    assert.equal(events["issueCompleted:o/r#8"], undefined, "closing as not planned is not completion");
    assert.equal(events["issueCompleted:o/r#9"], undefined);
    assert.deepEqual(events["issueOpened:o/r#9"].actors, ["burke"]);
    assert.equal(calls.filter(args => args.join(" ").includes("pullRequests(first:100")).length, 2, "stops paging once a page runs past the window");
    assert.ok(calls.filter(args => args.join(" ").includes("history(first:100")).every(args => args.includes(`since=${since}`)), "commits are filtered server-side");
});

test("history flags truncation instead of silently dropping older activity", async () => {
    const { run, calls } = fakeGh({
        "history(first:100": () => commitsPage([commitNode("c", 1)], "more"),
        "pullRequests(first:100": () => ({ data: { repository: { pullRequests: page([], null) } } }),
        "issues(first:100": () => ({ data: { repository: { issues: page([], null) } } }),
    });
    const result = await createGitHub({ run, now: () => T }).history("o/r", { days: 90 });
    assert.equal(result.truncated, true);
    assert.equal(calls.filter(args => args.join(" ").includes("history(first:100")).length, 10, "page cap");
});

test("history tolerates an empty repository and reports a missing one", async () => {
    const empty = fakeGh({
        "history(first:100": () => ({ data: { repository: { defaultBranchRef: null } } }),
        "pullRequests(first:100": () => ({ data: { repository: { pullRequests: page([]) } } }),
        "issues(first:100": () => ({ data: { repository: { issues: page([]) } } }),
    });
    assert.deepEqual(await createGitHub({ run: empty.run, now: () => T }).history("o/r", { days: 7 }), { events: [], truncated: false });
    const missing = fakeGh({
        "history(first:100": () => ({ data: { repository: null } }),
        "pullRequests(first:100": () => ({ data: { repository: null } }),
        "issues(first:100": () => ({ data: { repository: null } }),
    });
    await assert.rejects(createGitHub({ run: missing.run, now: () => T }).history("o/r", { days: 7 }), error => error.code === "not_found");
});

test("detail normalizes issues and merged PRs", async () => {
    const node = {
        __typename: "PullRequest", number: 9, title: "T", url: "https://github.com/o/r/pull/9", state: "MERGED", merged: true, isDraft: false,
        createdAt: iso(10), updatedAt: iso(1), bodyHTML: "<p>hi</p>", author: { login: "copilot-swe-agent", avatarUrl: "x" },
        labels: { nodes: [] }, assignees: { nodes: [{ login: "Burke", name: "B", avatarUrl: "a" }] },
        comments: { totalCount: 40, nodes: [{ url: "c", createdAt: iso(2), bodyHTML: "<p>c</p>", author: null }] },
    };
    const { run } = fakeGh({ issueOrPullRequest: () => ({ data: { repository: { issueOrPullRequest: node } } }) });
    const d = await createGitHub({ run }).detail({ repository: "o/r", number: 9 });
    assert.equal(d.kind, "pr");
    assert.equal(d.state, "merged");
    assert.equal(d.author.agent, true);
    assert.equal(d.comments.total, 40);
    assert.equal(d.comments.nodes[0].author, null);
});

// ---------- tracking store ----------

test("tracking store defaults, persists atomically, and remembers added entries", async () => {
    const home = await mkdtemp(join(tmpdir(), "team-load-"));
    try {
        const store = createTrackingStore(config, home);
        assert.deepEqual((await store.read()).people, config.people);
        await Promise.all([
            store.write({ people: ["burkeholland", "newperson"], repos: ["jamesmontemagno/tiny-clips", "someone/else"] }),
            store.write({ people: ["burkeholland"], repos: ["jamesmontemagno/tiny-clips-mac"] }),
        ]);
        const saved = JSON.parse(await readFile(store.path, "utf8"));
        assert.equal(saved.format, "tinyclips-team-load-tracking");
        assert.deepEqual(saved.people, ["burkeholland"]);
        assert.deepEqual(saved.repos, ["jamesmontemagno/tiny-clips-mac"]);
        assert.deepEqual(saved.knownPeople, ["newperson"]);
        assert.deepEqual(saved.knownRepos, ["someone/else"]);
        await writeFile(store.path, "{ broken");
        await assert.rejects(store.read(), error => error.code === "storage_error");
    } finally { await rm(home, { recursive: true, force: true }); }
});

test("tracking store still reads after remembering more people than can be tracked", async () => {
    const home = await mkdtemp(join(tmpdir(), "team-load-"));
    try {
        const store = createTrackingStore(config, home);
        for (let i = 0; i < 3; i++) {
            const people = Array.from({ length: 10 }, (_, j) => `person${i * 10 + j}`);
            await store.write({ people, repos: ["jamesmontemagno/tiny-clips"] });
        }
        const read = await store.read();
        assert.equal(read.knownPeople.length, 30);
        await store.write({ people: ["burkeholland"], repos: ["jamesmontemagno/tiny-clips"] });
        assert.equal((await store.read()).knownPeople.length, 30);
    } finally { await rm(home, { recursive: true, force: true }); }
});

// ---------- hub ----------

function hubFixture({ sendPrompt = async () => {}, issues = { "o/r": [issueNode(5)] }, failing = [], events = {}, now = () => T } = {}) {
    const cfg = { ...config, people: ["burkeholland", "jamesmontemagno", AGENT], repositories: ["o/r"], availableRepositories: ["o/other"] };
    const store = { data: null, read: async () => store.data ?? { people: cfg.people, repos: cfg.repositories, knownPeople: [], knownRepos: [] }, write: async next => (store.data = { ...normalizeTracking(next), knownPeople: [], knownRepos: [] }) };
    const writes = [];
    const historyCalls = [];
    const assignees = new Map();
    const github = {
        fetchRepo: async name => { if (failing.includes(name)) throw new TeamLoadError("permission_denied", "no access"); return (issues[name] ?? []).map(node => normalizeIssue({ ...node, assignees: { nodes: (assignees.get(`${name}#${node.number}`) ?? []).map(login => ({ login })) } }, name)); },
        history: async (name, { days }) => { historyCalls.push([name, days]); return { events: events[name] ?? [], truncated: false }; },
        repoInfo: async name => { if (name === "o/missing") throw new TeamLoadError("not_found", "nope"); return { nameWithOwner: name, url: `https://github.com/${name}`, open: 1, actors: [{ login: "Helper", name: "Help", avatarUrl: "h", agent: false }] }; },
        profile: async login => { if (login === "ghost404") throw new TeamLoadError("not_found", "no"); return { login, name: login.toUpperCase(), avatarUrl: null }; },
        me: async () => "burkeholland",
        detail: async ref => ({ ...ref, title: "x" }),
        // Mirrors the real layer: without `additional` an owned issue is a conflict; with it, an existing assignee is a no-op.
        assign: async ({ repository, number, login, additional }) => {
            const key = `${repository}#${number}`;
            const current = assignees.get(key) ?? [];
            if (additional && current.includes(login)) return { assignees: current, added: false };
            if (!additional && current.length) throw new TeamLoadError("conflict", `${key} is already assigned.`);
            writes.push(["assign", repository, number, login, ...(additional ? ["additional"] : [])]);
            assignees.set(key, [...current, login]);
            return { assignees: [...current, login], added: true };
        },
        unassign: async ({ repository, number, login }) => {
            writes.push(["unassign", repository, number, login]);
            const key = `${repository}#${number}`;
            const left = (assignees.get(key) ?? []).filter(value => value !== login);
            assignees.set(key, left);
            return { assignees: left };
        },
        reassign: async ({ repository, number, from, login }) => {
            const key = `${repository}#${number}`;
            if (!(assignees.get(key) ?? []).includes(from)) throw new TeamLoadError("conflict", `${key} is no longer assigned to ${from}.`);
            const result = await github.assign({ repository, number, login, additional: true });
            const removed = await github.unassign({ repository, number, login: from });
            return { ...removed, added: result.added };
        },
    };
    return { hub: createHub(cfg, { store, github, sendPrompt, now }), writes, store, github, historyCalls, assignees };
}

async function settle(hub) {
    for (let i = 0; i < 20 && hub.state().refreshing; i++) await new Promise(r => setTimeout(r, 1));
}

test("hub refresh builds state with directory and per-repo status", async () => {
    const { hub } = hubFixture();
    await hub.refresh();
    await settle(hub);
    const s = hub.state();
    assert.equal(s.refreshing, false);
    assert.equal(s.me, "burkeholland");
    assert.equal(s.repoStatus["o/r"].status, "ready");
    assert.deepEqual(s.items.map(i => i.number), [5]);
    assert.ok(s.directory.people.some(p => p.login === "helper" || p.login === "Helper"));
    assert.ok(s.directory.repos.some(r => r.nameWithOwner === "o/other"));
});

test("hub assign/unassign enforce tracking and return fresh state", async () => {
    const { hub, writes } = hubFixture();
    await hub.refresh(); await settle(hub);
    const result = await hub.assign({ repository: "o/r", number: 5, login: "@Copilot" });
    assert.deepEqual(writes.at(-1), ["assign", "o/r", 5, AGENT]);
    assert.deepEqual(result.state.items[0].assignees, [AGENT]);
    await hub.unassign({ repository: "o/r", number: 5, login: AGENT });
    assert.deepEqual(writes.at(-1), ["unassign", "o/r", 5, AGENT]);
    await assert.rejects(hub.assign({ repository: "o/untracked", number: 5, login: AGENT }), error => error.code === "invalid_input");
    await assert.rejects(hub.assign({ repository: "o/r", number: 5, login: "stranger" }), error => error.code === "invalid_input");
});

test("hub can add and remove assignees on an owned issue, including outside assignees", async () => {
    const { hub, writes, assignees } = hubFixture();
    await hub.refresh(); await settle(hub);
    await hub.assign({ repository: "o/r", number: 5, login: "jamesmontemagno" });
    await assert.rejects(hub.assign({ repository: "o/r", number: 5, login: AGENT }), error => error.code === "conflict", "a second assignee needs additional");
    const added = await hub.assign({ repository: "o/r", number: 5, login: AGENT, additional: true });
    assert.deepEqual(writes.at(-1), ["assign", "o/r", 5, AGENT, "additional"]);
    assert.equal(added.added, true);
    assert.deepEqual(added.state.items[0].assignees, ["jamesmontemagno", AGENT]);
    assert.equal((await hub.assign({ repository: "o/r", number: 5, login: AGENT, additional: true })).added, false, "adding someone already assigned is a no-op");
    await assert.rejects(hub.assign({ repository: "o/r", number: 5, login: "stranger", additional: true }), error => error.code === "invalid_input", "only known people can be added");
    assignees.set("o/r#5", ["jamesmontemagno", "stranger"]);
    const removed = await hub.unassign({ repository: "o/r", number: 5, login: "@Stranger" });
    assert.deepEqual(writes.at(-1), ["unassign", "o/r", 5, "stranger"], "an outside assignee can always be removed");
    assert.deepEqual(removed.state.items[0].assignees, ["jamesmontemagno"]);
    await assert.rejects(hub.unassign({ repository: "o/untracked", number: 5, login: "stranger" }), error => error.code === "invalid_input");
    await assert.rejects(hub.unassign({ repository: "o/r", number: 5, login: "stranger", additional: true }), error => error.code === "invalid_input");
});

test("reassign action enforces tracking, canonicalizes logins and returns post-write state", async () => {
    const { hub, writes, assignees } = hubFixture();
    assignees.set("o/r#5", ["burkeholland", "outside"]);
    const result = await perform({ hub }, "reassign", { repository: "O/R", number: 5, from: "@BurkeHolland", login: "@Copilot" });
    assert.deepEqual(result.assignees, ["outside", AGENT]);
    assert.deepEqual(result.state.items[0].assignees, ["outside", AGENT]);
    assert.equal(result.added, true);
    assert.deepEqual(writes, [["assign", "o/r", 5, AGENT, "additional"], ["unassign", "o/r", 5, "burkeholland"]]);
    for (const bad of [
        { repository: "o/untracked", number: 5, from: AGENT, login: "burkeholland" },
        { repository: "o/r", number: 5, from: AGENT, login: "stranger" },
        { repository: "o/r", number: 5, from: AGENT, login: AGENT },
        { repository: "o/r", number: 5, login: "burkeholland" },
    ]) {
        await assert.rejects(perform({ hub }, "reassign", bad), error => error.code === "invalid_input");
    }
    assert.equal(writes.length, 2);
});

test("hub refreshes after a failed move so partial writes do not leave stale load counts", async () => {
    const { hub, github, assignees } = hubFixture();
    assignees.set("o/r#5", ["burkeholland"]);
    await hub.refresh(); await settle(hub);
    github.reassign = async () => {
        assignees.set("o/r#5", ["burkeholland", AGENT]);
        throw new TeamLoadError("write_uncertain", "Removal failed.");
    };
    await assert.rejects(hub.reassign({ repository: "o/r", number: 5, from: "burkeholland", login: AGENT }), error => error.code === "write_uncertain");
    assert.deepEqual(hub.state().items[0].assignees, ["burkeholland", AGENT]);
});

test("start_session assigns me, sends the prompt, and rolls back if sending fails", async () => {
    const prompts = [];
    const ok = hubFixture({ sendPrompt: async prompt => prompts.push(prompt) });
    await ok.hub.refresh(); await settle(ok.hub);
    await ok.hub.startSession({ repository: "o/r", number: 5, title: "Fix it" });
    assert.deepEqual(ok.writes, [["assign", "o/r", 5, "burkeholland"]]);
    assert.match(prompts[0], /o\/r issue #5 title="Fix it"/);
    const bad = hubFixture({ sendPrompt: async () => { throw new Error("offline"); } });
    await bad.hub.refresh(); await settle(bad.hub);
    await assert.rejects(bad.hub.startSession({ repository: "o/r", number: 5 }));
    assert.deepEqual(bad.writes, [["assign", "o/r", 5, "burkeholland"], ["unassign", "o/r", 5, "burkeholland"]]);
});

test("start_session on an issue that already has assignees only undoes what it added", async () => {
    const ok = hubFixture();
    await ok.hub.refresh(); await settle(ok.hub);
    ok.assignees.set("o/r#5", ["jamesmontemagno"]);
    await assert.rejects(ok.hub.startSession({ repository: "o/r", number: 5 }), error => error.code === "conflict", "the strict guard applies without additional");
    const started = await ok.hub.startSession({ repository: "o/r", number: 5, additional: true });
    assert.deepEqual(started.assignees, ["jamesmontemagno", "burkeholland"]);
    assert.equal(started.added, true);
    assert.deepEqual(ok.writes, [["assign", "o/r", 5, "burkeholland", "additional"]]);
    const again = await ok.hub.startSession({ repository: "o/r", number: 5, additional: true });
    assert.equal(again.added, false, "starting a session while already assigned is a no-op assignment, so the UI must not offer an undo");
    assert.equal(ok.writes.length, 1);

    const failing = hubFixture({ sendPrompt: async () => { throw new Error("offline"); } });
    await failing.hub.refresh(); await settle(failing.hub);
    failing.assignees.set("o/r#5", ["jamesmontemagno"]);
    await assert.rejects(failing.hub.startSession({ repository: "o/r", number: 5, additional: true }), /assignment was undone/);
    assert.deepEqual(failing.assignees.get("o/r#5"), ["jamesmontemagno"], "only my own assignment is rolled back");

    failing.writes.length = 0;
    failing.assignees.set("o/r#5", ["jamesmontemagno", "burkeholland"]);
    await assert.rejects(failing.hub.startSession({ repository: "o/r", number: 5, additional: true }), error => /Could not start the session/.test(error.message) && !/undone/.test(error.message));
    assert.deepEqual(failing.writes, [], "an assignment this call did not make is never rolled back");
    assert.deepEqual(failing.assignees.get("o/r#5"), ["jamesmontemagno", "burkeholland"]);
});

test("start_session reports when the rollback unassign also fails", async () => {
    const f = hubFixture({ sendPrompt: async () => { throw new Error("offline"); } });
    await f.hub.refresh(); await settle(f.hub);
    f.github.unassign = async () => { throw new Error("rate limited"); };
    await assert.rejects(f.hub.startSession({ repository: "o/r", number: 5 }), error => /may still be assigned/.test(error.message) && !/was undone/.test(error.message));
});

test("set_tracking validates new people and repos, then persists", async () => {
    const { hub, store } = hubFixture();
    await hub.refresh(); await settle(hub);
    const s = await hub.setTracking({ people: ["burkeholland", "newbie"], repos: ["o/r", "o/other", "o/brandnew"] });
    assert.deepEqual(store.data.people, ["burkeholland", "newbie"]);
    assert.deepEqual(store.data.repos, ["o/r", "o/other", "o/brandnew"]);
    assert.deepEqual(s.tracking.people, ["burkeholland", "newbie"]);
    await assert.rejects(hub.setTracking({ people: ["ghost404"], repos: ["o/r"] }), error => error.code === "not_found");
    await assert.rejects(hub.setTracking({ people: [], repos: ["o/missing"] }), error => error.code === "not_found");
    assert.deepEqual(store.data.people, ["burkeholland", "newbie"], "failed validation does not persist");
});

test("a failing repo keeps the rest of the dashboard working", async () => {
    const { hub } = hubFixture({ failing: ["o/other"] });
    await hub.setTracking({ people: ["burkeholland"], repos: ["o/r", "o/other"] });
    await settle(hub);
    const s = hub.state();
    assert.equal(s.repoStatus["o/r"].status, "ready");
    assert.equal(s.repoStatus["o/other"].status, "error");
    assert.equal(s.repoStatus["o/other"].error.code, "permission_denied");
    assert.deepEqual(s.items.map(i => i.repository), ["o/r"]);
});

// ---------- momentum ----------

const eventAt = (type, hoursAgo, key, actors) => ({ type, at: iso(hoursAgo), repository: key.split(/[@#]/)[0], key, actors });
const momentumEvents = {
    "o/r": [eventAt("commit", 2, "o/r@a", ["burkeholland"]), eventAt("prOpened", 3, "o/r#1", [AGENT]), eventAt("commit", 4, "o/r@b", [])],
    "o/other": [eventAt("commit", 5, "o/other@c", ["jamesmontemagno"]), eventAt("commit", 6, "o/other@d", ["stranger"])],
};

async function momentumFixture(options = {}) {
    const fixture = hubFixture({ events: momentumEvents, ...options });
    await fixture.hub.setTracking({ people: ["burkeholland", "jamesmontemagno", AGENT], repos: ["o/r", "o/other"] });
    await settle(fixture.hub);
    return fixture;
}

test("momentum aggregates the tracked repositories for the team, a person, or everyone", async () => {
    const { hub, historyCalls } = await momentumFixture();
    const team = await hub.momentum({ days: 7 });
    assert.deepEqual(team.totals, { commits: 2, prsOpened: 1, prsMerged: 0, issuesOpened: 0, issuesCompleted: 0 });
    assert.deepEqual(team.repositories, ["o/r", "o/other"]);
    assert.deepEqual(team.errors, []);
    assert.deepEqual(team.coverage, { truncated: [], unattributed: 1 });
    assert.equal(team.series.length, 7);
    assert.equal(team.since, "2026-10-14");
    assert.equal(team.until, "2026-10-20");
    assert.equal(team.fetchedAt, new Date(T).toISOString());
    assert.equal((await hub.momentum({ days: 7, everyone: true })).totals.commits, 4);
    assert.equal((await hub.momentum({ days: 7, person: "@JamesMontemagno" })).totals.commits, 1);
    assert.equal((await hub.momentum({ days: 7, person: AGENT })).totals.prsOpened, 1);
    const one = await hub.momentum({ days: 7, repository: "O/Other" });
    assert.deepEqual(one.repositories, ["o/other"]);
    assert.equal(one.totals.commits, 1);
    await assert.rejects(hub.momentum({ repository: "o/untracked" }), error => error.code === "invalid_input");
    await assert.rejects(hub.momentum({ days: 5 }), error => error.code === "invalid_input");
    assert.equal(historyCalls.length, 2, "one fetch per tracked repository, shared by every scope");
});

test("momentum reuses wider cached windows and refetches for wider, forced or expired requests", async () => {
    let clock = T;
    const { hub, historyCalls } = await momentumFixture({ now: () => clock });
    await hub.momentum({ days: 30 });
    assert.deepEqual(historyCalls.map(call => call[1]), [30, 30]);
    await hub.momentum({ days: 7 });
    await hub.momentum({ days: 30, person: AGENT });
    assert.equal(historyCalls.length, 2, "a cached 30 day window serves 7 and 30 day requests");
    await hub.momentum({ days: 90 });
    assert.equal(historyCalls.length, 4, "a wider window needs a new fetch");
    await hub.momentum({ days: 7, force: true });
    assert.equal(historyCalls.length, 6, "force refetches");
    await hub.momentum({ days: 7 });
    assert.equal(historyCalls.length, 6, "the forced result is what later reads use");
    clock += 59 * 60_000;
    await hub.momentum({ days: 7 });
    assert.equal(historyCalls.length, 6, "history stays cached for the whole hour");
    await hub.refresh({ force: true });
    await settle(hub);
    await hub.momentum({ days: 7 });
    assert.equal(historyCalls.length, 6, "refreshing the dashboard does not refetch history");
    clock += 2 * 60_000;
    await hub.momentum({ days: 7 });
    assert.equal(historyCalls.length, 8, "entries older than an hour refetch");
});

test("momentum shares one in-flight fetch per repository", async () => {
    const f = await momentumFixture();
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    const original = f.github.history;
    f.github.history = async (...args) => { await gate; return original(...args); };
    const wide = f.hub.momentum({ days: 30 });
    const narrow = f.hub.momentum({ days: 7, everyone: true });
    const forced = f.hub.momentum({ days: 7, force: true });
    release();
    const [a, b, c] = await Promise.all([wide, narrow, forced]);
    assert.equal(f.historyCalls.length, 2, "narrower and forced requests join the wider fetch already running");
    assert.equal(a.totals.commits, 2);
    assert.equal(b.totals.commits, 4);
    assert.equal(c.totals.commits, 2);
});

test("momentum falls back to cached data when a refresh fails, and reports repositories it cannot read", async () => {
    const f = await momentumFixture();
    const first = await f.hub.momentum({ days: 7 });
    f.github.history = async () => { throw new TeamLoadError("rate_limit", "slow down"); };
    const stale = await f.hub.momentum({ days: 7, force: true });
    assert.deepEqual(stale.totals, first.totals, "cached data is still shown");
    assert.deepEqual(stale.errors.map(error => [error.repository, error.code, error.stale]), [["o/r", "rate_limit", true], ["o/other", "rate_limit", true]]);

    const partial = await momentumFixture();
    partial.github.history = async name => {
        if (name === "o/other") throw new TeamLoadError("permission_denied", "no access");
        return { events: momentumEvents[name], truncated: true };
    };
    const result = await partial.hub.momentum({ days: 7 });
    assert.deepEqual(result.errors, [{ repository: "o/other", code: "permission_denied", message: "no access", stale: false }]);
    assert.equal(result.totals.commits, 1, "the readable repository still reports");
    assert.deepEqual(result.coverage.truncated, ["o/r"]);
    assert.equal(partial.hub.state().repoStatus["o/r"].status, "ready", "history problems do not mark the dashboard as failing");
});

test("untracking a repository drops its cached history", async () => {
    const f = await momentumFixture();
    await f.hub.momentum({ days: 7 });
    assert.equal(f.historyCalls.length, 2);
    await f.hub.setTracking({ people: ["burkeholland"], repos: ["o/r"] });
    await settle(f.hub);
    await f.hub.setTracking({ people: ["burkeholland"], repos: ["o/r", "o/other"] });
    await settle(f.hub);
    await f.hub.momentum({ days: 7 });
    assert.deepEqual(f.historyCalls.slice(2).map(call => call[0]), ["o/other"], "only the re-tracked repository is fetched again");
});

// ---------- server ----------

test("perform exposes get_momentum with validated input", async () => {
    const f = await momentumFixture();
    const result = await perform({ hub: f.hub }, "get_momentum", { days: 7 });
    assert.equal(result.days, 7);
    assert.equal((await perform({ hub: f.hub }, "get_momentum", {})).days, 7, "seven days unless asked otherwise");
    await assert.rejects(perform({ hub: f.hub }, "get_momentum", { days: 1 }), error => error.code === "invalid_input");
    await assert.rejects(perform({ hub: f.hub }, "get_momentum", { person: "a", everyone: true }), error => error.code === "invalid_input");
});
test("perform validates action names and refresh input", async () => {
    const { hub } = hubFixture();
    await assert.rejects(perform({ hub }, "drop_tables", {}), error => error.code === "invalid_input");
    await assert.rejects(perform({ hub }, "refresh", { force: "yes" }), error => error.code === "invalid_input");
    await assert.rejects(perform({ hub }, "get_state", { extra: 1 }), error => error.code === "invalid_input");
    assert.equal(statusFor(new TeamLoadError("conflict", "x")), 409);
    assert.equal(statusFor(new TeamLoadError("rate_limit", "x")), 429);
    assert.equal(statusFor(new Error("boom")), 500);
});

test("renderer escapes nothing user-controlled and locks theme only for known values", () => {
    assert.match(renderHtml("abc", "dark"), /data-theme="dark" data-theme-locked/);
    assert.doesNotMatch(renderHtml("abc", "<x>"), /data-theme/);
    assert.doesNotMatch(renderHtml("abc"), /<script>(?!<\/script>)/);
});

test("renderer places the accessible theme toggle beside Refresh", () => {
    assert.match(renderHtml("abc"), /id="theme-toggle" aria-label="Switch theme"><\/button>\s*<button[^>]+id="refresh"/);
});

test("server enforces nonce, host, origin and CSP", async () => {
    const { hub } = hubFixture();
    const server = await startServer(() => ({ hub }));
    try {
        const page = await fetch(server.url);
        assert.equal(page.status, 200);
        assert.equal(page.headers.get("content-security-policy"), CSP);
        assert.doesNotMatch(CSP, /unsafe-inline|frame-ancestors \*/);
        const html = await page.text();
        const nonce = html.match(/name="team-load-nonce" content="([^"]+)"/)[1];
        const call = (headers, body = { action: "get_state", input: {} }) => fetch(new URL("/api/action", server.url), { method: "POST", headers: { "content-type": "application/json", ...headers }, body: JSON.stringify(body) });
        assert.equal((await call({})).status, 403);
        assert.equal((await call({ "x-team-load-nonce": "wrong" })).status, 403);
        assert.equal((await call({ "x-team-load-nonce": nonce, origin: "https://evil.example" })).status, 403);
        const ok = await call({ "x-team-load-nonce": nonce });
        assert.equal(ok.status, 200);
        assert.ok(Array.isArray((await ok.json()).tracking.people));
        assert.equal((await call({ "x-team-load-nonce": nonce }, { action: "assign", input: { repository: "o/r", number: 5, login: "stranger" } })).status, 400);
        for (const asset of ["/app.mjs", "/model.mjs", "/icons.mjs", "/markdown.mjs", "/momentum.mjs", "/styles.css"]) assert.equal((await fetch(new URL(asset, server.url))).status, 200, asset);
        assert.equal((await fetch(new URL("/config.json", server.url))).status, 404);
        assert.equal((await fetch(new URL("/../state.mjs", server.url))).status, 404);
    } finally { await server.close(); }
});

test("a write during an in-flight fetch returns post-write state, not the stale fetch", async () => {
    let release;
    const assignees = [];
    let calls = 0;
    const github = {
        fetchRepo: async name => {
            calls++;
            const snapshot = [...assignees];
            if (calls === 2) await new Promise(r => { release = r; });
            return [normalizeIssue({ ...issueNode(5), assignees: { nodes: snapshot.map(login => ({ login })) } }, name)];
        },
        repoInfo: async name => ({ nameWithOwner: name, url: "u", open: 1, actors: [] }),
        profile: async login => ({ login, name: login, avatarUrl: null }),
        me: async () => "burkeholland",
        assign: async ({ login }) => { assignees.push(login); return { assignees: [login] }; },
        unassign: async () => ({ assignees: [] }),
    };
    const cfg = { ...config, people: ["burkeholland"], repositories: ["o/r"], availableRepositories: [] };
    const store = { read: async () => ({ people: cfg.people, repos: cfg.repositories, knownPeople: [], knownRepos: [] }), write: async v => v };
    const gated = createHub(cfg, { store, github, sendPrompt: async () => {}, now: () => T });
    await gated.refresh(); await settle(gated);
    await gated.refresh({ force: true }); // starts fetch #2, which snapshots "unassigned" and hangs
    const write = gated.assign({ repository: "o/r", number: 5, login: "burkeholland" });
    await new Promise(r => setTimeout(r, 5));
    release();
    const result = await write;
    assert.deepEqual(result.state.items[0].assignees, ["burkeholland"]);
    assert.equal(calls, 3, "a fresh fetch ran after the write");
});

test("when every repo fails, status is error (not loading) so the UI can show it", async () => {
    const { hub } = hubFixture({ failing: ["o/r"] });
    await hub.refresh(); await settle(hub);
    const status = hub.state().repoStatus["o/r"];
    assert.equal(status.status, "error");
    assert.equal(status.error.message, "no access");
});
