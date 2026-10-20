import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, readFile, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { defaults, derive, matches, rosterCounts, platformOf, itemVersion, itemKey, dailyMomentum, createRefreshTracker, celebrationSummary, validateView, validateConfig, PulseError } from "../model.mjs";
import { createPreferences, readConfig } from "../state.mjs";
import { createProjectData } from "../project-data.mjs";
import { createProjectHub } from "../project-hub.mjs";
import { startServer, perform } from "../server.mjs";

const config = await readConfig();
const time = Date.parse("2026-10-19T23:00:00Z");
const recent = new Date(time - 3600_000).toISOString();
const person = login => ({ login, avatarUrl: `https://avatars.githubusercontent.com/u/1`, url: `https://github.com/${login}` });
const connection = nodes => ({ nodes, pageInfo: { hasNextPage: false, endCursor: null }, totalCount: nodes.length });
function rawIssue(number, extras = {}) {
    return {
        id: `I_${number}`, number, title: `Issue ${number}`, url: `https://github.com/${config.repository}/issues/${number}`,
        state: "OPEN", stateReason: null, createdAt: recent, updatedAt: recent, closedAt: null, viewerCanUpdate: true,
        author: person("jamesmontemagno"), assignees: connection([]), labels: connection([{ name: config.launchLabel }]),
        ...extras,
    };
}
function rawPr(number, extras = {}) {
    const value = rawIssue(number);
    delete value.stateReason;
    return {
        ...value, headRefOid: `sha${number}`, files: connection([{ path: "windows/src/Test.cs" }]),
        reviewRequests: connection([]),
        closingIssuesReferences: connection([]), commits: connection([{ commit: { statusCheckRollup: { state: "SUCCESS" } } }]),
        ...extras,
    };
}
function commit(oid = "abc123") {
    return { oid, messageHeadline: "Commit", url: `https://github.com/${config.repository}/commit/${oid}`, committedDate: recent,
        author: { name: "James", user: person("jamesmontemagno") }, associatedPullRequests: connection([]) };
}
function mock(overrides = {}) {
    const calls = [];
    const issue = rawIssue(1);
    const pr = rawPr(2);
    const run = async args => {
        calls.push(args);
        if (overrides.run) {
            const result = await overrides.run(args, { issue, pr, calls });
            if (result !== undefined) return result;
        }
        if (args.includes("graphql")) {
            const query = args.find(arg => arg.startsWith("query=")).slice(6);
            if (query.includes("issueOrPullRequest")) return { data: { repository: { issueOrPullRequest: issue } } };
            if (query.includes("viewerPermission")) return { data: { repository: {
                nameWithOwner: config.repository, url: `https://github.com/${config.repository}`, viewerPermission: "WRITE",
                issues: { totalCount: 1 }, pullRequests: { totalCount: 1 }, label: { name: config.launchLabel },
                defaultBranchRef: { name: "main", target: { statusCheckRollup: { state: "SUCCESS" } } },
            } } };
            if (query.includes("object(expression")) return { data: { repository: { object: { history: connection([commit()]) } } } };
            const field = query.includes("pullRequests(first") ? "pullRequests" : "issues";
            return { data: { repository: { [field]: connection(field === "issues" ? [issue] : [pr]) } } };
        }
        if (args.some(arg => arg.startsWith("users/"))) {
            const login = args.find(arg => arg.startsWith("users/")).slice(6);
            return { ...person(login), name: login, avatar_url: person(login).avatarUrl, html_url: person(login).url };
        }
        const additions = args.filter(arg => arg.startsWith("assignees[]=")).map(arg => arg.split("=")[1]);
        if (args.includes("POST")) issue.assignees.nodes.push(...additions.map(person));
        if (args.includes("DELETE")) issue.assignees.nodes = issue.assignees.nodes.filter(value => !additions.includes(value.login));
        if (args.includes("PATCH")) {
            issue.state = args.find(arg => arg.startsWith("state=")).split("=")[1].toUpperCase();
            issue.stateReason = args.find(arg => arg.startsWith("state_reason="))?.split("=")[1].toUpperCase() ?? null;
        }
        return {};
    };
    return { run, calls, issue, pr };
}
function normalized(number, extras = {}) {
    const raw = rawIssue(number);
    return { ...raw, kind: "issue", labels: [config.launchLabel], assignees: [], launch: true, platform: "unknown", ...extras };
}

test("configuration and view preferences reject invalid values and unknown fields", () => {
    assert.deepEqual(validateView({}), defaults);
    for (const value of [{ view: "unknown" }, { days: 8 }, { days: "7" }, { launchOnly: 1 }, { surprise: true }, null, [], { person: "-bad" }]) {
        assert.throws(() => validateView(value), PulseError);
    }
    assert.throws(() => validateConfig({ ...config, version: 2 }), PulseError);
    assert.throws(() => validateConfig({ ...config, team: ["James", "james"] }), PulseError);
});

test("automatic refresh accepts only the supported minute intervals and Never", () => {
    assert.equal(validateView({}).refreshMinutes, 5);
    for (const refreshMinutes of [5, 10, 15, 20, 30, 0]) {
        assert.equal(validateView({ refreshMinutes }).refreshMinutes, refreshMinutes);
    }
    for (const refreshMinutes of [-1, 1, 25, 60, 5.5, "5", "never", null, true, undefined]) {
        assert.throws(() => validateView({ refreshMinutes }), PulseError);
    }
});

test("platforms are label/path based and never guessed from titles", () => {
    assert.equal(platformOf(["macOS"], null, config), "mac");
    assert.equal(platformOf([], ["windows/src/A.cs"], config), "windows");
    assert.equal(platformOf([], ["mac/A.swift", "windows/A.cs"], config), "shared");
    assert.equal(platformOf([], ["docs/A.md"], config), "shared");
    assert.equal(platformOf([], null, config), "unknown");
});

test("multi-assignee totals remain unique; cancelled work is not delivered progress", () => {
    const issues = [
        normalized(1, { assignees: [person("jamesmontemagno"), person("burkeholland")] }),
        normalized(2, { state: "CLOSED", stateReason: "COMPLETED" }),
        normalized(3, { state: "CLOSED", stateReason: "NOT_PLANNED" }),
        normalized(4, { state: "CLOSED", stateReason: null }),
    ];
    const snapshot = { issues, prs: [], commits: [], config };
    assert.deepEqual(derive(snapshot, defaults, time).launch, { total: 4, open: 1, completed: 1, notPlanned: 1, otherClosed: 1 });
    assert.equal(derive(snapshot, { ...defaults, person: "burkeholland" }, time).openIssues.length, 1);
    assert.equal(derive(snapshot, { ...defaults, person: "unassigned" }, time).openIssues.length, 0);
});

test("person, launch, shared platform and external contributor filters are explicit", () => {
    const item = normalized(1, { platform: "shared", assignees: [person("outside")] });
    assert.equal(matches(item, { ...defaults, platform: "mac" }, config), true);
    assert.equal(matches(item, { ...defaults, person: "others" }, config), true);
    assert.equal(matches({ ...item, launch: false }, { ...defaults, launchOnly: true }, config), false);
    assert.equal(matches({ ...item, kind: "commit", author: person("outside"), assignees: [] }, { ...defaults, person: "unassigned" }, config), false);
    assert.equal(matches({ ...item, kind: "pr", author: person("katiesavage") }, { ...defaults, person: "katiesavage" }, config), true);
});

