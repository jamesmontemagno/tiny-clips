export class PulseError extends Error {
    constructor(code, message, retryAt = null) {
        super(message);
        this.code = code;
        this.retryAt = retryAt;
    }
}

export const defaults = Object.freeze({ view: "overview", person: "all", platform: "all", launchOnly: false, days: 7, repository: "all", refreshMinutes: 5 });
export const repositorySchema = { type: "string", pattern: "^[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$" };
export const loginPattern = /^(?!-)[a-z\d](?:[a-z\d-]{0,37}[a-z\d])?$/i;
export const viewSchema = {
    type: "object",
    properties: {
        view: { type: "string", enum: ["overview", "activity", "prs", "team", "momentum"] },
        person: { type: "string", pattern: "^(all|unassigned|others|[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,37}[a-zA-Z0-9])?)$" },
        platform: { type: "string", enum: ["all", "mac", "windows", "shared", "unknown"] },
        launchOnly: { type: "boolean" },
        days: { type: "integer", enum: [7, 30] },
        repository: { type: "string", pattern: "^(all|[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+)$" },
        refreshMinutes: { type: "integer", enum: [5, 10, 15, 20, 30, 0], description: "Automatic refresh interval in minutes; 0 disables automatic refresh." },
    },
    additionalProperties: false,
};
export const itemSchema = {
    type: "object",
    properties: { number: { type: "integer", minimum: 1 }, kind: { type: "string", enum: ["issue", "pr"] }, repository: repositorySchema },
    required: ["number", "kind"],
    additionalProperties: false,
};
export const assignmentSchema = {
    ...itemSchema,
    properties: {
        ...itemSchema.properties,
        assignees: { type: "array", maxItems: 10, uniqueItems: true, items: { type: "string", pattern: "^[a-zA-Z0-9][a-zA-Z0-9-]{0,38}$" } },
        expectedState: { type: "string", minLength: 1 },
        confirmed: { const: true, type: "boolean" },
    },
    required: [...itemSchema.required, "assignees", "expectedState", "confirmed"],
};
export const issueStateSchema = {
    type: "object",
    properties: {
        number: itemSchema.properties.number,
        repository: repositorySchema,
        state: { type: "string", enum: ["OPEN", "CLOSED"] },
        reason: { type: "string", enum: ["COMPLETED", "NOT_PLANNED"] },
        expectedState: { type: "string", minLength: 1 },
        confirmed: { const: true, type: "boolean" },
    },
    required: ["number", "state", "expectedState", "confirmed"],
    additionalProperties: false,
};
export const momentumSchema = {
    type: "object",
    properties: {
        repository: viewSchema.properties.repository,
        person: viewSchema.properties.person,
        platform: viewSchema.properties.platform,
        launchOnly: viewSchema.properties.launchOnly,
        days: { type: "integer", enum: [7, 30, 90] },
    },
    required: ["days"],
    additionalProperties: false,
};
export const repositoriesOf = config => config.repositories ?? [config.repository];
export const itemKey = item => `${item.repository ?? ""}:${item.kind}:${item.sha ?? item.number}`;

export function validate(schema, value) {
    const fail = () => { throw new PulseError("invalid_input", "Invalid input. Check the action fields and supported values."); };
    if (schema.type === "object") {
        if (!value || typeof value !== "object" || Array.isArray(value)) fail();
        if ((schema.required ?? []).some(key => !Object.hasOwn(value, key))) fail();
        for (const [key, entry] of Object.entries(value)) {
            if (!Object.hasOwn(schema.properties, key)) fail();
            validate(schema.properties[key], entry);
        }
    } else if (schema.type === "array") {
        if (!Array.isArray(value) || value.length > schema.maxItems) fail();
        for (const entry of value) validate(schema.items, entry);
        if (schema.uniqueItems && new Set(value.map(entry => entry.toLowerCase())).size !== value.length) fail();
    } else {
        if (schema.type === "integer" ? !Number.isSafeInteger(value) : typeof value !== schema.type) fail();
        if (schema.minimum !== undefined && value < schema.minimum) fail();
        if (schema.minLength !== undefined && value.length < schema.minLength) fail();
        if (schema.pattern && !new RegExp(schema.pattern).test(value)) fail();
        if (schema.enum && !schema.enum.includes(value)) fail();
        if (Object.hasOwn(schema, "const") && value !== schema.const) fail();
    }
    return value;
}

export function validateView(value) {
    validate(viewSchema, value);
    if (value.person && !["all", "others", "unassigned"].includes(value.person) && !loginPattern.test(value.person)) {
        throw new PulseError("invalid_input", "Choose a valid GitHub login.");
    }
    return { ...defaults, ...value };
}

