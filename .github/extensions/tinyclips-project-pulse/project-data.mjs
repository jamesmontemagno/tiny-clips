import { execFile } from "node:child_process";
import { promisify } from "node:util";
import { PulseError, validate, itemSchema, assignmentSchema, issueStateSchema, itemVersion, platformOf, loginPattern } from "./model.mjs";
import { repoRoot } from "./state.mjs";

const exec = promisify(execFile);
const personFields = "login avatarUrl url";
const reviewRequestFields = `requestedReviewer { __typename ... on User { ${personFields} } ... on Team { slug name url } }`;
const issueFields = `id number title url state stateReason createdAt updatedAt closedAt viewerCanUpdate
    author { ${personFields} } assignees(first:100) { nodes { ${personFields} } }
    labels(first:100) { nodes { name } }`;
const prFields = `id number title url state createdAt updatedAt closedAt mergedAt viewerCanUpdate isDraft reviewDecision headRefOid
    author { ${personFields} } assignees(first:100) { nodes { ${personFields} } }
    reviewRequests(first:100) { nodes { ${reviewRequestFields} } pageInfo { hasNextPage endCursor } }
    labels(first:100) { nodes { name } } files(first:100) { nodes { path } pageInfo { hasNextPage } }
    closingIssuesReferences(first:20) { nodes { labels(first:20) { nodes { name } } } pageInfo { hasNextPage } }
    commits(last:1) { nodes { commit { statusCheckRollup { state } } } }`;
const commitFields = `oid messageHeadline committedDate url author { name user { ${personFields} } }
    associatedPullRequests(first:5) { nodes { number labels(first:20) { nodes { name } }
        closingIssuesReferences(first:20) { nodes { labels(first:20) { nodes { name } } } } } pageInfo { hasNextPage } }`;
const connectionInfo = "pageInfo { hasNextPage endCursor }";
const names = connection => (connection?.nodes ?? []).map(node => node.name);
const launchPr = (pr, config) => names(pr.labels).includes(config.launchLabel)
    || pr.closingIssuesReferences.nodes.some(issue => names(issue.labels).includes(config.launchLabel));
const redact = text => String(text).replace(/\b(?:gh[pousr]_[\w]+|github_pat_[\w]+)\b/g, "[redacted]").slice(0, 1500);

export async function runGh(args) {
    try {
        const result = await exec("gh", args, {
            cwd: repoRoot, windowsHide: true, encoding: "utf8", timeout: 45_000, maxBuffer: 16 * 1024 * 1024,
            env: { ...process.env, GH_PROMPT_DISABLED: "1", GH_PAGER: "", GH_HOST: "github.com" },
        });
        return JSON.parse(result.stdout);
    } catch (error) {
        if (error.code === "ENOENT") throw new PulseError("missing_gh", "GitHub CLI (gh) is not installed or is not on PATH.");
        const message = redact(error.stderr || error.message);
        if (/rate limit|RATE_LIMITED/i.test(message)) {
            let retryAt = new Date(Date.now() + 300_000).toISOString();
            try {
                const result = await exec("gh", ["api", "--hostname", "github.com", "rate_limit"], {
                    cwd: repoRoot, windowsHide: true, encoding: "utf8", timeout: 10_000,
                    env: { ...process.env, GH_PROMPT_DISABLED: "1", GH_PAGER: "" },
                });
                const resources = JSON.parse(result.stdout).resources;
                const exhausted = Object.values(resources).filter(value => value.remaining === 0);
                if (exhausted.length) retryAt = new Date(Math.max(...exhausted.map(value => value.reset)) * 1000).toISOString();
            } catch (rateError) {
                throw new PulseError("rate_limit", `${message} Rate-limit reset could not be read: ${redact(rateError.stderr || rateError.message)}`, retryAt);
            }
            throw new PulseError("rate_limit", `${message} Refresh will resume after the rate-limit reset.`, retryAt);
        }
        throw new PulseError(/auth|401|403|permission|scope/i.test(message) ? "permission_denied" : "github_error",
            `GitHub request failed. ${message}`);
    }
}

