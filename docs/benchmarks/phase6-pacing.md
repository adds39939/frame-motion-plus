# Phase 6: pacing and latency

Scope (`docs/implementation-plan.md`, Phase 6; design doc milestone 3): how late and how evenly frames reach the screen.
Time-boxed by the owner: work down a priority list (trustworthy measurement; the latency the pipeline adds for live
capture; variable refresh; fixed refresh verified on both outputs; 3x and 4x; scheduler backlog if time remains), each
item to a working, tested, measured state, and say plainly what was not done.

Machine: RTX 5090, driver 620.70.06, Linux 7.3.4 (Arch), KDE Plasma 6.8.2 / KWin Wayland; DP-3 3840x2160 at 240.016 Hz
(primary) and HDMI-A-1 3840x2160 at 119.88 Hz, both HDR, VRR "automatic". Elgato 4K X on `/dev/video0`; the PS5 was asleep
when this work started (black frames and the card's "no signal" still) and on its home screen from about 13:50 (a moving
picture of 30 fps content: the runs below say which). Real-time scheduling refused for this user (every thread SCHED_OTHER,
nice 0). `fmp` is the NativeAOT binary from `dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64` (the "before"
binary is the same publish of the Phase 5 commit, `2b884e9`). A window was put on a chosen output with a KWin script loaded
for the run and unloaded after (a test aid only: `workspace.sendClientToScreen` for windows whose caption starts with
"fmp"). The owner was using the desktop; each run's load and GPU state are in its log.

## Summary

