# VFG's automatic shot-change detection and fast motion (`--scd`), 2026-11-20

## The report

Playing a 30 fps game at 2x and 3x, the owner saw "really fast scenes/action, and objects up close, especially foliage,
seem to disable the frame generation until they go away. It makes driving really fast a bit jarring."

## The cause

VFG's own automatic shot-change detection, the SDK's default, which the app kept on since Phase 3. On a pair it takes
for a cut, VFG runs its model and then outputs a real frame unchanged instead of the interpolation: the previous frame
for t = 0.25 and 0.5, the current one for t = 0.75 ([phase3-vfg.md](phase3-vfg.md), "5. Shot changes"). Large, uneven
change between two frames is taken for a cut, so during fast motion every generated frame is a copy and the picture
falls to the source's rate (at 3x: previous, previous, next). The app itself had asked for a generated frame in every
slot (the owner's 4 h 47 min capture log: `ring writes: 514364 copies, 1028564 generated, 0 fallbacks`, no holds, no
discontinuities); it has no cut detection of its own. The SDK has no sensitivity setting
(`nvVFXVideoFrameGeneration.h` offers only `ShotChange`, the manual flag, and `AutomaticShotChangeDetectionEnabled`).

### Measured: copies against motion

`ShotChangeMotionTests.Automatic_detection_takes_fast_motion_for_a_cut_and_off_it_never_copies` (Vfx tests; RTX 5090,
driver 620.70, VFX SDK 1.3.0; 3840x2160 RGBA8, Medium). Each case is five frames: four pairs at t = 0.25, 0.5 and 0.75,
the first pair priming the generator, the other three (9 generated frames) counted. A copy is an output bit-identical to
one of its two inputs. *Pan*: a detailed value-noise texture (features from 64 px down to 3 px) moved by whole pixels.
*Foliage*: green detail from 32 px down to 1 to 2 px, each pixel moved by the speed times a smooth field that varies over
100 to 250 px (0.2 to 1.0 of it to the right, up to ±0.35 up or down), so neighbouring patches move at different speeds
and directions, as leaves rushing past do.

| Motion per frame | Detection on: copies of 9 (p = previous, c = current, - = interpolated) | Detection off: copies | Off: mean distance to the nearer input (codes) |
|---|---|---|---|
| pan 8 px | 0 (`--- --- ---`) | 0 | 8.5 |
| pan 32 px | 0 | 0 | 16.9 |
| pan 96 px | 0 | 0 | 24.7 |
| pan 192 px | 0 | 0 | 29.0 |
| pan 384 px | 0 | 0 | 31.9 |
| foliage 8 px | 0 | 0 | 11.6 |
| foliage 32 px | 0 | 0 | 18.1 |
| foliage 48 px | **2** (`--- --- p-c`) | 0 | 19.8 |
| foliage 64 px | **6** (`--- ppc ppc`) | 0 | 21.0 |
| foliage 96 px | **9** (`ppc ppc ppc`) | 0 | 22.2 |

So it is not the size of the motion: a uniform pan of a tenth of the frame's width a frame is still interpolated. Uneven
motion is: detection starts copying at about 48 px a frame of foliage moving at different speeds and directions and
copies every generated frame from about 96 px, in exactly the documented pattern (previous, previous, current). With it
off, no output was a copy at any speed. The test asserts what it found (pan never copied, foliage 8 px never, foliage
96 px always, off never), so an SDK update that changes it fails there.

## The fix

`--scd on|off` on `fmp capture` and `fmp play` (it was only on `fmp bench vfg`), saved as
`generator.shotChangeDetection`, plumbed through `PlaybackOptions.ShotChangeDetection` to
`VfgOptions.AutomaticShotChangeDetection` (both of Q's instances come from the one generator's options). **Off by default
for `fmp capture`, on for `fmp play`.** Games have fast motion and few cuts, and the owner judged it on a real session
(below); films cut often, and detection shows each cut as a real frame. With it off every pair is interpolated: fast
motion keeps its generated frames, and a real cut shows one generated frame that is a morph of the two shots (35 to 74
codes from either shot, [phase3-vfg.md](phase3-vfg.md), "Why not simply turn detection off"). A source's own
discontinuities are unaffected: the scheduler never interpolates across a seek, a loop or a capture's marked
discontinuity, and the generator is still told (`NotifyDiscontinuity`, which sets VFG's manual flag for any pair across
it). The start-up line says which applies and names the command's default; `--gate` refuses `--scd` (the milestone run as
measured, detection on) and skips a saved value, as for the other generator keys.

