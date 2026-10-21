// Pure, dependency-free logic shared by the extension (Node) and the canvas page (browser).

export class TeamLoadError extends Error {
    constructor(code, message, retryAt = null) {
        super(message);
        this.code = code;
        this.retryAt = retryAt;
    }
}

export const AGENT = "copilot";
const AGENT_LOGINS = new Set(["copilot", "copilot-swe-agent", "copilot-swe-agent[bot]"]);
export const loginPattern = /^(?!-)[a-z\d](?:[a-z\d-]{0,37}[a-z\d])?$/i;
export const repoPattern = /^[a-z\d](?:[a-z\d-]{0,38})\/[\w.-]{1,100}$/i;
export const MAX_PEOPLE = 25;
export const MAX_REPOS = 15;

export const canonLogin = login => {
    if (typeof login !== "string" || !login) return null;
    const lower = login.toLowerCase();
    return AGENT_LOGINS.has(lower) ? AGENT : lower;
};
export const isAgent = login => canonLogin(login) === AGENT;
export const shortRepo = nwo => String(nwo).split("/")[1] ?? String(nwo);
export const cleanTitle = title => String(title ?? "")
    .replace(/^\s*\[?Feature\]?:\s*/i, "")
    .replace(/^\s*\[2\.0 Launch\]\s*/i, "")
    .trim() || String(title ?? "");

export function validateConfig(value) {
    const fail = message => { throw new TeamLoadError("invalid_config", `Team Load config.json: ${message}`); };
    if (!value || typeof value !== "object" || value.format !== "tinyclips-team-load-config" || value.version !== 1) fail("unknown format or version.");
    const repos = list => Array.isArray(list) && list.every(r => typeof r === "string" && repoPattern.test(r));
    if (!repos(value.repositories) || !value.repositories.length) fail("repositories must be a non-empty owner/name list.");
    if (value.availableRepositories !== undefined && !repos(value.availableRepositories)) fail("availableRepositories must be owner/name entries.");
    if (!Array.isArray(value.people) || !value.people.every(p => typeof p === "string" && loginPattern.test(p))) fail("people must be GitHub logins.");
    if (!Number.isInteger(value.limit) || value.limit < 1 || value.limit > 20) fail("limit must be an integer from 1 to 20.");
    if (!Number.isInteger(value.refreshSeconds) || value.refreshSeconds < 30) fail("refreshSeconds must be an integer of at least 30.");
    return {
        repositories: value.repositories,
        availableRepositories: value.availableRepositories ?? [],
        people: value.people.map(canonLogin),
        limit: value.limit,
        refreshSeconds: value.refreshSeconds,
    };
}

const uniq = list => [...new Set(list)];
export function normalizeTracking(value, { maxPeople = MAX_PEOPLE, maxRepos = MAX_REPOS } = {}) {
    const fail = message => { throw new TeamLoadError("invalid_input", message); };
    if (!value || typeof value !== "object" || Array.isArray(value)) fail("Provide people and repos lists.");
    const extra = Object.keys(value).filter(key => !["people", "repos"].includes(key));
    if (extra.length) fail(`Unknown tracking field: ${extra[0]}.`);
    if (!Array.isArray(value.people) || !Array.isArray(value.repos)) fail("people and repos must be lists.");
    if (!value.people.every(p => typeof p === "string" && loginPattern.test(p.replace(/^@/, "")))) fail("Every person must be a valid GitHub login.");
    if (!value.repos.every(r => typeof r === "string" && repoPattern.test(r))) fail("Every repository must look like owner/name.");
    const people = uniq(value.people.map(p => canonLogin(p.replace(/^@/, ""))));
    const seen = new Map();
    for (const repo of value.repos) if (!seen.has(repo.toLowerCase())) seen.set(repo.toLowerCase(), repo);
    const repos = [...seen.values()];
    if (people.length > maxPeople) fail(`Track at most ${maxPeople} people.`);
    if (repos.length > maxRepos) fail(`Track at most ${maxRepos} repositories.`);
    return { people, repos };
}