test("activity distinguishes merged and closed PRs and respects the history window", () => {
    const snapshot = { config, issues: [], commits: [], prs: [
        normalized(1, { kind: "pr", state: "MERGED", closedAt: recent }),
        normalized(2, { kind: "pr", state: "CLOSED", closedAt: recent }),
        normalized(3, { kind: "pr", createdAt: "2025-01-01", updatedAt: "2025-01-01" }),
    ] };
    const events = derive(snapshot, defaults, time).activity;
    assert(events.some(event => event.verb === "merged"));
    assert(events.some(event => event.verb === "closed"));
    assert(!events.some(event => event.item.number === 3));
});

test("roster PR counts use the creator, not assignees, and include only open PRs", () => {
    const snapshot = { config, issues: [normalized(1, { assignees: [person("jamesmontemagno"), person("burkeholland")] })], prs: [
        normalized(1, { kind: "pr", author: person("JamesMontemagno"), assignees: [person("burkeholland")] }),
        normalized(2, { kind: "pr", author: person("burkeholland"), assignees: [person("jamesmontemagno")] }),
        normalized(3, { kind: "pr", state: "MERGED" }),
        normalized(4, { kind: "pr", state: "CLOSED" }),
        normalized(5, { kind: "pr", author: null, assignees: [person("jamesmontemagno")] }),
        normalized(6, { kind: "pr", author: person("jamesmontemagno"), assignees: [person("jamesmontemagno"), person("burkeholland")], isDraft: true }),
    ] };
    assert.deepEqual(rosterCounts(snapshot, defaults, "jamesmontemagno"), { openIssues: 1, openPrs: 2 });
    assert.deepEqual(rosterCounts(snapshot, defaults, "burkeholland"), { openIssues: 1, openPrs: 1 });
    assert.deepEqual(rosterCounts(snapshot, defaults, "katiesavage"), { openIssues: 0, openPrs: 0 });
    assert.equal(derive({ ...snapshot, commits: [] }, { ...defaults, person: "jamesmontemagno" }, time).openPrs.length, 4);
});

test("open PR counts include creators or pending individual reviewers once, and filters show review work", () => {
    const reviewer = login => ({ ...person(login), __typename: "User" });
    const snapshot = { config, issues: [], commits: [], prs: [
        normalized(1, { kind: "pr", author: person("outside"), requestedReviewers: [reviewer("JamesMontemagno")] }),
        normalized(2, { kind: "pr", requestedReviewers: [reviewer("jamesmontemagno"), reviewer("JAMESMONTEMAGNO")] }),
        normalized(3, { kind: "pr", author: null, requestedReviewers: [reviewer("jamesmontemagno"), reviewer("katiesavage")] }),
        normalized(4, { kind: "pr", author: person("outside"), assignees: [person("jamesmontemagno")] }),
        normalized(5, { kind: "pr", author: person("outside"), requestedReviewers: [reviewer("jamesmontemagno")], state: "MERGED" }),
        normalized(6, { kind: "pr", author: person("outside"), requestedReviewers: [reviewer("jamesmontemagno")], state: "CLOSED" }),
        normalized(7, { kind: "pr", author: null, requestedReviewers: [{ __typename: "Team", slug: "jamesmontemagno", url: "https://github.com/orgs/example/teams/jamesmontemagno" }] }),
    ] };
    assert.deepEqual(rosterCounts(snapshot, defaults, "jamesmontemagno"), { openIssues: 0, openPrs: 3 });
    assert.equal(rosterCounts(snapshot, defaults, "katiesavage").openPrs, 1);
    assert.equal(derive(snapshot, { ...defaults, person: "jamesmontemagno" }, time).openPrs.length, 4);
    assert.equal(matches(snapshot.prs[0], { ...defaults, person: "jamesmontemagno" }, config), true);
    assert.equal(matches(snapshot.prs[6], { ...defaults, person: "jamesmontemagno" }, config), false);
    assert.equal(matches({ ...snapshot.prs[0], requestedReviewers: [] }, { ...defaults, person: "jamesmontemagno" }, config), false);
});

test("requested-reviewer counts retain repository, platform and launch scope and external contributor attribution", () => {
    const requestedReviewers = [{ ...person("jukasper"), __typename: "User" }, { ...person("outside"), __typename: "User" }];
    const snapshot = { config, issues: [], prs: [
        normalized(1, { kind: "pr", repository: config.repository, requestedReviewers, platform: "mac", createdAt: "2025-01-01" }),
        normalized(1, { kind: "pr", repository: config.repositories[1], requestedReviewers, platform: "shared" }),
        normalized(2, { kind: "pr", repository: config.repository, requestedReviewers, platform: "windows", launch: false }),
    ] };
    assert.equal(rosterCounts(snapshot, defaults, "jukasper").openPrs, 3);
    assert.equal(rosterCounts(snapshot, { ...defaults, repository: config.repositories[1] }, "jukasper").openPrs, 1);
    assert.equal(rosterCounts(snapshot, { ...defaults, platform: "mac", launchOnly: true }, "jukasper").openPrs, 2);
    assert.equal(rosterCounts(snapshot, defaults, "others").openPrs, 3);
});

test("roster counts respect repository, platform, and launch scope without a history cutoff or person filter", () => {
    const website = "jamesmontemagno/tiny-clips-website";
    const snapshot = { config, issues: [], prs: [
        normalized(1, { kind: "pr", repository: config.repository, platform: "mac", createdAt: "2025-01-01", updatedAt: "2025-01-01" }),
        normalized(1, { kind: "pr", repository: website, platform: "shared" }),
        normalized(2, { kind: "pr", repository: config.repository, platform: "windows", launch: false }),
    ] };
    const view = { ...defaults, person: "katiesavage", days: 7 };
    assert.equal(rosterCounts(snapshot, view, "jamesmontemagno").openPrs, 3);
    assert.equal(rosterCounts(snapshot, { ...view, repository: website }, "jamesmontemagno").openPrs, 1);
    assert.equal(rosterCounts(snapshot, { ...view, platform: "mac" }, "jamesmontemagno").openPrs, 2);
    assert.equal(rosterCounts(snapshot, { ...view, launchOnly: true }, "jamesmontemagno").openPrs, 2);
    assert.equal(rosterCounts(snapshot, { ...view, repository: config.repository, platform: "windows", launchOnly: true }, "jamesmontemagno").openPrs, 0);
});

test("Other contributors count PR creators while Unassigned counts open PRs without assignees", () => {
    const snapshot = { config, issues: [
        normalized(1),
        normalized(2, { assignees: [person("outside")] }),
    ], prs: [
        normalized(1, { kind: "pr", author: person("outside"), assignees: [person("jamesmontemagno")] }),
        normalized(2, { kind: "pr", assignees: [person("outside")] }),
        normalized(3, { kind: "pr", author: null }),
        normalized(4, { kind: "pr" }),
        normalized(5, { kind: "pr", author: person("JAMESMONTEMAGNO") }),
        normalized(6, { kind: "pr", author: person("outside"), state: "CLOSED" }),
    ] };
    assert.deepEqual(rosterCounts(snapshot, defaults, "others"), { openIssues: 1, openPrs: 1 });
    assert.deepEqual(rosterCounts(snapshot, defaults, "unassigned"), { openIssues: 1, openPrs: 3 });
});

