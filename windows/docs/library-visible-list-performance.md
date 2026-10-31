# Identical visible Library list fast path

`SyncVisibleClips` now returns after an ordered reference comparison when the
current and target lists have equal counts and identical item objects. The
no-op path is linear, allocation-free in the measured component, and emits no
collection notifications. Any count or reference/order difference enters the
original remove/move/insert algorithm unchanged.

This return is confined to `SyncVisibleClips`, not `ApplyQuery`: metadata updates,
sidebar rebuilding, selected-item checks, count/filter summaries and persistence
still run as before. No cache, indexed diff, search or editor changes are included.

The caller constructs the target from a path-keyed dictionary and a filtered,
sorted sequence of its values, so supported lists contain unique item references.
The sealed `ClipItemViewModel` uses reference equality. Equal metadata or names do
not make distinct item instances interchangeable. Duplicate-reference lists are
not produced by this caller: the legacy algorithm can reorder or throw on them.
Changed duplicate-reference inputs are tested for the same outputs, notifications
and exception behavior; this change does not attempt to support duplicate lists.

## Matched component results (2026-10-31)

Before source/main: `fa833d7e2e67493237f56d0c29a4bd14e63dd72e`.
The harness extracts both actual production method bodies, changing only method
visibility/override for UI-free hosts. It asserts that the fallback and all
surrounding VM source are byte-equivalent as decoded text. Production item/window
construction, bindings, collection consumers, dispatcher and rendering are not
measured. This is not a native UI speedup claim.

| Identical items | Before median / p95 ms | After median / p95 ms |
|---|---:|---:|
| 0 | 0.000004 / 0.000006 | 0.000006 / 0.000008 |
| 12 | 0.000647 / 0.000659 | 0.000067 / 0.000069 |
| 1,000 | 2.0147 / 2.1388 | 0.0051 / 0.0054 |
| 10,000 | 195.3994 / 220.7020 | 0.0515 / 0.0821 |

All measured operations allocated zero managed bytes and emitted zero collection
notifications in both paths. Original thresholds pass without weakening:
10,000-item median <=2 ms and p95 <=3 ms, zero no-op notifications. Empty/small
timings are resolution-sensitive; do not infer UI latency from nanosecond means.

The original assessment's 10,000-item median/p95 was 193.138/197.890 ms with
72 bytes/op. The matched current baseline above is authoritative for this change;
the earlier measurement wrapper returned `Task.FromResult` for synchronous work,
whereas this harness calls the synchronous methods directly, eliminating wrapper
allocation. No claim that this guard saves 72 production bytes. Shared-host
timing variance also differs; no sample filtering or tightened historical claim.

Two fresh sequential processes reverse algorithm order. Each size/algorithm has
five warmup batches, a forced GC outside timing, then 20 measured batches
(40 pooled samples, nearest-rank p95). Sizes 0/12 use 100 operations per batch;
1,000/10,000 use one. Fixtures are deterministic objects with synthetic IDs/names;
construction and parity assertions are outside timed regions. Raw evidence
retains wall/CPU time, managed allocations, GC deltas and boundary RSS, which is
not peak memory. Release, UTC, tiered compilation disabled, workstation GC,
.NET SDK 10.0.403/runtime 10.0.14, Ubuntu 24.04 Linux x64, two visible vCPUs.
The shared host is not an exclusive or native Windows performance environment.

## Reproduction and regression coverage

The standalone test project adds no dependency or shared project changes and is
not wired into the shipping solution/CI. Python 3 and .NET 10 are required; retain
the pinned base commit in Git history. From the repository root:

```sh
cd windows/tests/LibraryVisibleSync
python3 prepare.py
dotnet build -c Release --verbosity quiet
TZ=UTC DOTNET_TieredCompilation=0 dotnet bin/Release/net10.0/LibraryVisibleSync.dll obj/comparison-1.json before-first
TZ=UTC DOTNET_TieredCompilation=0 dotnet bin/Release/net10.0/LibraryVisibleSync.dll obj/comparison-2.json after-first
python3 summarize.py
```

For tests without benchmarks, run `dotnet run -c Release` after preparation.
Generated methods, source/excerpt hashes, raw JSON and summary live in ignored
`obj/`; preserve them separately before cleaning the build directory.

278 cases passed in each process: empty/small/1,000/10,000 no-ops, aliasing, same
payload/different references, add/remove/reorder/replacement, 250 seeded random
transitions, sequential filter transitions, metadata retained on the same object
and changed duplicate inputs (including the baseline's exception). Differential
checks compare every final item by reference plus the complete ordered
`CollectionChanged` action/indices/item-identity sequence and collection
`PropertyChanged` names. These use UI-free item/host stand-ins, not real WinUI.

Actual Core/Core.Tests Debug x64 compilation passes with zero warnings/errors:

```sh
dotnet build windows/tests/TinyClips.Core.Tests/TinyClips.Core.Tests.csproj -c Debug -p:Platform=x64 -p:EnableWindowsTargeting=true
```

This does not compile the actual App VM; its extracted method compiles in the
standalone harness. Real WinUI compilation and WindowsDesktop test execution
remain unavailable on Linux. The baseline's post-merge Windows run
[41209763565](https://github.com/jamesmontemagno/tiny-clips/actions/runs/41209763565)
passed (Core: 2,514 passed/four skipped; App: 245 passed), but predates this guard
and does not qualify it.

Before any application-level claim, obtain publication authority for Windows CI
and verify actual App builds/tests. Then follow
[first-open-responsiveness.md](first-open-responsiveness.md) on authorized Windows:
repeated refresh/search with unchanged results, metadata-only updates, changed
filters/reorder/replacement, retained selected objects/scroll position, keyboard
and accessibility behavior. Native UI/end-to-end latency is explicitly unverified.
