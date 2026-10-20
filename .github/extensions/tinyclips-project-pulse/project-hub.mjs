import { createProjectData, runGh } from "./project-data.mjs";
import { defaults, repositoriesOf, dailyMomentum, momentumSchema, itemSchema, assignmentSchema, issueStateSchema, validate, PulseError } from "./model.mjs";

export function createProjectHub(config, { run = runGh, now = () => Date.now(), log = () => {} } = {}) {
    const repositories = repositoriesOf(config);
    const profiles = new Map();
    let active = 0;
    const waiting = [];
    async function sharedRun(args) {
        if (active >= 3) await new Promise(resolve => waiting.push(resolve));
        else active++;
        try { return await run(args); }
        finally {
            const next = waiting.shift();
            if (next) next();
            else active--;
        }
    }
    const sources = new Map(repositories.map(repository => [repository, createProjectData(
        { ...config, repository }, { run: sharedRun, now, log, profiles, requireLaunchLabel: repository === config.repository },
    )]));
    function source(repository = config.repository) {
        const name = repositories.find(value => value.toLowerCase() === repository.toLowerCase());
        if (!name) throw new PulseError("invalid_input", "Choose one of the configured TinyClips repositories.");
        return sources.get(name);
    }
    let pending;
    const momentumPending = new Map();
    const momentumCache = new Map();
    async function snapshot(force = false) {
        if (pending) return pending;
        pending = (async () => {
            const results = await Promise.all(repositories.map(async repository => {
                try { return { repository, snapshot: await sources.get(repository).snapshot(force) }; }
                catch (error) {
                    log(`${repository}: ${error.message}`);
                    return { repository, error };
                }
            }));
            const available = results.filter(result => result.snapshot).map(result => result.snapshot);
            const repositoryStates = results.map(result => ({
                repository: result.repository,
                available: !!result.snapshot,
                errors: result.snapshot?.errors ?? [{ section: "repository", code: result.error.code ?? "github_error", message: result.error.message }],
                timestamps: result.snapshot?.timestamps ?? {},
                refreshedAt: result.snapshot?.refreshedAt ?? null,
                meta: result.snapshot?.meta ?? null,
            }));
            const errors = repositoryStates.flatMap(state => state.errors.map(error => ({ ...error, repository: state.repository })));
            const successful = available.map(value => value.refreshedAt).filter(Boolean).sort();
            return {
                config, repositories: repositoryStates,
                issues: available.flatMap(value => value.issues), prs: available.flatMap(value => value.prs),
                commits: available.flatMap(value => value.commits),
                team: available.find(value => !value.errors.some(error => error.section === "team"))?.team
                    ?? available[0]?.team ?? config.team.map(login => ({ login, name: login })),
                meta: {
                    openIssues: available.reduce((sum, value) => sum + value.meta.openIssues, 0),
                    openPrs: available.reduce((sum, value) => sum + value.meta.openPrs, 0),
                },
                timestamps: Object.fromEntries(repositoryStates.flatMap(value => Object.entries(value.timestamps).map(([key, time]) => [`${value.repository}/${key}`, time]))),
                refreshedAt: successful[0] ?? null, stale: errors.length > 0, errors,
                retryAt: [...available.map(value => value.retryAt), ...results.map(value => value.error?.retryAt)].filter(Boolean).sort().at(-1) ?? null,
                coverage: available[0]?.coverage ?? "GitHub data is unavailable; see the repository errors.",
            };
        })().finally(() => { pending = null; });
        return pending;
    }
    async function momentum(input) {
        validate(momentumSchema, input);
        const selection = { ...defaults, ...input };
        const selected = selection.repository === "all" ? repositories : [selection.repository];
        for (const repository of selected) source(repository);
        const key = JSON.stringify(selected) + ":" + selection.days;
        let records = momentumCache.get(key);
        if (!records || now() - records.time >= config.refreshSeconds * 1000) {
            // Cache history, not filtered counts, so changing a person or platform needs no extra GitHub calls.
            if (!momentumPending.has(key)) {
                const promise = Promise.all(selected.map(async repository => {
                    try { return { repository, history: await source(repository).history(selection.days) }; }
                    catch (error) {
                        log(`${repository} momentum: ${error.message}`);
                        return { repository, error: { code: error.code ?? "github_error", message: error.message, retryAt: error.retryAt ?? null } };
                    }
                })).then(results => {
                    const value = {
                        time: now(), records: results.filter(result => result.history).flatMap(result => [...result.history.issues, ...result.history.prs, ...result.history.commits]),
                        errors: results.filter(result => result.error).map(result => ({ ...result.error, repository: result.repository })),
                    };
                    momentumCache.set(key, value);
                    if (momentumCache.size > 12) momentumCache.delete(momentumCache.keys().next().value);
                    return value;
                }).finally(() => { momentumPending.delete(key); });
                momentumPending.set(key, promise);
            }
            records = await momentumPending.get(key);
        }
        return {
            days: selection.days, selection, repositories: selected, fetchedAt: new Date(records.time).toISOString(),
            series: dailyMomentum(records.records, selection, config, now()), errors: records.errors,
            coverage: "Daily counts in UTC. Commits include the default branch and open PR heads (deduplicated within each repository); PR opens/merges and issue opens/completions are counted separately. Issues opened counts each issue's creation date regardless of its current state, not later reopenings. Reopened issues and cancelled closures are not completions. This is activity, not a productivity score.",
        };
    }
    return {
        snapshot, momentum,
        detail: input => { validate(itemSchema, input); return source(input.repository).detail(input); },
        updateAssignees: input => { validate(assignmentSchema, input); return source(input.repository).updateAssignees(input); },
        setIssueState: input => { validate(issueStateSchema, input); return source(input.repository).setIssueState(input); },
    };
}