test("preferences are atomic, validated, shared per repository and isolated across repositories", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-preferences-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const first = createPreferences(config, home);
    assert.deepEqual(await first.read(), defaults);
    await Promise.all([first.write({ person: "jukasper" }), first.write({ launchOnly: true })]);
    const second = createPreferences(config, home);
    assert.equal((await second.read()).person, "jukasper");
    assert.equal((await second.read()).launchOnly, true);
    assert.deepEqual(await createPreferences({ ...config, repository: "other/repo" }, home).read(), defaults);
    assert.deepEqual(await createPreferences({ ...config, repository: config.repository.toUpperCase() }, home).read(), await first.read());
    assert.equal(JSON.parse(await readFile(first.path, "utf8")).version, 3);
});

test("automatic refresh persists through partial actions, other filters and reopened preferences", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-refresh-preferences-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    for (const refreshMinutes of [5, 10, 15, 20, 30, 0]) {
        const previous = await preferences.read();
        const updated = await perform({ preferences }, "set_view_state", { refreshMinutes });
        assert.deepEqual(updated, { ...previous, refreshMinutes });
        assert.deepEqual(await createPreferences(config, home).read(), updated);
    }
    await perform({ preferences }, "set_view_state", { view: "momentum", repository: config.repositories[1] });
    assert.equal((await preferences.read()).refreshMinutes, 0);
    const saved = await readFile(preferences.path, "utf8");
    await assert.rejects(() => perform({ preferences }, "set_view_state", { refreshMinutes: 25 }), PulseError);
    assert.equal(await readFile(preferences.path, "utf8"), saved);
});

test("the Momentum view is accepted by actions and persists without changing existing filters", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-momentum-view-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    const previous = await preferences.write({ view: "prs", repository: config.repositories[1], person: "jukasper", launchOnly: true, days: 30 });
    const updated = await perform({ preferences }, "set_view_state", { view: "momentum" });
    assert.deepEqual(updated, { ...previous, view: "momentum" });
    assert.deepEqual(await createPreferences(config, home).read(), updated);
    assert.deepEqual(await perform({ preferences }, "set_view_state", { view: "overview" }), { ...previous, view: "overview" });
});

test("corrupt or unknown preference data is reported and never overwritten", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-corrupt-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    await preferences.write({});
    for (const content of ["not json", JSON.stringify({ format: "other", version: 99 })]) {
        await writeFile(preferences.path, content);
        await assert.rejects(preferences.read, /Cannot read preferences/);
        await assert.rejects(() => preferences.write({ days: 30 }), /Cannot read preferences/);
        assert.equal(await readFile(preferences.path, "utf8"), content);
    }
});

test("storage failures are actionable", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-blocked-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    await writeFile(join(home, "extension-data"), "not a directory");
    await assert.rejects(() => createPreferences(config, home).write({}), /Cannot (read|save) preferences/);
});

test("snapshot coalesces panels, paginates exact work, caches profiles and deduplicates commits", async () => {
    let clock = time;
    const fake = mock({ run: async args => {
        const query = args.find(arg => arg.startsWith("query="));
        if (query?.includes("issues(first") && args.includes("after=null")) {
            return { data: { repository: { issues: { ...connection([rawIssue(1)]), pageInfo: { hasNextPage: true, endCursor: "page2" } } } } };
        }
        if (query?.includes("issues(first") && args.includes("after=page2")) return { data: { repository: { issues: connection([rawIssue(3)]) } } };
    } });
    const data = createProjectData(config, { run: fake.run, now: () => clock });
    const [a, b] = await Promise.all([data.snapshot(), data.snapshot(true)]);
    assert.equal(a, b);
    assert.equal(a.issues.length, 2);
    assert.equal(a.commits.length, 1);
    assert.equal(a.meta.openIssues, 1);
    const calls = fake.calls.length;
    await data.snapshot();
    assert.equal(fake.calls.length, calls);
    clock += 300_001;
    await data.snapshot();
    assert.equal(fake.calls.filter(args => args.some(value => value.startsWith("users/"))).length, 5);
    assert(fake.calls.every(args => !args.join(" ").includes("projectsV2")));
});

test("PR review requests are fully paginated and normalized for snapshots and details", async () => {
    const reviewer = login => ({ requestedReviewer: { ...person(login), __typename: "User" } });
    const team = { __typename: "Team", slug: "reviewers", name: "Reviewers", url: "https://github.com/orgs/example/teams/reviewers" };
    const fake = mock({ run: async args => {
        const query = args.find(arg => arg.startsWith("query="));
        if (query?.includes("reviewRequests(first:100,after:")) {
            assert(args.includes("after=review-page-2"));
            return { data: { repository: { pullRequest: { reviewRequests: connection([reviewer("katiesavage"), reviewer("JAMESMONTEMAGNO"), { requestedReviewer: team }, { requestedReviewer: null }]) } } } };
        }
        if (query?.includes("issueOrPullRequest")) return { data: { repository: { issueOrPullRequest: fake.pr } } };
    } });
    fake.pr.reviewRequests = { ...connection([reviewer("jamesmontemagno")]), pageInfo: { hasNextPage: true, endCursor: "review-page-2" } };
    const data = createProjectData(config, { run: fake.run, now: () => time });
    const snapshot = await data.snapshot();
    assert.deepEqual(snapshot.errors, []);
    assert.deepEqual(snapshot.prs[0].requestedReviewers.map(value => value.login ?? value.slug), ["JAMESMONTEMAGNO", "katiesavage", "reviewers"]);
    assert.equal(Object.hasOwn(snapshot.prs[0], "reviewRequests"), false);
    assert.deepEqual((await data.detail({ kind: "pr", number: 2 })).requestedReviewers, snapshot.prs[0].requestedReviewers);
    assert(fake.calls.some(args => args.some(value => value.includes("... on User { login avatarUrl url }"))));
});

test("review-request failures retain last-good PRs and report stale coverage instead of losing reviewer counts", async () => {
    let broken = false;
    const fake = mock({ run: async args => {
        if (broken && args.some(value => value.includes("reviewRequests(first:100,after:"))) throw new PulseError("github_error", "Review requests unavailable");
    } });
    fake.pr.reviewRequests = connection([{ requestedReviewer: { ...person("katiesavage"), __typename: "User" } }]);
    const data = createProjectData(config, { run: fake.run, now: () => time });
    const first = await data.snapshot();
    broken = true;
    fake.pr.reviewRequests.pageInfo = { hasNextPage: true, endCursor: "next" };
    const next = await data.snapshot(true);
    assert.equal(next.stale, true);
    assert(next.errors.some(error => error.section === "prs" && /Review requests unavailable/.test(error.message)));
    assert.deepEqual(next.prs, first.prs);
    assert.equal(next.prs[0].requestedReviewers[0].login, "katiesavage");
});

test("missing or unbounded review-request pagination is reported explicitly", async () => {
    const fake = mock({ run: async args => {
        if (args.some(value => value.includes("issueOrPullRequest"))) return { data: { repository: { issueOrPullRequest: fake.pr } } };
        if (args.some(value => value.includes("reviewRequests(first:100,after:"))) return { data: { repository: { pullRequest: { reviewRequests: fake.pr.reviewRequests } } } };
    } });
    const data = createProjectData(config, { run: fake.run, now: () => time });
    fake.pr.reviewRequests = null;
    await assert.rejects(() => data.detail({ kind: "pr", number: 2 }), /could not be read/);
    fake.pr.reviewRequests = { ...connection([]), pageInfo: { hasNextPage: true, endCursor: null } };
    await assert.rejects(() => data.detail({ kind: "pr", number: 2 }), /omitted the review-request cursor/);
    fake.pr.reviewRequests.pageInfo.endCursor = "next";
    await assert.rejects(() => data.detail({ kind: "pr", number: 2 }), /not silently truncated/);
});

