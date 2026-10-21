import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { runInNewContext } from "node:vm";
import { MOMENTUM_DAYS, MOMENTUM_DEFAULT_DAYS, MOMENTUM_METRICS, canonLogin, shortRepo } from "../model.mjs";

const source = await readFile(new URL("../assets/momentum.mjs", import.meta.url), "utf8");
const settle = () => new Promise(resolve => setImmediate(resolve));

function element(dataset = {}) {
    const listeners = new Map();
    const classes = new Set();
    return {
        dataset, hidden: false, disabled: false, innerHTML: "", textContent: "", clientWidth: 0,
        classList: {
            toggle(name, on) { if (on) classes.add(name); else classes.delete(name); },
            contains: name => classes.has(name),
        },
        setAttribute() {},
        removeAttribute() {},
        replaceChildren(...children) { this.children = children; },
        addEventListener(name, callback) { listeners.set(name, callback); },
        dispatch(name, target = this) { listeners.get(name)?.({ target }); },
        closest(selector) { return selector === "button" ? this : null; },
    };
}

// Exercise the real panel's async state without a browser layout or chart animation.
function panelFixture() {
    const selectors = [
        ".mo-chart", ".mo-scrub", ".mo-readout", ".mo-legend", ".mo-notes", ".mo-foot", "[role=status]",
        ".mo-message", ".mo-data", ".mo-skel", '[data-act="replay"]', '[data-act="refresh"]',
        '[data-pick="scope"]', '[data-pick="repo"]', "[data-repo-pick]",
    ];
    const nodes = new Map(selectors.map(selector => [selector, element()]));
    const root = element();
    const buttons = MOMENTUM_DAYS.map(days => element({ days: String(days) }));
    root.querySelector = selector => nodes.get(selector) ?? null;
    root.querySelectorAll = selector => selector === "[data-days]" ? buttons : [];
    nodes.get('[data-act="refresh"]').dataset.act = "refresh";
    const createMomentumPanel = runInNewContext(
        source.replace(/^import .+ from ".+";\r?$/gm, "").replace("export function createMomentumPanel", "function createMomentumPanel")
            + "\ncreateMomentumPanel;",
        {
            MOMENTUM_DAYS, MOMENTUM_DEFAULT_DAYS, MOMENTUM_METRICS, canonLogin, shortRepo, ICONS: {},
            matchMedia: () => ({ matches: true }),
            document: { createElement: () => root },
            ResizeObserver: class { observe() {} disconnect() {} },
            cancelAnimationFrame() {},
        },
    );
    const ctx = { settled: true, people: [{ login: "alice", name: "Alice" }, { login: "bob", name: "Bob" }], repos: ["o/first", "o/second"] };
    const requests = [];
    const panel = createMomentumPanel({
        host: element(),
        context: () => ctx,
        post: (action, input) => {
            const deferred = Promise.withResolvers();
            requests.push({ action, input: structuredClone(input), ...deferred });
            return deferred.promise;
        },
    });
    return {
        ctx, panel, requests, root,
        node: selector => nodes.get(selector),
        change(selector, value) {
            const select = nodes.get(selector);
            select.value = value;
            select.dispatch("change");
        },
        range(days) { root.dispatch("click", buttons.find(button => Number(button.dataset.days) === days)); },
        refresh() { root.dispatch("click", nodes.get('[data-act="refresh"]')); },
    };
}

function response(commits) {
    const totals = Object.fromEntries(MOMENTUM_METRICS.map(metric => [metric.key, metric.key === "commits" ? commits : 0]));
    return {
        days: 7, series: [{ date: "2026-10-20", ...totals }], totals,
        errors: [], coverage: { truncated: [], unattributed: 0 }, fetchedAt: "2026-10-20T12:00:00Z",
    };
}

