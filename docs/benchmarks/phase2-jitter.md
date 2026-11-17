# Phase 2 gate: pacing-thread jitter (`fmp bench jitter`)

Gate (design doc, Phase 0 / milestone 0, and `docs/implementation-plan.md` Phase 2): **runtime jitter under
0.5 ms p99** on the pacing thread, and **zero gen0 collections in a 10-minute steady state**. The design doc's
switch rule: move the scheduler and present loop to native code only if more than 0.5 ms p99 of pacing-thread
jitter *comes from the runtime*.

## Verdict

- **The design doc's gate is met everywhere it was measured, with a wide margin, and nothing measurable comes from
  the runtime.** p99 wake lateness of the realistic pacing loop on the published NativeAOT binary with
  `clock_nanosleep` was 17 to 22 µs on the desktop, at 120 and 240 Hz, in every 60 s and 10-minute run here, and
  31.9 µs in the reviewer's own 10-minute run with the desktop in use. It was 12 to 13 µs even with CPU hogs on the
  pacing thread's cores. The gate is 500 µs.
- **Zero GC collections and zero bytes allocated** on the pacing and source threads in every run, including four
  10-minute runs (three here, one by the reviewer). This covers both the non-concurrent GC of the first runs and the concurrent GC with
  `SustainedLowLatency` that `fmp` uses now.
- **The managed per-slot work adds no measurable jitter.** With the timer-only and full loops interleaved in 5 s
  blocks, the p99 difference is +0.2 to +2.0 µs. That is within the method's resolution of a few µs. In the first
  matrix, which ran the loops back to back, the resolution was only about ±10 µs (see "Attribution resolution"). On
  the desktop the work itself takes 1.7 to 3.7 µs per slot at p50 and 6 to 20 µs at p99 (less under contention,
  where the cores never idle). The switch rule is not triggered.
- **The stricter check added after the review, zero missed slots, is not guaranteed on a shared desktop without
  real-time scheduling.** It held in the reviewer's 10-minute run (0 missed, desktop in use, no other agent). It
  failed in two of mine: 14 and 2 of 72000 slots missed, while other agents' builds and GPU test hosts ran or
  started; the timer-only loop running alongside missed 18 and 2. Every skipped slot was a single wake 1.7 to 4.2 ms
  late (up to 6.3 ms in the timer-only loop) costing one slot, with normal per-slot work and no collections. Late
  wakes show no systematic excess in the full loop: over 0.5 ms there were 93 timer-only against 61 full in the
  loaded run, and 13 against 33 in the idle one. The reviewer's native C `clock_nanosleep` loop beside the bench was
  hit at the same rate. These misses are the OS's; they are the case for real-time priority, not for native code.
  Real-time priority is not available on this machine (see below), so whether it removes them is still unmeasured.
  With a CPU hog on the pacing thread's core, `SCHED_OTHER` misses 1 to 4 slots per 30 s.
- **Recommended default: `sleep`** (`clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME)`), per-thread timer slack of
  1 ns, default priority unless real-time is granted, no core pinning. Do not use the hybrid wait as a default (see
  "Reading the results").

## Changes after the independent review

The review approved the layer and the numbers. It asked for these changes, which also changed how the bench
measures, so some numbers below come from the first method and some from the second:

1. **Skipped slots now count.** Before, a late wake gave one lateness sample however many slots it skipped, and
   skipped slots did not fail the run. A run frozen eight times for 110 ms passed with p99 26.7 µs. Now each skipped
   slot adds its own sample (the wake that ended the gap, minus that slot's deadline). The run also fails when the full
   loop misses more slots than `--max-missed` (default 0). The same freeze now fails: 105 slots skipped, p99 97.5 ms,
   exit 1. Every run of the first matrix had 0 skipped slots, so its numbers are unchanged. The first extra runs are
   re-evaluated below.
2. **`fmp` runs with concurrent (background) workstation GC**, as the design doc asks. The playback scope now really
   obtains `SustainedLowLatency` (and restores `Interactive` afterwards). The first matrix, the first extra runs and
   the first 10-minute run used non-concurrent GC, which kept `Batch`. With no allocation there is no collection in
   either mode. Injected allocations and `GC.Collect` are still detected, which tests cover in both modes.
3. **The timer-only and full loops alternate in 5 s blocks** (100 ms after each switch is not measured), instead of
   one after the other. This is what makes the attribution resolution a few µs.
4. Timer slack is always set explicitly. A thread inherits its creator's slack, so `0` did not reliably mean the
   kernel default; use `--timer-slack-ns 50000` for that.
5. Corrections to this document.
6. After the re-check: the verdict is printed as two lines, the design doc's runtime-jitter gate (p99, GC,
   allocation) and the missed-slot budget, with the timer-only loop's misses beside the full loop's. The exit code is
   1 if either fails. The concurrent-GC probe runs once and is cached.