test("partial refresh preserves last-good section data and exposes the failure", async () => {
    let broken = false;
    const fake = mock({ run: async args => {
        if (broken && args.some(value => value.includes("pullRequests(first"))) throw new PulseError("github_error", "offline PRs");
    } });
    const data = createProjectData(config, { run: fake.run, now: () => time });
    const first = await data.snapshot();
    broken = true;
    const next = await data.snapshot(true);
    assert.equal(next.prs[0].number, first.prs[0].number);
    assert.equal(next.stale, true);
    assert.equal(next.errors[0].section, "prs");
    assert.equal(next.timestamps.prs, first.timestamps.prs);
});

test("rate-limit reset prevents repeated automatic and manual requests", async () => {
    let calls = 0;
    const data = createProjectData(config, { now: () => time, run: async () => {
        calls++;
        throw new PulseError("rate_limit", "Wait for reset", new Date(time + 600_000).toISOString());
    } });
    await assert.rejects(() => data.snapshot(), /Wait for reset/);
    await assert.rejects(() => data.snapshot(true), /paused until/);
    assert.equal(calls, 1);
});

test("confirmed assignment changes preserve untouched assignees and verify the result", async () => {
    const fake = mock();
    fake.issue.assignees.nodes = [person("burkeholland")];
    const data = createProjectData(config, { run: fake.run });
    const before = await data.detail({ kind: "issue", number: 1 });
    const after = await data.updateAssignees({ kind: "issue", number: 1, assignees: ["burkeholland", "jukasper"], expectedState: before.expectedState, confirmed: true });
    assert.deepEqual(after.assignees.map(value => value.login), ["burkeholland", "jukasper"]);
    assert(!fake.calls.some(args => args.includes("DELETE")));
});

test("invalid, unconfirmed, stale and permission-denied writes never mutate GitHub", async () => {
    const fake = mock();
    const data = createProjectData(config, { run: fake.run });
    const input = { kind: "issue", number: 1, assignees: [], expectedState: "old", confirmed: true };
    await assert.rejects(() => data.updateAssignees({ ...input, confirmed: false }), /Invalid input/);
    await assert.rejects(() => data.updateAssignees({ ...input, assignees: ["bad-"] }), /valid GitHub/);
    await assert.rejects(() => data.updateAssignees(input), /changed on GitHub/);
    fake.issue.viewerCanUpdate = false;
    await assert.rejects(() => data.updateAssignees(input), /cannot update/);
    assert(!fake.calls.some(args => args.includes("POST") || args.includes("DELETE") || args.includes("PATCH")));
});

test("issue state validates close reasons and verifies completed/reopened states", async () => {
    const fake = mock();
    const data = createProjectData(config, { run: fake.run });
    let item = await data.detail({ kind: "issue", number: 1 });
    await assert.rejects(() => data.setIssueState({ number: 1, state: "CLOSED", expectedState: item.expectedState, confirmed: true }), /requires/);
    item = await data.setIssueState({ number: 1, state: "CLOSED", reason: "COMPLETED", expectedState: item.expectedState, confirmed: true });
    assert.equal(item.stateReason, "COMPLETED");
    item = await data.setIssueState({ number: 1, state: "OPEN", expectedState: item.expectedState, confirmed: true });
    assert.equal(item.state, "OPEN");
});

test("ambiguous writes are never retried or reported as success", async () => {
    const fake = mock({ run: async args => { if (args.includes("POST")) throw new PulseError("github_error", "connection reset"); } });
    const data = createProjectData(config, { run: fake.run });
    const item = await data.detail({ kind: "issue", number: 1 });
    await assert.rejects(() => data.updateAssignees({ kind: "issue", number: 1, assignees: ["jukasper"], expectedState: item.expectedState, confirmed: true }), /may have applied/);
    assert.equal(fake.calls.filter(args => args.includes("POST")).length, 1);
});

test("item versions include state and ownership, independent of assignee ordering", () => {
    const first = normalized(1, { assignees: [person("jukasper"), person("burkeholland")] });
    assert.equal(itemVersion(first), itemVersion({ ...first, assignees: [...first.assignees].reverse() }));
    assert.notEqual(itemVersion(first), itemVersion({ ...first, state: "CLOSED" }));
});

test("detail queries select only the requested GraphQL type and reject missing projections", async () => {
    for (const kind of ["issue", "pr"]) {
        const type = kind === "issue" ? "Issue" : "PullRequest";
        const otherType = kind === "issue" ? "PullRequest" : "Issue";
        let missing = false;
        const fake = mock({ run: async args => {
            const query = args.find(arg => arg.startsWith("query="));
            if (!query?.includes("issueOrPullRequest")) return;
            assert.match(query, new RegExp(`\\.\\.\\. on ${type}\\s*\\{`));
            assert.doesNotMatch(query, new RegExp(`\\.\\.\\. on ${otherType}\\s*\\{`));
            return { data: { repository: { issueOrPullRequest: missing ? {} : kind === "issue" ? rawIssue(1) : rawPr(2) } } };
        } });
        const data = createProjectData(config, { run: fake.run });
        const input = { kind, number: kind === "issue" ? 1 : 2 };
        const item = await data.detail(input);
        assert.equal(item.kind, kind);
        assert.equal(item.expectedState, itemVersion(item));
        missing = true;
        await assert.rejects(() => data.detail(input), error => error.code === "invalid_input" && /not found/.test(error.message));
    }
});

test("loopback routes enforce origin, host, nonce, body shapes and static allowlists", async t => {
    const fake = mock();
    const context = { data: createProjectData(config, { run: fake.run, now: () => time }), preferences: { read: async () => defaults, write: async value => value } };
    const server = await startServer(() => context);
    t.after(server.close);
    const html = await (await fetch(server.url)).text();
    const nonce = html.match(/name="pulse-nonce" content="([^"]+)"/)[1];
    const post = (body, headers = {}) => fetch(`${server.url}api/action`, {
        method: "POST", headers: { "Content-Type": "application/json", "X-Pulse-Nonce": nonce, ...headers }, body: JSON.stringify(body),
    });
    assert.equal((await fetch(`${server.url}?scoutTheme=dark`)).status, 200);
    assert.equal((await fetch(`${server.url}model.mjs`)).status, 200);
    assert.equal((await fetch(`${server.url}celebration.mjs`)).status, 200);
    assert.equal((await fetch(`${server.url}config.json`)).status, 404);
    assert.equal((await fetch(`${server.url}api/preferences`, { headers: { Origin: "https://evil.invalid" } })).status, 403);
    assert.equal((await post({ action: "get_view_state", input: {} }, { "X-Pulse-Nonce": "bad" })).status, 403);
    assert.equal((await post({ action: "get_view_state", input: {} }, { Origin: "https://evil.invalid" })).status, 403);
    assert.equal((await post({ action: "get_view_state", input: {} })).status, 200);
    assert.equal((await post({ action: "get_view_state", input: {}, extra: true })).status, 400);
    assert.equal((await post({ action: "get_view_state", input: { surprise: true } })).status, 400);
    assert.equal((await post({ action: "unknown", input: {} })).status, 400);
    assert.equal((await fetch(`${server.url}api/snapshot`)).status, 200);
});

