import { defaults, derive, matches, rosterCounts, repositoriesOf, itemKey, createRefreshTracker, celebrationSummary } from "/model.mjs";
import { initMomentum } from "/momentum.mjs";
import { initCelebration } from "/celebration.mjs";

const $ = selector => document.querySelector(selector);
const nonce = $('meta[name="pulse-nonce"]').content;
let view = { ...defaults };
let snapshot;
let refreshing;
let timer;
let nextRefreshAt;
let currentItem;
let dirty = false;
let saving = false;
let viewRevision = 0;
let unsavedPreferences = false;
let preferenceError = "";
let saveQueue = Promise.resolve();
const limits = { activity: 20, issues: 12, prs: 12 };
const platformNames = { mac: "macOS", windows: "Windows", shared: "Shared", unknown: "Unclassified" };
const updates = createRefreshTracker();
const celebration = initCelebration();
const momentum = initMomentum({ action: (...args) => action(...args), selection: () => ({
    repository: view.repository, person: view.person, platform: view.platform, launchOnly: view.launchOnly,
}) });
function repositoryName(repository) { return repository?.split("/")[1] || "repository"; }
function updateBadge(item) {
    const change = updates.badges.get(itemKey(item));
    return change ? badge(change.type, "new") : null;
}

function h(tag, attributes = {}, ...children) {
    const node = document.createElement(tag);
    for (const [name, value] of Object.entries(attributes)) {
        if (name.startsWith("on")) node.addEventListener(name.slice(2), value);
        else if (name === "class") node.className = value;
        else node.setAttribute(name, String(value));
    }
    for (const child of children.flat()) {
        if (child !== undefined && child !== null) node.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return node;
}
function safeLink(url, avatar = false) {
    try {
        const parsed = new URL(url);
        return parsed.protocol === "https:" && parsed.hostname === (avatar ? "avatars.githubusercontent.com" : "github.com") ? parsed.href : null;
    } catch { return null; }
}
function link(text, url) {
    const href = safeLink(url);
    return href ? h("a", { href, target: "_blank", rel: "noreferrer" }, text) : h("span", {}, text);
}
function avatar(person, large = false) {
    const name = person?.name || person?.login || "GitHub";
    const initials = name.split(/[\s-]/).map(part => part[0]).join("").slice(0, 2).toUpperCase();
    const wrapper = h("span", { class: `avatar${large ? " large" : ""}`, "aria-hidden": "true" }, initials);
    const src = safeLink(person?.avatarUrl, true);
    if (src) wrapper.append(h("img", { src, alt: "", loading: "lazy", onerror: event => event.target.remove() }));
    return wrapper;
}
function relative(time) {
    if (!time) return "not yet";
    const minutes = Math.max(0, Math.floor((Date.now() - Date.parse(time)) / 60000));
    return minutes < 1 ? "just now" : minutes < 60 ? `${minutes}m ago` : minutes < 1440 ? `${Math.floor(minutes / 60)}h ago` : `${Math.floor(minutes / 1440)}d ago`;
}
function badge(text, tone = "") { return h("span", { class: `badge ${tone}` }, text); }
function announce(message) { $("#announcement").textContent = message; }
function notice(message) {
    $("#notice").hidden = !message;
    $("#notice").textContent = message || "";
}
async function request(path, options) {
    const response = await fetch(path, options);
    let body;
    try { body = await response.json(); }
    catch { throw new Error(`Project Pulse returned an unreadable response (${response.status}). Reopen the canvas.`); }
    if (!response.ok) throw new Error(body.error || `Request failed (${response.status}).`);
    return body;
}
const action = (name, input = {}) => request("/api/action", {
    method: "POST", headers: { "Content-Type": "application/json", "X-Pulse-Nonce": nonce },
    body: JSON.stringify({ action: name, input }),
});
function schedule(reset = false) {
    clearTimeout(timer);
    if (view.refreshMinutes === 0) { nextRefreshAt = null; return; }
    if (reset || nextRefreshAt == null) nextRefreshAt = Date.now() + view.refreshMinutes * 60_000;
    if (document.hidden || refreshing) return;
    const delay = Math.max(0, nextRefreshAt - Date.now(), Date.parse(snapshot?.retryAt || "") - Date.now() || 0);
    timer = setTimeout(() => refresh(), delay);
}
async function refresh(force = false) {
    if (refreshing) return refreshing;
    celebration.stop();
    $("#refresh").disabled = true;
    $("#refresh").textContent = "Refreshing...";
    refreshing = (async () => {
        try {
            const revision = viewRevision;
            await saveQueue;
            const stored = await request("/api/preferences");
            if (revision === viewRevision && !unsavedPreferences) view = stored;
            snapshot = await (force ? action("refresh_status") : request("/api/snapshot"));
            const previousRevision = updates.revision;
            const changes = updates.accept(snapshot);
            render();
            if (changes.length) {
                const added = changes.filter(change => change.type === "New").length;
                const moment = celebrationSummary(changes);
                $("#updates-message").textContent = `${moment ? `A little team momentum: ${moment}. ` : ""}${added} new · ${changes.length - added} updated since the previous successful section refresh across ${new Set(changes.map(change => change.repository)).size} repositor${new Set(changes.map(change => change.repository)).size === 1 ? "y" : "ies"}. Look for New / Updated badges.`;
                $("#updates").hidden = false;
                if (moment) celebration.burst();
            } else if (updates.revision !== previousRevision) $("#updates").hidden = true;
            notice([...snapshot.errors.map(error => `${error.repository ? `${repositoryName(error.repository)} / ` : ""}${error.section}: ${error.message}`), preferenceError].filter(Boolean).join(" "));
            announce(snapshot.stale ? "Refresh finished with unavailable or stale sections. See the error notice." : "Project activity updated.");
        } catch (error) {
            if (snapshot) {
                snapshot = { ...snapshot, stale: true, repositories: snapshot.repositories.map(source => ({
                    ...source, errors: [...source.errors.filter(value => value.code !== "refresh_failed"), { section: "repository", code: "refresh_failed", message: error.message }],
                })) };
                render();
            }
            notice(`${error.message}${snapshot ? " Keeping the last snapshot; it may be stale." : " Use Refresh now to retry."}`);
            $("#freshness").textContent = "Refresh failed";
            $("#content").setAttribute("aria-busy", "false");
            if (!snapshot) $("#content").replaceChildren(h("p", { class: "empty" }, "No snapshot available yet. The error above explains what to fix."));
        } finally {
            $("#refresh").disabled = false;
            $("#refresh").textContent = "Refresh now";
            refreshing = null;
            schedule(true);
        }
    })();
    return refreshing;
}
function setView(patch) {
    view = { ...view, ...patch };
    const revision = ++viewRevision;
    unsavedPreferences = true;
    for (const key of Object.keys(limits)) limits[key] = key === "activity" ? 20 : 12;
    render();
    momentum.scopeChanged();
    if (Object.hasOwn(patch, "refreshMinutes")) {
        schedule(true);
        announce(view.refreshMinutes === 0 ? "Automatic refresh disabled. Refresh now is still available."
            : `Automatic refresh set to every ${view.refreshMinutes} minutes while visible.`);
    }
    const pending = { ...view };
    saveQueue = saveQueue.then(async () => {
        try {
            await action("set_view_state", pending);
            if (revision === viewRevision) {
                unsavedPreferences = false;
                preferenceError = "";
            }
        } catch (error) {
            preferenceError = `Your view changed, but preferences could not be saved: ${error.message}`;
            notice(preferenceError);
        }
    });
}
function selectPerson(person) { setView({ person: view.person === person ? "all" : person }); }
function itemButton(item) {
    return h("button", { class: "item-title", "data-focus": `item-${itemKey(item)}`, onclick: () => openDetail(item) }, item.title);
}
function itemMeta(item) {
    return h("div", { class: "item-meta" }, link(`${item.repository}#${item.number}`, item.url), updateBadge(item),
        badge(item.state === "CLOSED" && item.stateReason === "NOT_PLANNED" ? "Not planned" : item.state.toLowerCase(), item.state === "MERGED" ? "good" : ""),
        badge(platformNames[item.platform]), item.launch ? badge("2.0") : null,
        item.isDraft ? badge("Draft") : null);
}
function reviewRequests(item) {
    return item.requestedReviewers?.length ? h("span", {}, "Review requested: ",
        item.requestedReviewers.map(reviewer => reviewer.__typename === "User" ? reviewer.login
            : reviewer.__typename === "Team" ? `${reviewer.name || reviewer.slug} (team)` : "unavailable reviewer").join(", ")) : null;
}
function workRow(item) {
    const row = h("li", { class: "work-row" }, itemButton(item), itemMeta(item));
    row.append(h("div", { class: "ownership" }, ...item.assignees.map(person => h("span", { title: person.login }, avatar(person), person.login)),
        item.assignees.length ? null : "Unassigned",
        item.kind === "pr" ? h("span", {}, `Author: ${item.author?.login || "unknown"} · Checks: ${item.checks} · Review: ${item.reviewDecision || "not available"}`) : null,
        reviewRequests(item)));
    return row;
}
function showMore(key, count) {
    if (limits[key] >= count) return null;
    return h("button", { class: "button load-more", "data-focus": `more-${key}`, onclick: () => { limits[key] += 20; render(); } },
        `Show more (${count - limits[key]} remaining)`);
}
function workList(items, key, empty) {
    return h("div", {}, items.length ? h("ul", { class: "work-list" }, ...items.slice(0, limits[key]).map(workRow)) : h("p", { class: "empty" }, empty), showMore(key, items.length));
}
function feed(events) {
    return h("div", {}, events.length ? h("ol", { class: "feed" }, ...events.slice(0, limits.activity).map(event => {
        const item = event.item;
        const actor = event.actor;
        return h("li", { class: "event" }, avatar(actor), h("div", {},
            h("div", { class: "event-top" }, h("span", {}, actor?.login || actor?.name || "GitHub record", " ", h("strong", {}, event.verb)),
                h("time", { datetime: event.time, title: new Date(event.time).toLocaleString() }, relative(event.time))),
            item.kind === "commit" ? link(item.title, item.url) : itemButton(item),
            h("div", { class: "item-meta" }, badge(`${repositoryName(item.repository)} / ${item.kind === "commit" ? item.sha.slice(0, 7) : `${item.kind === "pr" ? "PR" : "Issue"} #${item.number}`}`), updateBadge(item),
                badge(platformNames[item.platform]), item.launch ? badge("2.0") : null,
                event.verb === "updated" ? h("span", {}, "Updated record; actor not supplied") : null)));
    })) : h("p", { class: "empty" }, "No activity matches these filters in this time window."),
    showMore("activity", events.length));
}
function teamPanel() {
    const people = [...snapshot.team, { login: "unassigned", name: "Unassigned" }, { login: "others", name: "Other contributors" }];
    const shown = view.person === "all" ? people : [people.find(person => person.login === view.person) ?? { login: view.person, name: view.person }];
    return h("div", { class: "team-board" }, ...shown.map(person => {
        const selection = { ...view, person: person.login };
        const work = [...snapshot.issues, ...snapshot.prs].filter(item => item.state === "OPEN" && matches(item, selection, snapshot.config));
        return h("section", { class: "team-lane" },
            h("div", { class: "lane-heading" }, avatar(person), h("h3", {}, person.name || person.login), badge(`${work.length} open`)),
            workList(work, "issues", "Nothing assigned here in this scope."));
    }));
}
function render() {
    $("#refresh-interval").value = view.refreshMinutes;
    if (!snapshot) return;
    const focusKey = document.activeElement?.getAttribute("data-focus");
    const selected = derive(snapshot, view);
    const momentumView = view.view === "momentum";
    $("#momentum-view").hidden = !momentumView;
    $("#launch-summary").hidden = momentumView;
    $("#content").hidden = momentumView;
    $("#activity-window").hidden = momentumView;
    $("#skip-to-view").setAttribute("href", momentumView ? "#momentum-view" : "#content");
    momentum.setActive(momentumView);
    $("#content").setAttribute("aria-busy", "false");
    $("#launch").checked = view.launchOnly;
    $("#platform").value = view.platform;
    $("#days").value = view.days;
    const repositorySelect = $("#repository");
    if (repositorySelect.options.length !== repositoriesOf(snapshot.config).length + 1) {
        repositorySelect.replaceChildren(h("option", { value: "all" }, "All repositories"),
            ...repositoriesOf(snapshot.config).map(repository => h("option", { value: repository }, repositoryName(repository))));
    }
    repositorySelect.value = view.repository;
    for (const button of document.querySelectorAll("[data-view]")) {
        if (button.dataset.view === view.view) button.setAttribute("aria-current", "page");
        else button.removeAttribute("aria-current");
    }
    $("#freshness").textContent = `${snapshot.stale ? "Partial / stale" : `Updated ${relative(snapshot.refreshedAt)}`} · ${view.refreshMinutes === 0 ? "Auto refresh off" : `Every ${view.refreshMinutes} min`}`;
    $("#filter-context").textContent = `${view.repository === "all" ? `All ${repositoriesOf(snapshot.config).length} repositories` : view.repository} / ${view.launchOnly ? "2.0 Launch" : "All work"} / ${view.person === "all" ? "Everyone" : view.person} / ${view.platform === "all" ? "All platforms" : platformNames[view.platform]}. ${momentumView ? "Momentum history is gathered on demand using its History range." : "Open work is not limited by the activity window."}`;
    $("#coverage").textContent = `${snapshot.coverage} Last successful sections: ${Object.entries(snapshot.timestamps).map(([key, time]) => `${key} ${relative(time)}`).join("; ")}.`;
    $("#roster").replaceChildren(...[...snapshot.team, { login: "unassigned", name: "Unassigned" }, { login: "others", name: "Other contributors" }].map(person => {
        const counts = rosterCounts(snapshot, view, person.login);
        const prDescription = person.login === "unassigned" ? "Open PRs without an assignee"
            : person.login === "others" ? "Open PRs created by or requesting review from contributors outside the core team"
                : `Open PRs created by ${person.name || person.login} or requesting their review`;
        const changed = [...updates.badges.values()].filter(change => matches(change.item, { ...view, person: person.login }, snapshot.config)).length;
        return h("button", { class: "person", "aria-pressed": view.person === person.login, "data-focus": `person-${person.login}`, onclick: () => selectPerson(person.login) },
            avatar(person, true), h("span", { class: "person-copy" }, h("span", { class: "person-name" }, person.name || person.login),
                h("span", { class: "muted" }, `${counts.openIssues} open issue${counts.openIssues === 1 ? "" : "s"}`),
                h("span", { class: "muted", title: prDescription }, `${counts.openPrs} open PR${counts.openPrs === 1 ? "" : "s"}`)), changed ? badge(`+${changed}`, "new") : null);
    }));
    const launch = selected.launch;
    const repoStates = snapshot.repositories.filter(source => view.repository === "all" || source.repository === view.repository);
    const incomplete = repoStates.some(source => source.errors.some(error => ["issues", "launch", "repository"].includes(error.section)));
    const repoIssues = repoStates.reduce((sum, source) => sum + (source.meta?.openIssues ?? 0), 0);
    const repoPrs = repoStates.reduce((sum, source) => sum + (source.meta?.openPrs ?? 0), 0);
    const progress = h("progress", { max: Math.max(1, launch.total), value: launch.completed, "aria-label": "Launch issues completed in the current platform/person scope" });
    $("#launch-summary").replaceChildren(
        h("div", { class: "launch-copy" }, h("h2", {}, "The road to 2.0"), h("p", { class: "muted" }, incomplete ? "Launch accounting unavailable or stale. See the error above." : `${launch.completed} completed / ${launch.total} labeled issues in this person/platform scope`), progress),
        h("div", { class: "launch-facts" },
            h("div", {}, h("strong", {}, incomplete ? "?" : launch.open), " launch issues open"),
            h("div", {}, h("strong", {}, selected.openPrs.length), " open PRs in scope"),
            h("div", {}, h("strong", {}, launch.notPlanned), " not planned"),
            h("div", {}, ...repoStates.map(source => h("span", { class: "repo-check" }, `${repositoryName(source.repository)} checks: `, h("strong", {}, source.meta?.checks ?? "UNAVAILABLE"))))),
        h("p", { class: "summary-note" }, `${incomplete ? "Known repository totals (partial/stale)" : "Repository totals"}: ${repoIssues} open issues · ${repoPrs} open PRs.${launch.otherClosed ? ` ${launch.otherClosed} launch closures have no completion reason.` : ""} Launch counts require explicit labels; issue completion is not release certification.`));
    const heading = (title, detail) => h("div", { class: "section-heading" }, h("h2", {}, title), h("span", { class: "muted" }, detail));
    if (momentumView) {
        $("#content").replaceChildren();
    } else if (view.view === "activity") {
        $("#content").replaceChildren(heading("In motion", `${selected.activity.length} records · ${view.days} days`), feed(selected.activity));
    } else if (view.view === "prs") {
        $("#content").replaceChildren(heading("Pull requests", "All open + recent merged and closed"), workList(selected.prs, "prs", "No pull requests in this scope. A quiet queue is still useful information."));
    } else if (view.view === "team") {
        $("#content").replaceChildren(heading("Who is working on what", "Issue assignees · PR authors, assignees and requested reviewers"), teamPanel());
    } else {
        const unassigned = selected.openIssues.filter(item => !item.assignees.length);
        $("#content").replaceChildren(
            h("div", { class: "overview-grid" },
                h("section", {}, heading("In motion", `${view.days}-day activity`), feed(selected.activity)),
                h("aside", {}, heading("Needs a hand", `${unassigned.length} unassigned issues`), workList(unassigned, "issues", "Every open issue in this scope has an assignee."),
                    heading("Pull requests", `${selected.openPrs.length} open`), workList(selected.openPrs, "prs", "No open PRs right now. Merged and closed work is in the Pull requests view."))),
            h("section", { class: "current-work" }, heading("On the workbench", `${selected.openIssues.length} open issues in scope`), workList(selected.openIssues, "issues", "No open issues match these filters.")));
    }
    if (focusKey && document.activeElement === document.body) {
        [...document.querySelectorAll("[data-focus]")].find(node => node.dataset.focus === focusKey)?.focus({ preventScroll: true });
    }
}
function detailNotice(message) {
    $("#detail-notice").hidden = !message;
    $("#detail-notice").textContent = message || "";
}
async function openDetail(item) {
    currentItem = null;
    dirty = false;
    detailNotice("");
    $("#detail-status").textContent = "";
    $("#detail-heading").textContent = `${item.repository}#${item.number}`;
    $("#detail-content").replaceChildren(h("p", {}, "Reading current details from GitHub..."));
    $("#detail").showModal();
    await loadDetail(item);
}
async function loadDetail(item) {
    try {
        currentItem = await action("get_item_detail", { number: item.number, kind: item.kind, repository: item.repository });
        dirty = false;
        renderDetail();
    } catch (error) { detailNotice(error.message); }
}
function renderDetail() {
    const item = currentItem;
    detailNotice("");
    const assignees = h("input", { id: "assignees", value: item.assignees.map(person => person.login).join(", "), autocomplete: "off", oninput: () => { dirty = true; } });
    const state = h("select", { id: "issue-state", onchange: () => { dirty = true; } },
        h("option", { value: "OPEN" }, "Open"), h("option", { value: "COMPLETED" }, "Closed: completed"), h("option", { value: "NOT_PLANNED" }, "Closed: not planned"));
    state.value = item.state === "OPEN" ? "OPEN" : item.stateReason || "COMPLETED";
    const confirmation = h("input", { type: "checkbox", id: "confirm-edit" });
    const assignmentButton = h("button", { class: "button primary", type: "button", onclick: () => saveEdit("assign") }, "Apply assignees");
    const stateButton = h("button", { class: "button", type: "button", onclick: () => saveEdit("state") }, "Apply issue state");
    const editor = h("div", { class: "editor" },
        h("h3", {}, "Make an explicit change"),
        h("p", { class: "muted" }, "Only Apply writes to GitHub. Review the exact list: omitted existing assignees will be removed."),
        h("label", { for: "assignees" }, "Assignees (comma-separated GitHub logins; leave empty to unassign)"), assignees,
        h("div", { class: "quick-assign" }, ...snapshot.team.map(person => h("button", { class: "text-button", type: "button", onclick: () => {
            const values = assignees.value.split(",").map(value => value.trim()).filter(Boolean);
            if (!values.some(value => value.toLowerCase() === person.login.toLowerCase())) values.push(person.login);
            assignees.value = values.join(", ");
            dirty = true;
        } }, `+ ${person.name || person.login}`))),
        item.kind === "issue" ? h("label", { for: "issue-state" }, "Issue state") : null,
        item.kind === "issue" ? state : null,
        h("label", { class: "confirmation" }, confirmation, " I reviewed the proposed values and confirm this GitHub change."),
        h("div", { class: "edit-actions" }, assignmentButton, item.kind === "issue" ? stateButton : null));
    const repoStale = snapshot.repositories.find(source => source.repository === item.repository)?.errors.length > 0;
    const writable = item.viewerCanUpdate && !repoStale;
    if (!writable) {
        for (const node of editor.querySelectorAll("input, select, button")) node.disabled = true;
        editor.prepend(h("p", { class: "notice" }, repoStale ? "Edits are disabled while this repository is stale. Refresh successfully first." : "Your GitHub account does not have permission to edit this item."));
    }
    $("#detail-content").replaceChildren(h("h3", {}, item.title), itemMeta(item), reviewRequests(item),
        h("p", {}, link("Open on GitHub", item.url), " · ", h("button", { class: "text-button", onclick: () => {
            if (dirty && !window.confirm("Discard the unapplied values and reload from GitHub?")) return;
            loadDetail(item);
        } }, "Reload details")),
        h("p", { class: "body-copy" }, item.bodyText || "No description provided."), editor);
}
async function saveEdit(type) {
    if (saving) return;
    if (snapshot.repositories.find(source => source.repository === currentItem.repository)?.errors.length) {
        detailNotice("This repository is stale. Refresh successfully and reload details before applying changes.");
        return;
    }
    if (!$("#confirm-edit").checked) { detailNotice("Review the values and check the confirmation before applying."); return; }
    const item = currentItem;
    const selection = $("#issue-state")?.value;
    const input = type === "assign" ? {
        kind: item.kind, number: item.number, repository: item.repository,
        assignees: $("#assignees").value.split(",").map(value => value.trim()).filter(Boolean),
        expectedState: item.expectedState, confirmed: true,
    } : {
        number: item.number, repository: item.repository, state: selection === "OPEN" ? "OPEN" : "CLOSED",
        ...(selection === "OPEN" ? {} : { reason: selection }), expectedState: item.expectedState, confirmed: true,
    };
    saving = true;
    $("#detail-status").textContent = "Applying and verifying with GitHub...";
    for (const button of $("#detail-content").querySelectorAll("button")) button.disabled = true;
    try {
        currentItem = await action(type === "assign" ? "update_assignees" : "set_issue_state", input);
        dirty = false;
        renderDetail();
        $("#detail-status").textContent = "GitHub confirmed the change.";
        await refresh(true);
    } catch (error) {
        detailNotice(`${error.message} Reload details before applying another change.`);
        $("#detail-status").textContent = "Change not confirmed.";
        const reload = h("button", { class: "button", onclick: () => loadDetail(item) }, "Reload and reconcile");
        $("#detail-notice").append(" ", reload);
    } finally { saving = false; }
}
function closeDetail() {
    if (saving) { detailNotice("Wait for GitHub verification before closing."); return; }
    if (dirty && !window.confirm("Discard your unapplied changes?")) return;
    $("#detail").close();
}
$("#refresh").addEventListener("click", () => refresh(true));
$("#refresh-interval").addEventListener("change", event => setView({ refreshMinutes: Number(event.target.value) }));
$("#clear").addEventListener("click", () => setView({ person: "all", platform: "all", launchOnly: false, repository: "all" }));
$("#repository").addEventListener("change", event => setView({ repository: event.target.value }));
$("#dismiss-updates").addEventListener("click", () => { $("#updates").hidden = true; celebration.stop(); });
for (const button of document.querySelectorAll("[data-view]")) button.addEventListener("click", () => setView({ view: button.dataset.view }));
$("#launch").addEventListener("change", event => setView({ launchOnly: event.target.checked }));
$("#platform").addEventListener("change", event => setView({ platform: event.target.value }));
$("#days").addEventListener("change", event => setView({ days: Number(event.target.value) }));
$("#close-detail").addEventListener("click", closeDetail);
$("#detail").addEventListener("cancel", event => { event.preventDefault(); closeDetail(); });
document.addEventListener("visibilitychange", () => schedule());
refresh();