| | Before (Phase 5) | Now |
| --- | --- | --- |
| Live miss counters vs `fmp verify` | disagreed on a dropped repeat; one late show cascaded to 4 to 5 missed slots | agree (every run here); the cascade's cause fixed |
| Present lead at 119.88 Hz (windowed) | 12.5 ms (1.5 refreshes), only ever rising | measured before each run: latch about 0.7 to 2.5 ms, lead 2.7 to 4.5 ms (one run in twelve: KWin took presents a refresh earlier, lead 10.5) |
| Real frame after its capture timestamp, `4k30-hdr` → 60 | 63.3 ms (p99 67.5) | fixed refresh, windowed: 53.5 ms (p99 57.5); variable refresh, fullscreen: **46.6 ms (p99 46.9)** |
| Real frame after its capture timestamp, `1440p60-hdr` (30 fps content) → 60 | 50.9 ms (p99 61.7) | fixed refresh, windowed: 38.8 ms (p99 42.9); variable refresh, fullscreen: **31.7 ms (p99 31.9)** |
| Variable refresh | never run | **works**: KWin gives a fullscreen window VRR on both outputs; `--clock auto` (the default) uses it when fullscreen and a probe shows the display following the presents; `fmp capture` is fullscreen by default |
| Stride 2 on a phase-locked output | every real frame a whole refresh late | on the refresh τ reaches it |
| 60.000 fps file on 119.88 Hz | not locked: 96% generated, VFG at twice the rate | file clock stretched 0.1%: locked, half the slots real |
| The 10-minute gate run (1440p60 file, VFG Medium 2x, windowed), verified from the telemetry | never (4b's PASS withdrawn) | DP-3: **0 missed** of 143,533 slots; HDMI-A-1: **4 missed** of 71,688, the pipeline's, in one host stall at 38 s (the owner accepts a handful in 10 minutes for now) |
| 3x and 4x output intervals | not measured | sd 0.018 to 0.088 ms on fixed refresh (30 → 120, 40 → 120, 60 → 240), 0.130 ms under variable refresh; no miss |

**Best configuration** (the owner's commands):

```sh
fmp capture --mode 4k30-hdr       # 4K HDR 30 → 60, fullscreen (now capture's default), variable refresh
fmp capture --mode 1440p60-hdr    # 1440p HDR 60 → 120 (a 60 fps game; 30 fps content is 30 → 60)
```

Both on DP-3 (the 240 Hz output, the primary, where the window usually opens). Capture is fullscreen by default now
(`--windowed` or `--window WxH` for a window), and `--clock auto` (the default) takes variable refresh when the pre-roll's
probe shows the display following the presents; otherwise fixed refresh with the measured lead. On HDMI-A-1 variable
refresh works too, but its 119.88 Hz ceiling is below 120.00 fps: 60 → 120 stays on fixed refresh there (see "Variable
refresh"). The owner has played a game this way and finds it very good; a handful of misses in a 10-minute run is
acceptable to the owner for now.

## 1. Trustworthy measurement

### The review's disagreement and cascade (r4k.csv, 160.13 s)

Read from the review's CSV (slots 19299 to 19306, 4k30-hdr at 119.88 Hz, stride 2, present lead 12.5 ms):

| Slot | Kind | What happened |
| --- | --- | --- |
| 19300 | repeat | handed over 14.3 ms before its refresh, shown a refresh late: KWin missed refresh 578.5 (nothing was shown there) |
| 19301 | generated | queued in time; its report came **without a first-pixel time** and its refresh went to 19300: KWin **discarded** it |
| 19302 | repeat | dropped by the present thread to give the lost refresh back |
| 19303, 19304 | real, repeat | shown a refresh **early** |

Two things went wrong, both now fixed:

1. **The cascade: two give-backs for one lost refresh.** KWin gives a lost refresh back by itself: with two presents
   queued, of two ready at a later latch it shows the newer and discards the older. Every late-show episode in the Phase 4b
   and 5 telemetry with two presents queued shows it (the 4b 240 Hz runs a1/a2, 26 and 20 late shows in episodes of one
   or two, each episode followed by one present reported without a time and the FIFO back on time; the review's run:
   19301). The present thread also dropped a slot
   (19302), so the FIFO went a refresh ahead and the next two slots were early (counted as the pipeline's). Now, after a
   late show, the presents already queued are taken to be behind, but new ones are submitted for their own refresh; the
   present thread gives a refresh back itself only once a present submitted after the late show is late too (the
   compositor did not), or at once when one present is queued (lead under a refresh: KWin was seen staying behind then).
   `PresentLoop.cs` (`ExpectGiveBack`, the late-show branch of `OnShown`, `DiscardCause`).
2. **The disagreement: a dropped repeat was never checked against the screen.** The live accounting counted a given-up
   repeat as no miss ("its refresh shows the image it repeats"); here that image (19301) was discarded and the refresh showed
   19303. The collector now holds such repeats until the next timed show after their refresh and judges them by the image
   then on screen, exactly as the verifier does (`TelemetryCollector.OnScreen`, `SettleRepeats`).

Found while reproducing it on a model of the compositor (below), and fixed:

3. **A compositor's missed refresh poisoned the evidence for ten seconds.** The deadline for "in time" took the largest
   lead that was ever late: one compositor miss at the normal lead made the next one at that lead the pipeline's. A late
   show now stops counting once a later present at no longer a lead made its refresh (contradicted), and each on-time
   present is recorded as an upper bound of its real lead (the presenter call it went out in, plus a present-thread turn
   when its content was seen written after the submission) instead of adding a flat 0.25 ms to every one
   (`MissAccounting.RequiredLeadNs`, `RecordOnTime`).
4. **It also poisoned the latch estimate.** The latch's lower bound took "the refresh before was free" as proof the present
   missed that refresh's latch; after a compositor miss it read 12.9 ms (at 119.88 Hz) and made the next presents look
   unreachable, which dropped them. That bound is gone (the 4b note "1.4 to 6.4 ms within one session" was most likely
   this); the latch is measured directly now (below).
5. **The lead went up on the display's misses** (a compositor miss raised it a millisecond for ten minutes) and **a raise
   lost a refresh** (the slot about to be decided found its earlier deadline already past and was skipped). The lead goes up
   only on the pipeline's late shows or a second late show within a second (a latch that moved), and a raise applies from
   the slot after the one being decided (`PacingLoop.PumpDisplay`).
6. **A discarded present handed over in time is the display's** (`MissKind.Discarded`), and so is an early show right
   after one.
7. **A compositor miss attributed to the pipeline still poisoned the latch.** When the late show of a compositor's missed
   refresh could not be proven in time (the gate's conservative rule) it was taken as a missed latch at its lead (12.4 ms
   at the review's 12.5 ms lead); the next presents at that lead looked unreachable and one was dropped while the
   compositor gave the refresh back: the double give-back again, one slot early (seen in one of two simulated runs of the
   review's case). A miss at a lead that other presents made their refresh with (within 0.5 ms) is no longer latch
   evidence (`LatchEstimator.AddMissed`). Since: 11 of 11 simulated runs of that case without a drop or an early slot.

### The verifier next to the live counters

Every windowed run already ran the verifier; the summary now prints its total on the live counters' line (`missed N slot(s)
(the verifier, from the telemetry alone: M; agrees)`), with its own line below. `fmp verify` takes `--vrr` (and `--rate`)
for variable-refresh runs. Every run in this document agrees.

### A model of the compositor for tests

`SimulatedCompositor` (Pipeline; a test seam behind `PlaybackHooks.SimulatedDisplay`): the present thread's window logic
runs on the real clock against an offscreen target whose first-pixel reports come from a model of KWin's FIFO: a latch
before each refresh, one present per refresh in order, missed refreshes on a schedule, the give-back by discard (or not),
or variable refresh; presents complete (for the in-flight cap) when reported. Tests in `SimulatedDisplayTests`.

## 2. The latency the pipeline adds

### What the compositor does (measured with `fmp bench present`)

A new diagnostic, `fmp bench present --test latch|vrr|cadence` (App, `Bench/PresentProbe.cs`): a test pattern written by
Vulkan alone, presented at chosen times, judged from first-pixel times. It runs at most 120 s, and Escape, closing the
window, Ctrl+C or SIGTERM end it early with the results so far (a "stopped" line); test
`CliTests.A_signal_stops_the_present_probe_with_what_it_measured`.

**The latch, windowed** (a staircase: each probe into an empty queue at R − x; 8 s, 640 to 770 probes):

| Output | Made its refresh | A refresh late | The latch (before the first pixel) |
| --- | --- | --- | --- |
| HDMI-A-1, 119.88 Hz | x ≥ 2.25 ms: 98.8%; 2.0 ms: 22%; 1.75 ms: 3% | | **about 1.95 ms** (one probe late at 3.19 ms in the first second) |
| DP-3, 240.016 Hz | x ≥ 4.75 ms: 100%; 4.5 ms: 53% | x ≤ 4.25 ms: 100% | **about 4.44 ms: more than a refresh** |

Stable over each 8 s run (per-second brackets 1.93 to 1.95 ms; 4.43 to 4.44 ms). Earlier phases' "about 5 ms at 119.88 Hz"
came from the lower-bound estimate of item 4 above. The pipeline's own pre-roll sweep (below; its latest hand-over that missed)
measured 0.7 to 2.5 ms at 119.88 Hz in the runs here, and 4.2 to 4.7 ms at 240 Hz: noisier than the 8 s probe (24 to 54
probes over about a second).

**The latch is not fixed: KWin sometimes takes a present a whole refresh earlier.** In one of twelve windowed runs on
HDMI-A-1 (x3-40to120, a minute after a run that measured 1.97 ms, same window size) the sweep found a sharp threshold at
**8.47 ms**: a refresh (8.34) plus 0.13 ms; 15 probes made their refresh and 9 missed it, all within 2 µs of that. The
lead started at 10.47 ms and the run had no miss. At 240.016 Hz the latch was always of that shape: 4.44 ms is a refresh
(4.17) plus 0.27. This looks like KWin's triple buffering (it starts compositing a refresh ahead when its frames are slow;
not confirmed from KWin's side). The measured start covers it; a switch *during* a run from the short latch to the long one
would make every present late until the lead has climbed (a millisecond per step: about six steps at 119.88 Hz), and the
lead is never probed lower again, so a run that started in the long state keeps the long lead.

**Fullscreen**: no latch: **variable refresh is active** on both outputs (section 3).

### The present lead: measured before each run

Before playback the present thread probes the latch with the last pre-roll image (a staircase like the probe's, at most 1.5
s, usually 0.5 to 1 s; black frames, nothing visible) and starts the lead at the latest hand-over that missed plus 2 ms
(`PresentLoop.Vrr.cs`: `SweepStep`, `FinishSweep`; `SweepMarginNs`). The margin covers a present-thread turn (0.25 ms), the latch
moving with the compositor's load (it came 0.3 to 1 ms earlier once playback was under way than over the pre-roll's black
frames, in the first runs with a 1.25 ms margin), and a thread woken a millisecond late on a busy desktop. Then, as
before, the lead goes up a millisecond when a present the pipeline handed over late misses (and now on a second late show
within a second), held for ten minutes, and comes back down to the measured start, never below. Every change is in the
summary (`lead changes: Start 12.513 → Measured 3.476`) and the lead is part of the delay to light. When the late
present had been handed over earlier than the lead asked and still missed, a raise takes the lead to its hand-over lead
plus 2 ms; otherwise one step (found at hand-over: from a short configured start, one step at a time, the lead settled
0.06 ms past DP-3's latch, 4.5 against 4.44 ms, and kept missing there without raising, since those presents were held
by the in-flight cap; `WindowedTests.A_short_starting_lead_adapts_and_the_misses_stop` failed 7 of 12 runs. With the
jump: 1 or 2 raises to 4.7 to 5.7 ms, 8 of 8 runs clean, windowed and simulated. `LeadController.OnLateContent`; the
rule is pinned by `LeadRaiseTests` (fails without it). `SimulatedDisplayTests.A_short_starting_lead_rises_past_the_
latch_and_the_misses_stop` checks the climb end to end but does not reproduce the failure reliably: without the fix it
failed 1 run in 3 before the review and passed 4 of 4 and 8 of 8 since, so it is no proof of the fix. The windowed test
without the fix, on a quieter desktop at the end: 1 failure in 8 (9 misses of the pipeline's in its second half, 6 not
cleared by their timestamps), 7 passes, no skip. An unconditional jump broke two other simulated cases (a 2 ms jump at a
12.5 ms lead skipped vblanks), hence the condition.

| Output | Lead before | Lead now (the runs here) |
| --- | --- | --- |
| HDMI-A-1, 119.88 Hz | 12.5 ms | 2.7 to 4.5 ms (10.5 in one run of twelve: the long latch above) |
| DP-3, 240.016 Hz | 6.25 ms | about 6.4 ms (latch 4.4 + 2): unchanged in effect |

At 119.88 Hz the lead is now under a refresh: one present is queued at a time (the present for R is handed over after
refresh R − P), which is also why the catch-up rule above depends on it. Tests: `SimulatedDisplayTests.The_present_lead_
starts_from_the_latch_measured_before_playback` (2 ms latch: sweep 2.25 ms, lead 4.25 ms, no miss), `When_the_latch_moves_
earlier_the_lead_follows_and_the_misses_stop` (2 → 5 ms mid-run), and the four catch-up cases.

### The stride's extra refresh

At stride 2 on a phase-locked output every real frame was shown a refresh after τ reached it (4.11 ms p50 at 240 Hz in the
4b runs; Phase 1's simulator showed the same at 240.016 Hz: 15.9 ms real-frame latency against 12.5 at exactly 240). The
alignment keeps τ within its hysteresis of each real frame, often a few microseconds before it, and the stride buckets
(measured from the frame before, 1 µs tolerance) put that slot in the previous bucket. With the snap as the tolerance when
phase-locked, the real frame is shown at the slot τ reaches (`FrameScheduler.StrideToleranceNs`); not locked, the old
tolerance (a wider bucket would only lengthen the hold). Phase 1's guarantees are unchanged: all 675 existing Core tests and the
disturbance sweep pass; the new test `FallbackSchedulingTests.At_stride_two_a_phase_locked_source_shows_each_real_frame_at_
the_slot_its_time_reaches` fails before the change. Simulator, 60 fps at 2x on 240.016 Hz: real-frame latency 15.9 → 11.8 ms.

### Other delay between a frame and its first pixel

- **The present thread saw a new job up to a millisecond late.** Its idle wait slept through any wait under a millisecond,
  and the idle wait of a millisecond always measured just under one. Waits of 2 ms or more are on the event now; shorter
  ones sleep a tenth of a millisecond at a time (`PresentLoop.WaitForJob`). Seen as a 0.5 ms jitter of real frames under
  variable refresh; in fixed refresh it ate into the hand-over margin.
- **A live source's frames were noticed up to 2 ms late** by the pacing thread (it polls the source); now every 0.25 ms for
  a live source (`PacingLoop`, `LiveIdleNs`). Under variable refresh a real frame is decided the moment it is there.
- **The rounding to a refresh** (fixed refresh, not phase-locked: 4.16 ms p50 at 119.88 Hz) and **the present lead** are
  what fixed refresh costs; variable refresh removes both.
- **D's terms** (hold, generation lead = p99 d + wake + 0.5 ms margin, arrival lag p99) are as designed; the arrival lag
  (USB delivery, 25.9 ms at 2160p P010, 11.6 ms at 1440p P010) is the card's.

### Latency, before and after (the PS5's home screen, 30 fps content; 30 s runs)

A real frame shown after its capture timestamp (mean; p99), and its parts (p50). Model: ΔL = d + (M−1)/M·T.

| Mode, clock, output | Real frame after capture | = arrival | + generation (hold + lead) | + present lead | + rounding / display delay | Model ΔL |
| --- | --- | --- | --- | --- | --- | --- |
| 4k30-hdr → 60, before: fixed, HDMI-A-1 windowed | 63.3 (67.5) | 26.9 | 18.7 | 13.5 | 3.8 | 18.3 |
| 4k30-hdr → 60, now: fixed, HDMI-A-1 windowed | 53.5 (57.5) | 27.0 | 18.8 | 3.5 | 4.2 | 18.3 |
| 4k30-hdr → 60, now: **variable**, DP-3 fullscreen | **46.6 (46.9)** | 27.0 | 18.8 | 0 | 0.8 (hand-over to first pixel) | 18.2 |
| 1440p60-hdr (30 fps) → 60, before: fixed, HDMI-A-1 | 50.9 (61.7) | 12.3 | 18.8 | 12.5 | 4.4 | 18.2 |
| 1440p60-hdr (30 fps) → 60, now: fixed, HDMI-A-1 | 38.8 (42.9) | 12.2 | 18.6 | 3.7 | 4.2 | 18.0 |
| 1440p60-hdr (30 fps) → 60, now: **variable**, DP-3 fullscreen | **31.7 (31.9)** | 12.2 | 18.5 | 0 | 0.8 | 17.9 |

The generation's part is the model plus 0.5 to 0.6 ms (the scheduler's wake allowance and margin). Spread (p1 to p99 of
real frames): fixed refresh about 8 ms either way (the rounding to a refresh, not phase-locked: 49.3 to 57.5 ms, 34.6 to
42.9 ms); variable refresh 0.4 to 0.5 ms (46.36 to 46.87 ms, 31.52 to 31.87 ms). Every run: 0 missed by
the live counters and by `fmp verify` (with `--vrr --rate 240.016` for the variable-refresh ones), GC 0, 0 B on every thread.
The first variable-refresh capture runs (before the fixes in section 3) had problems that are described there.

A 60 fps game (true 60 → 120) was not available (the console showed its home screen). In its place, the kernel's test
capture device (vivid, 1280x720 NV12 at a nominal 60 fps; it delivers 58.8) at 2x: a real frame after its capture
timestamp 31.7 ms before (fixed refresh, HDMI-A-1, lead 12.5), 23.8 ms now on the same output (lead 4.2), and **11.5 ms
under variable refresh** on DP-3 (p99 11.6; model ΔL 9.7 + the display's 0.7). On fixed refresh a source not locked to
the refreshes has D tend to T + d (the design doc's non-integer case: 19.5 ms here against the model's 9.8, 143 of 3,358
slots real); under variable refresh it is T/2 + d and every other slot is real.

## 3. Variable refresh

### Established by measurement

`fmp bench present --test vrr --fullscreen`: presents at 1.37 and 1.71 refreshes in turn, into an empty queue (6 s each):

| Output, mode | Shows as far apart as their presents | On the refresh grid | Hand-over to first pixel p1 / p50 / p99 |
| --- | --- | --- | --- |
| HDMI-A-1 fullscreen, FIFO | 455 of 467 | 0 | 0.80 / 0.87 / 1.18 ms |
| DP-3 fullscreen, FIFO | 920 of 935 | 3 | 0.62 / 0.72 / 0.92 ms |
| DP-3 fullscreen, FIFO_LATEST_READY | 769 of 779 | 1 | 0.62 / 0.72 / 0.92 ms |
| DP-3 fullscreen, MAILBOX | 767 of 779 | 3 | 0.61 / 0.73 / 0.94 ms |
| DP-3 fullscreen, IMMEDIATE | 766 of 779 | 2 | 0.63 / 0.77 / 0.94 ms |
| DP-3 windowed, FIFO | 1 of 779 | 777 | 4.3 / 6.5 / 8.5 ms (latch + up to a refresh) |

So KWin's "automatic" VRR is on for a fullscreen window and off for a window, on both outputs. Every present mode gives the
same timing under VRR; **FIFO** is the one to use (no tearing, nothing discarded; IMMEDIATE may tear and buys nothing here,
MAILBOX and FIFO_LATEST_READY would discard a present a slot was meant to show). The swapchain takes a present mode now
(`SwapchainTargetOptions.PresentMode`); the pipeline keeps FIFO. Seen in every run, probe and playback alike: once
every 128 presents (exactly: every 1.067 s at 120 presents a second) one is shown 1.5 to 1.9 ms later than usual (2.3 to
2.6 ms after its hand-over); the driver's or the compositor's, not investigated.

### Implemented end to end

- `fmp capture` is fullscreen by default (after the measurement runs, at the owner's request that the plain commands give
  the best configuration): `--windowed` or a `--window WxH` size keeps a window; `fmp play` stays windowed unless
  `--fullscreen`. `CaptureOptions.Parse`; tested in `NegotiationTests.Capture_is_fullscreen_unless_a_window_is_asked_for`.
- `--clock auto|fixed|vrr` on `fmp play` and `fmp capture` (default auto). The window goes fullscreen during the pre-roll
  (it used to after playback started); then, for `auto` when fullscreen or for `vrr`, the present thread runs the same
  probe on the pre-roll's last image (24 presents, 0.6 s at most) and variable refresh is used when at least 80% of
  consecutive shows followed their presents (`PresentLoop.Vrr.cs`: `VrrProbeStep`; `VrrProbeResult`). Otherwise fixed refresh, with
  the latch sweep.
- The scheduler's VRR clock (Phase 1's) with the output's rate as its ceiling: V = ts + D + kT/M, slots at least the
  shortest refresh period apart.
- The present thread in VRR (`PresentLoop.Vrr.cs`: `VrrTurn`, `OnShownVrr`): each slot is handed over at its V (the job carries it: `DueNs`), no
  grid, no fillers, repeats not presented (the display holds the last image), at most two in flight as before. A slot whose
  first pixel is more than half the shortest refresh period from its target (V plus the expected delay to the first pixel,
  the median of the last 256, moved 0.1 ms at a time) is missed: late is the display's when it was handed over on time,
  else the pipeline's; one that cannot be shown within half a period of its time is given up. A generated slot the
  scheduler skips because the next real frame came too late is counted apart (`VrrSkippedSlots`), there being no refresh
  that went without a slot.
- Telemetry and the verifier have a VRR mode (no refresh grid: no refreshes without a slot or a present).
- Tests: `SimulatedDisplayTests.Variable_refresh_shows_each_slot_at_its_own_time` (60 fps at 2x on a simulated 240 Hz VRR
  display: intervals 8.332 ms, sd 0.029 ms; real frames D + 0.98 ms after their content time; no miss; the verifier agrees).

### Results

| Run | Slots | Missed (live / verifier) | Output intervals (excluding skipped slots) | Real frame after content |
| --- | --- | --- | --- | --- |
| 4k30-hdr → 60, DP-3 fullscreen, 30 s | 1,680 | 0 / 0 | 16.67 ms, sd 0.206 ms (4 generated slots skipped: the card delivered late) | 46.6 ms after capture |
| 1440p60-hdr (30 fps) → 60, DP-3 fullscreen, 30 s | 1,680 | 0 / 0 | 16.67 ms, sd 0.159 ms (2 skipped) | 31.7 ms after capture |
| 60 fps file → 120, DP-3 fullscreen, 30 s | 3,361 | 0 / 0 | 8.333 ms, sd 0.197 ms (content steps exact) | 10.9 ms after content (p99 12.4; model 9.7); the same file on fixed refresh at 240 Hz (the DP-3 gate): 19.2 |
| 30 fps file → 120 (4x), DP-3 fullscreen, 30 s | 3,361 | 0 / 0 | 8.333 ms, sd 0.130 ms | 27.5 ms (p99 27.7; model 26.3) |
| vivid at 60 fps (58.8 measured) → 120, DP-3 fullscreen, 30 s | 3,360 | 0 / 0 | 8.334 ms, sd 0.418 ms (the source's own timestamp jitter: content steps sd 0.395) | 11.5 ms after capture (p99 11.6); on fixed refresh at 119.88 Hz 23.8 now, 31.7 before |

**HDMI-A-1's ceiling.** Its fastest refresh is 119.88 Hz: 60 fps at 2x (120.00 fps) is 0.1% too fast; under variable
refresh the slots would queue behind the refreshes and one would have to go every few seconds. So `--clock auto` stays on
fixed refresh when the source's rate times the multiplier exceeds the output's (the summary says so; `--clock vrr` forces
it): a 1440p60 → 120 capture on HDMI-A-1 runs fixed (unlocked: almost every slot generated, D toward T + d). Use DP-3.
Checked on the final binary: a 60 fps file fullscreen on HDMI-A-1 says "not variable refresh: the source at its multiplier
needs 120 fps, more than this output's 119.88 Hz" and plays on fixed refresh, no miss; unit-tested (`ResampleTests`).

**What went wrong first** (the first variable-refresh capture run, 4k30-hdr, fixed since): (a) the expected delay to the first
pixel was a tail estimate and jumped to its 4 ms bound after one compositor stall, so every on-time slot was then judged
early (253 "early"): it is a slow median now; (b) the present thread picked up each real frame's job up to a millisecond
late (the wait above), a 0.5 ms jitter between real and generated slots; (c) the pacing thread noticed the card's frames up
to 2 ms late. Also seen there and **not fixed**: D started at 55 to 63 ms and took about 10 s to come down to 46.7 (a hold re-seeded
the source interval: Phase 1 backlog item 1, below); the later runs did not show it.

## 4. Fixed refresh on both outputs

### Gate-length runs (`fmp play c1440p60.mp4 --gate`: VFG Medium 2x, looped, 10 minutes, windowed 1280x720)

The 60 s 1440p60 test clip (the 4b gate's), each run on one output (KWin script), the published binary of this phase as
it stood at 14:00 (later changes: the summary's wording and lead-change log, the pre-roll's grid resend, the VRR ceiling
rule; short runs of the final binary are under "Final binary"). Desktop in use: KWin, Discord and NZXT CAM as the other
GPU clients; load average 0.5 to 2.

| Output | Slots (real / generated / repeated) | Missed: live (whose) / `fmp verify` | Output intervals | Lead | Real frame after content | GC, allocation | Result |
| --- | --- | --- | --- | --- | --- | --- | --- |
| DP-3, 240.016 Hz (60.000 fps stretched −67 ppm: locked, stride 2) | 143,533 (35,882 / 35,873 / 71,778) | 0 / 0, agree | 4.166 ms, sd 0.078 ms; 0 off the grid | 6.46 ms (latch 4.42 to 4.46 + 2; no change) | 19.2 ms (p99 21.2); the stride's rounding 0.02 ms | 0 / 0 / 0, 0 B | **0 missed** (the run's own verdict: PASS) |
| HDMI-A-1, 119.88 Hz (60.000 fps stretched 0.1%: locked, stride 1) | 71,688 (35,113 / 36,561 / 14) | **4** (the pipeline's, one event at 38 s) / 4, agree | 8.342 ms, sd 0.050 ms; 3 more than a quarter refresh off | 4.48 → 5.48 ms (one raise) | 17.9 ms (p99 19.8) | 0 / 0 / 0, 0 B | **4 missed** (the run's own verdict: FAIL). The owner: a handful of misses in a 10-minute run is acceptable for now |

DP-3: every one of 144,019 shows timed by its first pixel, no miss of any cause, the verifier agreeing (the first gate-length
run verified from raw timestamps: 4b's PASS was withdrawn). Real frames reach the screen on the refresh τ reaches (0.02 ms
of rounding p50, against 4.11 ms in the 4b runs: the stride fix). One collection (gen 2) happened in the start-up, outside
the measured window.

HDMI-A-1, the cause of the 4: a host stall at 37.95 s. Generated slot 4553's generation started 6.7 ms after its decision
(0.1 ms usually), past its refresh; it and the real frame queued behind it were shown a refresh late, and in the next 90 ms
the pacing thread woke 5 to 7 ms late twice (two skipped vblanks); the compositor showed two on-time slots 2 ms off the grid
in the same moment. Every thread runs SCHED_OTHER (real-time priority refused for this user). The reduced lead leaves about
3 ms between a generated slot's decision and the compositor's latch for such a stall (12.5 ms left about 11), so the old
lead would most likely have absorbed it; `--present-lead-ms 12.5` gives a file that margin back (a file is not
latency-bound). Not chased further, at the owner's word. Live counters and verifier agree on all 71,687 slots. Otherwise D
rose for a few seconds at each loop of the file and drifted from 14.8 to 10.7 ms over the run (the alignment following a
few ppm between the display's clock and the system's, it seems).

### 60.000 fps files on 119.88 Hz: the file's clock is stretched

A file within 0.5% of a whole number of refreshes per frame, played on fixed refresh, plays that much slower or faster so it
phase-locks (`PlaybackSession.ResampleFactor`, `PacingLoop.MapContent`; `PlaybackOptions.ResampleToDisplay`, on by default):
60.000 fps on 119.88 Hz is stretched by 0.1% (16.683 ms per frame), real frames land on every other refresh, the generator
runs on the other half. On DP-3 a 60 fps file is stretched by −67 ppm (240.016 Hz), which removes the slow phase drift the
alignment otherwise follows. Virtual-clock test `ResampleTests`: 228 real and 229 generated of 457 slots, phase-locked;
without it 29 real and 428 generated; the factor itself is unit-tested (24, 25, 30, 59.94, 60 fps against 119.88, 120 and
240.016 Hz). A file has no live constraint; audio (Phase 8) will have to follow the same stretch.

**Live capture on fixed refresh** cannot be resampled: a 60.00 Hz console on the 119.88 Hz output stays unlocked (almost
every slot generated, D toward T + d). The choices: variable refresh (the answer: it locks by construction, and costs the
least); or an output at exactly 120.00 Hz (DP-3 offers 3840x2160@120.00); or DP-3 at 240.016 Hz, where 60 Hz is within
67 ppm and phase-locks at stride 2. Documented in the summary's recommendation; nothing else changed.

## 5. 3x and 4x

Test clips at 30, 40 and 60 fps (ffmpeg's moving test picture, 1440p, 60 s), VFG Medium, windowed 960x540 on fixed
refresh, 60 s each, looped; then variable refresh. Output intervals from first-pixel times (after the first 10%); the
content step is the difference in content time between consecutive new images shown (even motion: constant).

| Run | Slots (real / generated) | Missed: live / verifier | Output interval, sd | Content step, sd | Steps off by > 0.5 ms | Real frame after content (model ΔL) |
| --- | --- | --- | --- | --- | --- | --- |
| 30 → 120, 4x, HDMI-A-1 119.88 Hz (stretched 0.1%) | 6,954 (1,728 / 5,226) | 0 / 0 | 8.342 ms, **0.018 ms** | 8.341 ms, 0.016 ms | 0 (after the first second) | 35.5 ms (26.4) |
| 40 → 120, 3x, HDMI-A-1 (stretched 0.1%; the long latch: lead 10.5 ms) | 6,954 (2,308 / 4,646) | 0 / 0 | 8.342 ms, **0.018 ms** | 8.341 ms, 0.015 ms | 0 (after the first second) | 33.9 ms (18.1) |
| 60 → 240, 4x, DP-3 240.016 Hz (stretched −67 ppm) | 13,922 (3,427 / 10,492) | 0 / 0 | 4.166 ms, **0.088 ms** | 4.166 ms, 0.015 ms | 0 | 22.5 ms (14.0) |
| 30 → 120, 4x, DP-3 **fullscreen, variable refresh**, 30 s | 3,361 (840 / 2,521) | 0 / 0 | 8.333 ms, **0.130 ms** | 8.333 ms, 0.000 ms | 22 intervals: one every 1.07 s (every 128th present, +1.5 ms; content exact) | 27.5 ms (26.3) |

Every interval sd is well under 0.5 ms; every fixed-refresh run phase-locked, a real frame exactly every M slots, no
miss. Under variable refresh the content steps are exact (each slot shown at its own time) and the only irregularity is
the display's: every 128th present (exactly: 1.07 s at 120 presents a second) is shown about 1.5 ms late, the next one on
time (section 3). The bridge
keeps up: VFG's d is 1.3 ms p50, 1.4 to 1.5 ms p99 at 3x and 4x as at 2x (one generation at a time on the generator
thread: at 240 Hz three of every four refreshes generate, 4.2 ms apart, a third of the budget); the scheduler's generation
lead (p99 d + wake + 0.5 ms margin) is 1.9 ms. The latency over the model is the present lead plus D's alignment (D ≡
−phase mod P, up to a refresh: 4.2 to 4.5 ms here) and the hold's margin; a file is not latency-bound.

**Periodic judder, 59.94 fps on "120 Hz", 10 minutes.** The HDMI-A-1 gate run above is that case: its 60.000 fps file,
stretched by 0.1%, is a 59.94 fps source on 119.88 Hz. Over its ten minutes the content step between consecutive new
images was off its median (8.342 ms) by more than 0.5 ms only at: each loop of the 60 s file (a seam in the content, 9
times), the stall at 38 s, and one scheduled repeat at 129.5 s (a generation decided too late, shown as the previous image
held for two refreshes; no miss). No periodic pattern. On DP-3 (the 60 → 240 run and the DP-3 gate) likewise none. A
59.94 fps source that is *not* locked (59.94 on a 120.000 Hz output, or a live 59.94 source on 240.016 Hz) was not run:
neither output here runs at 120.000 Hz without a mode change on the owner's desktop, and a file is always stretched to
lock (`PlaybackOptions.ResampleToDisplay` has no command-line switch).

## Final binary

Short runs at 14:55 of the NativeAOT publish just before capture became fullscreen by default (so the HDMI-A-1 capture
row is windowed), each with `fmp verify`; the owner's two commands on the final publish are below the table.

| Run | Clock | Missed: live / verifier | Lead | Real frame after its time | `fmp verify` |
| --- | --- | --- | --- | --- | --- |
| `fmp play c1440p60.mp4 --loop --duration-s 20`, HDMI-A-1 windowed | fixed (stretched 0.1%, locked) | 0 / 0 | 2.73 ms (sweep: latest miss 0.73 ms) | 14.3 ms after content (p99 14.9) | 0 missed; **3 unjudged** (reports without a first-pixel time, their refreshes not taken by another), so FAIL by its rule |
| the same, `--fullscreen`, 10 s | **fixed: the ceiling rule** ("needs 120 fps, more than this output's 119.88 Hz") | 0 / 0 | 12.5 ms (sweep not bracketed under KWin's VRR) | 29.3 ms | PASS |
| `fmp capture --mode 4k30-hdr --duration-s 20 --fullscreen`, DP-3 | variable | 0 / 0 | (0.77 ms to first pixel) | **46.6 ms after capture (p99 46.9)** | PASS (`--vrr --rate 240.016`) |
| `fmp capture --mode 4k30-hdr --duration-s 20`, HDMI-A-1 (windowed: the default then) | fixed | 0 / 0 | 3.47 ms | 53.1 ms after capture (p99 57.6) | PASS |

The capture numbers repeat the measurement runs' (46.6 and 53.5). The three untimed reports in the first run are the first
seen at 119.88 Hz in this session (4b saw them at 240 Hz); it ran at the shortest lead measured here (the sweep found the
latch at most 0.73 ms, against 1.2 to 2.5 in the other runs): whether a present handed over that close to KWin's latch
is what loses its timing report was not established.

**The owner's commands on the final publish** (no options but the duration; DP-3, the PS5's home screen):

| Command (+ `--duration-s 30 --csv`) | Clock chosen | Slots | Missed: live / verifier | Intervals (sd, source gaps excluded) | Real frame after capture | `fmp verify --vrr --rate 240.016` |
| --- | --- | --- | --- | --- | --- | --- |
| `fmp capture --mode 4k30-hdr` | variable (fullscreen by default; probe 61 of 63 pairs following) | 1,678 (840 real) | 0 / 0 | 16.69 ms, 0.171 ms (2 gaps) | **46.6 ms (p99 46.9)** = arrival 27.0 + generation 18.8 + display 0.8 | PASS |
| `fmp capture --mode 1440p60-hdr` | variable | 1,679 (840 real; 30 fps content) | 0 / 0 | 16.70 ms, 0.201 ms (3 gaps) | **31.7 ms (p99 32.0)** = arrival 12.2 + generation 18.6 + display 0.9 | PASS |

GC 0, 0 B on every thread, in both. The source gaps are frames the console did not deliver (an interval of two source
frames), followed by the pipeline as they come.

## Tests

New: `CatchUpTests` (3, pure: the review's cascade replayed record by record through the collector, which now agrees with
the verifier on it; a repeat given up after a real frame shown late; a discarded present the display's),
`SimulatedDisplayTests` (7, `Gpu`: the review's case, 119.88 Hz at a 12.5 ms lead, where the compositor gives a missed
refresh back and the present thread must not as well; the same at 240 Hz, where the in-flight cap keeps the compositor
from giving back and the present thread does; a compositor that stays behind; the lead from the latch sweep, with the
lead-change log; the latch moving; variable refresh; nothing missed), `LatchEstimatorTests` (pure: a miss at a lead that
made its refresh is no latch evidence), `CommandLineTests.An_unknown_clock_or_probe_choice_is_a_usage_error` (5 cases),
`ResampleTests` (2 `Gpu` on the
virtual clock; 2 pure theories: which file rates are stretched and by how much, and when `--clock auto` may choose
variable refresh against the output's ceiling),
`MissAccountingTests.A_late_show_that_later_presents_at_the_same_lead_contradict_no_longer_raises_the_deadline`,
`WindowedTests.The_swapchain_takes_the_present_mode_asked_for_and_refuses_one_the_surface_does_not_offer` (Vulkan, a small
window), Core
`FallbackSchedulingTests.At_stride_two_a_phase_locked_source_shows_each_real_frame_at_the_slot_its_time_reaches`.
Shown to fail before the change: the cascade replay (the old collector called the dropped repeat fine: 1 disagreement);
the 240 Hz give-back simulation against the Phase 5 present thread (14 misses for 3 compositor misses, early and dropped
ones the pipeline's, against 6 the display's, at the time the simulated compositor still gave refreshes back there); the
latch test against the old estimator (by construction: it took the miss); the stride test against the old scheduler
(τ − ts 4.1 ms). The simulated tests assert what must hold (the compositor missed what it was told to, live counters and
verifier agree, nothing early, no double give-back, misses bounded near the events and away from them); whose each miss
is, they log: the conservative rule calls a compositor miss the pipeline's whenever the late slot's hand-over is not
provably as early as one that made its refresh, which depends on a millisecond's scheduling (a generated slot's hand-over
is when its content was seen written). No existing test was
changed (Phase 1's scheduler tests and the disturbance sweep pass as they were; the accounting's existing tests pass with
the new rule's default). The simulated tests run the present thread on the real clock: their assertions allow a few
misses away from the scheduled events (a thread woken late on a busy desktop) and say so.

**Runs.** A first whole-solution run at 14:30 (before the last test fixes, fix 7 and the capture default) had 1 failure:
the 240 Hz give-back simulation, on an assertion that counted give-backs with counters that miss the one the in-flight cap
makes (replaced), and its test helper allocated 56 B on the present thread (a list growing in the simulated compositor's
skip schedule; sized up front now). The new and changed tests were then run repeatedly while fixing what they found (the
stay-behind test's attribution assertion, the 240 Hz bound, the review's case at 12.5 ms that showed fix 7) and twice
after the last pipeline change: all passed both times (Core 11, Pipeline 36, Vulkan 1); Pipeline.Tests in full after fix
7: 137 of 137. The final whole-solution run, on the tree as handed over (`dotnet test FrameMotionPlus.slnx -c Release`,
`FMP_VFX_SDK` set), after the safety review's fixes: Core 681, Platform 100 (+1 skipped), Vulkan 98, Sources.V4l2 115,
Gpu 121, Cuda 144, Vfx 92 (+1 skipped), Sources.File 154, Pipeline 142: **1,647 passed, 2 skipped, 0 failed**. After the
lead-raise fix, Pipeline three times in a row, 139 of 139 each (the windowed short-lead test passing, not skipping). Solution build 0 warnings; NativeAOT publish clean (no trim or AOT warnings); `dotnet build
src/FrameMotionPlus.App -c Release -r win-x64` 0 warnings.

## Changes outside the listed scope

| Where | What and why |
| --- | --- |
| `src/FrameMotionPlus.App/Bench/PresentProbe.cs` (new), `src/FrameMotionPlus.App/Cli/BenchCommand.cs:18` and `:27`, its help in `Cli/CommandDispatcher.cs:75-77` and `:183-193` | `fmp bench present`: the compositor measurements of this phase (latch, VRR, cadence) are reproducible with it. A diagnostic command, not the pipeline. |
| `src/FrameMotionPlus.Vulkan/Presentation/OffscreenTarget.cs` (`PresentTimeSource` public, `PresentSubmitted`, `CompletesOnReport`) | The simulated compositor's seam (Presentation, in scope). |
| `src/FrameMotionPlus.Vulkan/Presentation/SwapchainTarget.cs` (`SwapchainPresentMode`, `SwapchainTargetOptions.PresentMode`, `PresentModeName`) | For the probe's present-mode comparison (Presentation, in scope); the pipeline keeps FIFO, the default. |

No change to Platform, Cuda, CudaVulkan, Vfx, Sources.File or Sources.V4l2 (a test added in `Sources.V4l2.Tests` for the
capture default). Core: only `Scheduling/FrameScheduler.cs` (the stride tolerance). `docs/implementation-plan.md` shows a
change made at 14:58 by someone else (the owner's tolerance and a "Perfecting frame pacing" deferral), not by this work.

## What `docs/implementation-plan.md` and the design doc should change (not edited)

- The latency model should carry the presentation term per clock mode: fixed refresh adds the present lead (now the
  measured latch plus 2 ms: 2.7 to 4.5 ms at 119.88 Hz in most runs, 6.4 at 240) plus the rounding to a refresh (up to a refresh, half
  on average when not phase-locked); variable refresh adds the display's hand-over-to-first-pixel delay (0.7 to 0.9 ms here).
- "VRR (preferred)": confirmed; on KWin it needs a fullscreen window (VRR "automatic"), and the output's ceiling matters
  (119.88 Hz cannot take 120.00 fps).
- The "Found in Phase 2" note that present timing cannot target times stands; the latch is measurable from feedback alone.
- KWin's FIFO gives a lost refresh back by itself when two presents are queued (it discards one): a pacing rule.
- The compositor's latch is not a constant of the output: at 119.88 Hz it was 0.7 to 2.5 ms in eleven runs and a refresh plus
  0.13 ms in one; at 240 Hz always a refresh plus 0.27 ms. Measure it per run (done) and expect it to move.
- Phase 1 backlog item 11 (VRR accepts up to 2% over the panel's maximum) is answered for the pipeline by the auto rule
  here (stay on fixed refresh when the rate does not fit); the scheduler itself still accepts it.
- Phase 6's gate: 30 → 120, 40 → 120 and 60 → 240 interval sd under 0.5 ms are met on fixed refresh (0.018 to 0.088
  ms); the 10-minute 59.94 judder check was met by a locked 59.94-equivalent source only (section 5).
- The 4b gate's rule: with a lead near the latch, a host stall of more than about 3 ms between a slot's decision and the
  compositor's latch costs slots that count as the pipeline's (SCHED_OTHER threads on a desktop in use). Either the gate
  accepts that a file may run with a longer lead (`--present-lead-ms`), or it needs real-time scheduling (refused here).

## Known issues / not done

Priority by priority (the brief's order), then the backlogs.

1. **Measurement.** Done. Left: a compositor miss of a present handed over at the usual lead is the display's only when
   the timestamps prove it in time. After one, its late show stays evidence that this lead can miss until a later present
   provably made its refresh with less lead (each one's measured uncertainty included), which presents at one lead seldom
   do; so a second compositor miss within 10 s of a first is often counted as the pipeline's. Seen in the simulated
   stay-behind test (in one of its runs: the second episode, 1.5 s after the first, the pipeline's); not in the hardware
   runs here (no compositor misses close together). Conservative, as the gate's rule asks, but a gate run can be failed by
   two compositor misses. When the compositor does *not* give a lost refresh back with two presents queued (the
   simulation does that in some runs, depending on whether the next present stands beside the late one at a latch), the
   present thread's own catch-up waits for a present submitted after the late show's report to be late too: 3 to 4
   refreshes at 240 Hz, so one lost refresh costs 4 to 7 slots instead of 2 to 3. A generated slot's hand-over is taken
   as when its content was seen written (a present-thread turn coarse), so a compositor miss that lands on one is often
   not provable and counts as the pipeline's (the review's case simulated: the first episode the pipeline's in 2 of 13
   runs). Fix 7 is argued from the one failing run's records and checked by 11 clean runs since; the failing condition
   (the first late show called the pipeline's) recurred once in those, without the double give-back. Reports without a
   first-pixel time: 3 in the final binary's 20 s windowed run at 119.88 Hz (unjudged, so `fmp verify` fails it); none in
   the other runs here.
2. **Latency.** The lead is measured once, before playback, on black frames; it is then raised by evidence, never probed
   lower during playback (that would cost visible misses). The 2 ms margin is a judgement from a handful of runs. The
   stride's extra refresh is gone only where phase-locked (where it occurred). A 60 fps game (true 60 → 120) was not
   available: the 1440p60-hdr rows are 30 fps content (vivid stood in for a 60 fps source). The shorter lead leaves less
   room for a host stall (the HDMI-A-1 gate, section 4). D's start-up rise on a capture (item 6) cost latency for the first
   seconds of one run.
3. **Variable refresh.** Works fullscreen on both outputs; windowed is fixed refresh (KWin's choice). Left: generated slots
   skipped when the card delivers a frame later than D's arrival-lag p99 (2 to 4 in 1,680 slots: the image before stays
   twice as long; a higher percentile or a margin for live sources would trade latency for it); about every 128th present
   is shown 1.5 to 1.9 ms late (driver or compositor); `fmp verify` needs `--vrr` (the CSV does not say which clock it was);
   the VRR misses judged with half the shortest refresh period are generous for a 16.7 ms slot; HDMI-A-1's 119.88 Hz ceiling
   (above); no test drives the pre-roll's VRR probe itself (the simulated display enters VRR directly). Fullscreen on the
   fixed clock (the ceiling rule, or `--clock fixed`) KWin's VRR is still on: the latch sweep cannot bracket (every probe
   made its refresh) and the lead stays at 1.5 refreshes (final binary, HDMI-A-1 fullscreen 60 → 120: no miss, real frames
   29.3 ms after their content time where the windowed run had 14.3); the VRR probe's delay would be the right lead there.
4. **Fixed refresh.** Gate-length runs: DP-3 0 missed; HDMI-A-1 4 missed in one host stall (38 s, the pipeline's by the
   rule; the owner accepts a handful in 10 minutes for now, so not chased): the shorter lead leaves about 3 ms for a stall
   where 12.5 ms left 11. One run per output, not repeated; the strict gate (zero of the pipeline's) is not shown met on
   HDMI-A-1. The latch can change by a whole refresh between runs (KWin); a change during a run is handled only by the
   lead's climb. A window moving between outputs during a run is still untested end to end.
5. **3x and 4x.** Interval sd 0.018 to 0.088 ms, no miss, on both outputs. The 10-minute judder check is the HDMI-A-1 gate
   run (a 59.94-equivalent source on 119.88 Hz: no periodic judder); a 59.94 source *not* locked to its output was not run
   (no 120.000 Hz output without a mode change; files are always stretched to lock).
6. **Scheduler tuning backlog (Phase 1 review): not started** (the brief's "only if time remains"; the owner then asked
   to wrap up). None of its 12 items is done; item 11 (VRR accepting rates over the panel's maximum) is side-stepped for
   the pipeline by the auto rule (fixed refresh when the rate does not fit). Item 1 showed in capture: in the first variable-refresh run D
   started at 55 to 63 ms (a hold and the start-up's primed repeats widen the source interval; that run's pacing thread also
   saw frames up to 2 ms late, since fixed) and took about 10 s to come down; the 1440p60 baseline on fixed refresh had D
   reach 41.9 ms. The later runs' D stayed within 0.3 ms of its value.
7. **Perfecting the pacing** (zero missed slots in every 10-minute run, on every output): deferred by the owner, who
   accepts a handful of misses per run for now.
8. **A compositor that skips refreshes on its own.** At hand-over, with a browser and Discord open, KWin missed one
   refresh every second on DP-3 (windowed, fixed refresh): presents handed over up to 7.8 ms ahead, nearly two refreshes,
   were shown a refresh late too, so no lead helps (and none is raised for it). A user sees one hitch a second (2 slots a
   refresh late, then 2 more behind them) for as long as it lasts; the misses count as the display's, but one within 10 s
   of another can count as the pipeline's by the conservative rule. The windowed short-lead test now says "environment"
   and skips only when every miss of the pipeline's in its second half is cleared by its own timestamps (handed over at
   least 1 ms before presents that made their refresh, or within four refreshes of a miss the display owns); any other
   fails it. It did not skip in the runs since.
   Not investigated on KWin's side (it was absent during the gate runs earlier: 0 misses in 10 minutes on DP-3).
9. **Tracked from the safety review, not fixed:** the latch sweep leaves `_sweepOut` set when a probe's report never comes,
   so the sweep then waits out its budget (`PresentLoop.Vrr.cs`, `SweepStep`); forced `--clock vrr` with the output's rate
   never known gives a half-period of 0 and could freeze the picture (`VrrTurn`, plausible, not reproduced); variable
   refresh never falls back to fixed mid-run if the display stops following the presents (it keeps presenting at the
   slots' times); `SetFullscreen`'s result is ignored (`PlaybackSession`, the pre-roll), so a refused fullscreen goes
   unreported; `fmp` started as a background job from a non-interactive shell ignores SIGINT (the shell sets it to
   ignored; SIGTERM works). Untested by an automated test: at the end of a run a fullscreen window is now minimised
   before the teardown waits for the threads, so with stuck GPU work the desktop comes back at once instead of after
   the stall timeout (`PlaybackSession`, the `finally` of the run).

The 4b pacing backlog: 1 (gate runs) run on both outputs, verified (DP-3 0 missed; HDMI-A-1 4 missed, one stall); 2 (the latch)
measured directly, the poisoned estimate removed; 3 (240 Hz) regular in this session (the 60 → 240 run: interval sd 0.088
ms, 0 of 13,920 intervals off the grid, every show timed; its latch 4.44 ms, more than a refresh; the 10-minute gate run there
missed nothing, every one of 144,019 shows timed); 4 (the lead) done; 5 (the stride) done where phase-locked; 6 (60 fps files
on 119.88 Hz) done (stretched); 7 (millisecond container timestamps), 8 (moving between outputs), 9 (`FMP_PLAY_FAULT` in
the release binary): not done.

Other: the "decode thread allocated 32 B" failure seen in fullscreen runs of the JIT build (`dotnet build`'s `fmp`, not the
NativeAOT binary) was not chased; NativeAOT runs here allocate nothing on any thread.