test("action validation does not accept invented routes or unexpected fields", async () => {
    await assert.rejects(() => perform({}, "merge_pr", {}), /Unknown/);
    await assert.rejects(() => perform({}, "refresh_status", { surprise: 1 }), /no input/);
});

test("the set-view action preserves preferences omitted by a partial update", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-partial-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    await preferences.write({ person: "jukasper", view: "team" });
    const updated = await perform({ preferences }, "set_view_state", { launchOnly: true });
    assert.equal(updated.person, "jukasper");
    assert.equal(updated.view, "team");
    assert.equal(updated.launchOnly, true);
});

test("commit histories are reused within the enriched metadata cache lifetime", async () => {
    const fake = mock({ run: async args => {
        const query = args.find(arg => arg.startsWith("query="));
        if (query?.includes("viewerPermission")) return { data: { repository: {
            nameWithOwner: config.repository, url: "https://github.com", viewerPermission: "WRITE",
            issues: { totalCount: 1 }, pullRequests: { totalCount: 0 }, label: { name: config.launchLabel },
            defaultBranchRef: { name: "main", target: { oid: "a".repeat(40), statusCheckRollup: null } },
        } } };
        if (query?.includes("pullRequests(first")) return { data: { repository: { pullRequests: connection([]) } } };
    } });
    const data = createProjectData(config, { run: fake.run, now: () => time });
    await data.snapshot();
    await data.snapshot(true);
    assert.equal(fake.calls.filter(args => args.some(arg => arg.includes("object(expression"))).length, 1);
});

test("unchanged commit SHAs refresh mutable launch associations at the five-minute boundary", async () => {
    let clock = time;
    let linked = { number: 2, labels: connection([]), closingIssuesReferences: connection([]) };
    const fake = mock({ run: async args => {
        const query = args.find(arg => arg.startsWith("query="));
        if (query?.includes("viewerPermission")) return { data: { repository: {
            nameWithOwner: config.repository, url: "https://github.com", viewerPermission: "WRITE",
            issues: { totalCount: 1 }, pullRequests: { totalCount: 0 }, label: { name: config.launchLabel },
            defaultBranchRef: { name: "main", target: { oid: "a".repeat(40), statusCheckRollup: null } },
        } } };
        if (query?.includes("pullRequests(first")) return { data: { repository: { pullRequests: connection([]) } } };
        if (query?.includes("object(expression")) {
            return { data: { repository: { object: { history: connection([{
                ...commit("b".repeat(40)), associatedPullRequests: connection([structuredClone(linked)]),
            }]) } } } };
        }
    } });
    const data = createProjectData(config, { run: fake.run, now: () => clock });
    const count = () => fake.calls.filter(args => args.some(arg => arg.includes("object(expression"))).length;
    assert.equal((await data.snapshot()).commits[0].launch, false);
    linked.labels = connection([{ name: config.launchLabel }]);
    clock += config.refreshSeconds * 1000 - 1;
    assert.equal((await data.snapshot(true)).commits[0].launch, false);
    assert.equal(count(), 1);
    clock++;
    assert.equal((await data.snapshot(true)).commits[0].launch, true);
    assert.equal(count(), 2);
    linked.labels = connection([]);
    clock += config.refreshSeconds * 1000;
    assert.equal((await data.snapshot(true)).commits[0].launch, false);
    assert.equal(count(), 3);
    linked.closingIssuesReferences = connection([{ labels: connection([{ name: config.launchLabel }]) }]);
    clock += config.refreshSeconds * 1000;
    const history = await data.history(30);
    assert.equal(history.commits[0].launch, true);
    assert.equal(history.commits[0].sha, "b".repeat(40));
    assert.equal(count(), 4);
});

test("transient reads retry once but persistent errors remain visible", async () => {
    let attempts = 0;
    const fake = mock({ run: async args => {
        if (args.some(arg => arg.startsWith("users/") && arg.endsWith("katiesavage")) && attempts++ < 2) {
            throw new PulseError("github_error", "HTTP 502");
        }
    } });
    const messages = [];
    const snapshot = await createProjectData(config, { run: fake.run, now: () => time, log: value => messages.push(value) }).snapshot();
    assert.equal(attempts, 2);
    assert(snapshot.errors.some(error => error.section === "team"));
    assert(messages.some(value => value.includes("Retrying")));
});

test("failed post-write verification reports an uncertain result", async () => {
    let written = false;
    const fake = mock({ run: async args => {
        if (args.includes("POST")) written = true;
        if (written && args.some(arg => arg.includes("issueOrPullRequest"))) throw new PulseError("github_error", "offline verification");
    } });
    const data = createProjectData(config, { run: fake.run });
    const item = await data.detail({ kind: "issue", number: 1 });
    await assert.rejects(() => data.updateAssignees({ kind: "issue", number: 1, assignees: ["jukasper"], expectedState: item.expectedState, confirmed: true }), /could not be verified/);
});

test("GitHub subprocess concurrency stays bounded across nested section queries", async () => {
    let active = 0;
    let maximum = 0;
    const fake = mock({ run: async () => {
        maximum = Math.max(maximum, ++active);
        await new Promise(resolve => setTimeout(resolve, 5));
        active--;
    } });
    await createProjectData(config, { run: fake.run, now: () => time }).snapshot();
    assert.equal(maximum, 3);
    assert.equal(active, 0);
});

test("legacy personal preferences migrate without losing filters", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-migration-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    await preferences.write({});
    const legacy = JSON.parse(await readFile(preferences.path, "utf8"));
    legacy.version = 1;
    delete legacy.view.repository;
    delete legacy.view.refreshMinutes;
    legacy.view.person = "burkeholland";
    legacy.view.launchOnly = true;
    await writeFile(preferences.path, JSON.stringify(legacy));
    assert.deepEqual(await preferences.read(), { ...defaults, person: "burkeholland", launchOnly: true });
    await preferences.write({ repository: config.repositories[1] });
    const updated = JSON.parse(await readFile(preferences.path, "utf8"));
    assert.equal(updated.version, 3);
    assert.equal(updated.view.person, "burkeholland");
    assert.equal(updated.view.repository, config.repositories[1]);
    assert.equal(updated.view.refreshMinutes, 5);
});

test("version 2 preferences retain all selections and gain the default refresh interval", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-v2-migration-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    const view = await preferences.write({
        view: "momentum", person: "katiesavage", platform: "windows", repository: config.repositories[2], days: 30, launchOnly: true,
    });
    const legacy = JSON.parse(await readFile(preferences.path, "utf8"));
    legacy.version = 2;
    delete legacy.view.refreshMinutes;
    const content = JSON.stringify(legacy);
    await writeFile(preferences.path, content);
    assert.deepEqual(await preferences.read(), view);
    assert.equal(await readFile(preferences.path, "utf8"), content);
    await preferences.write({ refreshMinutes: 0 });
    assert.deepEqual(await createPreferences(config, home).read(), { ...view, refreshMinutes: 0 });
    assert.equal(JSON.parse(await readFile(preferences.path, "utf8")).version, 3);
});