export function validateConfig(value) {
    if (!value || value.format !== "tinyclips-project-pulse-config" || value.version !== 1
        || Object.keys(value).some(key => !["format", "version", "repository", "repositories", "team", "launchLabel", "platformLabels", "refreshSeconds"].includes(key))
        || typeof value.repository !== "string" || !/^[\w.-]+\/[\w.-]+$/.test(value.repository)
        || (value.repositories !== undefined && (!Array.isArray(value.repositories) || !value.repositories.length
            || value.repositories.length > 10 || value.repositories.some(repo => typeof repo !== "string" || !/^[\w.-]+\/[\w.-]+$/.test(repo))
            || new Set(value.repositories.map(repo => repo.toLowerCase())).size !== value.repositories.length
            || !value.repositories.includes(value.repository)))
        || !Array.isArray(value.team) || value.team.length === 0 || value.team.length > 20
        || value.team.some(login => typeof login !== "string" || !loginPattern.test(login))
        || new Set(value.team.map(login => login.toLowerCase())).size !== value.team.length
        || typeof value.launchLabel !== "string" || !value.launchLabel.trim()
        || typeof value.platformLabels?.mac !== "string" || !value.platformLabels.mac.trim()
        || typeof value.platformLabels?.windows !== "string" || !value.platformLabels.windows.trim()
        || Object.keys(value.platformLabels).some(key => !["mac", "windows"].includes(key))
        || value.refreshSeconds !== 300) {
        throw new PulseError("invalid_config", "Project Pulse config.json has an unsupported format or invalid repository, team, labels, or refresh interval.");
    }
    return value;
}

export function platformOf(labels, paths, config) {
    let mac = labels.includes(config.platformLabels.mac);
    let windows = labels.includes(config.platformLabels.windows);
    if (paths) {
        mac ||= paths.some(path => path.startsWith("mac/"));
        windows ||= paths.some(path => path.startsWith("windows/"));
    }
    if (mac && windows) return "shared";
    if (mac) return "mac";
    if (windows) return "windows";
    return paths?.length ? "shared" : "unknown";
}

export function itemVersion(item) {
    return JSON.stringify([item.repository, item.id, item.updatedAt, item.state, item.stateReason,
        item.assignees.map(person => person.login.toLowerCase()).sort(),
        (item.requestedReviewers ?? []).map(reviewer => `${reviewer.__typename}:${reviewer.login ?? reviewer.url ?? ""}`.toLowerCase()).sort()]);
}

function prCreatorsAndReviewers(item) {
    return [item.author?.login, ...(item.requestedReviewers ?? []).filter(reviewer => reviewer.__typename === "User").map(reviewer => reviewer.login)];
}

function matchesOwners(owners, person, config) {
    if (person === "others") return owners.some(login => login && !config.team.some(member => member.toLowerCase() === login.toLowerCase()));
    return owners.some(login => login?.toLowerCase() === person.toLowerCase());
}

export function matches(item, view, config) {
    if (view.repository && view.repository !== "all" && item.repository !== view.repository) return false;
    if (view.launchOnly && !item.launch) return false;
    if (view.platform !== "all" && item.platform !== view.platform
        && !(["mac", "windows"].includes(view.platform) && item.platform === "shared")) return false;
    if (view.person === "all") return true;
    if (view.person === "unassigned") return item.kind !== "commit" && item.assignees.length === 0;
    const owners = item.kind === "commit" ? [item.author?.login]
        : item.kind === "pr" ? [...prCreatorsAndReviewers(item), ...item.assignees.map(person => person.login)]
            : item.assignees.map(person => person.login);
    return matchesOwners(owners, view.person, config);
}

export function rosterCounts(snapshot, view, person) {
    const selection = { ...view, person };
    return {
        openIssues: snapshot.issues.filter(item => item.state === "OPEN" && matches(item, selection, snapshot.config)).length,
        openPrs: snapshot.prs.filter(item => item.state === "OPEN" && matches(item, selection, snapshot.config)
            && (person === "unassigned" || matchesOwners(prCreatorsAndReviewers(item), person, snapshot.config))).length,
    };
}

export function derive(snapshot, view, now = Date.now()) {
    const issues = snapshot.issues.filter(item => matches(item, view, snapshot.config));
    const prs = snapshot.prs.filter(item => matches(item, view, snapshot.config));
    const cutoff = now - view.days * 86400_000;
    const activity = [];
    for (const item of [...issues, ...prs]) {
        const events = [[item.createdAt, "opened"]];
        if (item.closedAt) events.push([item.closedAt, item.state === "MERGED" ? "merged" : "closed"]);
        if (item.updatedAt !== item.createdAt && item.updatedAt !== item.closedAt) events.push([item.updatedAt, "updated"]);
        for (const [time, verb] of events) {
            if (Date.parse(time) >= cutoff) activity.push({ id: `${itemKey(item)}-${verb}-${time}`, item, time, verb, actor: verb === "opened" ? item.author : null });
        }
    }
    for (const item of snapshot.commits.filter(item => matches(item, view, snapshot.config))) {
        if (Date.parse(item.time) >= cutoff) activity.push({ id: itemKey(item), item, time: item.time, verb: "committed", actor: item.author });
    }

    activity.sort((a, b) => Date.parse(b.time) - Date.parse(a.time));
    const launch = snapshot.issues.filter(item => item.launch && matches(item, { ...view, launchOnly: false }, snapshot.config));
    return {
        issues, prs, activity,
        openIssues: issues.filter(item => item.state === "OPEN"),
        openPrs: prs.filter(item => item.state === "OPEN"),
        launch: {
            total: launch.length,
            open: launch.filter(item => item.state === "OPEN").length,
            completed: launch.filter(item => item.state === "CLOSED" && item.stateReason === "COMPLETED").length,
            notPlanned: launch.filter(item => item.state === "CLOSED" && item.stateReason === "NOT_PLANNED").length,
            otherClosed: launch.filter(item => item.state === "CLOSED" && !["COMPLETED", "NOT_PLANNED"].includes(item.stateReason)).length,
        },
    };
}

