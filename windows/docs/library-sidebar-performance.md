# Library sidebar aggregation

`ClipSidebarAggregate` builds tag/collection counts in one pass per sidebar
refresh, then sorts the distinct display values. `ClipsLibraryViewModel` uses
that same snapshot for navigation counts, tag choices, and detail suggestions.
There is no long-lived cache or invalidation state. Smart-collection counts,
navigation synchronization, selected-filter handling, list synchronization and
search remain unchanged.

Counts are per clip, not per tag occurrence. Comparisons remain ordinal
case-insensitive; the first encountered spelling is displayed. Loaded metadata
is not normalized or rewritten: duplicate tags count once per clip, blank/null
tag values retain the old display behavior, and blank filters count every clip.
Blank collections remain omitted. Each new snapshot reflects current metadata.

## Component comparison (2026-10-31)

Before source: `c605079a97d6a22ea57c7474ac5637986376b2d4`.
Remote main inspected before/after: `2fd61ca89b8af842497952e09ef03e9dfba51794`;
its intervening Studio changes do not touch the Library files in this change.

The local source-linked comparison compiles the production aggregate and
unchanged `ClipQueryEngine`, comparing the original sidebar callbacks against
the wired helper. Both include unchanged smart-collection counting. It does
not measure WinUI controls, navigation diffing, rendering, or full window opens.

| Clips / distinct tags | Before median / p95 ms | After median / p95 ms | Before / after managed bytes |
|---|---:|---:|---:|
| 0 / 0 | 0.0023 / 0.0129 | 0.0017 / 0.0198 | 2,552 / 2,408 |
| 12 / 12 | 0.0192 / 0.0328 | 0.0079 / 0.0185 | 23,816 / 6,000 |
| 1,000 / 20 | 1.4308 / 2.7925 | 0.3089 / 0.3439 | 2,439,872 / 39,720 |
| 1,000 / 1,000 | 69.5859 / 70.9157 | 0.5243 / 0.5496 | 120,413,624 / 368,504 |
| 10,000 / 20 | 14.2254 / 14.8344 | 3.1037 / 3.2378 | 24,328,144 / 327,720 |
| 10,000 / 10,000 | 7,038.5378 / 7,411.7953 | 7.1554 / 8.7945 | 12,003,989,416 / 3,440,256 |

All predeclared component targets passed: distinct 10,000 median <=50 ms,
p95 <=75 ms, managed allocation <=5 MiB/operation; ordinary 10,000 median <=5 ms.
Empty-case sub-millisecond tails are noisy; no small-case latency guarantee is
inferred from them. Lower allocation does not establish lower peak memory or
GC-pause time: distinct-case measured GC0/1/2 totals were 28,708/32/8 before and
16/14/14 after, and after boundary RSS reached 77,611,008 bytes versus 71,946,240
before. These are process samples including retained fixtures, not peak/retained
memory measurements.

## Reproduction and scope

The private assessment handoff preserves the complete comparison harness, raw
distributions, source hashes and patch as durable artifacts. It is not installed
in the app or CI. Restore its `sidebar-improvement/` directory next to the pinned
`tiny-clips/` checkout and the retained `performance-assessment/Measurement.cs`.
Apply the patch before preparing and running:

```bash
cd sidebar-improvement
python3 prepare.py
dotnet build Comparison.csproj -c Release --verbosity quiet
TZ=UTC DOTNET_TieredCompilation=0 dotnet bin/Release/net10.0/Comparison.dll comparison-1.json before-first
TZ=UTC DOTNET_TieredCompilation=0 dotnet bin/Release/net10.0/Comparison.dll comparison-2.json after-first
TZ=UTC dotnet run --project tests/Tests.csproj -c Release -- -noLogo
python3 summarize.py
```

Environment: Ubuntu 24.04 Linux x64, kernel 6.12.8+, AMD EPYC 9V74, two visible
virtual CPUs, approximately 7.46 GiB RAM, no swap, .NET SDK 10.0.403/runtime
10.0.14, workstation GC, Python 3.12.3. Shared virtual host, no CPU affinity or
exclusive-host guarantee. Release with tiered compilation disabled deliberately
stabilizes these component measurements; it is not the shipping configuration.

Two sequential fresh processes reverse before/after order. Each algorithm/cohort
gets five warmup batches, one full GC outside timing, then 20 measured
single-operation batches (40 pooled samples; nearest-rank p95). All observations
are retained, not filtered. Runtime reports total managed allocations, CPU time,
GC deltas and boundary RSS. No native allocations, GPU or GC-pause durations are
available.

Fixtures match the initial sidebar cohorts: 0, 12, 1,000, 10,000 clips; fixed UTC
2026-10-31 timestamps descending by one second; balanced capture types; one tag
per clip; favorite every seventh clip; large-file logical size every tenth clip.
Ordinary cohort has up to 20 tags and five collections, distinct cohort has N
tags and N collections. Paths are synthetic and are not scanned or decoded.
Fixtures/metadata creation and parity assertions are outside measured intervals.

Focused verification: four production aggregate tests compare old callbacks
against empty, loaded duplicate/blank/null/case-varied metadata, 100 seeded random
fixtures in both orders, mutations, snapshot isolation and single enumeration.
The private harness also compiles the actual before/after `RebuildSidebar` and
`SyncNavigationEntries` method bodies over UI-free hosts and compares navigation
counts, suggestions, entry identity and selected filters through mutations.
Together with the original query tests, 20 portable cases passed. This does not
exercise real WinUI bindings, dispatcher reentrancy or input/presentation.

Windows solution restore, app build and Core tests stop at NETSDK1100 on Linux.
No shipping build/test or native UI success is claimed for this patch. Before an
application-level improvement claim, run native Windows builds/tests and the
Library protocol in [first-open-responsiveness.md](first-open-responsiveness.md),
including refresh/search, grid/list, metadata edits, selected filters, keyboard
navigation and accessibility. Compare native x64 and ARM64 separately.

Only sidebar tag/collection aggregation is addressed here. Large navigation/list
diff costs remain, and no native end-to-end speedup or product superiority is
claimed. The shared changelog was left untouched to avoid another writer's
editor changes; this document records the focused behavior-preserving change.