test("preference versions reject missing, invalid, and unexpected refresh fields without overwriting them", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-refresh-invalid-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    await preferences.write({});
    const current = JSON.parse(await readFile(preferences.path, "utf8"));
    const missing = structuredClone(current);
    delete missing.view.refreshMinutes;
    const invalid = structuredClone(current);
    invalid.view.refreshMinutes = 25;
    for (const value of [missing, invalid, { ...current, version: 2 }, { ...current, version: 1 }]) {
        const content = JSON.stringify(value);
        await writeFile(preferences.path, content);
        await assert.rejects(preferences.read, /Cannot read preferences/);
        await assert.rejects(() => preferences.write({ refreshMinutes: 5 }), /Cannot read preferences/);
        assert.equal(await readFile(preferences.path, "utf8"), content);
    }
});

test("three repositories retain distinct issue/PR/commit identities and exact scoped counts", async () => {
    const fake = mock();
    const snapshot = await createProjectHub(config, { run: fake.run, now: () => time }).snapshot();
    assert.equal(snapshot.repositories.length, 3);
    assert.equal(snapshot.issues.length, 3);
    assert.equal(snapshot.commits.length, 3);
    assert.equal(new Set(snapshot.issues.map(itemKey)).size, 3);
    assert.equal(new Set(snapshot.commits.map(itemKey)).size, 3);
    const scoped = derive(snapshot, { ...defaults, repository: config.repositories[1] }, time);
    assert.equal(scoped.issues.length, 1);
    assert.equal(scoped.issues[0].repository, config.repositories[1]);
    assert.equal(snapshot.meta.openIssues, 3);
});

test("multi-repository detail and write routing cannot target an unconfigured repository", async () => {
    const fake = mock();
    const hub = createProjectHub(config, { run: fake.run });
    const repository = config.repositories[1];
    const item = await hub.detail({ repository, kind: "issue", number: 1 });
    const updated = await hub.updateAssignees({ repository, kind: "issue", number: 1, assignees: ["jukasper"], expectedState: item.expectedState, confirmed: true });
    assert.equal(updated.repository, repository);
    assert(fake.calls.some(args => args.includes(`repos/${repository}/issues/1/assignees`) && args.includes("POST")));
    assert.throws(() => hub.detail({ repository: "outside/repo", kind: "issue", number: 1 }), /configured/);
    assert.throws(() => hub.detail({ repository: 12, kind: "issue", number: 1 }), /Invalid input/);
    assert.throws(() => hub.setIssueState({ repository: "outside/repo", number: 1, state: "OPEN", expectedState: "x", confirmed: true }), /configured/);
});

test("one unavailable repository does not hide healthy work or silently report a successful zero", async () => {
    const fake = mock({ run: async args => {
        if (args.includes(`name=${config.repositories[1].split("/")[1]}`)) throw new PulseError("permission_denied", "Repository inaccessible");
    } });
    const snapshot = await createProjectHub(config, { run: fake.run, now: () => time }).snapshot();
    assert.equal(snapshot.issues.length, 2);
    assert.equal(snapshot.stale, true);
    assert.equal(snapshot.errors[0].repository, config.repositories[1]);
    assert.equal(snapshot.repositories[1].available, false);
    assert(snapshot.repositories[0].available);
});

test("secondary repositories without a launch label remain healthy and do not gain launch work", async () => {
    const fake = mock({ run: async args => {
        const query = args.find(value => value.startsWith("query="));
        const secondary = !args.includes("name=tiny-clips");
        if (query?.includes("viewerPermission") && secondary) {
            return { data: { repository: {
                nameWithOwner: "jamesmontemagno/secondary", viewerPermission: "WRITE",
                issues: { totalCount: 0 }, pullRequests: { totalCount: 0 }, label: null, defaultBranchRef: null,
            } } };
        }
        if (secondary && query?.includes("issues(first")) return { data: { repository: { issues: connection([rawIssue(1, { labels: connection([]) })]) } } };
        if (secondary && query?.includes("pullRequests(first")) return { data: { repository: { pullRequests: connection([]) } } };
    } });
    const snapshot = await createProjectHub(config, { run: fake.run, now: () => time }).snapshot();
    assert.deepEqual(snapshot.errors, []);
    assert(snapshot.issues.filter(item => item.repository !== config.repository).every(item => !item.launch));
});

function trackerSnapshot(issues, timestamps, errors = []) {
    return {
        issues, prs: [], commits: [],
        repositories: config.repositories.map(repository => ({
            repository, timestamps: { issues: timestamps[repository] ?? recent, prs: recent, commits: recent },
            errors: errors.filter(error => error.repository === repository),
        })),
    };
}

function milestoneSnapshot(items, timestamp = recent, errors = []) {
    const snapshot = trackerSnapshot(items.filter(item => item.kind === "issue"),
        Object.fromEntries(config.repositories.map(repository => [repository, timestamp])), errors);
    snapshot.prs = items.filter(item => item.kind === "pr");
    snapshot.commits = items.filter(item => item.kind === "commit");
    for (const source of snapshot.repositories) source.timestamps.prs = source.timestamps.commits = timestamp;
    return snapshot;
}

test("refresh celebrations name real PR and issue milestones once, not initial or cached activity", () => {
    const repository = config.repository;
    const initial = [
        normalized(1, { repository, kind: "pr" }), normalized(2, { repository, kind: "pr" }),
        normalized(3, { repository, kind: "pr", state: "CLOSED" }), normalized(4, { repository }),
        normalized(5, { repository }), normalized(6, { repository, state: "CLOSED", stateReason: "NOT_PLANNED" }),
    ];
    const tracker = createRefreshTracker();
    assert.equal(celebrationSummary(tracker.accept(milestoneSnapshot(initial))), "");
    const timestamp = new Date(time).toISOString();
    const next = milestoneSnapshot([
        { ...initial[0], state: "MERGED" }, { ...initial[1], state: "CLOSED" }, { ...initial[2], state: "OPEN" },
        { ...initial[3], state: "CLOSED", stateReason: "COMPLETED" },
        { ...initial[4], state: "CLOSED", stateReason: "NOT_PLANNED" },
        { ...initial[5], state: "OPEN", stateReason: "REOPENED" },
        normalized(7, { repository, kind: "pr", createdAt: timestamp }),
        normalized(8, { repository, kind: "pr", createdAt: timestamp, state: "MERGED" }),
        normalized(9, { repository, createdAt: timestamp }),
    ], timestamp);
    const changes = tracker.accept(next);
    assert.equal(celebrationSummary(changes), "1 PR opened · 1 PR reopened · 2 PRs merged · 1 PR closed · 1 issue opened · 1 issue reopened · 1 issue completed");
    assert.equal(celebrationSummary(tracker.accept(next)), "");
    assert.equal(celebrationSummary(tracker.accept(milestoneSnapshot(
        [...next.issues, ...next.prs], new Date(time + 300_000).toISOString()))), "");
});

test("metadata and pending review changes notify without celebrating", () => {
    const pr = normalized(1, { repository: config.repository, kind: "pr", requestedReviewers: [] });
    const tracker = createRefreshTracker();
    tracker.accept(milestoneSnapshot([pr]));
    const next = { ...pr, title: "Renamed", updatedAt: new Date(time).toISOString(),
        assignees: [person("katiesavage")], requestedReviewers: [{ ...person("jukasper"), __typename: "User" }] };
    const changes = tracker.accept(milestoneSnapshot([next], new Date(time).toISOString()));
    assert.equal(changes.length, 1);
    assert.equal(changes[0].type, "Updated");
    assert.equal(celebrationSummary(changes), "");
});