for (const tracking of ["people", "repos"]) {
    test(`changing tracked ${tracking} replaces an in-flight Whole team / All repos request`, async () => {
        const f = panelFixture();
        f.panel.sync();
        if (tracking === "people") f.ctx.people = [{ login: "bob", name: "Bob" }];
        else f.ctx.repos = ["o/second"];
        f.panel.sync();
        assert.equal(f.requests.length, 2);
        f.requests[1].resolve(response(22));
        await settle();
        const legend = f.node(".mo-legend").innerHTML;
        assert.match(legend, /<b>22<\/b>/);
        f.requests[0].resolve(response(11));
        await settle();
        assert.equal(f.node(".mo-legend").innerHTML, legend, "the superseded response must not overwrite the new selection");
        f.panel.destroy();
    });
}

test("a tracking change invalidates history before the new tracking state has settled", async () => {
    const f = panelFixture();
    f.panel.sync();
    f.ctx.settled = false;
    f.ctx.repos = ["o/second"];
    f.panel.sync();
    f.requests[0].resolve(response(11));
    await settle();
    assert.equal(f.requests.length, 1, "wait for the tracking state before requesting new history");
    assert.equal(f.node(".mo-data").hidden, true);
    assert.equal(f.root.dataset.state, "loading");
    f.ctx.settled = true;
    f.panel.sync();
    assert.equal(f.requests.length, 2);
    f.requests[1].resolve(response(22));
    await settle();
    assert.equal(f.root.dataset.state, "ready");
    assert.match(f.node(".mo-legend").innerHTML, /<b>22<\/b>/);
    f.panel.destroy();
});

for (const selection of ["person", "repository", "days", "tracking"]) {
    test(`a failed ${selection} change never displays totals from the previous selection`, async () => {
        const f = panelFixture();
        f.panel.sync();
        f.requests[0].resolve(response(11));
        await settle();
        assert.equal(f.root.dataset.state, "ready");
        if (selection === "person") f.change('[data-pick="scope"]', "p:bob");
        else if (selection === "repository") f.change('[data-pick="repo"]', "o/second");
        else if (selection === "days") f.range(30);
        else { f.ctx.people = [{ login: "bob", name: "Bob" }]; f.panel.sync(); }
        assert.equal(f.requests.length, 2);
        assert.equal(f.node(".mo-data").hidden, true, "hide the old totals as soon as the selection changes");
        f.requests[1].reject(new Error("History unavailable"));
        await settle();
        assert.equal(f.root.dataset.state, "error");
        assert.equal(f.node(".mo-data").hidden, true, "the old chart and repository footer must remain hidden");
        assert.equal(f.node('[data-act="replay"]').hidden, true);
        assert.match(f.node(".mo-message").innerHTML, /History unavailable/);
        f.panel.destroy();
    });
}

for (const force of [false, true]) {
    test(`a failed same-selection ${force ? "forced refresh" : "revalidation"} retains its own earlier data`, async () => {
        const f = panelFixture();
        f.panel.sync();
        f.requests[0].resolve(response(11));
        await settle();
        const legend = f.node(".mo-legend").innerHTML;
        const foot = f.node(".mo-foot").textContent;
        if (force) f.refresh(); else f.panel.sync();
        assert.equal(f.node(".mo-data").hidden, false);
        f.requests[1].reject(new Error("History unavailable"));
        await settle();
        assert.equal(f.root.dataset.state, "ready");
        assert.equal(f.node(".mo-data").hidden, false);
        assert.equal(f.node(".mo-legend").innerHTML, legend);
        assert.equal(f.node(".mo-foot").textContent, foot);
        assert.match(f.node(".mo-notes").innerHTML, /showing earlier data/);
        f.panel.destroy();
    });
}

test("reordering tracking does not replace an equivalent pending request", () => {
    const f = panelFixture();
    f.panel.sync();
    f.ctx.people.reverse();
    f.ctx.repos.reverse();
    f.panel.sync();
    assert.equal(f.requests.length, 1);
    f.panel.destroy();
});