export function dailyMomentum(records, view, config, now = Date.now()) {
    const end = new Date(now);
    const today = Date.UTC(end.getUTCFullYear(), end.getUTCMonth(), end.getUTCDate());
    const series = Array.from({ length: view.days }, (_, index) => ({
        date: new Date(today - (view.days - index - 1) * 86400_000).toISOString().slice(0, 10),
        commits: 0, opened: 0, merged: 0, issuesOpened: 0, completed: 0,
    }));
    const byDate = new Map(series.map(day => [day.date, day]));
    const seen = new Set();
    function add(item, metric, time) {
        const timestamp = Date.parse(time);
        if (!Number.isFinite(timestamp) || timestamp > now) return;
        const day = byDate.get(new Date(timestamp).toISOString().slice(0, 10));
        const key = `${itemKey(item)}:${metric}`;
        if (day && !seen.has(key)) { day[metric]++; seen.add(key); }
    }
    for (const item of records.filter(item => matches(item, view, config))) {
        if (item.kind === "commit") add(item, "commits", item.time);
        if (item.kind === "pr") {
            add(item, "opened", item.createdAt);
            if (item.state === "MERGED") add(item, "merged", item.closedAt);
        }
        if (item.kind === "issue") {
            add(item, "issuesOpened", item.createdAt);
            if (item.state === "CLOSED" && item.stateReason === "COMPLETED") add(item, "completed", item.closedAt);
        }
    }
    return series;
}

function refreshMilestone(item, previous, timestamp) {
    if (!["pr", "issue"].includes(item.kind)) return null;
    if (!previous && !(Date.parse(item.createdAt) > Date.parse(timestamp))) return null;
    if (previous?.state === item.state
        && (item.kind !== "issue" || item.state !== "CLOSED" || previous.stateReason === item.stateReason)) return null;
    if (item.state === "OPEN") return `${item.kind}-${previous ? "reopened" : "opened"}`;
    if (item.kind === "pr" && ["MERGED", "CLOSED"].includes(item.state)) return `pr-${item.state.toLowerCase()}`;
    if (item.kind === "issue" && item.state === "CLOSED" && item.stateReason === "COMPLETED") return "issue-completed";
    return null;
}

export function celebrationSummary(changes) {
    return [
        ["pr-opened", "PR opened", "PRs opened"], ["pr-reopened", "PR reopened", "PRs reopened"],
        ["pr-merged", "PR merged", "PRs merged"], ["pr-closed", "PR closed", "PRs closed"],
        ["issue-opened", "issue opened", "issues opened"], ["issue-reopened", "issue reopened", "issues reopened"],
        ["issue-completed", "issue completed", "issues completed"],
    ].flatMap(([milestone, singular, plural]) => {
        const count = changes.filter(change => change.milestone === milestone).length;
        return count ? [`${count} ${count === 1 ? singular : plural}`] : [];
    }).join(" · ");
}

export function createRefreshTracker() {
    const sections = new Map();
    const badges = new Map();
    let revision = 0;
    return {
        badges,
        get revision() { return revision; },
        accept(snapshot) {
            const changes = [];
            for (const source of snapshot.repositories ?? []) {
                for (const section of ["issues", "prs", "commits"]) {
                    if (source.errors.some(error => error.section === section || error.section === "repository")) continue;
                    const timestamp = source.timestamps?.[section];
                    if (!timestamp) continue;
                    const key = `${source.repository}:${section}`;
                    const previous = sections.get(key);
                    if (previous && Date.parse(timestamp) <= Date.parse(previous.timestamp)) continue;
                    revision++;
                    const items = snapshot[section].filter(item => item.repository === source.repository);
                    const versions = new Map(items.map(item => [itemKey(item), item.kind === "commit" ? item.sha : itemVersion(item)]));
                    for (const [id, value] of badges) if (value.repository === source.repository && value.section === section) badges.delete(id);
                    if (previous) {
                        for (const item of items) {
                            const id = itemKey(item);
                            if (previous.versions.get(id) === versions.get(id)) continue;
                            const type = previous.versions.has(id) || (item.kind !== "commit" && Date.parse(item.createdAt) <= Date.parse(previous.timestamp)) ? "Updated" : "New";
                            const change = { item, type, repository: source.repository, section,
                                milestone: refreshMilestone(item, previous.states.get(id), previous.timestamp) };
                            badges.set(id, change);
                            changes.push(change);
                        }
                    }
                    sections.set(key, { timestamp, versions,
                        states: new Map(items.map(item => [itemKey(item), { state: item.state, stateReason: item.stateReason }])) });
                }
            }
            return changes;
        },
    };
}