test("issue reason changes only celebrate an actual newly recorded completion", () => {
    const repository = config.repository;
    const open = normalized(1, { repository });
    const cancelled = normalized(2, { repository, state: "CLOSED", stateReason: "NOT_PLANNED" });
    const tracker = createRefreshTracker();
    tracker.accept(milestoneSnapshot([open, cancelled]));
    const changes = tracker.accept(milestoneSnapshot([
        { ...open, stateReason: "REOPENED", updatedAt: new Date(time).toISOString() },
        { ...cancelled, stateReason: "COMPLETED", updatedAt: new Date(time).toISOString() },
    ], new Date(time).toISOString()));
    assert.equal(changes.length, 2);
    assert.equal(celebrationSummary(changes), "1 issue completed");
});

test("historical work newly entering coverage and commits do not invent milestones", () => {
    const tracker = createRefreshTracker();
    tracker.accept(milestoneSnapshot([]));
    const repository = config.repository;
    const changes = tracker.accept(milestoneSnapshot([
        normalized(1, { repository, kind: "pr" }),
        normalized(2, { repository, kind: "pr", state: "MERGED" }),
        normalized(3, { repository }),
        normalized(4, { repository, state: "CLOSED", stateReason: "COMPLETED" }),
        normalized(5, { repository, kind: "pr", createdAt: "unknown" }),
        { kind: "commit", repository, sha: "new-sha" },
    ], new Date(time).toISOString()));
    assert.equal(changes.length, 6);
    assert.equal(celebrationSummary(changes), "");
});

test("failed sections retain milestone baselines, first recovery and older responses do not celebrate", () => {
    const repository = config.repository;
    const pr = normalized(1, { repository, kind: "pr" });
    const issue = normalized(2, { repository });
    const initial = milestoneSnapshot([pr, issue]);
    const next = [{ ...pr, state: "MERGED" }, { ...issue, state: "CLOSED", stateReason: "COMPLETED" }];
    const timestamp = new Date(time).toISOString();
    const tracker = createRefreshTracker();
    tracker.accept(initial);
    assert.equal(celebrationSummary(tracker.accept(milestoneSnapshot(next, timestamp, [{ repository, section: "prs" }]))), "1 issue completed");
    assert.equal(celebrationSummary(tracker.accept(milestoneSnapshot(next, timestamp))), "1 PR merged");
    assert.deepEqual(tracker.accept(initial), []);
    assert.equal(celebrationSummary(tracker.accept(milestoneSnapshot(next, new Date(time + 300_000).toISOString()))), "");
    const recovered = createRefreshTracker();
    recovered.accept(milestoneSnapshot([], recent, [{ repository, section: "repository" }]));
    assert.equal(celebrationSummary(recovered.accept(milestoneSnapshot(next, timestamp))), "");
});

test("celebrations aggregate milestones across repositories without PR-number collisions", () => {
    const tracker = createRefreshTracker();
    tracker.accept(milestoneSnapshot([]));
    const timestamp = new Date(time).toISOString();
    const changes = tracker.accept(milestoneSnapshot(config.repositories.map(repository =>
        normalized(1, { repository, kind: "pr", createdAt: timestamp })), timestamp));
    assert.equal(celebrationSummary(changes), "3 PRs opened");
    assert.equal(tracker.badges.size, 3);
});

test("pending review changes update versions and refresh notifications without depending on updatedAt", () => {
    const repository = config.repository;
    const pr = normalized(1, { repository, kind: "pr", requestedReviewers: [] });
    const reviewed = { ...pr, requestedReviewers: [{ ...person("katiesavage"), __typename: "User" }, { ...person("jukasper"), __typename: "User" }] };
    assert.notEqual(itemVersion(pr), itemVersion(reviewed));
    assert.equal(itemVersion(reviewed), itemVersion({ ...reviewed, requestedReviewers: [...reviewed.requestedReviewers].reverse() }));
    const tracker = createRefreshTracker();
    const first = trackerSnapshot([], {});
    first.prs = [pr];
    tracker.accept(first);
    const next = trackerSnapshot([], {});
    next.prs = [reviewed];
    next.repositories[0].timestamps.prs = new Date(time).toISOString();
    assert.deepEqual(tracker.accept(next).map(change => change.type), ["Updated"]);
});

test("refresh notifications suppress first load and cached refreshes, then mark new and updated records", () => {
    const tracker = createRefreshTracker();
    const repository = config.repository;
    const initial = normalized(1, { repository });
    const first = trackerSnapshot([initial], { [repository]: recent });
    assert.deepEqual(tracker.accept(first), []);
    const nextTime = new Date(time).toISOString();
    const changed = { ...initial, updatedAt: nextTime, title: "Updated" };
    const added = normalized(2, { repository, createdAt: nextTime, updatedAt: nextTime });
    const next = trackerSnapshot([changed, added], { [repository]: nextTime });
    assert.deepEqual(tracker.accept(next).map(change => change.type), ["Updated", "New"]);
    assert.equal(tracker.badges.size, 2);
    assert.deepEqual(tracker.accept(next), []);
    assert.equal(tracker.badges.size, 2);
    assert.deepEqual(tracker.accept(trackerSnapshot([changed, added], { [repository]: new Date(time + 300_000).toISOString() })), []);
    assert.equal(tracker.badges.size, 0);
});

test("notification baselines survive partial failures, and first recovered sections do not notify historical work", () => {
    const tracker = createRefreshTracker();
    const repository = config.repository;
    const first = trackerSnapshot([normalized(1, { repository })], { [repository]: recent });
    tracker.accept(first);
    const later = new Date(time).toISOString();
    const changed = normalized(1, { repository, updatedAt: later });
    assert.deepEqual(tracker.accept(trackerSnapshot([changed], { [repository]: later }, [{ repository, section: "issues" }])), []);
    assert.equal(tracker.accept(trackerSnapshot([changed], { [repository]: later })).length, 1);
    const fresh = createRefreshTracker();
    fresh.accept(trackerSnapshot([], { [repository]: recent }, [{ repository, section: "repository" }]));
    assert.deepEqual(fresh.accept(trackerSnapshot([changed], { [repository]: later })), []);
});

test("notifications distinguish old issues newly entering history from newly created issues across repositories", () => {
    const tracker = createRefreshTracker();
    tracker.accept(trackerSnapshot([], {}));
    const later = new Date(time).toISOString();
    const items = config.repositories.map(repository => normalized(1, { repository, createdAt: "2026-01-01T00:00:00Z", updatedAt: later }));
    const changes = tracker.accept(trackerSnapshot(items, Object.fromEntries(config.repositories.map(repository => [repository, later]))));
    assert.equal(changes.length, 3);
    assert(changes.every(change => change.type === "Updated"));
    assert.equal(tracker.badges.size, 3);
});

