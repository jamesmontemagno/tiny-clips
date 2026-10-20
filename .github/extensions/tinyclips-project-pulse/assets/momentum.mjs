const metrics = [
    ["commits", "Commits"], ["opened", "PRs opened"], ["merged", "PRs merged"],
    ["issuesOpened", "Issues opened"], ["completed", "Issues completed"],
];
const $ = selector => document.querySelector(selector);
function element(tag, attributes = {}, text) {
    const node = document.createElement(tag);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    if (text !== undefined) node.textContent = text;
    return node;
}
function svg(tag, attributes = {}, text) {
    const node = document.createElementNS("http://www.w3.org/2000/svg", tag);
    for (const [key, value] of Object.entries(attributes)) node.setAttribute(key, value);
    if (text !== undefined) node.textContent = text;
    return node;
}
const total = day => metrics.reduce((sum, [metric]) => sum + day[metric], 0);
const description = day => `${day.date}: ${metrics.map(([metric, label]) => `${day[metric]} ${label.toLowerCase()}`).join(", ")}`;

export function initMomentum({ action, selection }) {
    let result;
    let loading = false;
    let active = false;
    let frame;
    let chart;
    let cursor;
    let scrubber;
    let readout;
    let selectedScope;
    const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)");
    const loadButton = $("#load-momentum");
    const replayButton = $("#replay-momentum");
    const status = $("#momentum-status");
    function currentScope() { return JSON.stringify({ ...selection(), days: Number($("#momentum-days").value) }); }
    function stop() { cancelAnimationFrame(frame); frame = null; }
    function inspect(index) {
        if (!result) return;
        const day = result.series[index];
        scrubber.value = index;
        scrubber.setAttribute("aria-valuetext", description(day));
        readout.textContent = description(day);
        cursor.setAttribute("x1", 48 + (index + .5) * 884 / result.series.length);
        cursor.setAttribute("x2", cursor.getAttribute("x1"));
        for (const node of chart.querySelectorAll("[data-day]")) {
            node.setAttribute("aria-pressed", Number(node.dataset.day) === index ? "true" : "false");
        }
    }
    function replay() {
        stop();
        if (!result) return;
        const mask = chart.querySelector("#momentum-reveal");
        if (!active || reducedMotion.matches || document.hidden) {
            mask.setAttribute("width", "884");
            inspect(result.series.length - 1);
            status.textContent = "Timeline ready. Use the slider or select a day to inspect it.";
            return;
        }
        const started = performance.now();
        status.textContent = "Replaying daily activity...";
        function advance(now) {
            const progress = Math.max(0, Math.min(1, (now - started) / 1800));
            mask.setAttribute("width", String(progress * 884));
            inspect(Math.min(result.series.length - 1, Math.floor(progress * result.series.length)));
            if (progress < 1) frame = requestAnimationFrame(advance);
            else { frame = null; status.textContent = "Replay complete. Select a day for its breakdown."; }
        }
        frame = requestAnimationFrame(advance);
    }
    function revealAll() {
        stop();
        chart?.querySelector("#momentum-reveal").setAttribute("width", "884");
        if (status.textContent === "Replaying daily activity...") status.textContent = "Timeline ready. Select a day for its breakdown.";
    }
    function draw() {
        stop();
        const host = $("#momentum-chart");
        host.replaceChildren();
        if (result.errors.length === result.repositories.length) {
            host.append(element("p", { class: "empty" }, "Momentum is unavailable. No zero-activity chart is shown for failed repositories."));
            replayButton.disabled = true;
            return;
        }
        const max = Math.max(1, ...result.series.map(total));
        chart = svg("svg", { viewBox: "0 0 960 236", class: "momentum-svg", role: "group", "aria-label": "Daily activity chart. Select a day or use the slider for exact counts." });
        const defs = svg("defs");
        const clip = svg("clipPath", { id: "momentum-clip" });
        clip.append(svg("rect", { id: "momentum-reveal", x: 48, y: 12, width: 884, height: 188 }));
        defs.append(clip);
        chart.append(defs);
        for (const fraction of [0, .5, 1]) {
            const y = 200 - fraction * 176;
            chart.append(svg("line", { x1: 48, x2: 932, y1: y, y2: y, class: "chart-grid" }));
            chart.append(svg("text", { x: 36, y: y + 4, "text-anchor": "end", class: "chart-label" }, Math.round(max * fraction)));
        }
        const plot = svg("g", { "clip-path": "url(#momentum-clip)" });
        const step = 884 / result.series.length;
        result.series.forEach((day, index) => {
            const group = svg("g", {
                "data-day": index, role: "button", tabindex: index === 0 ? 0 : -1,
                "aria-label": description(day), "aria-pressed": "false", class: "chart-day",
            });
            group.append(svg("title", {}, description(day)));
            group.append(svg("rect", { x: 48 + index * step, y: 12, width: step, height: 188, class: "chart-hit" }));
            let height = 0;
            for (const [metric] of metrics) {
                const segment = day[metric] / max * 176;
                if (segment) group.append(svg("rect", { x: 48 + index * step + step * .16, y: 200 - height - segment, width: step * .68, height: segment, class: `chart-bar metric-${metric}` }));
                height += segment;
            }
            group.addEventListener("click", () => { revealAll(); inspect(index); });
            group.addEventListener("focus", () => inspect(index));
            group.addEventListener("keydown", event => {
                if (["Enter", " "].includes(event.key)) { event.preventDefault(); revealAll(); inspect(index); }
                if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(event.key)) {
                    event.preventDefault();
                    revealAll();
                    const target = event.key === "Home" ? 0 : event.key === "End" ? result.series.length - 1
                        : Math.max(0, Math.min(result.series.length - 1, index + (event.key === "ArrowLeft" ? -1 : 1)));
                    group.tabIndex = -1;
                    const next = chart.querySelector(`[data-day="${target}"]`);
                    next.tabIndex = 0;
                    next.focus();
                }
            });
            plot.append(group);
        });
        chart.append(plot);
        cursor = svg("line", { y1: 12, y2: 200, class: "chart-cursor", "aria-hidden": "true" });
        chart.append(cursor);
        for (const index of [0, Math.floor((result.series.length - 1) / 2), result.series.length - 1]) {
            chart.append(svg("text", { x: 48 + (index + .5) * step, y: 226, "text-anchor": index === 0 ? "start" : index === result.series.length - 1 ? "end" : "middle", class: "chart-label" }, result.series[index].date.slice(5)));
        }
        const scroll = element("div", { class: "chart-scroll" });
        scroll.append(chart);
        host.append(scroll);
        const legend = element("div", { class: "chart-legend", "aria-label": "Activity totals by type" });
        for (const [metric, label] of metrics) {
            const entry = element("span");
            entry.append(element("span", { class: `legend-swatch metric-${metric}`, "aria-hidden": "true" }),
                document.createTextNode(`${label}: ${result.series.reduce((sum, day) => sum + day[metric], 0)}`));
            legend.append(entry);
        }
        host.append(legend);
        readout = element("p", { id: "momentum-readout", class: "momentum-readout" });
        scrubber = element("input", { type: "range", min: 0, max: result.series.length - 1, value: result.series.length - 1, id: "momentum-scrubber", "aria-describedby": "momentum-readout" });
        scrubber.addEventListener("input", () => { revealAll(); inspect(Number(scrubber.value)); });
        host.append(element("label", { for: "momentum-scrubber" }, "Inspect a day (UTC)"), scrubber, readout);
        const details = element("details", { class: "momentum-data" });
        details.append(element("summary", {}, "Daily counts as a table"));
        const table = element("table");
        const header = element("tr");
        header.append(element("th", { scope: "col" }, "Date (UTC)"), ...metrics.map(([, label]) => element("th", { scope: "col" }, label)));
        const head = element("thead");
        head.append(header);
        const body = element("tbody");
        for (const day of result.series) {
            const row = element("tr");
            row.append(element("th", { scope: "row" }, day.date), ...metrics.map(([metric]) => element("td", {}, day[metric])));
            body.append(row);
        }
        table.append(head, body);
        details.append(table);
        host.append(details, element("p", { class: "muted chart-coverage" }, result.coverage));
        replayButton.disabled = false;
        inspect(result.series.length - 1);
        replay();
    }
    async function load() {
        if (loading) return;
        loading = true;
        stop();
        $("#momentum-content").hidden = false;
        loadButton.setAttribute("aria-expanded", "true");
        loadButton.disabled = true;
        replayButton.disabled = true;
        $("#momentum-days").disabled = true;
        $("#momentum-error").hidden = true;
        status.textContent = "Gathering history on demand...";
        const scope = currentScope();
        try {
            const next = await action("get_momentum", JSON.parse(scope));
            result = next;
            selectedScope = scope;
            $("#momentum-scope").textContent = `${next.errors.length ? "Partial history · " : ""}${next.selection.repository === "all" ? "All configured repositories" : next.selection.repository} · ${next.days} UTC days · ${next.selection.person} · ${next.selection.platform} platforms${next.selection.launchOnly ? " · 2.0 Launch only" : ""} · Gathered ${new Date(next.fetchedAt).toLocaleTimeString()}`;
            $("#momentum-error").textContent = next.errors.map(error => `${error.repository}: ${error.message}`).join(" ");
            $("#momentum-error").hidden = !next.errors.length;
            draw();
            if (next.errors.length) status.textContent = "Partial history: failed repositories are excluded. See the errors above.";
            if (currentScope() !== scope) scopeChanged();
        } catch (error) {
            $("#momentum-error").textContent = `${error.message}${result ? " The previous chart remains; its scope is shown below." : ""}`;
            $("#momentum-error").hidden = false;
            status.textContent = "History could not be loaded. Try Explore momentum again.";
        } finally {
            loading = false;
            loadButton.disabled = false;
            loadButton.textContent = "Refresh momentum";
            $("#momentum-days").disabled = false;
            replayButton.disabled = !result || result.errors.length === result.repositories.length;
        }
    }
    function scopeChanged() {
        if (result && currentScope() !== selectedScope) {
            revealAll();
            status.textContent = "Filters changed. Refresh momentum to chart this scope.";
        }
    }
    function setActive(value) {
        active = value;
        if (!active) revealAll();
    }
    loadButton.addEventListener("click", load);
    replayButton.addEventListener("click", replay);
    $("#momentum-days").addEventListener("change", load);
    document.addEventListener("visibilitychange", () => { if (document.hidden) revealAll(); });
    reducedMotion.addEventListener("change", () => { if (reducedMotion.matches) revealAll(); });
    return { scopeChanged, setActive };
}