export function validateItemRef(input, { title = false, login = false, additional = false, from = false } = {}) {
    const fail = message => { throw new TeamLoadError("invalid_input", message); };
    if (!input || typeof input !== "object" || Array.isArray(input)) fail("Provide an item.");
    const logins = [...(login ? ["login"] : []), ...(from ? ["from"] : [])];
    const allowed = ["repository", "number", ...(title ? ["title"] : []), ...logins, ...(additional ? ["additional"] : [])];
    const extra = Object.keys(input).filter(key => !allowed.includes(key));
    if (extra.length) fail(`Unknown field: ${extra[0]}.`);
    if (typeof input.repository !== "string" || !repoPattern.test(input.repository)) fail("repository must look like owner/name.");
    if (!Number.isInteger(input.number) || input.number < 1) fail("number must be a positive integer.");
    if (title && input.title !== undefined && (typeof input.title !== "string" || input.title.length > 300)) fail("title must be a short string.");
    const normalized = {};
    for (const name of logins) {
        if (typeof input[name] !== "string" || !loginPattern.test(input[name].replace(/^@/, ""))) fail(`${name} must be a valid GitHub login.`);
        normalized[name] = canonLogin(input[name].replace(/^@/, ""));
    }
    if (from && normalized.from === normalized.login) fail("Choose a different assignee to move the issue to.");
    if (additional && input.additional !== undefined && typeof input.additional !== "boolean") fail("additional must be true or false.");
    return { ...input, ...normalized };
}

export const MOMENTUM_DAYS = Object.freeze([7, 30, 90]);
export const MOMENTUM_DEFAULT_DAYS = 7;
export const MOMENTUM_METRICS = Object.freeze([
    { key: "commits", label: "Commits" },
    { key: "prsOpened", label: "PRs opened" },
    { key: "prsMerged", label: "PRs merged" },
    { key: "issuesOpened", label: "Issues opened" },
    { key: "issuesCompleted", label: "Issues completed" },
]);
const MOMENTUM_EVENT_METRIC = { commit: "commits", prOpened: "prsOpened", prMerged: "prsMerged", issueOpened: "issuesOpened", issueCompleted: "issuesCompleted" };
const DAY_MS = 86_400_000;
const dayKey = time => new Date(time).toISOString().slice(0, 10);
// Start (UTC midnight, ms) of a window that ends today and spans `days` calendar days.
export const momentumStart = (days, now = Date.now()) => Math.floor(now / DAY_MS) * DAY_MS - (days - 1) * DAY_MS;

// `person` and `everyone` are separate fields so that a GitHub login can never collide with a scope keyword.
export function validateMomentumInput(input = {}) {
    const fail = message => { throw new TeamLoadError("invalid_input", message); };
    if (!input || typeof input !== "object" || Array.isArray(input)) fail("Provide momentum options.");
    const extra = Object.keys(input).filter(key => !["days", "person", "everyone", "repository", "force"].includes(key));
    if (extra.length) fail(`Unknown field: ${extra[0]}.`);
    const days = input.days ?? MOMENTUM_DEFAULT_DAYS;
    if (!MOMENTUM_DAYS.includes(days)) fail(`days must be one of ${MOMENTUM_DAYS.join(", ")}.`);
    if (input.person !== undefined && input.person !== null && (typeof input.person !== "string" || !loginPattern.test(input.person.replace(/^@/, "")))) fail("person must be a valid GitHub login.");
    if (input.everyone !== undefined && typeof input.everyone !== "boolean") fail("everyone must be true or false.");
    if (input.person && input.everyone) fail("Choose a person or everyone, not both.");
    if (input.repository !== undefined && input.repository !== null && (typeof input.repository !== "string" || !repoPattern.test(input.repository))) fail("repository must look like owner/name.");
    if (input.force !== undefined && typeof input.force !== "boolean") fail("force must be true or false.");
    return {
        days,
        person: input.person ? canonLogin(input.person.replace(/^@/, "")) : null,
        everyone: input.everyone === true,
        repository: input.repository ?? null,
        force: input.force === true,
    };
}

// Bucket activity events into UTC days. Events are { type, at, key, actors }, with actors as GitHub logins.
// Scope: a person (any event they are an actor on), everyone (every event), or the team (any tracked actor).
export function dailyMomentum(events, { days, person = null, everyone = false, team = [] }, now = Date.now()) {
    const start = momentumStart(days, now);
    const series = Array.from({ length: days }, (_, index) => ({ date: dayKey(start + index * DAY_MS), ...Object.fromEntries(MOMENTUM_METRICS.map(metric => [metric.key, 0])) }));
    const byDate = new Map(series.map(day => [day.date, day]));
    const totals = Object.fromEntries(MOMENTUM_METRICS.map(metric => [metric.key, 0]));
    const teamLogins = new Set(team.map(canonLogin));
    const wanted = person ? canonLogin(person) : null;
    const seen = new Set();
    let unattributed = 0;
    for (const event of events) {
        const metric = MOMENTUM_EVENT_METRIC[event.type];
        const time = Date.parse(event.at);
        if (!metric || !Number.isFinite(time) || time > now) continue;
        const day = byDate.get(dayKey(time));
        if (!day) continue;
        const id = `${event.type}:${event.key}`;
        if (seen.has(id)) continue;
        seen.add(id);
        const actors = (event.actors ?? []).map(canonLogin).filter(Boolean);
        if (!actors.length) unattributed++;
        if (wanted ? !actors.includes(wanted) : !everyone && !actors.some(login => teamLogins.has(login))) continue;
        day[metric]++;
        totals[metric]++;
    }
    return { series, totals, unattributed };
}