test("daily momentum has exact zero-filled UTC bins, excludes cancellations and counts PR opens/merges separately", () => {
    const repository = config.repository;
    const records = [
        normalized(1, { repository, state: "CLOSED", stateReason: "COMPLETED", closedAt: recent }),
        normalized(2, { repository, state: "CLOSED", stateReason: "NOT_PLANNED", closedAt: recent }),
        normalized(3, { repository, state: "OPEN", stateReason: "REOPENED", closedAt: null }),
        normalized(4, { repository, kind: "pr", state: "MERGED", closedAt: recent }),
        { kind: "commit", repository, sha: "same", time: recent, platform: "unknown", launch: false, author: person("jamesmontemagno"), assignees: [] },
    ];
    for (const days of [7, 30, 90]) {
        const series = dailyMomentum([...records, records[4]], { ...defaults, days }, config, time);
        assert.equal(series.length, days);
        assert.deepEqual(series.at(-1), { date: "2026-10-19", commits: 1, opened: 1, merged: 1, issuesOpened: 3, completed: 1 });
        assert.equal(series[0].commits, 0);
        assert.equal(series[0].issuesOpened, 0);
    }
    const midnight = Date.parse("2026-10-20T00:00:01Z");
    assert.equal(dailyMomentum(records, { ...defaults, days: 7 }, config, midnight).at(-2).commits, 1);
    assert.equal(dailyMomentum(records, { ...defaults, days: 7 }, config, midnight).at(-1).commits, 0);
    assert.equal(dailyMomentum(records, { ...defaults, repository: config.repositories[1] }, config, time).reduce((sum, day) => sum + day.commits, 0), 0);
});

test("issue-opening momentum counts creation once per repository regardless of current issue state", () => {
    const repository = config.repository;
    const opened = normalized(1, { repository });
    const records = [
        opened,
        normalized(2, { repository, state: "CLOSED", stateReason: "COMPLETED", closedAt: recent }),
        normalized(3, { repository, state: "CLOSED", stateReason: "NOT_PLANNED", closedAt: recent }),
        normalized(4, { repository, stateReason: "REOPENED", createdAt: "2025-01-01", updatedAt: recent }),
        normalized(5, { repository, stateReason: "REOPENED" }),
        opened,
        { ...opened, repository: config.repositories[1] },
    ];
    for (const days of [7, 30, 90]) {
        const series = dailyMomentum(records, { ...defaults, days }, config, time);
        assert.equal(series.at(-1).issuesOpened, 5);
        assert.equal(series.at(-1).completed, 1);
        assert.equal(series.reduce((sum, day) => sum + day.issuesOpened, 0), 5);
    }
});

test("issue-opening momentum respects UTC creation dates, valid timestamps, and active filters", () => {
    const records = [
        normalized(1, { repository: config.repository, createdAt: "2026-10-20T00:30:00+02:00", platform: "mac", assignees: [person("jamesmontemagno")] }),
        normalized(2, { repository: config.repositories[1], platform: "windows", launch: false, assignees: [person("jukasper")] }),
        normalized(3, { repository: config.repository, createdAt: "2026-10-21T00:00:00Z" }),
        normalized(4, { repository: config.repository, createdAt: "invalid" }),
    ];
    const count = selection => dailyMomentum(records, { ...defaults, ...selection }, config, time).at(-1).issuesOpened;
    assert.equal(count({}), 2);
    assert.equal(count({ repository: config.repository }), 1);
    assert.equal(count({ platform: "mac" }), 1);
    assert.equal(count({ launchOnly: true }), 1);
    assert.equal(count({ person: "jamesmontemagno" }), 1);
    assert.equal(count({ person: "unassigned" }), 0);
});

test("momentum is on-demand, cached/coalesced across panels and reuses history for person filters", async () => {
    const fake = mock();
    const hub = createProjectHub(config, { run: fake.run, now: () => time });
    assert.equal(fake.calls.length, 0);
    const [a, b] = await Promise.all([hub.momentum({ days: 7 }), hub.momentum({ days: 7 })]);
    assert.deepEqual(a.series, b.series);
    assert.equal(a.series.length, 7);
    assert.equal(a.series.at(-1).commits, 3);
    assert.equal(a.series.at(-1).issuesOpened, 3);
    const calls = fake.calls.length;
    await hub.momentum({ days: 7, person: "jukasper" });
    assert.equal(fake.calls.length, calls);
    const ninety = await hub.momentum({ days: 90, repository: config.repositories[1] });
    assert.equal(ninety.series.length, 90);
    assert.equal(ninety.series.at(-1).commits, 1);
    assert.equal(ninety.series.at(-1).issuesOpened, 1);
    const queries = fake.calls.filter(args => args.some(value => value.startsWith("since=")));
    assert(queries.some(args => args.includes(`since=${new Date(time - 90 * 86400_000).toISOString()}`)));
    await assert.rejects(() => hub.momentum({ days: 365 }), /Invalid input/);
});

test("momentum exposes partial repository failures instead of attributing them zero activity", async () => {
    const fake = mock({ run: async args => {
        if (args.includes("name=tiny-clips-website")) throw new PulseError("github_error", "Unavailable website");
    } });
    const result = await createProjectHub(config, { run: fake.run, now: () => time }).momentum({ days: 30 });
    assert.equal(result.errors.length, 1);
    assert.equal(result.errors[0].repository, config.repositories[1]);
    assert.equal(result.series.at(-1).commits, 2);
    assert.equal(result.series.at(-1).issuesOpened, 2);
});

test("momentum canonicalizes repository casing before filtering, caching and coalescing", async () => {
    const fake = mock();
    const hub = createProjectHub(config, { run: fake.run, now: () => time });
    const repository = config.repositories[1];
    const [canonical, uppercase] = await Promise.all([
        hub.momentum({ repository, days: 7 }),
        hub.momentum({ repository: repository.toUpperCase(), days: 7 }),
    ]);
    assert.deepEqual(uppercase, canonical);
    assert.deepEqual(uppercase.repositories, [repository]);
    assert.equal(uppercase.selection.repository, repository);
    assert.equal(uppercase.series.at(-1).commits, 1);
    assert.equal(uppercase.series.at(-1).issuesOpened, 1);
    assert.equal(uppercase.errors.length, 0);
    assert.equal(fake.calls.filter(args => args.some(value => value.includes("viewerPermission"))).length, 1);
    const calls = fake.calls.length;
    assert.deepEqual(await hub.momentum({ repository: "JamesMontemagno/Tiny-Clips-Website", days: 7 }), canonical);
    assert.equal(fake.calls.length, calls);
    await assert.rejects(() => hub.momentum({ repository: "outside/repo", days: 7 }), /configured/);
    assert.equal(fake.calls.length, calls);
});

test("repository preference selection is allowlisted and normalizes configured identity casing", async t => {
    const home = await mkdtemp(join(tmpdir(), "pulse-repository-view-"));
    t.after(() => rm(home, { recursive: true, force: true }));
    const preferences = createPreferences(config, home);
    assert.throws(() => preferences.write({ repository: "other/repo" }), /configured/);
    assert.equal((await preferences.write({ repository: config.repositories[1].toUpperCase() })).repository, config.repositories[1]);
});

test("momentum UTC bins normalize timestamp offsets and exclude future events", () => {
    const item = { kind: "commit", repository: config.repository, sha: "offset", time: "2026-10-20T00:30:00+02:00", assignees: [], platform: "unknown", launch: false };
    const series = dailyMomentum([item, { ...item, sha: "future", time: "2026-10-21T00:00:00Z" }], { ...defaults, days: 7 }, config, time);
    assert.equal(series.at(-1).date, "2026-10-19");
    assert.equal(series.at(-1).commits, 1);
});

test("all repository sources share a global subprocess concurrency budget", async () => {
    let active = 0;
    let maximum = 0;
    const fake = mock({ run: async () => {
        maximum = Math.max(maximum, ++active);
        await new Promise(resolve => setTimeout(resolve, 5));
        active--;
    } });
    await createProjectHub(config, { run: fake.run, now: () => time }).snapshot();
    assert.equal(maximum, 3);
    assert.equal(active, 0);
});