With detection off `NvVFX_Run` does not block: it only enqueues (40 to 90 µs; High about 0.5 ms), so the generator thread
returns at once and the GPU work completes behind the ring's semaphores.

## The owner's session

A preview build on the card: 46 minutes, a PS5 game in 4K HDR at 29.97 fps → 3x on the TV with variable refresh. "That
was it. It now looks perfect… I didn't notice any issues" at cuts. PASS; 10 missed slots of 244,523, all the display's;
variable refresh never lost; generation GPU time p50 1.319 / p99 1.334 ms (with detection on: 1.411 /
2.466 ms); generator thread per write p50 0.054 ms; latency 52.4 ms (p99 52.7); 0 B allocated on every thread, no GC in
the measured window; audio error 0.00 ms.

## The pipeline with detection off

This path had run on the owner's card but never under test.

### `fmp play`, offscreen on the real clock

The published binary (NativeAOT), a dim 4K HDR10 HEVC clip at 29.97 fps (RGB10A2 through the ring), `--offscreen
--realtime` at 120 Hz, 20 s (18 s measured), Medium with High loaded too for Q (the default), so the bridge is budgeted
for High: 192 operations a write, a lead of at most 5 writes.

| Run | Detection | Effective multiplier | d p50 / p99 (ms) | Generator thread per write p50 / p99 (ms) | Real frames shown after their content time p50 (p99), ms | Missed | Lead (limit) | Writes: copies / generated / fallbacks | Hot threads, GC |
|---|---|---|---|---|---|---|---|---|---|
| 2x Medium | off | 2 | 1.317 / 1.331 | 0.043 / 0.094 | 23.440 (23.490) | 0 | 1 (5) | 601 / 600 / 0 | 0 B, 0/0/0 |
| 3x Medium at 120 Hz | off | 2 (3 does not divide 4 refreshes) | 1.317 / 1.338 | 0.050 / 0.133 | 23.441 (23.500) | 0 | 1 (5) | 601 / 600 / 0 | 0 B, 0/0/0 |
| 3x Medium at 89.91 Hz | off | 3 | 1.321 / 1.400 | 0.055 / 0.134 | 26.063 (26.150) | 0 | 1 (5) | 601 / 1199 / 0 | 0 B, 0/0/0 |
| 4x Medium | off | 4 | 1.317 / 1.395 | 0.062 / 0.132 | 31.757 (31.820) | 0 | 1 (5) | 601 / 1800 / 0 | 0 B, 0/0/0 |
| 4x Medium | on | 4 | 1.418 / 1.515 | 1.424 / 1.521 | 31.745 (31.792) | 0 | 1 (5) | 601 / 1800 / 0 | 0 B, 0/0/0 |
| 4x High | off | 4 | 5.989 / 6.246 | 0.474 / 0.901 | 40.098 (40.150) | 0 | 1 (5) | 596 / 1805 / 0 | 0 B, 0/0/0 |
| 4x High | on | 4 | 6.151 / 6.228 | 6.150 / 6.231 | 40.097 (40.200) | 0 | 1 (5) | 601 / 1800 / 0 | 0 B, 0/0/0 |

Every run PASS, every teardown clean. Latency and misses are the same with detection off as on; the generator thread
spends 0.05 to 0.06 ms a write instead of the whole generation (High: 0.5 ms, the SDK enqueuing its 170 operations). d is
about 0.1 ms lower with detection off, and its tail tighter in the owner's session (p99 1.334 against 2.466 ms with it
on): with detection on the generator's span ends with an event recorded only after the blocking call has returned, so it
includes the thread's wake-up; off, the event follows the run on the stream. The scheduler's D, which takes the p99 of d,
is a little smaller and steadier for it.