export function createProjectData(config, { run = runGh, now = () => Date.now(), log = () => {}, requireLaunchLabel = true, profiles = new Map() } = {}) {
    const [owner, name] = config.repository.split("/");
    let cached = null;
    let pending = null;
    let retryAt = 0;
    let active = 0;
    const waiting = [];
    const histories = new Map();
    async function limited(args) {
        if (active >= 3) await new Promise(resolve => waiting.push(resolve));
        else active++;
        try {
            try { return await run(args); }
            catch (error) {
                if (args.includes("--method") || !/HTTP 50[234]|timed out/i.test(error.message)) throw error;
                log(`Retrying a transient GitHub read failure once: ${error.message}`);
                await new Promise(resolve => setTimeout(resolve, 500));
                return await run(args);
            }
        }
        finally {
            const next = waiting.shift();
            if (next) next();
            else active--;
        }
    }
    async function gql(query, variables = {}) {
        const args = ["api", "--hostname", "github.com", "graphql", "-f", `query=${query}`, "-f", `owner=${owner}`, "-f", `name=${name}`];
        for (const [key, value] of Object.entries(variables)) args.push(typeof value === "number" || value === null ? "-F" : "-f", `${key}=${value}`);
        const response = await limited(args);
        if (response.errors?.length) throw new PulseError("github_error", response.errors.map(error => error.message).join("; "));
        if (!response.data) throw new PulseError("github_error", "GitHub returned no query data.");
        return response.data;
    }
    const repositoryQuery = content => `query($owner:String!,$name:String!){repository(owner:$owner,name:$name){${content}}}`;
    const api = (path, fields = null, method = "GET") => limited([
        "api", "--hostname", "github.com", `repos/${config.repository}/${path}`,
        ...(fields ? ["--method", method, ...Object.entries(fields).flatMap(([key, value]) =>
            Array.isArray(value) ? value.map(entry => ["-f", `${key}[]=${entry}`]).flat() : ["-f", `${key}=${value}`])] : []),
    ]);
    async function requestedReviewers(node) {
        let connection = node.reviewRequests;
        const reviewers = new Map();
        for (let page = 0; page < 100; page++) {
            if (!connection?.nodes || !connection.pageInfo) {
                throw new PulseError("github_error", `Review requests could not be read for PR #${node.number}.`);
            }
            for (const { requestedReviewer: reviewer } of connection.nodes) {
                if (reviewer) reviewers.set(`${reviewer.__typename}:${reviewer.login ?? reviewer.url ?? ""}`.toLowerCase(), reviewer);
            }
            if (!connection.pageInfo.hasNextPage) return [...reviewers.values()];
            if (!connection.pageInfo.endCursor) throw new PulseError("github_error", `GitHub omitted the review-request cursor for PR #${node.number}.`);
            connection = (await gql(`query($owner:String!,$name:String!,$number:Int!,$after:String!){
                repository(owner:$owner,name:$name){pullRequest(number:$number){
                    reviewRequests(first:100,after:$after){nodes{${reviewRequestFields}} ${connectionInfo}}
                }}}`, { number: node.number, after: connection.pageInfo.endCursor })).repository.pullRequest?.reviewRequests;
        }
        throw new PulseError("coverage_limit", `More than 100 pages of review requests were returned for PR #${node.number}; counts were not silently truncated.`);
    }
    async function normalize(node, kind) {
        const labels = names(node.labels);
        const result = {
            ...node, kind, repository: config.repository, labels, assignees: node.assignees.nodes,
            stateReason: node.stateReason ?? null,
            launch: kind === "issue" ? labels.includes(config.launchLabel) : launchPr(node, config),
            platform: platformOf(labels, kind === "pr" && !node.files.pageInfo.hasNextPage ? node.files.nodes.map(file => file.path) : null, config),
            checks: kind === "pr" ? node.commits.nodes[0]?.commit.statusCheckRollup?.state ?? "UNKNOWN" : null,
            closedAt: node.mergedAt ?? node.closedAt,
        };
        delete result.commits;
        delete result.files;
        delete result.closingIssuesReferences;
        if (kind === "pr") result.requestedReviewers = await requestedReviewers(node);
        delete result.reviewRequests;
        return result;
    }
    async function collect(kind, mode, days = 30) {
        const field = kind === "issue" ? "issues" : "pullRequests";
        const fields = kind === "issue" ? issueFields : prFields;
        const modifier = mode === "open" ? ",states:OPEN" : mode === "launch" ? ",labels:[$label]" : "";
        const query = `query($owner:String!,$name:String!,$after:String${mode === "launch" ? ",$label:String!" : ""}){
            repository(owner:$owner,name:$name){${field}(first:${kind === "pr" ? 25 : 100},after:$after,orderBy:{field:UPDATED_AT,direction:DESC}${modifier}){
                totalCount nodes{${fields}} ${connectionInfo}}}}`;
        let after = null;
        const result = [];
        for (let page = 0; page < 100; page++) {
            const data = (await gql(query, { after, ...(mode === "launch" ? { label: config.launchLabel } : {}) })).repository[field];
            const nodes = mode === "recent" ? data.nodes.filter(item => Date.parse(item.updatedAt) >= now() - days * 86400_000) : data.nodes;
            result.push(...await Promise.all(nodes.map(node => normalize(node, kind))));
            if (!data.pageInfo.hasNextPage || (mode === "recent" && nodes.length < data.nodes.length)) return result;
            after = data.pageInfo.endCursor;
        }
        throw new PulseError("coverage_limit", `More than 100 pages of ${mode} ${field} were returned. Narrow the configured scope; totals were not silently truncated.`);
    }
    async function getMeta() {
        const data = (await gql(repositoryQuery(`nameWithOwner url viewerPermission
            issues(states:OPEN){totalCount} pullRequests(states:OPEN){totalCount}
            label(name:${JSON.stringify(config.launchLabel)}){name}
            defaultBranchRef{name target{... on Commit{oid statusCheckRollup{state}}}}`))).repository;
        if (!data) throw new PulseError("permission_denied", "Repository not found or not accessible with the current GitHub authentication.");
        return {
            repository: data.nameWithOwner, url: data.url, permission: data.viewerPermission,
            openIssues: data.issues.totalCount, openPrs: data.pullRequests.totalCount,
            defaultBranch: data.defaultBranchRef?.name ?? null,
            headOid: data.defaultBranchRef?.target.oid ?? null,
            checks: data.defaultBranchRef?.target.statusCheckRollup?.state ?? "UNKNOWN",
            launchLabelExists: data.label !== null,
        };
    }
    async function getTeam() {
        return Promise.all(config.team.map(async login => {
            const previous = profiles.get(login);
            if (previous && now() - previous.time < 86400_000) return previous.value;
            const data = await limited(["api", "--hostname", "github.com", `users/${encodeURIComponent(login)}`]);
            const value = { login: data.login, name: data.name || data.login, avatarUrl: data.avatar_url, url: data.html_url };
            profiles.set(login, { time: now(), value });
            return value;
        }));
    }
    async function getCommits(meta, prs, days = 30) {
        const query = `query($owner:String!,$name:String!,$ref:String!,$after:String,$since:GitTimestamp!){
            repository(owner:$owner,name:$name){object(expression:$ref){... on Commit{
                history(first:100,after:$after,since:$since){nodes{${commitFields}} ${connectionInfo}}
            }}}}`;
        const refs = [...new Set([meta.headOid ?? meta.defaultBranch, ...prs.filter(pr => pr.state === "OPEN").map(pr => pr.headRefOid)].filter(Boolean))];
        const commits = new Map();
        for (const ref of refs) {
            let after = null;
            const historyKey = `${ref}:${days}`;
            const previous = histories.get(historyKey);
            let nodes = previous && now() - previous.time < config.refreshSeconds * 1000 ? previous.nodes : null;
            if (!nodes) {
                nodes = [];
                for (let page = 0; page < 100; page++) {
                    const data = await gql(query, { ref, after, since: new Date(now() - days * 86400_000).toISOString() });
                    const history = data.repository.object?.history;
                    if (!history) throw new PulseError("github_error", `Cannot read commit history for ${ref}.`);
                    nodes.push(...history.nodes);
                    if (!history.pageInfo.hasNextPage) break;
                    if (page === 99) throw new PulseError("coverage_limit", "Commit history exceeds 10,000 commits per branch. The feed is not presented as complete.");
                    after = history.pageInfo.endCursor;
                }
                if (/^[a-f\d]{40}$/i.test(ref)) {
                    histories.set(historyKey, { nodes, time: now() });
                    if (histories.size > 20) histories.delete(histories.keys().next().value);
                }
            }
            for (const node of nodes) {
                if (Date.parse(node.committedDate) >= now() - days * 86400_000) {
                    const linked = node.associatedPullRequests.nodes;
                    const known = linked.map(pr => prs.find(value => value.number === pr.number)).filter(Boolean);
                    const platforms = known.map(pr => pr.platform).filter(platform => platform !== "unknown");
                    commits.set(node.oid, {
                        kind: "commit", repository: config.repository, sha: node.oid, title: node.messageHeadline, url: node.url, time: node.committedDate,
                        author: node.author.user ?? { login: null, name: node.author.name, avatarUrl: null },
                        assignees: [], launch: linked.some(pr => launchPr(pr, config)),
                        platform: platforms.includes("shared") || (platforms.includes("mac") && platforms.includes("windows")) ? "shared" : platforms[0] ?? "unknown",
                    });
                }
            }
        }
        return [...commits.values()];
    }
    function unique(items) { return [...new Map(items.flat().map(item => [`${item.kind}-${item.number}`, item])).values()]; }
    async function refresh() {
        const attemptedAt = new Date(now()).toISOString();
        let meta;
        try { meta = await getMeta(); }
        catch (error) {
            log(error.message);
            retryAt = error.retryAt ? Date.parse(error.retryAt) : now() + config.refreshSeconds * 1000;
            if (!cached) throw error;
            cached = { ...cached, attemptedAt, errors: [{ section: "repository", code: error.code, message: error.message }], stale: true, retryAt: new Date(retryAt).toISOString() };
            return cached;
        }
        const previous = cached;
        const errors = [];
        const timestamps = { ...previous?.timestamps, repository: attemptedAt };
        async function section(key, load, fallback) {
            try {
                const value = await load();
                timestamps[key] = attemptedAt;
                return value;
            } catch (error) {
                errors.push({ section: key, code: error.code ?? "github_error", message: error.message });
                log(`${key}: ${error.message}`);
                if (error.retryAt) retryAt = Math.max(retryAt, Date.parse(error.retryAt));
                return fallback;
            }
        }
        const [issues, prs, team] = await Promise.all([
            section("issues", async () => unique(await Promise.all([collect("issue", "open"), collect("issue", "launch"), collect("issue", "recent")])), previous?.issues ?? []),
            section("prs", async () => unique(await Promise.all([collect("pr", "open"), collect("pr", "recent")])), previous?.prs ?? []),
            section("team", getTeam, previous?.team ?? config.team.map(login => ({ login, name: login }))),
        ]);
        const commits = await section("commits", () => getCommits(meta, prs), previous?.commits ?? []);
        if (!meta.launchLabelExists && requireLaunchLabel) errors.push({ section: "launch", code: "missing_label", message: `The configured label "${config.launchLabel}" does not exist. Correct config.json or create the label on GitHub.` });
        if (errors.length && retryAt < now()) retryAt = now() + config.refreshSeconds * 1000;
        cached = {
            config, meta, issues, prs, team, commits, timestamps, attemptedAt,
            refreshedAt: errors.length ? previous?.refreshedAt ?? null : attemptedAt,
            stale: errors.length > 0, errors, retryAt: retryAt > now() ? new Date(retryAt).toISOString() : null,
            coverage: "All open work and all launch issues; recent issue/PR history and commits cover 30 days. Commits cover the default branch and open PR heads, deduplicated by SHA. Association metadata covers up to 5 PRs per commit, 20 closing issues per PR and 20 labels per linked item; platform/launch associations can be unknown. No local work or presence is tracked.",
        };
        return cached;
    }
    async function snapshot(force = false) {
        if (pending) return pending;
        if (retryAt > now()) {
            if (cached) return cached;
            throw new PulseError("retry_later", `Refresh is paused until ${new Date(retryAt).toISOString()}.`, new Date(retryAt).toISOString());
        }
        if (!force && cached && now() - Date.parse(cached.attemptedAt) < config.refreshSeconds * 1000) return cached;
        pending = refresh().finally(() => { pending = null; });
        return pending;
    }
    async function detail(input) {
        validate(itemSchema, input);
        if (input.repository && input.repository.toLowerCase() !== config.repository.toLowerCase()) {
            throw new PulseError("invalid_input", "The requested repository does not match this data source.");
        }
        const type = input.kind === "issue" ? "Issue" : "PullRequest";
        const fields = input.kind === "issue" ? issueFields : prFields;
        const data = (await gql(`query($owner:String!,$name:String!,$number:Int!){
            repository(owner:$owner,name:$name){issueOrPullRequest(number:$number){
                ... on ${type}{${fields} bodyText}
            }}}`, { number: input.number })).repository.issueOrPullRequest;
        if (!data?.id || (input.kind === "pr") !== Object.hasOwn(data, "headRefOid")) throw new PulseError("invalid_input", "The requested issue or pull request was not found.");
        const item = await normalize(data, input.kind);
        return { ...item, expectedState: itemVersion(item) };
    }
    async function mutate(input, type) {
        validate(type === "assign" ? assignmentSchema : issueStateSchema, input);
        if (type === "assign" && input.assignees.some(login => !loginPattern.test(login))) {
            throw new PulseError("invalid_input", "Assignees must be valid GitHub logins.");
        }
        if (type === "state" && ((input.state === "CLOSED" && !input.reason) || (input.state === "OPEN" && input.reason))) {
            throw new PulseError("invalid_input", "Closing an issue requires COMPLETED or NOT_PLANNED; reopening must omit the closing reason.");
        }
        const key = { kind: type === "assign" ? input.kind : "issue", number: input.number, ...(input.repository ? { repository: input.repository } : {}) };
        const before = await detail(key);
        if (!before.viewerCanUpdate) throw new PulseError("permission_denied", "Your GitHub account cannot update this item.");
        if (before.expectedState !== input.expectedState) throw new PulseError("conflict", "This item changed on GitHub. Reload its details and review the proposed change again.");
        if (type === "assign" && before.assignees.length > 10) throw new PulseError("invalid_input", "This item has more than ten assignees. Edit it on GitHub to avoid removing assignments.");
        try {
            if (type === "assign") {
                const current = before.assignees.map(person => person.login);
                const desired = input.assignees;
                const added = desired.filter(login => !current.some(value => value.toLowerCase() === login.toLowerCase()));
                const removed = current.filter(login => !desired.some(value => value.toLowerCase() === login.toLowerCase()));
                if (added.length) await api(`issues/${input.number}/assignees`, { assignees: added }, "POST");
                if (removed.length) await api(`issues/${input.number}/assignees`, { assignees: removed }, "DELETE");
            } else {
                await api(`issues/${input.number}`, { state: input.state.toLowerCase(), ...(input.reason ? { state_reason: input.reason.toLowerCase() } : {}) }, "PATCH");
            }
        } catch (error) {
            throw new PulseError("write_uncertain", `GitHub may have applied part or all of this change. Reload details before retrying. ${error.message}`);
        }
        let after;
        try { after = await detail(key); }
        catch (error) { throw new PulseError("write_uncertain", `GitHub accepted the write but its result could not be verified. Reload before retrying. ${error.message}`); }
        const verified = type === "assign"
            ? JSON.stringify(after.assignees.map(person => person.login.toLowerCase()).sort()) === JSON.stringify(input.assignees.map(login => login.toLowerCase()).sort())
            : after.state === input.state && (input.state === "OPEN" || after.stateReason === input.reason);
        if (!verified) throw new PulseError("write_uncertain", "GitHub did not confirm the requested result. Reload details and reconcile before retrying.");
        if (cached) cached = { ...cached, attemptedAt: new Date(0).toISOString() };
        return after;
    }
    async function history(days) {
        if (retryAt > now()) throw new PulseError("retry_later", `Refresh is paused until ${new Date(retryAt).toISOString()}.`, new Date(retryAt).toISOString());
        try {
            const meta = await getMeta();
            const [issues, recentPrs, openPrs] = await Promise.all([collect("issue", "recent", days), collect("pr", "recent", days), collect("pr", "open")]);
            const prs = unique([recentPrs, openPrs]);
            const commits = await getCommits(meta, prs, days);
            return { issues, prs, commits };
        } catch (error) {
            if (error.retryAt) retryAt = Math.max(retryAt, Date.parse(error.retryAt));
            throw error;
        }
    }
    return { snapshot, history, detail, updateAssignees: input => mutate(input, "assign"), setIssueState: input => mutate(input, "state") };
}
