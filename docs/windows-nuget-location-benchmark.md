# TinyClips Windows NuGet location pilot

**Result:** Moving the global packages and HTTP cache from the hosted Windows OS volume to runner temp substantially reduced cold restore time. The comparable-hardware subset also improved whole-job runtime, but did not cross a rounded-minute boundary. This is a small pilot, not proof of a universal Windows or ARM64 speedup.

## Reproducible evidence

- Run: https://github.com/jamesmontemagno/tiny-clips/actions/runs/40139631527
- Measured source: `f8367967089335b4947cad912ce87617c71b1c0e`.
- Base main: `1fd74828f23aac604fc2d5b566164f46c744a41a`.
- Preserved evidence branch: `nuget-location-benchmark-evidence`.
- Dispatch (reproduction only; the bounded pilot is complete): `gh workflow run windows-build.yml --ref nuget-location-benchmark-evidence -f nuget_location_benchmark=true`.
- Exactly four fresh GitHub-hosted Windows x64 jobs, two per location, in one dispatch. All four restore/build/MSIX inspections succeeded. No retries or extra benchmark runs.
- SDK pinned to `10.0.402`, the version resolved by the recent main CI run. SDK stayed at `C:\Program Files\dotnet\sdk\10.0.402\`; setup/workloads/source/Release x64 direct restore and build flags were unchanged between variants.
- Global packages and HTTP caches started empty in all four jobs. No Actions package cache restore/save, no sequential warmed second arm, no build-output reuse. These are cold application-cache measurements, not restored-cache measurements; hosted network/CDN state is uncontrolled.
- Only timing/environment evidence was uploaded, with 14-day retention. No application package was published, uploaded, signed, installed, or launched.

## Observed environment

Every job used `windows-latest`, resolving to `windows-2025-vs2026`, image `20261021.278.1`, Windows Server 2025 Datacenter `10.0.26100`, x64, with 2 visible cores / 4 logical processors. Image matching does **not** guarantee matching hardware:

| Jobs | CPU model | Storage presented to Windows |
| --- | --- | --- |
| Default trials 1 and 2; runner-temp trial 1 | AMD EPYC 7763 | Separate C: and D: 150-GiB SAS "Msft Virtual Disk" devices |
| Runner-temp trial 2 | AMD EPYC 9V45 | C: 150-GiB NVMe "Virtual_Disk NVME Premium"; D: 220-GiB NVMe "Microsoft NVMe Direct Disk v2" |

C: and D: were NTFS on all four runners. These are reported virtual device characteristics, not an independent physical disk throughput test.

| Purpose | Resolved location |
| --- | --- |
| Workspace (all jobs) | `D:\a\tiny-clips\tiny-clips` |
| Runner temp | `D:\a\_temp` |
| User profile | `C:\Users\runneradmin` |
| Default global packages | `C:\Users\runneradmin\.nuget\packages\` |
| Default HTTP cache | `C:\Users\runneradmin\AppData\Local\NuGet\v3-cache` |
| Experimental global packages | `D:\a\_temp\nuget-location-40139631527-{trial}\packages` |
| Experimental HTTP cache | `D:\a\_temp\nuget-location-40139631527-{trial}\http-cache` |
| NuGet scratch, unchanged | `C:\Users\runneradmin\AppData\Local\Temp\NuGetScratch` |
| NuGet plugins cache, unchanged | `C:\Users\runneradmin\AppData\Local\NuGet\plugins-cache` |

The initial metadata writer accidentally used the same `temp` key for runner temp and NuGet scratch. Raw `environment.json` therefore labels NuGet scratch as `paths.temp`. Runner temp is independently visible in effective package/HTTP paths and the logged `EVIDENCE` environment variable. The branch fixes future telemetry to use `runnerTemp` and prefixed `nuget-*` keys; that telemetry-only correction was not another benchmark run.

## Timings

Restore/build/inspection use in-process stopwatches around the commands. Whole-job runtime uses GitHub job start/end timestamps, including setup, metadata collection, upload and post steps, but excluding queue time.

| Location / trial | Restore | Build + package | MSIX inspection | Whole job | Ceil(job seconds / 60) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Default 1 | 56.19 s | 144.35 s | 4.83 s | 237 s | 4 min |
| Default 2 | 50.93 s | 137.98 s | 4.91 s | 227 s | 4 min |
| Runner-temp 1, matching CPU/storage class | 12.95 s | 137.11 s | 4.48 s | 183 s | 4 min |
| Runner-temp 2, different CPU/storage class | 12.15 s | 93.96 s | 4.53 s | 137 s | 3 min |

| Metric | Default median [range], n=2 | Runner-temp median [range], n=2 |
| --- | ---: | ---: |
| Restore | 53.56 s [50.93-56.19] | 12.55 s [12.15-12.95] |
| Build + package | 141.17 s [137.98-144.35] | 115.53 s [93.96-137.11] |
| Inspection | 4.87 s [4.83-4.91] | 4.51 s [4.48-4.53] |
| Restore + build/package + inspection | 199.60 s [193.83-205.37] | 132.59 s [110.64-154.54] |
| Whole job | 232 s [227-237] | 160 s [137-183] |

The raw all-runner restore median decreased by 41.01 seconds (76.6%); whole-job median decreased by 72 seconds (31.0%). **Do not attribute the full whole-job or compilation median difference to cache location:** runner-temp trial 2 used a different CPU and storage class.

Restricting attention to the matching EPYC 7763 / SAS subset, runner-temp trial 1 restored in 12.95 seconds versus default's 50.93-56.19 seconds. Its build/package time, 137.11 seconds, was close to default's 137.98-144.35 seconds. The comparable whole job was 183 seconds versus default's 227-237 seconds: 44-54 seconds faster (49 seconds / 21.1% versus the default median). That comparison has only **one** runner-temp sample; network, VM load, placement and other variability remain uncontrolled. It supports a scoped x64 candidate, not a precise expected percentage.

Setup/overhead from GitHub step timestamps:

| Component | Default trials 1 / 2 | Runner-temp trials 1 / 2 |
| --- | ---: | ---: |
| Job setup | 1 / 1 s | 1 / 1 s |
| Checkout | 6 / 6 s | 5 / 5 s |
| Setup .NET | 3 / 4 s | 3 / 3 s |
| Environment/cache-location diagnostics | 14 / 14 s | 12 / 10 s |
| Evidence upload | 1 / 1 s | 1 / 1 s |
| Post-checkout | 3 / 2 s | 3 / 2 s |
| Remote cache transfer/save | None | None |
| Tests | Not run in this bounded packaging pilot | Not run in this bounded packaging pilot |

Creation of the two experimental directories itself took approximately 0.01 seconds. Diagnostics are measurement overhead, not a proposed production setup step; step timestamp rounding/gaps mean component sums need not equal whole-job timestamps.

Total observed job runtime was 464 seconds for defaults versus 320 seconds for runner-temp, with ceilings of 8 versus 7 job-minutes. **The apparent rounded-minute reduction came from the unmatched NVMe/9V45 runner. The comparable-hardware jobs all rounded to four minutes.** Do not claim a proven billed-minute reduction. This public repository's standard hosted compute does not establish a net-dollar saving; no bills or billing amounts are used here.

## Recommendation and scope

At the end of the analysis-first pilot, ordinary production builds retained their existing package locations. The opt-in benchmark left ordinary direct x64/ARM64 builds, Store validation, test suites, required check names/dependencies, signing/release gates and launch-smoke workflows intact. No PR, merge, release, Store publishing, disk formatting, Defender changes, SDK relocation, shared-cache deletion, or self-hosted modifications occurred during the pilot. The user subsequently authorized a separate production review PR; that follow-up removes the opt-in benchmark from its diff and applies guarded cache placement consistently. The runnable benchmark remains on the evidence branch above.

The evidence supports considering the simplest change for the **direct x64 GitHub-hosted Windows job only**: create package and HTTP-cache directories under the actual `RUNNER_TEMP`, set `NUGET_PACKAGES` and `NUGET_HTTP_CACHE_PATH` through `GITHUB_ENV` before restore, and do not add remote caching. Gate on `runner.os == 'Windows' && runner.environment == 'github-hosted'` and, within the existing direct matrix, `matrix.platform == 'x64'`. Resolve/record the target volume rather than assuming every future Windows runner temp is D: or faster.

ARM64 direct builds here cross-compile on x64 hosts, but their dependency graph and timings were not measured; native ARM64 smoke runners have different storage/setup. Store x64 was also unmeasured. Do not silently apply the measured gain to either, and do not overwrite the unrelated ARM smoke NVMe mode.

The existing test job already places global packages at `github.workspace/.nuget/packages`, on the observed D: workspace volume. Leave it and its package-only `actions/cache` untouched. No additional test benchmark was needed to establish the path applicability difference; this pilot does not measure test runtime or moving only the HTTP cache.

If a cached partition is later moved, set `actions/cache.with.path` to the **same effective `NUGET_PACKAGES` directory** before both restore and save, not the old workspace path. Use a fresh key namespace (for example `nuget-v2-...`) if path/partition semantics change; retain OS, host architecture, target architecture, SDK and dependency-input separation. Do not add the HTTP cache, NuGet configuration, credentials or build outputs to an indiscriminate cache. No existing key/path needs changing for this pilot because the test cache was not moved.

This is distinct from [PR #511](https://github.com/jamesmontemagno/tiny-clips/pull/511), which removed direct/Store remote package caching after transfer/post-step costs offset restore savings. Faster local placement and unhelpful remote cache transfer can both be true.

## Public-safe blog follow-up

Suggested text, not published:

> A cache hit is not automatically faster, but cache *location* is a different question. Inspired by [AwakeCoding's Windows NuGet investigation](https://awakecoding.com/posts/speeding-up-dotnet-builds-on-windows-nuget-cache/), I ran a four-job TinyClips WinUI pilot with fresh hosted runners and no remote package cache. Default NuGet packages and HTTP responses landed on C:, while runner temp was on a separate D: volume. In the comparable CPU/storage subset, cold restore took about 13 seconds instead of 51-56 seconds, and the whole direct build/package/inspection job took 183 seconds instead of 227-237 seconds. Those jobs still all rounded to four minutes. One additional runner-temp job was faster again, but had different CPU/NVMe hardware, so I would not credit that difference to cache placement. This is a promising local-path optimization, not evidence that we should bring back remote caches or claim a billing reduction.

Attach the canonical run link above, attribute the original hypothesis to AwakeCoding, and keep the sample-size/hardware caveat with the timings. It complements, rather than contradicts, the cache-transfer lesson in https://montemagno.com/reducing-github-actions-costs-ai-agents/. Blog edits remain with the coordinating session; this pilot did not modify or publish the blog.