Fullscreen with variable refresh could not be judged tonight: both monitors were powered down (DPMS off on DP-3 and
HDMI-A-1, the owner asleep), so the compositor took about 20 presents a second without first-pixel times, the same with
detection on and off (`fs-off-3x-med`: 456 shown, 2,302 skipped for presenter backlog; `fs-on-3x-med`: 456, 2,303),
which is no measure of either. The owner's 46-minute session above is the variable-refresh evidence. Those runs still
showed what does not depend on the screen: generation and the generator thread as offscreen (3x Medium off: d p50 1.316
ms, 0.060 ms a write; 4x High off over 118 s: d p50 5.980 ms, 0.508 ms a write), 0 B on every thread, GC 0/0/0, clean
teardown. Exits: `--duration-s` (every run), SIGTERM (`stopped: by the user` after 16.4 s, teardown clean) and SIGINT
(offscreen 4x High, `stopped: by the user` after 14.6 s, 0 missed, teardown clean; started with SIGINT at its default disposition: a background job of a non-interactive shell inherits SIGINT ignored, and the first attempt, from such a job, played on to its duration).

### The stream queue and back-pressure

With detection on a run never queues behind another (the call waits for the stream first). Off, up to the bridge's lead
of writes can be queued on the generator stream, so the budget matters. Measured again on this driver
(`CharacterizationTests.A_run_adds_a_few_dozen_stream_operations`, the queue filled until the call blocks): **Low 28.4,
Medium 32.9, High 170.2 operations per run**, at 1080p and 2160p alike, as in Phase 3. A write adds three waits, two span
records, the signal and the GPU clock's mark: 35 / 40 / 177 against the declared 40 / 48 / 192
(`PlaybackSession.OperationsPerWrite`), and the lead is at most min(slots + 1, 1,024 / budget) = 5, so at most 5 × 177 = 885
operations of the ~1,021 the stream takes. In every real-clock run above the lead never passed 1: a write is handed over
D before its refresh and finishes in about d, so only one is ever in flight. It fills only when the GPU falls behind:
`ShotChangeDetectionOffTests.A_slow_gpu_is_held_back_at_the_bridge` (VFG High, detection off, 40 calls each 40 ms behind a
delay kernel) reached the lead limit of 5 and no further, skipped 150 slots for back-pressure instead, signalled every
write in order and was back to 120 presents a second in its last second.

### Offscreen pipeline tests (`ShotChangeDetectionOffTests`, `QualitySwitchPlaybackTests`, `CliTests`)

- **Every slot generated, in order, nothing allocated** (real clock, no hooks on the hot threads): 320x180 2x at 120 Hz,
  4K 10-bit 4x at 240 Hz Medium and 2x High, and the 4K 4x run with detection on for comparison. No fallback, fault, miss
  or skipped slot; lead 1 of 5; 0 B on every hot thread; clean teardown. 4K 4x Medium: d p50 1.319 ms and 0.056 ms a
  write off, against 1.409 ms and 1.420 ms a write on; 4K 2x High off: d p50 5.999 ms, 0.484 ms a write.
- **A source discontinuity with detection off** (a loop of a lossless clip, 2x and 4x, every present read back): no
  frame is generated between a frame before the loop and one after (which VFG would now morph); the generator is told
  (`NotifyDiscontinuity`), and since the scheduler never enqueues a pair across it, VFG ran 0 manual shot-change runs;
  the first new image after the loop is made of frames after it only (here the interpolation between the loop's first two
  frames: the slots fall between them); every real frame bit-exact, every one of 178 (2x) and 488 (4x) generated frames
  a genuine interpolation (at least 6.5 codes from either neighbour). That the manual flag still outputs the current frame
  bit for bit with detection off is `GeneratorTests.A_discontinuity_covers_every_frame_of_the_pair_across_the_cut` (Vfx).
- **Quality switching (Q)** with detection off and on, Medium → High → Medium on the lossless clip: both instances
  loaded from the one generator's options (2 loads, detection as asked, `EnqueueMayBlock` only with it on), both switches
  made, every generated frame, the first ones after each switch included, between its neighbours and closer to the true
  middle frame than the blend; d at Medium p50 0.651 ms and at High 3.105 ms off (1.001 / 3.188 on).
- **A generator fault** with detection off (a throw at call 40 after queueing its waits): caught, the slot gets the real
  frame behind it on the same stream, generation stays off, 141 fallbacks, presents at the refresh rate, no miss.
- **GPU work that never finishes** (`FMP_PLAY_FAULT=gpu-wait@40`, out of process) with `--scd off`: the generator
  thread is not stuck in the SDK, the stream is; the stall check fails the run with "No ring write finished", teardown
  abandons what it must and the process ends in 16.4 s (blend 16.3 s, VFG with detection on 21.4 s).