## What is measured

`fmp bench jitter` (NativeAOT, `artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp`):

- **Full loop.** A dedicated pacing thread runs the real `FrameScheduler` (fixed refresh, app default settings,
  synthetic generation time d = 1.2 ms) against the real clock. For each output slot it waits with the platform
  `DeadlineWaiter` until `NextDecisionTime`, then drains the frame ring into `OnFrameArrived`, calls `Schedule`,
  reports d for generated slots and writes a `FrameTimelineRecord` into the real `TelemetryRing`. A second dedicated
  "source" thread produces 60 fps unique frames on an exact grid through the real `SpscRing<FrameArrival>`, and the
  main thread drains the telemetry ring every 10 ms, as the telemetry writer will.
- **Timer-only loop.** The same pacing thread runs the same wait loop with the scheduler and telemetry removed (it
  only drains and discards the ring), on the same vblank grid and lead, with the source thread running throughout.
  It measures what the OS alone delivers. It now alternates with the full loop in 5 s blocks (full loop first). The
  first block of each loop carries a 1 s warm-up, and 100 ms after every switch is not measured. When the full loop
  resumes, the scheduler sees the gap as a hold and catches up its vblanks inside those 100 ms.
- **Wake lateness** = time observed on waking minus the requested absolute deadline (never negative; the waiter
  never returns early). There is one sample per slot, including the slots the loop came too late for. **Per-slot
  work** = wake to telemetry record written. **Missed** = slots whose display time passed before the loop got to
  them (the scheduler's skipped vblanks) plus slots whose work finished after their display time.
- GC counts (`GC.CollectionCount`) and the pacing thread's `GC.GetAllocatedBytesForCurrentThread()` are read by the
  pacing thread from its first measured slot to the end of the run. Percentiles are exact nearest-rank over every
  slot.
- Two checks, printed on separate lines. The **jitter gate** (the design doc's) fails if the full loop's p99 wake
  lateness exceeds 0.5 ms, or on any collection or hot-thread allocation. The **missed-slot budget** fails if the
  full loop misses more slots than `--max-missed` (default 0), and the timer-only loop's misses are printed beside
  it. The exit code is 1 if either fails.

## Machine and conditions

| Item | State |
| --- | --- |
| CPU | AMD Ryzen 9 9950X3D, 16 cores / 32 threads |
| Kernel | Linux 7.3.4-arch1-2, `PREEMPT_DYNAMIC` (the active preemption mode is not readable without root) |
| Clock | clocksource `tsc`; `clock_getres(CLOCK_MONOTONIC)` = 1 ns; `clock_gettime` read cost 20 ns from managed code |
| Power | amd-pstate EPP, governor `powersave`, EPP `balance_performance`; cpuidle `acpi_idle` with C1 (1 µs), C2 (18 µs), C3 (350 µs declared exit latency). `/dev/cpu_dma_latency` is root-only, not used |
| GC, re-runs | workstation, **concurrent**; the playback scope requested and **obtained `SustainedLowLatency`**, then restored `Interactive` (printed by every run) |
| GC, first runs | workstation, non-concurrent; the scope's `SustainedLowLatency` request was not applicable, so the mode stayed `Batch` |
| Threads | `SCHED_OTHER`, nice 0, unpinned (except where stated), timer slack 1 ns (see below) |
| Desktop | Plasma 6.8.2 Wayland session with Discord, Chrome and two Wine apps open (together about half a core) |
| Other load | Other agents built .NET projects and ran GPU test hosts intermittently throughout. 1-minute load average during the first matrix: 0.08 to 2.05, median 0.63. During the re-runs: 0.04 to 5.3, median 1.65, starting right after another reviewer's 48-thread load test ended. Each run below states its own conditions |

### Real-time scheduling on this machine

`--rt` asks for `SCHED_FIFO` priority 10 on both threads and, if that is refused, nice -10. **Both were refused**,
and the threads ran as `SCHED_OTHER` nice 0. The bench (and `ThreadSchedulingReport`) states it:

```
refused real-time: EPERM: SCHED_FIFO priority 10 needs CAP_SYS_NICE or RLIMIT_RTPRIO >= 10 (it is 0)
refused nice: EACCES: nice -10 needs CAP_SYS_NICE or RLIMIT_NICE >= 30 (it is 0)
limits RLIMIT_RTPRIO 0, RLIMIT_NICE 0 (no negative nice), CAP_SYS_NICE no, rtkit not installed
```

So the `--rt` rows below are repeat measurements of the default configuration. (KWin itself runs `SCHED_RR` 1
through its file capability. The xdg-desktop-portal `Realtime` interface exists but reports
`MaxRealtimePriority` 0, because it forwards to rtkit, which is not installed.)

To allow real-time scheduling, the owner of the machine can do either of the following:

1. **An rtprio limit (preferred).** Either install `realtime-privileges` and `gpasswd -a adam realtime`, or add
   `/etc/security/limits.d/99-fmp.conf` with `adam - rtprio 98` (and `adam - nice -10` for the nice fallback). Then
   log in again and check `ulimit -r` in a new terminal. Plasma starts apps from the systemd user manager, so if
   `ulimit -r` still says 0 there, add `LimitRTPRIO=98` in a drop-in for `user@.service`. `ThreadScheduling` uses
   an rtprio limit automatically. It lowers the requested priority to the limit if needed and reports that it did.
2. **`sudo setcap cap_sys_nice+ep fmp`** on the binary. It must be redone after every publish. File capabilities
   also make glibc ignore `LD_LIBRARY_PATH`, which the VFX SDK under `lib/` currently relies on, so option 1 is the
   better fit.

rtkit (`pacman -S rtkit`) would also work, but only through its D-Bus API, which `fmp` does not call yet. Under
rtkit the thread also needs `RLIMIT_RTTIME`, and the priority is capped at its configured maximum.

**Is it needed?** Not for the design doc's p99 gate, which passes in every configuration, contention included. It
is the remedy to try for the misses: with a CPU-bound thread on the pacing core, or during other heavy activity,
`SCHED_OTHER` wake-ups are occasionally 1 to 6 ms late, and a few slots are skipped. `SCHED_FIFO` preempts normal
work at once, which addresses the scheduling part. Whether it also removes the multi-millisecond delays seen while
GPU test hosts ran cannot be measured here until real-time priority is granted. Some of those delays may be kernel
or driver sections that no priority overrides. For the live app (a game console feeding a capture card while the
desktop does other things), granting an rtprio limit is worth it, and `fmp bench jitter --rt` will show the
difference.

### Timer slack

Normal threads get 50 µs of timer slack: the kernel may fire their timers up to 50 µs late so that wake-ups can be
batched. `ThreadSchedulingRequest.TimerSlackNs` defaults to 1 ns (`prctl(PR_SET_TIMERSLACK)`, per thread,
unprivileged). Measured, sleep at 120 Hz (first method; the 50 µs row ran as `--timer-slack-ns 0`, and both threads
reported 50000 ns obtained):

| Timer slack | Phase | p50 | p90 | p99 | p99.9 | max (µs) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 50 µs (kernel default) | timer only | 58.3 | 66.6 | 68.5 | 73.4 | 105.6 |
| 50 µs (kernel default) | full loop | 56.1 | 66.5 | 68.4 | 71.6 | 81.8 |
| 1 ns (`fmp` default) | timer only | 15.0 | 16.6 | 18.4 | 21.6 | 28.9 |
| 1 ns (`fmp` default) | full loop | 15.1 | 16.8 | 18.6 | 23.9 | 32.5 |

That is 50 µs of latency removed for free.

## Results: the first matrix

First method: the timer-only loop ran for 61 s, then the full loop for 61 s; non-concurrent GC. 60 s measured per
phase after 1 s warm-up, 1 ns timer slack, unpinned. This used the binary published at 03:21, which differed from the
one used for the first 10-minute run only in the `--wait` default (then `hybrid`) and in help and advice text; every
run names its wait strategy explicitly. Hybrid used a 200 µs spin window. Wake lateness and work in µs. "rt" means
`--rt` was requested and refused (see above).

Every run had **0 GC collections, 0 B allocated on the pacing and source threads, 0 skipped slots** (so counting
skipped slots changes nothing here), 0 telemetry records dropped, 0 source frames dropped, and **passed**.

| Rate | Wait | Prio | Phase | Slots | p50 | p90 | p99 | p99.9 | max | Work p50 / p99 / max | Missed |
| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| 120 | sleep | default | timer only | 7200 | 15.0 | 16.6 | 18.4 | 21.6 | 28.9 | 0.3 / 0.6 / 1.0 | 0 |
| 120 | sleep | default | **full loop** | 7200 | 15.1 | 16.8 | **18.6** | 23.9 | 32.5 | 3.7 / 9.7 / 18.6 | 0 |
| 120 | sleep | rt | timer only | 7200 | 14.9 | 17.5 | 21.1 | 30.6 | 51.4 | 0.4 / 0.9 / 8.6 | 0 |
| 120 | sleep | rt | **full loop** | 7200 | 14.8 | 17.6 | **20.9** | 30.3 | 42.9 | 2.8 / 11.1 / 41.7 | 0 |
| 120 | hybrid | default | timer only | 7200 | 0.0 | 0.0 | 0.1 | 0.1 | 15.4 | 0.3 / 1.0 / 4.5 | 0 |
| 120 | hybrid | default | **full loop** | 7200 | 0.0 | 0.0 | **0.1** | 0.1 | 1.7 | 2.9 / 10.8 / 19.7 | 0 |
| 120 | hybrid | rt | timer only | 7200 | 0.0 | 0.0 | 0.1 | 0.1 | 9.0 | 0.3 / 0.8 / 1.5 | 0 |
| 120 | hybrid | rt | **full loop** | 7200 | 0.0 | 0.0 | **0.1** | 0.1 | 1.2 | 3.2 / 11.4 / 22.3 | 0 |
| 240 | sleep | default | timer only | 14400 | 11.3 | 17.1 | 18.8 | 26.5 | 53.5 | 0.3 / 0.7 / 1.4 | 0 |
| 240 | sleep | default | **full loop** | 14400 | 3.7 | 12.2 | **16.7** | 41.2 | 163.1 | 1.8 / 6.2 / 61.0 | 0 |
| 240 | sleep | rt | timer only | 14400 | 13.4 | 17.3 | 19.2 | 29.8 | 49.3 | 0.3 / 0.6 / 1.6 | 0 |
| 240 | sleep | rt | **full loop** | 14400 | 14.2 | 17.3 | **19.0** | 30.3 | 41.6 | 1.8 / 7.7 / 14.2 | 0 |
| 240 | hybrid | default | timer only | 14400 | 0.0 | 0.0 | 0.1 | 0.1 | 50.8 | 0.3 / 0.8 / 22.5 | 0 |
| 240 | hybrid | default | **full loop** | 14400 | 0.0 | 0.0 | **0.1** | 0.1 | 11.5 | 1.9 / 9.2 / 20.1 | 0 |
| 240 | hybrid | rt | timer only | 14400 | 0.0 | 0.0 | 0.1 | 0.1 | 1095.6 | 0.3 / 0.8 / 63.4 | 0 |
| 240 | hybrid | rt | **full loop** | 14400 | 0.0 | 0.0 | **0.1** | 4.2 | 774.9 | 1.7 / 8.3 / 128.2 | 0 |

The scheduler made correct decisions throughout: 120 Hz gave 3600 real and 3600 generated slots, and 240 Hz gave
3600 real and 10800 generated (M = 2 and M = 4). The only exception was in the last run: 2 decisions came too late
for generation and kept the previous image (reason `Late`) instead of missing their vblank, at the stalls noted under
"Tails". The scheduler learnt a wake-lateness p99 of 16 to 27 µs with sleep (5 to 6 µs with hybrid; its measurement
includes the ring drain), and its decision lead settled at 1.71 to 1.73 ms (p99 of d, plus wake lateness, plus the
0.5 ms margin).

**Attribution resolution of this matrix.** The p99 differences between the two phases (-2.1 to +0.1 µs) are smaller
than the drift between phases that ran a minute apart: in sleep-default-240 the p50 moved from 11.3 µs (timer only)
to 3.7 µs (full loop). These runs therefore bound our contribution only to about ±10 µs. That is still 50 times
below the gate; the interleaved re-runs below narrow it.

## Extra runs (first method)

240 Hz unless stated, `SCHED_OTHER` nice 0, 1 ns timer slack, non-concurrent GC. Percentiles recomputed with skipped
slots counted. This changed only the hybrid-under-contention timer-only p99, from 1831.8 to 1758.2 µs. "Verdict"
applies today's rule, including the missed-slot budget of 0.

| Run | Phase | p50 | p90 | p99 | p99.9 | max (µs) | Over 0.5 ms | Missed | Verdict |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| hybrid, 50 µs spin, 60 s | timer only | 0.0 | 0.0 | 0.1 | 0.1 | 17.3 | 0 | 0 | |
| | full loop | 0.0 | 0.0 | 0.1 | 0.1 | 33.6 | 0 | 0 | pass |
| sleep, pinned (pacing CPU 30, source CPU 31), 60 s | timer only | 15.9 | 17.4 | 18.8 | 197.4 | 1792.7 | 7 | 1 | |
| | full loop | 16.2 | 17.5 | 18.7 | 25.8 | 586.9 | 1 | 0 | pass |
| sleep, pinned, a nice-0 CPU hog on each of CPUs 30 and 31, 30 s | timer only | 11.2 | 11.8 | 12.1 | 1478.1 | 3302.3 | 15 | 4 | |
| | full loop | 11.2 | 11.8 | 12.4 | 1339.6 | 2677.5 | 19 | 4 | **fail** (4 missed) |
| hybrid (200 µs), pinned, nice-0 hogs on CPUs 30 and 31, 30 s | timer only | 0.0 | 712.4 | 1758.2 | 2529.2 | 2696.2 | 896 | 77 | |
| | full loop | 0.0 | 661.9 | **1464.2** | 2563.8 | 2976.5 | 901 | 1 | **fail** (p99, 1 missed) |
| sleep, pinned, nice-19 hogs on CPUs 30 and 31, 30 s | timer only | 11.3 | 11.8 | 12.1 | 874.0 | 2715.3 | 8 | 3 | |
| | full loop | 11.3 | 11.8 | 12.1 | 897.4 | 2723.1 | 9 | 2 | **fail** (2 missed) |

The hogs were confined to CPUs 30 and 31, so the rest of the machine (and the other agents) were unaffected. All
these runs also had zero collections and zero hot-thread allocations.

## Re-measurement after the review

Second method: concurrent GC with `SustainedLowLatency` obtained, loops interleaved in 5 s blocks, skipped slots
counted, missed-slot budget 0. The binary was published from the final code at 09:35 (with the other engineer's
`fmp pattern` changes linked in, which the bench does not use). Every run had 0 GC collections, 0 B allocated on the
pacing and source threads, and 0 telemetry records or source frames dropped.

| Run | Phase | Samples | p50 | p90 | p99 | p99.9 | max (µs) | Work p50 / p99 / max | Over 0.5 ms | Missed | Verdict |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | --- |
| sleep, 120 Hz, 60 s, load 1.7 falling to 0.8 | timer only | 7200 | 13.0 | 16.7 | 19.7 | 27.9 | 46.8 | 0.3 / 0.8 / 21.5 | 0 | 0 | |
| | **full loop** | 7200 | 13.4 | 17.2 | **21.6** | 29.5 | 51.5 | 2.4 / 10.5 / 32.1 | 0 | 0 | **PASS** |
| sleep, 240 Hz, 30 s, process confined to 4 CPUs each running a CPU hog | timer only | 7200 | 11.6 | 12.3 | 14.9 | 979.5 | 1816.0 | 0.1 / 0.4 / 0.5 | 12 | 1 | |
| | **full loop** | 7200 | 11.5 | 12.1 | **12.6** | 1331.3 | 2825.4 | 0.4 / 2.9 / 6.4 | 15 | 4 | **FAIL** (4 missed) |
| hybrid (200 µs), 240 Hz, 30 s, same 4 busy CPUs | timer only | 7200 | 0.0 | 739.1 | 740.5 | 1740.0 | 2711.4 | 0.1 / 0.2 / 0.8 | 891 | 10 | |
| | **full loop** | 7200 | 0.0 | 654.1 | **811.2** | 2152.6 | 2778.3 | 0.4 / 3.6 / 10.1 | 973 | 1 | **FAIL** (p99, 1 missed) |
| deliberate stall: process frozen 8 x 110 ms, 120 Hz, 10 s, no baseline | full loop | 1200 | 13.0 | 18.7 | **97470.1** | 108789.6 | 110668.0 | 2.3 / 11.3 / 26.8 | 105 | 105 | **FAIL** (p99, 105 missed), as intended |

The 4-busy-CPU runs reproduce the reviewer's independent result for that setup: sleep p99 about 14 µs with about
0.3% of wake-ups 1 to 4 ms late, and hybrid failing the gate.

## Ten-minute runs

All three runs used the recommended configuration: `fmp bench jitter --duration-s 600`, i.e. 120 Hz, `sleep`,
default priority, unpinned, 1 ns timer slack, 600 s measured per loop.

| Run | Conditions | Phase | Samples | p50 | p90 | p99 | p99.9 | max (µs) | Work p50 / p99 / max | Over 0.5 ms | Missed | GC / alloc | Verdict |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | --- | --- |
| 1: first method, 03:50-04:10 | quiet: load 0.00-0.91, median 0.15, no other agent's build in the log; non-concurrent GC | timer only | 72000 | 15.2 | 17.6 | 19.8 | 30.2 | 297.1 | 0.3 / 0.8 / 26.1 | 0 | 0 | | |
| | | **full loop** | 72000 | 15.1 | 17.8 | **22.2** | 30.8 | 58.7 | 2.8 / 20.1 / 61.8 | 0 | 0 | 0 / 0 B | **PASS** (also under today's rule: 0 skipped) |
| 2: second method, 09:48-10:09 | not quiet: load 0.04-5.3, median 1.65; other agents' `dotnet` builds and GPU test hosts at 100% for minutes; concurrent GC, `SustainedLowLatency` | timer only | 72000 | 13.3 | 17.1 | 19.7 | 706.6 | 6289.7 | 0.3 / 0.9 / 18.4 | 93 | 18 | | |
| | | **full loop** | 72000 | 13.4 | 17.2 | **19.9** | 365.7 | 4214.3 | 2.4 / 8.9 / 43.1 | 61 | 14 | 0 / 0 B | **FAIL** (14 missed) |
| 3: second method, 10:26-10:47 | mostly quiet (load median 0.99), but other agents started GPU test hosts and another `fmp` GPU process several times (load up to 8.7); concurrent GC, `SustainedLowLatency` | timer only | 72000 | 13.1 | 16.8 | 19.8 | 27.6 | 3654.6 | 0.3 / 1.0 / 9.2 | 13 | 2 | | |
| | | **full loop** | 72000 | 13.0 | 16.9 | **20.1** | 45.3 | 3204.5 | 2.4 / 9.4 / 31.6 | 33 | 2 | 0 / 0 B | **FAIL** (2 missed) |

In all three runs, the design doc's zero-gen0 10-minute steady state held: 0 collections (gen0/1/2 0/0/0), counted
in-process with `GC.CollectionCount` rather than EventPipe (the count is the same), and 0 B allocated on the pacing
and source threads. In run 2 the scheduler decided 35997 real and 35966 generated slots. 23 decisions kept the
previous image because they came too late for generation; 14 vblanks were skipped.

**Run 2's misses come from other load, not from our loop.** The late wakes and skipped slots cluster in two
windows, 09:59 to 10:01 and 10:06 to 10:07, the minutes when the load log shows other agents' `dotnet` builds and
`FrameMotionPlus` GPU test hosts at 100% CPU. Both loops were hit in those windows: 18 skipped slots and 93 wakes
over 0.5 ms in the timer-only loop, against 14 and 61 in the full loop. Each skipped slot was a single late wake
(1.7 to 6.3 ms in the timer-only loop, 1.8 to 4.2 ms in the full loop) costing one slot, and the pacing thread's work
stayed at 9 µs p99 and 43 µs max. Without root (ftrace, perf) the kernel-side cause cannot be pinned down further: a
scheduling delay, which `SCHED_FIFO` would fix, or a non-preemptible kernel or driver section, which it would not.

**Run 3, on a mostly idle machine, still missed 2 slots in each loop, but not at the same moments.** The timer-only
loop's two misses were both at 10:32:13. The full loop's were at 10:33:42 and 10:36:21, and the adjacent timer-only
blocks had no wake over 0.5 ms (the reviewer checked this). Each miss lined up within seconds with another GPU
process starting (FrameMotionPlus test hosts, and an `fmp` process at 93% CPU), while the 1-minute load average was
only 0.2 to 1.8. Each was again a single wake 2.7 to 3.7 ms late costing one slot. Over 0.5 ms the full loop had
more late wakes in this run (33 against 13), while in run 2 the timer-only loop had more (93 against 61). So there is
no systematic excess either way. On this desktop, a normal-priority wake-up is occasionally delayed a few
milliseconds when GPU work starts elsewhere. These delays are the main open item. Whether `SCHED_FIFO` removes them
needs an rtprio limit to find out (see "Real-time scheduling on this machine").

### Independent measurements by the reviewer

The reviewer's raw files are in
`/tmp/claude-1000/-mnt-disk1-dev-repos-frame-motion-plus/57daa41d-f0c3-4fa6-92a6-137243ce1199/scratchpad/p2-review-platform/_review/`.

- **Native C control.** A plain C loop sleeping with `clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME)` ran beside
  the bench under a build loop. It was hit at the same rate and size as the managed loops. Over 0.5 ms: C 3.7%,
  timer-only 3.3%, full loop 3.8%. p99: 1.77, 1.69 and 1.72 ms. Late wakes under load are the OS's; code that has
  no runtime at all sees them too.
- **Ten-minute run, which passed the strict check.** Recommended configuration, final binary, with Chrome and Discord
  active and no other agent running; load average 0.41 to 2.15, median 0.87. 0 collections and 0 B allocated on the
  hot threads.

| Phase | Samples | p50 | p90 | p99 | p99.9 | max (µs) | Missed |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| timer only | 72000 | 9.7 | 18.9 | 32.3 | 148.5 | 1430.7 | 0 |
| **full loop** | 72000 | 8.9 | 19.1 | **31.9** | 153.8 | 289.3 | 0 |

## Reading the results

**OS versus ours.** In the interleaved re-runs, the full loop's wake-lateness distribution matches the timer-only
loop's: p99 differences of +2.0 µs (60 s), +0.2 µs (10 minutes) and -2.4 µs (contended). The resolution of that
method is a few µs: the two loops see the same machine conditions within 5 s of each other. The first matrix, with
back-to-back phases, bounds our contribution only to about ±10 µs. Either way, this is expected. The managed work
runs *after* the wake and takes 2 to 20 µs of an 8.3 ms (or 4.2 ms) slot (p50 to p99), so it cannot delay the next
wake. The other ways the runtime could add jitter were measured as absent:

- No GC, so no suspension.
- No allocation.
- No JIT or tiering, since this is NativeAOT. Work max was 14 to 62 µs on a quiet machine, and 128 µs during another
  agent's build.

The 10 to 20 µs of lateness under `clock_nanosleep` is the kernel's timer and wake-up path (hrtimer interrupt,
idle-state exit, context switch). The idle-state part shows up when the core is kept busy. Pinned to the same CPU,
p90 is 17.4 µs with the core otherwise idle and 11.8 µs with a CPU hog on it. **Runtime-attributable jitter is zero
within measurement precision, far below the 0.5 ms switch threshold.**

**Tails and misses under other load.** On a quiet machine, sleep's p99.9 is 22 to 41 µs and its max 29 to 163 µs.
There were no skipped slots in the first matrix, the quiet 10-minute run or the 60 s re-run. When other agents'
builds and GPU test hosts run, single wakes are 1 to 6 ms late. In the matrix they stayed under the 1.7 ms decision
lead (the scheduler then skipped a generation rather than miss a vblank). In 10-minute runs 2 and 3 a few exceeded
it and cost a vblank: 14 and 2 in 72000, the latter on a mostly idle machine as other GPU processes started. The
timer-only loop missed slots too (18 and 2), though not always in the same seconds. The reviewer's native C loop
was hit at the same rate, and the reviewer's own 10-minute run missed none.

**Contention.** When a CPU-bound thread at the same nice level shares the pacing thread's core, `SCHED_OTHER`
still meets the p99 gate (12 to 15 µs). But 0.2 to 0.3% of wake-ups are 0.5 to 3.3 ms late: EEVDF lets the running
task finish its slice before the woken one runs. That costs 1 to 4 skipped slots per 30 s, which now fails the
missed-slot budget. Even a nice-19 hog did this 0.1% of the time. Priority within `SCHED_OTHER` does not fix it;
`SCHED_FIFO` would. This is the case for granting an rtprio limit, and the reason not to pin a `SCHED_OTHER` pacing
thread. Unpinned, the kernel can move it to another core while idle cores exist. Pinned, even with no deliberate
hog, its tail grew to 1.8 ms when other work landed on that core. When the whole process is confined to busy CPUs
(the reviewer's setup), there is no idle core to move to, and the result is the same as pinning.

**Why not hybrid by default.** On a quiet machine, the hybrid wait hides the 15 µs wake-up latency completely (p99
0.1 µs). A 50 µs window is already enough here (max 34 µs). But:

1. The precision is not needed. The scheduler measures its wake lateness and adds the p99 to its decision lead, and
   presentation is paced by the vblank (and later by present timing), not by the wake. Sleep's 20 µs costs 20 µs of
   pipeline delay and nothing in pacing.
2. It costs CPU: the spin window per slot, 2.4% of a core at 120 Hz and 4.8% at 240 Hz with 200 µs. It also keeps
   that core out of idle.
3. Under contention it is much worse than sleep, and it fails the gate. The p99 is 0.8 to 1.5 ms, and 12 to 14% of
   wake-ups are over 0.5 ms, both with hogs on the pinned cores and with the process confined to busy CPUs. The
   spinning uses up the thread's fair share, so after its sleep the thread no longer preempts the competing task and
   waits for that task's slice.

Hybrid remains available (`DeadlineWaiter.Hybrid`, `--wait hybrid`) for a real-time thread on a reserved core,
where spinning does not cost fairness.

**Recommendation.** Default to `sleep` with 1 ns timer slack, default priority unless real-time is granted, and no
pinning. `fmp bench jitter` uses exactly this by default. The design doc's 0.5 ms p99 gate is met: 17 to 22 µs on
the desktop, with zero collections over ten minutes. There is no case for moving the loop to native code. The open
item is the zero-missed-slot check: it is not guaranteed on a shared desktop without real-time scheduling. It held
in the reviewer's ten-minute run, and 2 to 14 slots per ten minutes were missed in mine while other GPU work ran or
started. Grant an rtprio
limit (above), then re-run `fmp bench jitter --rt --duration-s 600` while the machine is busy, to see how many of
those misses real-time priority removes.

## Reproducing

```sh
dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64
fmp=artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp
"$fmp" bench jitter --duration-s 600 --csv tenmin.csv                       # recommended configuration
"$fmp" bench jitter --duration-s 60                                         # 60 s re-run
for rate in 120 240; do for wait in sleep hybrid; do
  "$fmp" bench jitter --rate $rate --wait $wait --duration-s 60             # matrix; add --rt for the "rt" rows
done; done
"$fmp" bench jitter --timer-slack-ns 50000 --duration-s 60                  # the usual kernel-default timer slack
"$fmp" bench jitter --rate 240 --duration-s 30 --pin-core 30 --source-core 31
# with hogs on the pacing cores: for c in 30 31; do taskset -c $c sh -c 'while :; do :; done' & done
taskset -c 28-31 "$fmp" bench jitter --rate 240 --duration-s 30             # process confined to 4 busy CPUs
# with: for c in 28 29 30 31; do taskset -c $c sh -c 'while :; do :; done' & done
"$fmp" bench jitter --duration-s 10 --no-baseline & pid=$!; sleep 2.5      # deliberate stall: must FAIL
for i in 1 2 3 4 5 6 7 8; do kill -STOP $pid; sleep 0.11; kill -CONT $pid; sleep 0.9; done; wait $pid
```
