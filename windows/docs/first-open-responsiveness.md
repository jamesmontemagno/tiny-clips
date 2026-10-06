# First-open responsiveness

Settings, Clips Library and the screenshot editor expose an opt-in
`TinyClips-WindowOpen` EventSource. It does not write files, collect paths/content,
warm windows at startup, move XAML onto a worker, or change runtime settings.
With no listener, no per-window trace or diagnostic event subscriptions are created.

## Source findings and scoped change

- Settings already realizes and caches one section at a time. General is selected
  first; Analytics and Video device initialization stay section-specific.
  `SettingsViewModel` restoration and OS/credential I/O are separate from shell/XAML cost.
- The Library constructs its shell before scanning clips on first activation.
  `SyncNavigationEntries` emits one collection notification per insert/remove/move.
  Previously, each notification synchronously cleared and rebuilt **all** navigation
  controls. For a burst adding M distinct entries, that visits M(M+1)/2 dynamic entries,
  in addition to repeatedly rebuilding the fixed smart collections and headers.
  The shell now queues one normal-priority owner-thread rebuild per burst, using the
  latest entries. Initial navigation is still built synchronously. Count bindings,
  automation names/IDs and final selection synchronization are unchanged.
  Closing unsubscribes the notifications and suppresses queued rebuilds.
- The editor constructs its toolbar, inspector and canvas synchronously, then starts
  asynchronous image loading. Inspector attachment also initializes color, font,
  emoji and background controls. This is a profiling candidate, **not** evidence that
  those controls should be deferred. Image decode/preview and export are separate costs.
- Placement still uses the existing DPI-safe helpers and minimum sizes. No XAML,
  theme, title-bar, keyboard, or accessibility structure is changed.

UI-free tests exercise empty, small and large synthetic navigation bursts (0, 12,
10,000 entries), final ordering/removals, repeated/reentrant bursts, enqueue failure
and close-before-dispatch. These establish reduced rebuild work, not native Library
scan, XAML layout or first-interactive latency.

## Private collection protocol

Use a dedicated test account/VM and disposable synthetic folders/metadata. Do not
use personal captures, mutate the actual clipboard, or enable uploads. Schedule
native profiling exclusively; concurrent recording/profiling invalidates comparisons.
Launch with package identity using the existing [build/run commands](../README.md).
Attach a locally installed .NET EventPipe collector **before opening a window**, e.g.:

```powershell
dotnet-trace collect --process-id <app-pid> --providers TinyClips-WindowOpen
```

Keep traces, identifiers, machine details, paths and measured distributions private.
Other providers/native stacks can contain more information than this numeric schema.
Do not attach raw traces to public issues. Public reports should contain only code
findings and generic reproduction instructions.

For each architecture and candidate revision:

1. Use the same Release packaged flavor, disposable content and settings. Start a
   fresh process, attach the collector, and open only the target window. This measures
   process-cold first use, not an OS disk-cache-cold launch.
2. Close and reconstruct the target repeatedly in that process for warm opens.
   Merely foregrounding an existing window is a separate activation-only category.
3. Settings: record General and each first/cached section navigation separately.
   Library: separately run empty, small (12 clips) and large (10,000 clips) fixtures,
   including a many-distinct-tags/collections case; do not aggregate these populations.
   Editor: use fixed synthetic image dimensions and distinguish file and memory entry
   paths; do not take a desktop capture to create a fixture.
4. Repeat fresh launches and warm reconstructions (at least 20 per category). Record
   sample count, median, p95 and range before/after **privately**. Compare native x64
   with native x64 and native ARM64 with native ARM64; label emulation separately.
5. Run keyboard-only/UIA checks for section navigation, Library search/grid/list,
   selection/detail actions and editor tool shortcuts. Exclude clipboard/upload actions.
   Cover Light, Dark, Contrast, mixed-DPI movement/resizing, and immediate closure during
   loading/queued work. A build or UI-free unit test does not replace this matrix.

## Interpreting the events

`Timing` (event ID 1) has six numeric fields:
`openId`, `windowKind`, `eventKind`, `stage`, `elapsedMs`, `durationMs`.
IDs are process-local correlation tokens. Window kinds are Settings=1, Library=2,
ScreenshotEditor=3. Stages use the enum values in
`Infrastructure/WindowOpenTrace.cs`; event kinds are milestone=0, phase start=1,
phase stop=2. A phase stop records scope exit, **not** successful initialization.
Missing `ConstructorCompleted`/`ContentReady` or `ContentFailed` must not be reported
as a successful ready sample.

| Boundary | What it measures / does not measure |
| --- | --- |
| ConstructionRequested -> ConstructorEntered | Base WinUI `Window` construction plus managed field initialization; before the constructor body |
| ConstructorBody | Inclusive synchronous body; child scopes and the synchronous prefix of image loading can overlap |
| ServicesAndViewModel | DI resolution and VM construction/restoration together; not pure XAML |
| Xaml | `InitializeComponent`, including nested control constructors and binding callbacks |
| ControllerAndBindings | Editor controller attachment/control initialization |
| ChromeAndPlacement | Title-bar, native placement and minimum-size setup; Settings preserves its two separate existing setup steps |
| ThemeAndSubscriptions / SettingsSection / NavigationRebuild | Shell setup, first section creation, and individual full navigation rebuilds |
| NativeActivation / ForegroundRequest / DeferredActivation | Calls in `App` including their synchronous event handlers; the existing 100 ms delay before the deferred call is **not** CPU work |
| RootLoaded | First root `Loaded` callback, not an isolated measure/arrange duration or proof of paint |
| FirstActivated | First non-deactivated event, not proof that every action/content item is ready |
| LoadedDispatcherTurn / ActivatedDispatcherTurn | A single low-priority dispatcher callback after each milestone; a responsiveness proxy, **not** measured input/presentation latency |
| ContentLoad / ContentReady | Inclusive async wall time and availability of loaded data/bitmap, including I/O waits; not CPU time or proof of presentation |
| Closed | Ends the trace; later load/queued callback timing is suppressed and diagnostic handlers are removed |

Do not sum inclusive scopes. Subtracting constructor milestones is not a pure
layout measurement. To establish first-interactive/input-to-presentation latency,
pair the events with scripted UIA readiness probes and appropriate native/XAML
measure/arrange, scheduling and presentation stacks. Native waits and idle message-loop
time must not be described as computation. If they dominate, collect the stacks before
selecting a fix; do not add architecture branches or speculative GC/JIT/runtime tuning.

Native x64/ARM64 cold/warm distributions, native layout/paint attribution and the full
UI matrix remain required before claiming end-to-end latency improvement for #409.
The source-demonstrated Library batching change does not establish a Settings/editor
latency improvement.