- **The start-up line**, in-process `fmp play` runs: "automatic shot-change detection on (--scd on, play's default: …"
  without `--scd`, and "… off (--scd off: …" with it; "its Enqueue blocks for the generation" only when on.

Nothing in the fault handling, the stall check, the d estimate or `TryReadGpuTime` depended on the call blocking: the
writer catches a throw and copies the real frame behind whatever was queued; the stall check judges finished writes, not
the generator thread; d is a GPU span read without waiting (a ring of 32 spans, against a lead of 5). One difference
remains untested because it cannot be injected: an asynchronous failure inside VFG's own kernels would no longer come
back from `NvVFX_Run` (which with detection on reads the GPU's result back) but at the next CUDA call; such an error is
sticky and ends the CUDA context in either case.

### `fmp capture` on the card

`fmp capture --mode 4k30-hdr --audio off --windowed --duration-s 15 --no-settings` on the Elgato 4K X, once as it is
and once with `--scd on`. The PS5 was off, so the card sent one unchanging picture: 1 real frame, every slot held, no
generated frame and no d (nothing to interpolate), and with the monitors powered down the window took about 20 presents
a second (1,303 slots counted missed for presenter backlog, as in the fullscreen runs). What it shows: without `--scd`
the start-up line reads "automatic shot-change detection off (--scd off, a capture's default: …" and the generator line
no longer says its call blocks; with `--scd on` it reads "… on (--scd on: …" and "its Enqueue blocks for the generation,
so it runs on the generator thread". Both runs negotiated 3840x2160 P010 at 29.97, HDR10, started, stopped at their
duration, 0 B on every thread and the capture thread, GC 0/0/0, teardown clean, the device free again afterwards.

## What is not done

- **No app-side cut detector**, by the owner's choice. With detection off a real cut in a game (a cutscene's camera
  change, a menu) shows one generated frame that is a morph of the two shots, for one output slot (11 to 17 ms at 3x).
  The owner did not notice any.
- No per-content or automatic choice between on and off; `--scd` and the saved key choose.
- The capture source marks no scene cuts, only its own discontinuities (a signal change, a gap); those are never
  interpolated across.

## Found by the independent review (2026-11-20)

Approved with no must-fix. The reviewer traced the lifetime of source frames: a frame is released only after the
generator thread has enqueued its last reading write, and the source's stream then waits on that write's signal on
the GPU before rewriting the buffer, the mechanism the blend generator and every real-frame copy already used, so it
never depended on `NvVFX_Run` blocking. Generation times are read first in, first out and only once their events have
completed. Six 60 s runs fullscreen on the TV with variable refresh (the 4K HDR 29.97 fps clip), all PASS with no slot
missed, 0 B and no collection:

| Run | Real frame after content, p50 (p99) ms | d p50 / p99 ms | Generator thread per write p50 ms |
| --- | --- | --- | --- |
| 2x, detection off | 19.47 (19.75) | 1.320 / 1.347 | 0.052 |
| 2x, on | 19.62 (20.00) | 1.418 / 1.492 | 0.099 |
| 3x, off | 25.01 (25.25) | 1.317 / 1.335 | 0.052 |
| 3x, on | 25.19 (25.40) | 1.412 / 1.481 | 1.405 |
| 3x High, off | 29.79 (30.20) | 5.990 / 6.026 | 0.481 |
| 3x off, Q every 5 s (12 switches) | Medium 24.93, High 29.71 | 1.314 / 5.986 | 0.057 |

Done afterwards: `ShotChangeMotionTests` now asserts only that detection off never copies and that detection on
copies in the fastest foliage (the exact counts are the SDK's and the driver's and are logged); the reviewer's test
that scores generated frames while generations are queued behind a delay (`QueuedGenerationInputsTests`: 104 of 108
beat the blend with detection off, with it off and delayed, and with it on) is in the tree with a nine-in-ten rule.
The "mean distance" figures in the table above differ a little from what the test prints now; the copy counts match.
Not fixed: an asynchronous failure inside VFG's kernels would now surface at the next CUDA call instead of from
`NvVFX_Run`, possibly outside the generator's fallback (untestable; such an error ends the context either way). Not
exercised: H (HDR/SDR) during play with detection off, and the capture audio path during the review.

