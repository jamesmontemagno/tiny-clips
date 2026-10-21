// Momentum: stacked daily activity (commits, PRs, issues) for the team, one person, or everyone.
// One panel owns its controls, fetching, states and chart; the app creates one for the home card and one per profile.
import { MOMENTUM_DAYS, MOMENTUM_DEFAULT_DAYS, MOMENTUM_METRICS, canonLogin, shortRepo } from "/model.mjs";
import { ICONS } from "/icons.mjs";

const icon = name => `<svg class="octicon" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">${ICONS[name] ?? ""}</svg>`;
const esc = value => String(value ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;

const HEIGHT = 148;
const MARGIN = { left: 30, right: 6, top: 8, bottom: 22 };
const MIN_WIDTH = 160;
const REVEAL_MS = 1100;
const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)");
// The server buckets days in UTC, so dates are labelled in UTC too.
const axisDate = new Intl.DateTimeFormat([], { month: "short", day: "numeric", timeZone: "UTC" });
const shortDate = new Intl.DateTimeFormat([], { weekday: "short", month: "short", day: "numeric", timeZone: "UTC" });
const longDate = new Intl.DateTimeFormat([], { weekday: "long", month: "long", day: "numeric", timeZone: "UTC" });
const format = (formatter, date) => formatter.format(new Date(`${date}T00:00:00Z`));
const stackTotal = day => MOMENTUM_METRICS.reduce((sum, metric) => sum + day[metric.key], 0);
const fix = value => value.toFixed(1);

// A round, even ceiling so the three gridlines (0, half, top) all land on whole numbers.
function niceTop(max) {
    const step = max < 10 ? 2 : 2 * 10 ** (Math.floor(Math.log10(max)) - 1);
    return Math.max(2, Math.ceil(max / step) * step);
}

let sharedDays = MOMENTUM_DEFAULT_DAYS; // the chosen range follows you between the home card and profiles
let nextId = 0;

/**
 * @param host     element that becomes the panel (the panel renders its own header and body)
 * @param post     (action, input) => Promise, the app's server call
 * @param context  () => { settled, people: [{login, name}], repos: [nwo] } read on every sync
 * @param person   fix the panel to one login (profile mode: no person or repo pickers)
 */
