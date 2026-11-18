# Phase 4b: the playback pipeline and first light (`fmp play`)

Scope (`docs/implementation-plan.md`, Phase 4, step 4b): source, scheduler, generator and present threads joined by
SPSC rings; `fmp play` with repeat, blend and VFG; telemetry CSV; the 10-minute gate run. HUD and the quality harness are
4c.

**The gate (relaxed by the owner, 2026-11-18): a 1440p60 file played with VFG Medium 2x for 10 minutes with zero missed
slots caused by the pipeline, and zero gen0 collections.** Slots the desktop compositor shows late or skips are counted
apart; the strict zero-of-any-cause gate moves to direct display (Phase 9). The plan's rule: *a miss is the pipeline's
unless the timestamps show the present was handed over in time for its refresh and the display showed it late.* This
document was revised after an independent review (three blockers: a teardown that hung for good when GPU work never
finished, a miss accounting that misstated whose misses were, and bursts of misses the pipeline itself sustained); every
finding and what changed is under "What broke".

Machine: RTX 5090, driver 620.70.06, Linux 7.3.4 (Arch), KDE Plasma 6.8.2 / KWin 6.8.2 Wayland; DP-3 3840x2160 at
240.02 Hz and HDMI-A-1 3840x2160 at 119.88 Hz (both HDR, VRR "automatic", i.e. for fullscreen windows only). .NET SDK
10.0.114; `fmp` is the NativeAOT binary from `dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64`. The owner
uses this desktop: each run's conditions are given with it.

## The gate: not verified (the PASS below is withdrawn)

**No gate run of the current code has been verified from raw timestamps as free of pipeline-caused misses.** The run
below printed "missed 0" and PASS, but the reviewer's re-check of its telemetry, and the offline verifier written since
(`fmp verify`, "Known issues carried to Phase 6"), find 368 missed slots in it: 367 slots shown a whole refresh before
their target (a regression of the previous fix, now corrected in the tree) and one 16.685 ms interval, a refresh without
a new present. The live summary could not see either kind, so a "missed 0" from that binary understates. The 10-minute
gate has not been run again on this revision (the owner time-boxed the pass); the live counters and the verifier now
count early shows, untimed presents and refreshes without a present, and `fmp play --gate` fails on any disagreement
between them, but that is checked only by the short windowed tests below, not by a gate run.

The withdrawn run, for the record. `fmp play gate-1440p60.mp4 --gate --csv gate.csv` on the published NativeAOT binary of the previous revision: windowed (1280x720
logical), VFG Medium 2x, the 60 s 1440p60 clip looped (10 loops), 600 s with the first 2 s as warm-up.

**Conditions** (18 Nov, 02:34 to 02:44): desktop idle, load average 0.04 / 0.23 / 0.47 before; RTX 5090 at P3, 195 MHz,
35.6 °C, 36.4 W; other GPU clients KWin, NZXT CAM (under Wine) and Discord. **The window opened on HDMI-A-1, 3840x2160 at
119.88 Hz** (the literal 120 Hz case; scale 1.25, 1600x900 px) and stayed there; DP-3 (240.02 Hz) was not used.

| Category | Missed slots |
| --- | --- |
| The pipeline's: late present / dropped / refused / skipped vblank / back-pressure / presenter backlog | **0** / 0 / 0 / 0 / 0 / 0 |
| The display's: shown late although handed over in time / given up catching up after those | 0 / 0 |
| During a refresh-rate change | 0 |
| Untimed shows (no first-pixel report) | 0 |

598.0 s measured: 71,690 slots (2,835 real, 68,845 generated, 10 repeated: at 119.88 Hz the 60.000 fps file is not
phase-locked, see "What Phase 5 and Phase 6 must know"), 71,688 presents shown (119.9/s), 6 fillers in the whole run
(start-up, before the measured window), no drop. Intervals from first-pixel feedback: mean 8.342 ms, sd 0.032 ms, p99 8.380
ms, max 16.685 ms (refresh period 8.342 ms). Hand-over before the refresh: p1 12.650 ms, p50 12.830 ms; the present lead
never had to rise (12.513 ms, 1.5 refreshes, throughout). d p50 1.325, p99 1.374, max 2.081 ms; real-frame copy p50 0.019
ms. Delay to light for a real frame: present lead 12.513 + the scheduler's D 18.285 + rounding 0.020 ms; measured 30.828
ms after its content time (p99 31.670), against the latency model's ΔL = d + (M − 1)/M · T = 9.707 ms: the rest is the
present lead (12.5 ms) and the scheduler's hold, 16.3 ms in its D here against the model's 8.3 ms (as observed in this
run, where the slots are not phase-locked; not analysed further in this phase). Bridge lead
at most 1 (limit 5); no back-pressure skip; no write out of order; no generator fault; frame-pool growth 0; GC 0/0/0 in
the window and the whole run; 0 B allocated on the pacing, generator, present, control and decode threads; teardown clean.
CPU per slot: pacing 17.1, generator 102.4 (VFG at twice the rate it would need phase-locked), present 114.2, control 16.4
µs. Verdict: `gate PASS: 71690 slots over 598.0 s, zero missed slots caused by the pipeline, zero gen0 collections`.

The binary was published before two late edits that change no behaviour: the summary's label for the rounding line
(reworded) and a test's bound; the published binary at the end of this revision includes them.

**Earlier runs, corrected.** The first version of this document reported five gate runs judged by the accounting the
review rejected (a fixed 1 ms latch, GPU-clock times, report times standing in for missing first-pixel times). It said run
5's 28 misses were "all the compositor's"; that log reads "compositor skipped 0, unattributed 14, and 14 catch-up drops",
and under the plan's rule (in time only when the timestamps show it) those 28 are not proven to be the display's. The
coordinator's run on the same binary had 87 misses, 80 of them three self-sustained bursts (B3). Those runs, by the
binary before this revision: run 1 deadlocked at 99.6 s; run 2 (idle desktop, 240 Hz) had no miss but allocated 120 B on
two threads; runs 3 and 4 moved between outputs and had misses then and in compositor bursts; run 5 as above. They are
superseded by the run above; their logs remain in the scratch directory of this work.

## Known issues carried to Phase 6

The owner stopped this pass early (2026-11-18): the playback looks right on screen, and the frame-timing and accounting
work left from the reviewer's re-check moves to the pacing phase (Phase 6). What the re-check raised, and where it stands
in the tree. "Fixed" means in the code with a test; none of it has been through a 10-minute gate run.

1. **Early shows / the hand-over window: fixed, not gate-verified.** The re-check: `PresentLoop.cs:514` used the present
   lead as the compositor's deadline for an empty FIFO (`:738-740`), so a present handed over 1.5 refreshes ahead into an
   empty queue was taken for the refresh before its target; the early branch (`:980-984`) only corrected the prediction.
   Its gate/ CSV: 367 early slots in the "missed 0" gate run; this author's second gate run (gate2/): 164. Now a present
   into an empty FIFO waits until the latch of the refresh before its target has passed, from the measured latch's lower
   bound (`PresentLoop.cs:490`, `:549`, `LatchEstimator` in `PresentAccounting.cs:325`; latch evidence only after a known
   timed flip, `PresentLoop.cs:1024`); an early show is a miss (`Early`, `:1064`, `:1078`). Test
   `WindowedTests.No_slot_is_shown_before_its_refresh` (29.97 and 60 fps clips): 0 early slots at 119.88 Hz; with the old
   rule put back, 597 and 582 early slots. Fix direction left: the window rule rests on a latch estimate from lower and
   upper bounds; at 240 Hz its lower bound read 1.4 to 6.4 ms within one session (item 5).