// Build each tracked person's current load from the open items in the tracked repositories.
export function computeLoad({ items = [], people = [], profiles = {} }) {
    const roster = people.map(login => {
        const profile = profiles[login] ?? {};
        return {
            login,
            agent: login === AGENT,
            name: login === AGENT ? "Copilot" : profile.name || profile.login || login,
            avatarUrl: login === AGENT ? null : profile.avatarUrl || `https://avatars.githubusercontent.com/${login}?s=64`,
            items: [],
        };
    });
    const byLogin = new Map(roster.map(person => [person.login, person]));
    const unowned = [];
    for (const item of items) {
        const base = { repository: item.repository, number: item.number, title: cleanTitle(item.title), url: item.url, updatedAt: item.updatedAt, labels: item.labels ?? [] };
        if (item.kind === "issue") {
            const owners = uniq((item.assignees ?? []).map(canonLogin).filter(Boolean));
            if (!owners.length) { unowned.push({ ...base, kind: "issue" }); continue; }
            for (const login of owners) byLogin.get(login)?.items.push({ ...base, kind: "issue" });
            continue;
        }
        const roles = new Map();
        for (const login of (item.reviewers ?? []).map(canonLogin)) if (login) roles.set(login, "review");
        for (const login of [item.author, ...(item.assignees ?? [])].map(canonLogin)) if (login) roles.set(login, "pr");
        for (const [login, kind] of roles) byLogin.get(login)?.items.push({ ...base, kind, isDraft: !!item.isDraft });
    }
    const recent = (a, b) => String(b.updatedAt).localeCompare(String(a.updatedAt));
    for (const person of roster) person.items.sort(recent);
    unowned.sort(recent);
    return { people: roster, unowned };
}

// Open-item count for any login (tracked or not), for the pickers.
export function loadOf(items, login) {
    const target = canonLogin(login);
    let count = 0;
    for (const item of items) {
        const who = new Set([...(item.assignees ?? []), ...(item.kind === "pr" ? [item.author, ...(item.reviewers ?? [])] : [])].map(canonLogin));
        if (who.has(target)) count++;
    }
    return count;
}

export function tone(count, { agent = false, limit = 6 } = {}) {
    if (agent) return { key: "agent", label: count ? `Agent · ${count} running` : "Agent · idle" };
    if (count === 0) return { key: "free", label: "Free" };
    if (count <= Math.floor(limit / 2)) return { key: "room", label: "Has room" };
    if (count <= limit) return { key: "busy", label: count === limit ? "At limit" : "Busy" };
    return { key: "over", label: `${count - limit} over limit` };
}

export const meterScale = (counts, limit = 6) => Math.min(16, Math.max(limit + 4, ...counts));

export function sortRoster(people) {
    const humans = people.filter(p => !p.agent).sort((a, b) => b.items.length - a.items.length || a.name.localeCompare(b.name));
    return [...humans, ...people.filter(p => p.agent)];
}

export function sortForAssign(people) {
    const humans = people.filter(p => !p.agent).sort((a, b) => a.items.length - b.items.length || a.name.localeCompare(b.name));
    return { humans, agent: people.find(p => p.agent) ?? null };
}

export function ago(iso, now = Date.now()) {
    const hours = Math.max(0, (now - new Date(iso).getTime()) / 36e5);
    if (!Number.isFinite(hours)) return "";
    if (hours < 1) return "now";
    if (hours < 24) return `${Math.round(hours)}h`;
    if (hours < 24 * 60) return `${Math.round(hours / 24)}d`;
    return `${Math.round(hours / 24 / 30)}mo`;
}

// Parse a github.com issue/PR URL into an in-canvas reference.
export function parseItemUrl(href) {
    let url;
    try { url = new URL(href, "https://github.com"); } catch { return null; }
    if (url.hostname !== "github.com") return null;
    const match = url.pathname.match(/^\/([\w.-]+)\/([\w.-]+)\/(?:issues|pull)\/(\d+)\/?$/);
    if (!match) return null;
    const repository = `${match[1]}/${match[2]}`;
    return repoPattern.test(repository) ? { repository, number: Number(match[3]) } : null;
}