export function createMomentumPanel({ host, post, context, person = null }) {
    const id = ++nextId;
    const fixed = person ? canonLogin(person) : null;
    const state = {
        days: sharedDays,
        scope: fixed ? `p:${fixed}` : "team",
        repo: "",
        data: null,       // last good response
        dataKey: "",      // request that produced it
        selectionKey: "", // controls and tracking the panel currently represents
        signature: "",    // what is drawn, so an identical refresh doesn't redraw or replay
        pending: "",      // request key in flight
        busy: false,      // a user-visible load (as opposed to a silent revalidation)
        token: 0,
        error: "",
        empty: null,      // { title, text } when there is nothing to ask for
        selected: 0,
        width: 0,
        animate: true,
        raf: 0,
        optionsKey: null,
    };

    const root = document.createElement("div");
    root.className = "mo";
    root.innerHTML = `
      <div class="box-head mo-head">
        <h2 id="mo-h-${id}">${icon("graph")}<span>${fixed ? "Contributions" : "Momentum"}</span></h2>
        <div class="mo-tools">
          <div class="mo-range" role="group" aria-label="Date range">${MOMENTUM_DAYS.map(days => `<button type="button" data-days="${days}" aria-pressed="false">${days}d</button>`).join("")}</div>
          <button class="btn-icon tip" type="button" data-act="replay" aria-label="Replay animation" hidden>${icon("play")}</button>
          <button class="btn-icon tip" type="button" data-act="refresh" aria-label="Refresh activity">${icon("sync")}</button>
        </div>
      </div>
      ${fixed ? "" : `<div class="mo-filters">
        <label class="mo-select"><span class="sr-only">Show activity for</span><select data-pick="scope"></select></label>
        <label class="mo-select" data-repo-pick hidden><span class="sr-only">Repository</span><select data-pick="repo"></select></label>
      </div>`}
      <div class="mo-body">
        <div class="mo-skel" aria-hidden="true"><span class="skeleton"></span><span class="skeleton"></span><span class="skeleton"></span></div>
        <div class="mo-message" hidden></div>
        <div class="mo-data" hidden>
          <div class="mo-stage">
            <div class="mo-chart"></div>
            <input class="mo-scrub sr-only" type="range" min="0" max="0" step="1" value="0" aria-label="Selected day">
          </div>
          <p class="mo-readout" aria-hidden="true"></p>
          <ul class="mo-legend"></ul>
          <div class="mo-notes"></div>
          <p class="mo-foot"></p>
        </div>
      </div>
      <div class="sr-only" role="status" aria-live="polite"></div>`;
    host.replaceChildren(root);
    host.setAttribute("aria-labelledby", `mo-h-${id}`);

    const $ = selector => root.querySelector(selector);
    const chartEl = $(".mo-chart");
    const scrub = $(".mo-scrub");
    const readoutEl = $(".mo-readout");
    const legendEl = $(".mo-legend");
    const notesEl = $(".mo-notes");
    const footEl = $(".mo-foot");
    const statusEl = $("[role=status]");
    const messageEl = $(".mo-message");
    const dataEl = $(".mo-data");
    const skelEl = $(".mo-skel");
    const replayBtn = $('[data-act="replay"]');
    const refreshBtn = $('[data-act="refresh"]');
    const scopeSelect = $('[data-pick="scope"]');
    const repoSelect = $('[data-pick="repo"]');
    const repoPick = $("[data-repo-pick]");

    const announce = text => { statusEl.textContent = text; };
    const summary = data => MOMENTUM_METRICS.map(metric => `${metric.label}: ${data.totals[metric.key]}`).join(", ");
    const dayText = day => `${format(longDate, day.date)}. ${MOMENTUM_METRICS.map(metric => `${metric.label}: ${day[metric.key]}`).join(", ")}`;

    // ---- Requests ----
    function buildRequest(force) {
        const request = { days: state.days };
        if (state.scope === "everyone") request.everyone = true;
        else if (state.scope.startsWith("p:")) request.person = state.scope.slice(2);
        if (state.repo) request.repository = state.repo;
        if (force) request.force = true;
        return request;
    }

    // Called whenever the app has settled data (and after any control change). Cheap: the server caches history.
    function sync({ force = false } = {}) {
        if (state.days !== sharedDays) state.days = sharedDays;
        const ctx = context();
        syncOptions(ctx);
        const key = JSON.stringify([state.days, state.scope, state.repo, ctx.people.map(p => p.login).sort(), [...ctx.repos].sort()]);
        if (key !== state.selectionKey) {
            state.selectionKey = key;
            state.token++;
            cancelAnimationFrame(state.raf);
            Object.assign(state, { pending: "", busy: false, data: null, dataKey: "", signature: "", error: "", empty: null });
        }
        if (!ctx.settled) { render({ redraw: false }); return; }
        const missing = !ctx.repos.length ? { title: "No repositories tracked", text: "Track a repository to chart its activity." }
            : !fixed && state.scope === "team" && !ctx.people.length ? { title: "No one tracked", text: "Track people to chart team momentum." }
            : null;
        if (missing) {
            state.token++;
            Object.assign(state, { pending: "", busy: false, data: null, dataKey: "", signature: "", error: "", empty: missing });
            render();
            return;
        }
        load(force, key);
    }

    async function load(force, key) {
        const request = buildRequest(force);
        if (!force && state.pending === key) return;
        // Same question as what is on screen: refresh quietly so the chart doesn't dim or replay.
        const silent = !!state.data && state.dataKey === key && !force;
        const token = ++state.token;
        state.pending = key;
        state.empty = null;
        state.error = "";
        state.busy = !silent;
        render({ redraw: false });
        if (!silent) announce("Loading activity…");
        try {
            const data = await post("get_momentum", request);
            if (token !== state.token) return;
            state.pending = "";
            state.busy = false;
            // Every repository failed and nothing was cached: a flat chart would be a lie.
            if (data.fetchedAt == null && data.errors.length) throw new Error(data.errors[0].message || "GitHub didn’t respond.");
            accept(data, key, silent);
        } catch (error) {
            if (token !== state.token) return;
            state.pending = "";
            state.busy = false;
            state.error = error.message || "Couldn’t load activity.";
            render({ redraw: false });
            announce(state.error);
        }
    }

    function accept(data, key, silent) {
        const signature = `${key}|${JSON.stringify(data.series)}`;
        const changed = signature !== state.signature;
        state.data = data;
        state.dataKey = key;
        if (changed) {
            state.signature = signature;
            state.animate = !silent;
            state.selected = data.series.length - 1;
        }
        render({ redraw: changed });
        if (!silent) announce(`Activity loaded. ${summary(data)}`);
    }

    // ---- Controls ----
    function syncOptions(ctx) {
        if (fixed) return;
        const key = JSON.stringify([ctx.people.map(p => [p.login, p.name]), ctx.repos]);
        if (key === state.optionsKey) return;
        state.optionsKey = key;
        if (state.scope.startsWith("p:") && !ctx.people.some(p => p.login === state.scope.slice(2))) state.scope = "team";
        if (state.repo && !ctx.repos.includes(state.repo)) state.repo = "";
        scopeSelect.innerHTML = `<option value="team">Whole team</option><option value="everyone">Everyone</option>`
            + (ctx.people.length ? `<optgroup label="People">${ctx.people.map(p => `<option value="p:${esc(p.login)}">${esc(p.name)}</option>`).join("")}</optgroup>` : "");
        scopeSelect.value = state.scope;
        repoSelect.innerHTML = `<option value="">All repos</option>${ctx.repos.map(repo => `<option value="${esc(repo)}">${esc(shortRepo(repo))}</option>`).join("")}`;
        repoSelect.value = state.repo;
        repoPick.hidden = ctx.repos.length < 2;
    }

    function renderControls() {
        for (const button of root.querySelectorAll("[data-days]")) button.setAttribute("aria-pressed", String(Number(button.dataset.days) === state.days));
        const working = !!state.pending;
        refreshBtn.classList.toggle("is-spinning", working);
        refreshBtn.disabled = working;
        replayBtn.hidden = !state.data || reduceMotion.matches;
    }

    root.addEventListener("click", event => {
        const button = event.target.closest("button");
        if (!button || button.disabled) return;
        if (button.dataset.days) {
            const days = Number(button.dataset.days);
            if (days !== state.days) { state.days = sharedDays = days; sync(); }
            return;
        }
        if (button.dataset.act === "replay") return replay();
        if (button.dataset.act === "refresh" || button.dataset.act === "retry") sync({ force: true });
    });
    scopeSelect?.addEventListener("change", () => { state.scope = scopeSelect.value; sync(); });
    repoSelect?.addEventListener("change", () => { state.repo = repoSelect.value; sync(); });

    // ---- Rendering ----
    function messageHTML() {
        if (state.empty) return `${icon("people")}<strong>${esc(state.empty.title)}</strong><span>${esc(state.empty.text)}</span>`;
        if (state.error && !state.data) return `${icon("alert")}<strong>Couldn’t load activity</strong><span>${esc(state.error)}</span><button class="btn" type="button" data-act="retry">Try again</button>`;
        return "";
    }

    function notesHTML() {
        const data = state.data;
        const note = (html, warn = true) => `<p class="mo-note">${warn ? icon("alert") : ""}<span>${html}</span></p>`;
        const out = [];
        if (state.error) out.push(note(`Couldn’t refresh, showing earlier data. <button class="mo-link" type="button" data-act="retry">Retry</button>`));
        for (const failure of data.errors) out.push(note(`<b>${esc(shortRepo(failure.repository))}</b>: ${esc(failure.message)}${failure.stale ? " (showing earlier data)" : ""}`));
        if (data.coverage.truncated.length) out.push(note(`Older activity in <b>${esc(data.coverage.truncated.map(shortRepo).join(", "))}</b> may be missing because history was capped.`));
        if (data.coverage.unattributed && state.scope !== "everyone") {
            const n = data.coverage.unattributed;
            out.push(note(`${plural(n, "item")} ${n === 1 ? "has" : "have"} no linked GitHub account and ${n === 1 ? "isn’t" : "aren’t"} counted.`, false));
        }
        return out.join("");
    }

    function footText() {
        const data = state.data;
        const where = state.repo ? shortRepo(state.repo) : "Tracked repositories";
        const time = data.fetchedAt ? new Date(data.fetchedAt).toLocaleTimeString([], { hour: "numeric", minute: "2-digit" }) : "";
        return `${where}${time ? ` · Updated ${time}` : ""}`;
    }

    function renderTotals() {
        const { totals } = state.data;
        const span = `last ${plural(state.data.days, "day")}`;
        legendEl.setAttribute("aria-label", `Totals for the ${span}`);
        legendEl.innerHTML = `<li class="mo-legend-title" aria-hidden="true">${esc(span[0].toUpperCase() + span.slice(1))}</li>`
            + MOMENTUM_METRICS.map(metric => `<li><i class="sw metric-${metric.key}"></i><span>${esc(metric.label)}</span><b>${totals[metric.key]}</b></li>`).join("");
    }

    function render({ redraw = true } = {}) {
        const ready = !!state.data;
        const mode = ready ? "ready" : state.empty ? "empty" : state.error ? "error" : "loading";
        root.dataset.state = mode;
        root.classList.toggle("is-stale", ready && state.busy);
        root.setAttribute("aria-busy", String(state.busy));
        dataEl.hidden = !ready;
        skelEl.hidden = mode !== "loading";
        messageEl.hidden = mode !== "empty" && mode !== "error";
        messageEl.classList.toggle("is-error", mode === "error");
        messageEl.innerHTML = messageHTML();
        renderControls();
        if (!ready) return;
        notesEl.innerHTML = notesHTML();
        footEl.textContent = footText();
        if (redraw) { renderTotals(); draw(); }
    }

    // ---- Chart ----
    function draw() {
        const data = state.data;
        if (!data) return;
        const width = Math.round(chartEl.clientWidth);
        if (width < MIN_WIDTH) { state.width = 0; return; } // hidden or not laid out yet; the resize observer redraws
        cancelAnimationFrame(state.raf);
        state.width = width;
        const animate = state.animate && !reduceMotion.matches;
        state.animate = false;
        const { series, totals } = data;
        const n = series.length;
        const plotW = width - MARGIN.left - MARGIN.right;
        const plotH = HEIGHT - MARGIN.top - MARGIN.bottom;
        const step = plotW / n;
        const barW = Math.max(1.5, Math.min(24, step * 0.72));
        const top = niceTop(Math.max(0, ...series.map(stackTotal)));
        const y = value => MARGIN.top + plotH - (value / top) * plotH;
        const cx = index => MARGIN.left + step * (index + 0.5);

        let svg = `<svg class="mo-svg${barW >= 6 ? " is-wide" : ""}" viewBox="0 0 ${width} ${HEIGHT}" width="${width}" height="${HEIGHT}" role="img" aria-label="${esc(`Daily activity over ${plural(n, "day")}. ${summary(data)}`)}">`;
        svg += `<defs><clipPath id="mo-clip-${id}"><rect class="mo-reveal" x="0" y="0" width="${width}" height="${HEIGHT}"/></clipPath></defs>`;
        for (const value of [0, top / 2, top]) {
            svg += `<line class="mo-grid" x1="${MARGIN.left}" x2="${width - MARGIN.right}" y1="${fix(y(value))}" y2="${fix(y(value))}"/>`
                + `<text class="mo-tick" x="${MARGIN.left - 6}" y="${fix(y(value) + 3.5)}" text-anchor="end">${value}</text>`;
        }
        svg += `<rect class="mo-cursor" x="${MARGIN.left}" y="${MARGIN.top}" width="${fix(step)}" height="${plotH}"/>`;
        svg += `<g clip-path="url(#mo-clip-${id})">`;
        series.forEach((day, index) => {
            let stacked = 0;
            for (const metric of MOMENTUM_METRICS) {
                const value = day[metric.key];
                if (!value) continue;
                const y1 = y(stacked + value);
                const y0 = y(stacked);
                svg += `<rect class="mo-seg metric-${metric.key}" x="${fix(cx(index) - barW / 2)}" y="${fix(y1)}" width="${fix(barW)}" height="${fix(Math.max(0.5, y0 - y1))}"/>`;
                stacked += value;
            }
        });
        svg += `</g>`;
        const labels = [[0, "start", MARGIN.left], [Math.floor((n - 1) / 2), "middle", null], [n - 1, "end", width - MARGIN.right]];
        for (const [index, anchor, x] of labels) {
            svg += `<text class="mo-xlabel" x="${fix(x ?? cx(index))}" y="${HEIGHT - 6}" text-anchor="${anchor}">${esc(format(axisDate, series[index].date))}</text>`;
        }
        if (!MOMENTUM_METRICS.some(metric => totals[metric.key])) svg += `<text class="mo-none" x="${fix(MARGIN.left + plotW / 2)}" y="${fix(MARGIN.top + plotH / 2 + 4)}" text-anchor="middle">No activity in this range</text>`;
        svg += `</svg>`;
        chartEl.innerHTML = svg;

        scrub.max = String(n - 1);
        select(state.selected);
        if (animate) reveal();
    }

    function reveal() {
        const rect = chartEl.querySelector(".mo-reveal");
        if (!rect) return;
        const width = state.width;
        const start = performance.now();
        rect.setAttribute("width", "0");
        const tick = now => {
            // The frame timestamp can predate `start`, which would give a negative width.
            const t = Math.max(0, Math.min(1, (now - start) / REVEAL_MS));
            rect.setAttribute("width", fix(width * (1 - (1 - t) ** 3)));
            if (t < 1) state.raf = requestAnimationFrame(tick);
        };
        state.raf = requestAnimationFrame(tick);
    }

    function replay() {
        if (!state.data) return;
        state.animate = true;
        draw();
    }

    // ---- Selection (pointer, touch, or the hidden slider for keyboards and screen readers) ----
    function select(index) {
        const data = state.data;
        if (!data) return;
        const n = data.series.length;
        const i = Math.max(0, Math.min(n - 1, index));
        state.selected = i;
        const cursor = chartEl.querySelector(".mo-cursor");
        if (cursor && state.width) cursor.setAttribute("x", (MARGIN.left + ((state.width - MARGIN.left - MARGIN.right) / n) * i).toFixed(1));
        const day = data.series[i];
        scrub.value = String(i);
        scrub.setAttribute("aria-valuetext", dayText(day));
        const parts = MOMENTUM_METRICS.filter(metric => day[metric.key]).map(metric => `<span class="mo-chip"><i class="sw metric-${metric.key}"></i>${esc(metric.label)} <b>${day[metric.key]}</b></span>`);
        readoutEl.innerHTML = `<b class="mo-date">${esc(format(shortDate, day.date))}</b>${parts.length ? parts.join("") : `<span class="mo-quiet">No activity</span>`}`;
    }
    function indexAt(clientX) {
        const left = chartEl.getBoundingClientRect().left;
        const plotW = state.width - MARGIN.left - MARGIN.right;
        return Math.floor(((clientX - left - MARGIN.left) / plotW) * state.data.series.length);
    }
    chartEl.addEventListener("pointermove", event => { if (state.data && state.width) select(indexAt(event.clientX)); });
    chartEl.addEventListener("pointerdown", event => { if (state.data && state.width) select(indexAt(event.clientX)); });
    scrub.addEventListener("input", () => select(Number(scrub.value)));

    // Redraw (without replaying) when the card changes width or comes back from being hidden.
    const observer = new ResizeObserver(() => {
        const width = Math.round(chartEl.clientWidth);
        if (!state.data || width === state.width) return;
        if (width < MIN_WIDTH) { state.width = 0; return; }
        draw();
    });
    observer.observe(chartEl);

    render({ redraw: false });

    return {
        sync,
        replay,
        destroy() {
            state.token++;
            cancelAnimationFrame(state.raf);
            observer.disconnect();
            host.removeAttribute("aria-labelledby");
            host.replaceChildren();
        },
    };
}