2. **Untimed presents: fixed.** The re-check: an untimed present got a display time in the CSV (the report's) and was never
   judged. Now no display time, judged from its timed neighbours, unjudged ones fail the gate (`TelemetryCollector.cs:153`,
   CSV column `unjudged`, `FrameTimelineRecord.cs:64`). Found while doing it: KWin on the 240 Hz output at times sends
   complete reports without the first-pixel time, for runs of every fourth present (17 to 31% of presents in some one-second
   runs, none in others); they now release the feedback at once and are counted apart (`SwapchainTarget.cs:748`).
3. **The two-refresh-interval check: fixed.** The re-check: the 16.685 ms interval in the gate run (two refreshes, no new
   present) was invisible. Now counted live (`TelemetryCollector.cs:567`, with the last period, net of untimed presents in
   between) and by the verifier, the same way.
4. **The offline verifier: written, passing on fixtures, not run on a long run of the current code.**
   `TelemetryVerifier.cs:143` (from the definitions; `fmp verify <csv>`; `fmp play --gate` runs it at the end and fails on
   any disagreement, `PlaybackSession.cs:715`). Its fixtures (excerpts of the review's CSVs) flag every early slot and the
   16.685 ms interval in the review's gate/ CSV and in gate2/, slots 5945 to 5951 in the review's dist/ CSV, and nothing in
   a clean 240 Hz excerpt.
5. **Behaviour on the 240 Hz output: unverified.** In this pass the window opened on DP-3 (240.02 Hz) for part of the
   session, and the output's own timing was not regular then: intervals of 3.5 to 8.5 ms on a 4.17 ms period (18 of 395,
   90 of 680, 142 of 827 intervals between timed shows more than a quarter refresh off the grid in three runs; 0 of 597 in
   others) together with the time-less reports above. The windowed tests now skip as "environment" when more than one
   interval in fifty is off the grid. Also seen at 240 Hz: presents in flight capped at 2 with a 6.25 ms lead (presenter
   refusals 419 to 2144 in 8 s runs); a pacing wake a few hundred microseconds late lost its refresh to a filler (fillers
   now go in 0.25 ms after the lead instead of before, `PresentLoop.cs:519`); and in the JIT test host only, tier-up
   suspends the runtime for 8 to 9 ms about 2.5 s into a run (test host now runs without tiering; NativeAOT has no tiers).
   Needs a clean session on DP-3 and a gate-length run.
6. **The adaptive lead never probes lower: not done.** `LeadController` (`PresentAccounting.cs:226`, `Tick` `:296`)
   decays only back to the starting lead (1.5 refreshes: 12.5 ms at 119.88 Hz) and never below it, though the measured
   latch is about 5 ms there; the delay to light keeps the full lead. A lead too short to reach the latch now raises
   itself even when every such slot is dropped (`PresentLoop.cs:443`; before, nothing was shown late and it never rose).
   Fix direction: probe down toward the latch's upper bound plus a margin, using the latch estimator.
7. **The first image was a generated frame on a non-locked output: fixed.** The re-check: on the virtual clock and at
   119.88 Hz the first image was generated between frames 0 and 1 (t 0.45, 0.24). Now the first slot with content is the
   real frame due at τ (`FrameScheduler.cs:399`); `FirstImageTests` (3 of its 4 cases fail without the change).
8. **The 50 ms feedback hold: fixed.** One lost report held every feedback behind it for 50 ms. Now released when the
   next present's report has come or the report came without a time (`VulkanPresenter.cs:1187`, `HasPresentReport`);
   `OffscreenPresentTests.Feedback_waits_for_late_first_pixel_reports_and_counts_the_one_that_never_comes`.
9. **The start offset was not guarded by `IsLive`: fixed.** `PacingLoop.cs:125`, `:333` (a live source keeps capture
   times); `StartOffsetTests`.
10. **`FMP_PLAY_FAULT` in the shipped binary: unchanged.** The fault hook is compiled into the release binary; it is read
    only by `fmp play` (`PlayCommand.cs:111`) and listed in `fmp --help`. Fix direction: compile it out of release
    builds, or keep it as a documented diagnostic.

Also from the re-check. SIGINT: the two SIGINT tests failed when the suite ran from a background shell (SIGINT inherited
ignored). They now start the child with SIGINT at its default (`env --default-signal=INT`), or skip saying why
(`tests/Shared/ChildProcess.cs`). fmp itself keeps an inherited SIG_IGN for SIGINT (the .NET runtime leaves it ignored:
the convention for background jobs); SIGTERM, closing the window and Escape still stop it. A stop during warm-up is its
own outcome: "STOPPED by the user before anything was measured (no verdict)", exit 0.

## How misses are counted

Every slot the scheduler decides is meant for one refresh. A slot is missed when its present is not shown at that refresh
(shown later, or not presented), or when it was never produced (the pacing thread skipped it: back-pressure, a present
backlog, a decision woken too late). Whose a miss is, the present thread decides from host-clock facts only
(`MissAccounting`):

- **Hand-over.** When the present was submitted, and when the present thread first saw its content written (it polls the
  ring's *written* counter every turn while a write is outstanding, so this is at most a turn, 250 µs, late: the
  conservative direction). Hand-over = the later of the two; its lead = the refresh it was meant for minus hand-over.
- **Shown.** The first-pixel time from VK_EXT_present_timing (calibrated to `CLOCK_MONOTONIC`). The presenter holds a
  completed present's feedback until its report arrives; a report that comes without the time (KWin leaves it out at
  times, below), or the next present's report coming first (reports come in present order), releases it at once,
  *untimed*; 50 ms is only the last bound. An untimed present gets no display time in the CSV; it is judged from the
  timed shows around it: missed when one of them took its refresh, otherwise *unjudged* (the CSV's `unjudged` column),
  and unjudged slots fail the gate. Untimed shows stay out of the refresh grid, the lateness test and the intervals.
- **Early.** A slot shown more than half a refresh before its target is missed (`Early`), and the present thread's
  predictions move back at once.
- **A refresh without a new present.** Between consecutive timed shows, every refresh beyond one per present in between
  (untimed ones included) had no new present: counted, and the pipeline's (`MissedRefresh`) unless a missed slot
  scheduled for it (or for the refresh before) explains it.
- **The deadline (measured).** The hand-over lead a present needs to count as in time: the larger of the smallest lead
  that made its refresh and the largest that did not, over the last 10 s on this output, plus 0.25 ms. No present that
  made its refresh in that time means no evidence.
- **A late show** (shown a refresh or more after the refresh it was predicted for) is the display's (`DisplayLate`) only if
  its hand-over lead is at least the deadline; otherwise, or without evidence, the pipeline's (`LatePresent`). It leaves the
  FIFO that many refreshes behind; the slots given up to catch up (or a real frame shown a refresh late instead, below)
  carry the late show's attribution (`CatchUp` for the display's, `Dropped`/`LatePresent` for the pipeline's), and a slot
  submitted on time clears what is owed.
- **A change of the output's rate** (the window moved to another output) starts two seconds in which misses count as the
  change's (`RateChange`), and forgets the old output's evidence.
- Everything else (a present not presented because its refresh was unreachable with nothing owed, a refused present, a
  skipped slot) is the pipeline's.

The GPU clock (CUDA events put on `CLOCK_MONOTONIC`) is used for the statistics and the CSV only; it is re-anchored on host
time every 2 s (it used to chain anchors by elapsed time and kept the device clock's drift: 10 to 28 µs/s here, 6 to 7 ms
after ten minutes, enough to make generated frames appear to finish after the refresh they were shown at).

## What was built

```
src/FrameMotionPlus.Pipeline/
  PlaybackOptions.cs     what a session plays and how (generator, quality, multiplier cap, display, clock, ...)
  PlaybackSession.cs     assembly (Open), pre-roll, the control loop, teardown (Run); the generator and bridge budget
  PacingLoop.cs          the pacing thread: the scheduler and the source's consumer
  RingWriter.cs          the generator thread: every ring write in order on one stream; GeneratorHandle
  PresentLoop.cs         the present thread: one present per refresh, feedback, the refresh grid
  DisplayGrid.cs         the refresh grid from present feedback (lower envelope of the shows)
  GpuClock.cs            CUDA events on CLOCK_MONOTONIC (anchored, renewed every 2 s)
  TelemetryCollector.cs  joins the three threads' records into Core's FrameTimelineRecord; the statistics
  PlaybackStatistics.cs  the measured window: slots, presents, misses and whose, intervals, d, D, latency
  PresentAccounting.cs   whose each miss is (MissAccounting), the adaptive present lead (LeadController), catching up
                         without losing a new real frame (CatchUpPolicy): pure, unit-tested
  PlaybackFaults.cs      fault injection (FMP_PLAY_FAULT): GPU work that never finishes, or outlasts the teardown
  PlaybackSource.cs      IPlaybackSource (live or decoded ahead, start with an origin, end, failure); the file adapter
  ThreadMeter.cs         per-thread allocated bytes and CPU time over the measured window
  Messages.cs            the jobs and records the threads hand each other
src/FrameMotionPlus.App/Cli/PlayCommand.cs, Play/PlayOptions.cs, Play/PlayPrinter.cs    fmp play
tests/FrameMotionPlus.Pipeline.Tests/   101 tests (pure ones always; Gpu, Display, Vfx skip without the hardware)
```

## Threading model

```
                 decode thread (the source's own)            pacing thread                     generator thread
                 FFmpeg/NVDEC → copy stream                   ┌───────────────────────┐          ┌───────────────────────────┐
  file ───────►  convert → compute stream ──SpscRing──────►   │ TryRead → scheduler   │          │ in write order, ONE stream│
                 (decodes ahead, waits on                     │ wait NextDecisionTime │──jobs──► │ (high priority):          │
                  release points GPU-side)  ◄──Release(id,    │ Schedule → slot:      │  Spsc    │  real: wait slot release, │
                                               last writer's  │  TryAcquireSlot       │          │   frame Ready, copy, signal│
                                               written point) │  (refused → skip,     │          │  generated: Enqueue (VFG  │
                                                              │   counted)            │          │   blocks for d), signal   │
                              d (GPU spans) ◄─────────────────│ ReportGenerationTime  │◄─Spsc────│  fault → real frame, signal│
                                                              │ UpdateDisplayTiming   │          └─────────────┬─────────────┘
                                                              └───────────┬───────────┘                        │ written(n)
                                                                presents  │ Spsc                               ▼ (timeline)
                                                              ┌───────────▼───────────┐           presentation ring (4 slots)
                              grid (anchor, rate) ◄──Spsc─────│ present thread        │◄──────────────────────────┘
                                                              │ every presenter call: │  released(n) ─► back to the writer
                                                              │ one present/refresh,  │
                                                              │ ≤ 2 in flight, fillers│──► Vulkan presenter ─► FIFO swapchain
                                                              │ feedback → grid, lates│◄── present_wait2 + first-pixel times
                                                              └───────────────────────┘
  control thread (the caller of Run): window events, telemetry join → Core TelemetryRing → CSV, stop conditions
```

Five threads, all dedicated OS threads (`DedicatedThread`; no thread pool, no `async` anywhere per frame), joined by
`SpscRing`s (lock-free, preallocated) and an `AutoResetEvent` per sleeping consumer to wake it (not a
`ManualResetEventSlim`: see "What broke", 7):

- **Decode thread** (inside `FileFrameSource`, unchanged): decodes ahead into the frame history and waits, GPU-side, on
  each buffer's release point before rewriting it. It blocks inside NVDEC once per frame (4a); nothing waits for it.
- **Pacing thread** (`PacingLoop`): the scheduler's only thread and the source's consumer. It drains the source into
  the scheduler, takes measured generation times and the refresh grid back, sleeps (absolute `clock_nanosleep`) until
  the scheduler's next decision time (at most 2 ms, to keep draining), and turns each decision into a ring write for the
  generator thread and a present for the present thread. It never calls the generator, never waits for the GPU or the
  presenter, and when the bridge refuses a slot (writes unfinished on the GPU) or the present thread is more than three
  presents behind, it does not produce the slot (counted) instead of queueing it. Every 100 ms it checks that ring writes
  are finishing; none for the stall timeout fails the run.
- **Generator thread** (`RingWriter`): every write into the ring, real frames and generated frames alike, in the order
  the slots were handed out, on one CUDA stream (high priority; VFG's `NVVFX_CUDA_STREAM`). So *written* is signalled in
  write order by construction (Core's ordering rule) without any cross-stream waits. A real frame is copied in behind the
  slot's release and the frame's conversion; a generated one is the generator's `Enqueue` straight into the slot. VFG
  (shot-change detection on, `EnqueueMayBlock`) blocks this thread for d: it is a thread of its own, never the pacing or
  present thread, and nothing it does releases a slot or writes a source frame (the presenter releases slots, the decode
  thread writes frames; this thread only waits on both, GPU-side). When the generator throws (or VFG reports itself
  faulted) the slot gets the nearest real frame on the same stream, behind whatever the failed call enqueued, and is
  signalled in order (Core's failure contract); generation then stays off and playback carries on with real frames.
- **Present thread** (`PresentLoop`): every call into the presenter (which is single-threaded), feedback, the refresh
  grid, the adaptive present lead, catching up, and whose each miss is (it has the facts). See "Pacing on this desktop"
  and "How misses are counted".
- **Control thread** (the caller of `PlaybackSession.Run`; the main thread for `fmp play`): window events (SDL wants the
  thread that created the window), the stop conditions, and the telemetry join and CSV writing, off the hot path but
  allocation-free all the same (its collections would be the process's).

Why this split, against what was measured: VFG's `Enqueue` blocks for its generation and for its inputs' readiness
(Phase 3), so it cannot be on the pacing or present thread; putting the real-frame copies on the same thread and stream
gives the write order for free (a second producer stream would need a GPU wait on the previous write before every
signal); the presenter is single-threaded and its calls cost 0.05 to 0.3 ms (Phase 2), so the present thread owns it and
polls feedback every 250 µs without holding up decisions; the pacing loop's own work is 10 µs per slot and its wake-ups
20 to 30 µs late at p99 (Phase 2), far inside a 4.17 ms slot.

**Release with the last reader, after it is enqueued.** A frame goes back to the source once the scheduler no longer
refers to it, with the GPU point of the last ring write that read it (the ring's *written* timeline at that write's
value, CUDA-side), and only once the generator thread has *enqueued* that write: the source re-records a buffer's Ready
event when it reuses the buffer, and a wait enqueued after that would wait for the new frame. See "What broke".

**Startup and teardown.** `Open` builds everything (a drained point): the window, the Vulkan device, the CUDA streams,
the source (decoder, frame pool, history), the generator (VFG loaded and warmed up), the presenter and the bridge. `Run`
pre-rolls black frames through the whole path (the window goes on its output and its rate becomes known, the present
thread learns the refresh grid from feedback, every per-frame path runs once on every thread), then builds the scheduler
on the learnt grid, starts the source with its content-time origin and starts the pacing thread. Teardown stops the
pacing thread, then the generator thread once every write handed to it is enqueued and signalled, then the present
thread; then stops the source (its decode thread may wait inside NVDEC on release points, all of which complete), frees
the source while the ring's semaphores (its release points) still exist, then the bridge, the generator (VFG's destroy
synchronises the context: a drained point), the streams, the presenter, the window and the device. No CUDA allocation,
free, module load or VFG load happens between `Open` and teardown. If GPU work on the generator stream does not finish
within 3 s (a wait nothing will satisfy, a generator call that never returns), nothing GPU-side is freed: every call
that would wait for that work (cuMemFree, cuModuleUnload, the generator's destroy, NVDEC, the device's idle wait) is
skipped, the report says what was left, `Run` returns, and `fmp play` prints its summary and ends the process with
`_exit` (a normal exit would wait in the CUDA driver's clean-up). A stop asked for at any point of the start-up (a
signal while the device opens, a window close or Escape during the pre-roll) is honoured as soon as there is a session.

## Time model

Every time is `long` nanoseconds on `CLOCK_MONOTONIC` (`Monotonic.NowNs`; Linux), on every thread and in every record.

- **Content time.** A file's frame is due at *content time = origin + media time + start offset*: the file source's
  `ContentClock` puts the stream's own timestamps (repaired where they jump; loops and seeks keep it increasing) on
  `CLOCK_MONOTONIC`; the pacing thread holds the first frames until the scheduler's start-up frames are in, then adds one
  constant offset to every content time so the file's first frame is due two refreshes from then: the first frame of the
  file is the first one shown (it used to be frame 2 to 11: the frames due while the decoder started were skipped). The
  generator is told about discontinuities in the source's own time (its frames carry that time).
- **Arrival.** For a file (not live) a frame is reported to the scheduler as arrived at *min(read time, content time)*:
  having it early is not latency, and reading it a little late must not raise D for the rest of the run. A live source
  (Phase 5) reports its real arrival. The virtual clock reports *content − feed-ahead*.
- **Never slow.** The scheduler decides each refresh's content from the refresh time (τ = V − D, the design doc's model);
  a late show never makes later ones later: the FIFO behind it catches up (below) and later content is shown on time.
- **The display grid.** The present thread learns the refresh grid from first-pixel times (VK_EXT_present_timing,
  `ImageFirstPixelOut`, a present-stage-local domain calibrated to `CLOCK_MONOTONIC` with `vkGetCalibratedTimestampsKHR`,
  re-calibrated every second: 6.7 µs per calibration, driver deviation about 80 ns). The period is the output's rate; the
  phase is the lower envelope of the timed shows (`DisplayGrid`); predictions step along the grid (`DisplayGrid.NextAfter`),
  never by adding the nominal period (off the refreshes by tens of ppm; see "What broke", 6). The grid and the present lead
  go to the pacing thread (`UpdateDisplayTiming(refresh − lead)`) when first known, when the rate or the lead changes, and
  when the phase has moved by 100 µs.
- **The present lead.** Windowed, a slot's content is due one *present lead* before its refresh: 1.5 refreshes to start
  with (`--present-lead-ms` sets another start), adaptive (`LeadController`, see "Pacing"). The scheduler is given the grid
  shifted earlier by it, so its V is the time a slot's content must be ready by; its margin and generation budget are
  unchanged, and a lead change moves the decisions, never the refreshes the slots are for.
- **GPU times.** CUDA events on `CLOCK_MONOTONIC`, re-anchored on host time every 2 s (statistics and CSV only).
- **Offscreen**, real clock: slots at the requested rate, each submitted just before its time, shown when its GPU work
  completes. Virtual clock (the tests' default): the pacing thread's clock jumps from decision to decision, the threads run
  in lockstep, every run of the same file decides, generates and presents the same slots.

## Pacing on this desktop

KWin's Wayland FIFO cannot be given a present time; it shows one queued present per refresh in order, and only presents
whose buffer is ready by its latch, which is some milliseconds before the refresh and moves with its own load. The present
thread keeps the FIFO fed with exactly one present per refresh, at most two in flight: each slot is submitted for its
refresh, a scheduler Repeat is the last image again, and when nothing is ready by a refresh's hand-over deadline the last
image goes in as a *filler* (a statistic: the slot it stood in for is counted when it is shown late or given up). Fillers
and the prediction for an empty queue use the measured deadline (the present lead), not the 1 ms latch assumed before
(fillers landed 3.6 and 5.2 ms after their refresh with it).

1. **First-pixel times, not `present_wait2` times.** The latter's phase jumps by about 2 ms from one report to the next on
   KWin. With first-pixel times, consecutive shows are whole refresh periods apart within about ±0.7 ms (the Vulkan test
   `Feedback_carries_first_pixel_times_on_the_refresh_grid`: 0 of 236 intervals off the grid).
2. **The adaptive present lead.** A present handed over after the compositor's latch is shown a refresh late. When a
   present is late and it was handed over as soon as it was there (its job came, or its content was written, only then:
   decided too late, not held back by the in-flight cap), the lead goes up 1 ms at once, and that level is held for ten
   minutes (no drifting back into the same miss); later late frames already decided with the old lead do not raise it
   again for 50 ms. Without a raise it comes down 0.25 ms every 10 s after 30 s quiet, never below its start; it never
   exceeds two refreshes (the in-flight cap's hand-over). Latency cost: the lead is part of the delay to light; it costs
   what it is raised by (1 ms a step) on top of the start's 6.25 ms at 240 Hz or 12.5 ms at 119.88 Hz.
   Generated frames are the last content to be ready (their generation takes d after the decision, a real copy 0.02 ms),
   so they are what goes late when the latch moves and what drives the raise: the lead keeps *them* clear of the latch.
   Handing every frame over d earlier as well would cost about 1.3 ms of latency all the time for no gain while the latch
   is where it was.
3. **Catching up without losing a new real frame** (`CatchUpPolicy`). After a late show, a refresh must be given back. A
   repeat is given up first (at stride 2 the repeat after a late real frame costs nothing: its refresh shows that real
   frame); then a generated frame; a real frame a single refresh late is shown late rather than dropped, once. Before, the
   slot after the late one was always dropped: with a generated frame late and the real frame after it dropped, the next
   generated frame was just as marginal, and for as long as the latch stayed early every source frame cost two misses
   (the coordinator's run: 80 of 87 misses in three bursts, every late show a generated frame and every drop the real frame
   after it). With the lead raised at the first late show and the catch-up giving up a repeat, the burst does not sustain:
   the reduced-lead reproduction under "Other runs".

## Decisions

- **All ring writes on the generator thread, one stream.** Real-frame copies too: *written* is signalled in write order by
  construction, and a generator that blocks (VFG) holds up only that thread. High priority; VFG generates on it.
- **Three CUDA streams** (copy and compute for the source, the generator stream for ring writes); with NVDEC's internal
  streams the process stays within 8, so `CUDA_DEVICE_MAX_CONNECTIONS` is not set (reasoned, not instrumented).
- **CUDA scheduling `BlockingSync`, by measurement** (VFG Medium 2x at 240 Hz, 20 s windowed each): generator thread CPU
  57.4 µs per slot against 345.9 µs spinning; d p99 1.418 against 1.324 ms (`--cuda-sync spin` overrides it).
- **The bridge budget per generator**: `OperationsPerWrite` 48 for VFG Medium (40 Low, 192 High), 16 for blend and repeat;
  the lead is `min(slots + 1, 960 / ops)` = 5 writes (4 ring slots).
- **Release rules.** A source frame goes back with its last reader's GPU point, after that write is enqueued; every
  dropped present releases its write; the writes still queued when the present thread stops are released.
- **The miss accounting** (above): host-clock facts, a measured deadline, conservative without evidence.
- **The adaptive present lead and the catch-up policy** (above).
- **A stalled generator fails the run** (no ring write finished for the stall timeout, 10 s), and **GPU work that does not
  finish is abandoned at teardown** (nothing that would wait for it is called; the report says what was left) and
  **`fmp play` then ends the process with `_exit`** (`Platform.Runtime.ImmediateExit`).
- **Stop requests are kept from the first line of `fmp play`** (signal handlers registered before anything else; a stop
  before the session exists is applied to it at once; window close and Escape are honoured in the pre-roll).
- **The first frame of the file is the first shown** (the start offset, above).
- **Fault injection in the shipping binary**, as diagnostics: `FMP_PLAY_FAULT=gpu-wait@N` (a GPU wait that never
  completes at the N-th generated frame) and `gpu-busy@N:S` (S seconds of GPU work there).
- **Fillers**, not empty refreshes: one present per refresh is what lets the loop know which refresh each lands on.
- **VFG by default, blend when the SDK is absent** (with the reason); `--generator vfg` without it exits 69.
- **Fullscreen** is SDL's borderless fullscreen on the window's current output, once its timing is known; off by default,
  not used by the tests (and not run in this phase's measurements).
- **GC**: `SustainedLowLatency` during playback; nothing allocates per frame on any of the five threads (each thread's
  bytes are measured; the tests prove a 64-byte allocation per turn is caught on each), and no thread sleeps on a
  `ManualResetEventSlim` (its NativeAOT slow path allocated once in a long run).
- **Scheduler fix** (Core): `MultiplierSlack` (3%) for millisecond container timestamps.

## What broke and how it was fixed

Before the review:

1. **Deadlock in about half of the virtual-clock runs**: a source frame went back before the write that read it was
   enqueued, the decoder re-recorded the buffer's Ready event, the write waited for the next frame. Fix: release only after
   the last reader is enqueued (`RingWriter.EnqueuedWrite`).
2. **Present-thread stalls of 2 to 3 ms at about 2.3 s** in the JIT host (tiering); none in the NativeAOT binary.
3. **False late shows** from `present_wait2` times: first-pixel times.
4. **Compositor skips with a one-refresh lead**: the 1.5-refresh start (now adaptive).
5. **60 → 120 from a Matroska file generated nothing**: `MultiplierSlack`.
6. **The first gate run deadlocked at 99.6 s**: the chained prediction drifted half a refresh (it added the nominal period,
   ~20 ppm off), presents were dropped without releasing their slots, the generator waited for those releases for good.
   Fixes: predictions step on the grid (`DisplayGrid.NextAfter`), dropped presents release their writes, a stall check,
   (and the teardown, completed in the review's B1 below). Tests: `DisplayGridTests`,
   `Dropped_presents_release_their_slots_and_playback_goes_on` (both deadlock with the release switched off),
   `A_generator_thread_that_makes_no_progress_fails_the_run`.
7. **120 B allocated once on the generator and decode threads in ten minutes** (NativeAOT: the slim event's Monitor slow
   path; a probe showed it, `AutoResetEvent` never allocated): the threads sleep on `AutoResetEvent`
   (`Every_read_and_release_wakes_a_held_back_decode_thread`).
8. **"Max in flight 3"** was the present thread's lagging count: the presenter's own count is reported.

From the review (each test was confirmed to fail on the code before the fix, as said):

- **B1. Teardown hung for good when GPU work never finished** (cuModuleUnload on the caller, `cuEventSynchronize` inside
  NVDEC on the decode thread; the reviewer's backtraces). Teardown now waits 3 s for the generator stream and, if it is not
  idle (or the generator thread did not stop), abandons everything GPU-side (`PlaybackSession.Abandon`), reports it
  (`PlaybackReport.GpuAbandoned`, `Abandoned`), and `fmp play` prints its summary and ends with `_exit` (an ordinary exit
  waited in the CUDA driver's clean-up: that was seen, the run printed its summary and then hung). Tests:
  `Gpu_work_that_does_not_finish_is_abandoned_and_the_run_still_returns` (12 s of GPU work: Run returns after 7 s, the
  stream drains later, a new session plays), `A_gpu_wait_that_never_completes_still_ends_fmp_play_with_its_summary`
  (out of process, blend and VFG, where the generator thread itself is stuck in the SDK; ends in 16 and 21 s, exit 1).
  The old code hung in the reviewer's run of the same fault (over 150 s).
- **B2a. The GPU clock drifted** (10 to 28 µs/s): re-anchored on host time at every renewal; no miss is judged from it.
  `GpuClockTests.Gpu_times_stay_on_the_host_clock`: the old clock was 333.8 µs off after 12 s (fails; limit 60 µs).
- **B2b. "Queued in time" assumed a 1 ms latch**: a measured deadline (above), conservative without evidence; the windowed
  tests skip only for misses proven to be the display's. `A_present_lead_too_short_for_this_compositor_is_counted_as_the_
  pipelines` (a fixed 1.5 ms lead: the old accounting reported "the pipeline's own 0" for such runs, as the reviewer's 2.6
  to 4.5 ms runs and the old binary's below show), plus `MissAccountingTests`.
- **B2c. An untimed show was given the report's time**: the presenter holds feedback until the first-pixel report comes
  (`VulkanPresenter.TimingHoldNs`), untimed shows are counted and excluded. `Feedback_waits_for_late_first_pixel_reports_and_
  counts_the_one_that_never_comes` (Vulkan, synthetic reports): with the hold set to 0 (the old behaviour) it fails
  (expected the report's time, got 0).
- **B3. The pipeline sustained bursts**: the adaptive lead and the catch-up policy (above). `CatchUpPolicyTests`,
  `LeadControllerTests`, and the windowed `A_short_starting_lead_adapts_and_the_misses_stop` (a 1.5 ms start: with the
  lead fixed the misses go on for the whole run, as the old binary's reproduction below shows). The reproduction on the
  published binaries is under "Other runs".
- **The start of the file was never shown**: the start offset. `Playback_starts_with_the_first_frame_of_the_file` (both
  clocks); the old binary's first shown frame on the virtual clock was 2, the new one's 0 (CSV).
- **Stops during start-up were lost or fatal**: handlers first, the pre-roll honours window events.
  `A_signal_during_start_up_stops_fmp_play_with_its_summary` (out of process). Old binary, one SIGINT straight to the
  process: offscreen at 0.05/0.15 s killed with exit 130 and no summary; windowed at 0.15 s swallowed (played all 8 s).
  New: every case stopped cleanly with its summary within 0.3 to 0.7 s.
- **The reported delay was a refresh short at stride 2**: the summary now prints the measured delay to light as present
  lead + D + the scheduler's rounding to real frames (`PlaybackStatistics.StrideDelay`; the windowed tests bound it). At
  stride s a new image lands on the next refresh of its stride, up to s − 1 refreshes after τ (one refresh, 4.13 to
  4.17 ms, at stride 2); when not phase-locked, a real frame within the snap of τ is shown up to half an interval before
  τ reaches it (−8.6 to −8.8 ms measured on 60 fps files at 119.88 Hz). Removing the stride's refresh would mean the
  scheduler choosing the stride's phase from τ rather than from frame N (Core): recorded for Phase 6, not done.
- **Empty-queue prediction and the filler guard used the 1 ms latch**: they use the measured lead.
- **The catch-up credit was never cleared**: what is owed is cleared by a slot submitted on time
  (`Catch_up_carries_the_late_shows_attribution_only_for_what_it_owes_and_is_cleared_by_an_on_time_present`).
- **An output move had no test**: misses during a rate change count on their own; `MissAccountingTests.Misses_during_a_
  rate_change_are_its_own_and_the_old_outputs_evidence_is_forgotten` and `DisplayGridTests.After_a_move_to_another_output_
  the_first_show_puts_the_grid_on_the_new_refreshes` (synthetic feedback, 240 → 119.88 Hz). The transition: the grid and the
  lead are reset at once and the scheduler told; slots decided on the old grid are given up (counted as the change's).
- **A run that ended inside its warm-up passed**: it fails ("nothing was measured"); `A_run_that_measured_nothing_does_not_
  pass` (the old binary printed PASS for `--offscreen --duration-s 1`).
- **`PresentLoop.Grid` read the rate unsynchronised**: a sequence-counted snapshot.
- Found while fixing: shifting content times (the start offset) moved the generator's discontinuities 84 ms early (it
  compares them with its frames' own times), which `Nothing_is_interpolated_across_a_loop_or_a_timestamp_jump` caught; the
  generator is given the source-clock time.

## Changes outside the new project

| Where | What and why | Test |
| --- | --- | --- |
| `src/FrameMotionPlus.Core/Abstractions/IFrameSource.cs:57` | `Release(long frameId, in GpuSignal lastReader)`: release with the last reader's GPU point (4a carry-over). | `FrameTypesTests.Ready_points_travel_on_the_frame_and_releases_carry_the_last_reader` |
| `src/FrameMotionPlus.Core/Frames/SourceFrame.cs:25-30`, `Abstractions/IPresenter.cs:79-84`, `Simulation/PipelineSimulator.cs:355` | `SourceFrame.Ready`, `PresentRequest.Ready` removed: one ready point per frame. | same; Vulkan `OffscreenPresentTests` |
| `src/FrameMotionPlus.Core/Scheduling/FrameScheduler.cs:27, :849` | `MultiplierSlack` (3%): millisecond container timestamps. | `MillisecondTimestampTests` |
| `src/FrameMotionPlus.CudaVulkan/CudaVulkanBridge.cs:445` | Present request without the removed `Ready`. | existing bridge tests |
| `src/FrameMotionPlus.Platform/Interop/LibC.cs:25`, `Time/ThreadCpuClock.cs` (new) | Per-thread CPU time. | `ThreadCpuClockTests` |
| `src/FrameMotionPlus.Platform/Interop/LibC.cs:142`, `Runtime/ImmediateExit.cs` (new) | `_exit`: `fmp play` ends the process after a teardown that abandoned GPU work (B1). | `ImmediateExitTests`; end to end `A_gpu_wait_that_never_completes_still_ends_fmp_play_with_its_summary` |
| `src/FrameMotionPlus.Sources.File/ContentClock.cs:90`, `FileFrameSource.cs:330, :351, :727` | `SetContentTimeOrigin`; no second ready point. | `DecodeTests.The_content_time_origin_can_be_set_until_the_source_starts` |
| `src/FrameMotionPlus.Sources.File/FileFrameSource.cs:88, :560, :643` | The decode thread sleeps on an `AutoResetEvent` (NativeAOT slow-path allocation). | `SerialSourceTests.Every_read_and_release_wakes_a_held_back_decode_thread` |
| `src/FrameMotionPlus.Vulkan/Presentation/SwapchainTarget.cs:106, :247, :494, :578, :623` (and the swapchain flag, present chain, buffers), `PresentTarget.cs:155-167`, `VulkanPresenter.cs:265` | VK_EXT_present_timing first-pixel times in feedback, calibrated to `CLOCK_MONOTONIC`; `SwapchainTarget.Calibrations`/`MaxCalibrationNs` (`:619`). | `WindowedTests.Feedback_carries_first_pixel_times_on_the_refresh_grid` |
| `src/FrameMotionPlus.Vulkan/Presentation/VulkanPresenter.cs:88, :615, :641, :1138, :1174` | Feedback waits (bounded, in order) for the first-pixel report; `PresenterStats.UntimedPresents` (B2c). | `OffscreenPresentTests.Feedback_waits_for_late_first_pixel_reports_and_counts_the_one_that_never_comes` |
| `src/FrameMotionPlus.Vulkan/Presentation/OffscreenTarget.cs:216-224` | Internal test seam: synthetic first-pixel reports. | same |
| `src/FrameMotionPlus.Vulkan/Presentation/VulkanPresenter.cs:442, :567` | `TryPresent`/`Release` read the frame's `Ready`. | `OffscreenPresentTests` |
| `src/FrameMotionPlus.Vulkan/Windowing/SdlNative.cs:22, :83-85`, `SdlWindow.cs:216, :226` | `SDL_SetWindowFullscreen`. | `SdlBindingTests` |
| `src/FrameMotionPlus.App/Decode/DecodeRun.cs:564` (in scope) | `fmp decode` waits on the frame's `Ready`. | existing tests |

Tests in other projects updated for the collapsed ready point: `tests/FrameMotionPlus.Core.Tests/Frames/FrameTypesTests.cs`,
`tests/FrameMotionPlus.Sources.File.Tests/{DecodeTests,SerialSourceTests,Support/Frames}.cs`,
`tests/FrameMotionPlus.Vulkan.Tests/{AllocationTests,OffscreenPresentTests,Support/OffscreenRig,WindowedTests}.cs`.

## CPU per slot, per thread

CPU time each thread used over the measured window (`CLOCK_THREAD_CPUTIME_ID`), divided by the slots:

| Thread | The gate (119.88 Hz, stride 1, not phase-locked) | Before the review, 240 Hz stride 2 (run 5) |
| --- | --- | --- |
| pacing | 17.1 µs | 12.7 µs |
| generator | 102.4 µs | 40.5 µs |
| present | 114.2 µs | 95.4 µs |
| control | 16.4 µs | 14.9 µs |

At 119.88 Hz 96% of the slots are generated (one VFG call per refresh), so the generator thread costs twice what it does
when the file is phase-locked; at 120 slots a second the four threads together use about 3% of a core. The present thread
polls for feedback, readiness and (new) the written counter every 250 µs between presents. With CUDA's spin scheduling the
generator thread cost 345.9 µs per slot (see "Decisions").

## Other runs

All on the published binaries (the one before the review as "old", this one as "new"), windowed unless said.

**The burst, reproduced** (the reviewer's way: a starting lead too short for the compositor; 640x360, 13 s, the gate clip,
VFG Medium 2x; both binaries' windows opened on the 119.88 Hz output). Misses per 2 s over the whole run, warm-up included:

| Run | Misses per 2 s | Missed (as the binary reported it) | Lead |
| --- | --- | --- | --- |
| old, 3.5 ms | 0, 4, 4, 4, 4, 2, 2 | 20: 10 "unattributed" + 10 catch-up, "the pipeline's own 0" | 3.5 ms fixed |
| new, 3.5 ms start | 0, 3, 0, 0, 0, 0, 0 | 3: 1 shown late although handed over in time + 2 catch-up (the display's); the pipeline's 0 | raised 3x to 6.5 ms |
| old, 2.6 ms | 0, 4, 4, 4, 4, 4, 2 | 22: 11 "unattributed" + 11 catch-up, "the pipeline's own 0" | 2.6 ms fixed |
| new, 2.6 ms start | 0, 0, 0, 0, 0, 0, 0 | 0 | raised 2x to 4.6 ms (before the first frame) |

With the old binary every miss was a generated frame (shown late or not shown) and they went on at the same rate for the
whole run; the new one adapts within its first second and they stop. (At 240 Hz the reviewer's burst had the same shape:
a generated frame late, the real frame after it dropped.)

**GPU work that never finishes** (`FMP_PLAY_FAULT=gpu-wait@200`, offscreen, real clock, 240 Hz): blend: stall detected
(no ring write finished for 10 s), teardown abandoned, summary printed, exit 1 after 19.0 s; VFG (the generator thread
blocked inside the SDK as well): exit 1 after 24.0 s, "the generator thread did not stop" and the abandonment reported.

**A signal during start-up**, one SIGINT straight to the process (offscreen real clock and a 320x180 window, 8 s runs):

| Sent at | old offscreen | old windowed | new offscreen | new windowed |
| --- | --- | --- | --- | --- |
| 0.05 s | exit 130, no summary | exit 130, no summary | stopped by the user, exit 1, 0.3 s | stopped by the user, exit 1, 0.3 s |
| 0.15 s | exit 130, no summary | lost: played all 8 s | stopped by the user, exit 1, 0.3 s | stopped by the user, exit 1, 0.3 s |
| 0.6 s | stopped by the user, 0.7 s | stopped by the user, 0.7 s | stopped by the user, exit 1, 0.7 s | stopped by the user, exit 1, 0.7 s |

(Exit 1: stopped before anything was measured, which no longer passes.)

**Short runs** (30 s, looped, VFG Medium 2x; all three windows on the 119.88 Hz output, not phase-locked):

| Run | Clip | Missed | Hand-over p1 / p50 | Delay to light (lead + D + rounding) | GC; allocated | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| 30 → 60 | H.264 1440p30 | 0 | 12.68 / 14.14 ms | 12.51 + 18.58 + 4.13 ms (stride 2) | 0/0/0; 0 B | PASS |
| Matroska | H.264 1440p60, MKV (1 ms timestamps) | 0 | 12.65 / 12.82 ms | 12.51 + 18.57 − 8.75 ms | 0/0/0; 0 B | PASS |
| HEVC Main10 | HEVC Main 10 2160p60, BT.2020 / ST 2084 (HDR10 swapchain) | 0 | 12.66 / 12.87 ms | 12.51 + 18.47 − 8.56 ms | 0/0/0; 0 B | PASS |

Before the review (the earlier binary, judged with the earlier accounting): 30 → 60 and 4x at 240 Hz had no miss; 4K and
HEVC Main10 had their misses at output moves and one 50 ms display stall. Offscreen runs of every generator, and the
existing commands (`fmp decode --verify`, `fmp pattern --offscreen --validate`, `fmp simulate --check`, `fmp bench vfg
--size 1440p --mode medium`: d p99 1.39 ms) passed; exit 69 without a GPU, FFmpeg, a display, or the SDK for
`--generator vfg`.

## Tests

`tests/FrameMotionPlus.Pipeline.Tests` (112 cases; the pure ones always run; `Gpu`, `Display`, `Vfx` ones skip, never
fail, without the hardware; serial; concurrent GC and TieredPGO off; clips made with the `ffmpeg` CLI at test time; every
wait bounded):

- **OffscreenVerificationTests** (13): every presented frame, read back on the virtual clock, is what the scheduler
  decided: real frames bit-exact, blend frames bit-exact with a CPU blend, repeats equal, content in order; 30 → 60,
  60 → 120, 30 → 120 at 4x, 24 → 60, VFR, 10-bit HEVC, 1080p/1440p/2160p; nothing interpolated across a loop, a seek or
  a timestamp jump.
- **VfgPlaybackTests** (1): VFG closer to the true middle frame than blend.
- **FaultTests** (15): a throwing generator, a slow GPU, the present and generator threads stalled 0.5 and 3 s, dropped
  presents releasing their slots (both clocks), a generator thread that makes no progress, **GPU work that does not finish
  (abandoned, Run returns, the process recovers)**, **the first frame of the file shown first (both clocks)**, the end of
  the file, 15 sessions in a row, a stop with writes in flight.
- **AllocationTests** (6): 0 B on every thread and no collection, blend/none/VFG at 240 Hz; an injected allocation caught.
- **WindowedTests** (5): one present per refresh (blend, none, VFG; the delay to light's rounding within its bounds);
  **a fixed lead too short for the compositor counted as the pipeline's misses**; **a short starting lead adapts and its
  misses stop**. They skip only for misses proven to be the display's.
- **CliTests** (39): options, `--gate`, help, offscreen runs, a missing file, the relaxed gate verdict, **a run that measured
  nothing does not pass**, exit 69 cases, the fallback to blend, **a never-finishing GPU wait ends `fmp play` with its
  summary (blend, VFG; out of process)**, **SIGINT during start-up (out of process)**.
- Added by the re-check pass: **VerifierTests** (5: the review's gate/, gate2/ and dist/ excerpts and a clean 240 Hz one
  in `Fixtures/`, and each definition on its own), **WindowedTests.No_slot_is_shown_before_its_refresh** (29.97 and
  60 fps; 597 and 582 early slots at 119.88 Hz with the old rule put back), **StartOffsetTests** (1), the gate verdict's
  verifier cases in CliTests (unjudged, disagreement, verifier not run), `fmp verify` in the help. The windowed tests
  also require the verifier to agree with the live counters, skip as "environment" when the output is off a fixed grid,
  and keep each run's CSV with `FMP_TEST_CSV_DIR=<dir>`; this test host runs without tiered compilation (see its csproj).
- **MissAccountingTests** (7), **LeadControllerTests** (4), **CatchUpPolicyTests** (4), **DisplayGridTests** (4, one for a
  move between outputs), **GpuClockTests** (1), GcConfigurationTests (2).

Other projects: Vulkan `OffscreenPresentTests.Feedback_waits_for_late_first_pixel_reports_and_counts_the_one_that_never_
comes` (now also a report without a time, and the release as soon as the next report has come) and the windowed feedback
test (reports that never came against the compositor's time-less ones); Core `FirstImageTests` (4) and the CSV's
`unjudged` column; Gpu and Pipeline SIGINT tests started with SIGINT at its default; Platform `ImmediateExitTests`; and the earlier ones (Core, Platform, Sources.File, Vulkan) listed in the table above.

How each new test was shown to fail on the code before its fix: GPU clock, run against the old `GpuClock` (333.8 µs off,
limit 60 µs); feedback hold, the hold set to 0 (the old behaviour: expected the report's time, got 0); first frame, the
old binary's first shown frame 2 (CSV), and the reviewer's 11; SIGINT, the old binary killed (no summary) or deaf to it
(the table above); a short lead's misses, the old binary's reduced-lead runs (all reported as not the pipeline's, never
stopping); measured nothing, the old binary printed PASS; abandonment, the reviewer's run of the same fault hung for over
150 s (and the first version of this work hung in teardown in its first gate run); the attribution, lead and catch-up
classes are new (their tests describe the old behaviour they replace, where it applies).

The whole solution, `dotnet test FrameMotionPlus.slnx -c Release`, once after the last code change (with `FMP_VFX_SDK` set:
this work's build output was outside the checkout, where `fmp` does not look for `lib/VideoFX`), then the new and changed
tests twice more on their own:

| Project | Passed | Skipped | Before this phase |
| --- | --- | --- | --- |
| Core | 675 | 0 | 669 |
| Platform | 100 | 1 (as before) | 97 + 1 |
| Cuda | 143 | 0 | 143 |
| Vulkan | 97 | 0 | 94 |
| Gpu | 121 | 0 | 121 |
| Vfx | 92 | 1 (needs `FMP_VFX_SDK` unset; set for this run) | 93 |
| Sources.File | 154 | 0 | 152 |
| Pipeline | 112 | 0 | new |

(The re-check pass, 18 Nov: the whole solution once after its last code change, the window on HDMI-A-1 at 119.88 Hz for
the windowed tests in that run; then the NativeAOT binary published clean and played a 30 s windowed VFG run of
gate-1440p60.mp4 there: 3,356 slots, 0 missed live and from the verifier, 0 of 3,356 intervals off the grid, all shows
timed, 0 B on every thread, GC 0/0/0, PASS; and a 10 s offscreen blend run, PASS. Not a gate run.)

## What Phase 5 and Phase 6 must know

- **Phase 5 (capture).** `IPlaybackSource` is the seam: a live source reports its real arrival and needs no start offset
  (a live stream has no "first frame of the file"). Frames are released with the last reader's GPU point after that write
  is enqueued. A capture device would stay held if teardown hung: teardown now abandons GPU work that does not finish and
  `fmp` exits; a capture source must likewise not wait for the GPU in its own stop.
- **Phase 6 (display timing).** In place: first-pixel feedback held until it arrives, the grid from it, rate changes, the
  measured hand-over deadline, the adaptive lead. Still Phase 6: estimating the period from feedback; VRR (untested end to
  end); a lead that also comes down below its start when the latch allows (the adaptation only goes up and back to the
  start); the stride's refresh in the delay (above); the start-up slew of D.
- **A 60.000 fps file on the 119.88 Hz output** is not phase-locked (the rates differ by 0.1%): the scheduler keeps τ = V − D
  on the refreshes and interpolates at whatever timestep τ falls on, so almost every slot is generated (96% in the
  reviewer's run; 29 real against 536 generated in a 5 s windowed test here), and VFG runs at twice the rate it needs (103
  µs of generator-thread CPU per slot measured by the reviewer). That is the scheduler doing what the design says for a
  live source. For a file there is no live constraint, and resampling the playback clock to the display by 0.1% (playing
  the file at 59.94 fps on a 119.88 Hz output) would phase-lock it, show real frames at every other refresh and halve the
  generation work; the design doc does not forbid it. Recorded as a decision for Phase 6 (it belongs with the display
  clock work), not changed here.
- **Millisecond container timestamps** (Matroska, MPEG-TS at 1 kHz): the scheduler still reports T = 17.000 ms for a 60
  fps MKV, and the cap can be exceeded by the 3% slack. De-quantising in the file source (snapping to the nominal rate's
  grid when the time base is 1/1000 and every timestamp is within 1 ms of it) would be cleaner; not done: it changes the
  file source's content clock for every container and needs its own tests (Phase 6 or a 4a follow-up).
- **Once a second.** The reviewer saw, at a marginal lead, one late show per second exactly 1.000 s apart. Ours once a
  second: the present-timing re-calibration on the present thread (measured 6.7 µs) and the watchdog (only with
  `FMP_PLAY_WATCHDOG`); nothing else in the pipeline runs on a one-second period (the GPU clock renews every 2 s, on the
  generator thread, a few µs). The cause is likely the desktop (the Plasma panel's clock repaints every second, making the
  compositor's frame heavier then): not established.

## What `docs/implementation-plan.md` and the design doc should change

Not edited here; suggested:

- The Phase 4 gate already carries the owner's relaxation of 2026-11-18 and the attribution rule; it could add the measured
  deadline as its definition of "in time" (the hand-over lead that has made its refresh on this output in the last 10 s,
  plus 0.25 ms; no evidence, no display fault).
- Present-timing feedback (first-pixel times, held until they arrive) is a 4b dependency on KWin, not Phase 6.
- The latency model should include the present lead (and say it adapts) and the stride's rounding.
- The release rules as pipeline rules (after the read is enqueued; every write presented or released).
- Teardown with GPU work that never finishes: abandon, report, exit (a rule Phase 5's capture source needs too).
- The playback-clock resampling decision for files (above).

## Unfinished and uncertain

- **The window's output** is the compositor's choice (and the window may be moved): runs land on 240 or 119.88 Hz; a move
  costs a few misses, counted as the rate change's. Not testable end to end (a Wayland client cannot move its window).
- **Display-proven misses** need recent evidence: right after a rate change or at the start, a late show is the pipeline's
  until on-time shows have been seen (conservative by design).
- **The adaptation** only goes up from the configured start and back to it; it does not probe below.
- **CUDA stream count** is reasoned, not instrumented; **the convert-done point** is not on the GPU clock.
- **An abandoned teardown** leaves the decode thread blocked (it is never told to stop: stopping it waits for it), which
  is harmless only because the process then exits; a long-lived host (Phase 5's service, if any) would need the source to
  have a non-blocking stop request.
- **VFG against blend** on the tests' synthetic clips is a modest margin; quality scoring is 4c's harness.
- **The published binary** finds the VFX SDK in `lib/VideoFX` only inside its checkout; elsewhere `FMP_VFX_SDK`.

## Reproducing

```sh
dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64
fmp=artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp
export FMP_VFX_SDK=$PWD/lib/VideoFX     # only needed for a binary outside the checkout

ffmpeg -f lavfi -i "testsrc2=size=2560x1440:rate=60,trim=duration=60" -c:v libx264 -preset veryfast \
  -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 gate-1440p60.mp4
$fmp play gate-1440p60.mp4 --gate --csv gate.csv      # 10 minutes windowed, VFG Medium 2x, looped; verdict

# the burst reproduction (a starting lead too short for the compositor; it adapts)
$fmp play gate-1440p60.mp4 --loop --duration-s 13 --window 640x360 --present-lead-ms 3.5 --csv lead.csv
# GPU work that never finishes: the run stops, says what teardown left, and the process exits
FMP_PLAY_FAULT=gpu-wait@200 $fmp play gate-1440p60.mp4 --offscreen --realtime --duration-s 120
# a signal during start-up
$fmp play gate-1440p60.mp4 --window 320x180 --duration-s 8 & sleep 0.15; kill -INT $!

ffmpeg -f lavfi -i "testsrc2=size=2560x1440:rate=30,trim=duration=60" -c:v libx264 -preset veryfast \
  -pix_fmt yuv420p -colorspace bt709 -color_primaries bt709 -color_trc bt709 c1440p30.mp4
ffmpeg -i gate-1440p60.mp4 -c copy -t 30 c1440p60.mkv
ffmpeg -f lavfi -i "testsrc2=size=3840x2160:rate=60,trim=duration=20,format=yuv420p10le" -c:v libx265 -preset ultrafast \
  -x265-params "colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400" \
  -pix_fmt yuv420p10le -tag:v hvc1 c2160p60-hevc10.mp4
$fmp play c1440p30.mp4 --loop --duration-s 30        # 30 → 60
$fmp play c1440p60.mkv --loop --duration-s 30        # Matroska (millisecond timestamps)
$fmp play c2160p60-hevc10.mp4 --loop --duration-s 30 # HEVC Main10, PQ (HDR10 swapchain where offered)
```

Test suites: `dotnet test FrameMotionPlus.slnx -c Release` (hardware tests skip without a GPU, display, SDK or FFmpeg).
