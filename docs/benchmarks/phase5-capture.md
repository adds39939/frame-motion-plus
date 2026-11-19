# Phase 5: the capture card (`fmp capture`)

Scope (`docs/implementation-plan.md`, Phase 5; design doc milestone 2): a live V4L2 source for a PS5 on an HDMI capture
card, captured into CUDA-pinned buffers, judged by a GPU cadence detector, converted on the GPU, and played through the
Phase 4b pipeline with VFG. The owner's target mode is **4K HDR, 30 → 60**; second, **1440p60 HDR → 120**.

Machine: RTX 5090, driver 620.70.06, Linux 7.3.4 (Arch), KDE Plasma 6.8.2 Wayland (DP-3 240 Hz, HDMI-A-1 119.88 Hz, both
HDR). Capture card: Elgato 4K X (`0fd9:009b`, `uvcvideo`, 10 Gbps USB), `/dev/video0`; a PS5 on its input with HDCP off.
The kernel's `vivid` test driver loaded (`/dev/video2`, webcam input). .NET 10.0.114; `fmp` is the NativeAOT binary from
`dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64` unless a run says otherwise. Real-time scheduling is
refused for this user (no rtprio, no RLIMIT_NICE): every thread ran SCHED_OTHER, nice 0.

**Status after review (18 Nov, afternoon).** An independent review found the capture path sound (struct layouts and
ioctl numbers, 1,593 frames bit-exact under random delays in every buffer mode, stop/signal/disconnect, colour against a
hand derivation and an independent conversion of a real Elgato frame) and one blocker, fixed below ("Fixes after
review"). **The owner has run `fmp capture` on the PS5 and confirmed it works.** The console was on again from about 12:00,
and the runs on the card that the first pass could not make are in "The runs".

**Conditions of the card measurements.** The PS5 was on its home screen (nobody playing; nothing was pressed on it). The
picture was dim: its brightest 4x4 cell was code 226 in P010 (the owner's capture earlier in the day reached about 595),
which fits the console dimming its screen before rest mode. **Between 10:33 and about 10:45 the console went to sleep and
the card's input carried no signal for the rest of this work** (checked every few minutes until 11:16, see "Known
issues"). Everything below marked *signal* was measured before that; the runs that need a picture and were not made are
listed as such.

## Verdict

- **`fmp capture` works end to end**, on the card and on vivid: V4L2 negotiation, USERPTR capture into page-aligned CUDA
  pinned memory, upload, the cadence detector, conversion to RGB10A2/RGBA8, the scheduler, VFG, the presenter. With the PS5
  signal, offscreen, short runs: 4K P010 30 → 60 with blend (every frame new, 0 misses, GC 0) and 1440p P010 60 with VFG
  Medium, the 30 fps home screen detected as 30 fps inside the 60 Hz signal (repeats dropped; 0 misses, GC 0). VFG at 4K
  P010, the published binary and windowed presentation with `--validate` ran on vivid and on the card's no-signal frames,
  all clean. *After review, with the console on:* windowed on the card from the published binary, 4k30-hdr → 60 (30 s
  here, 60 s by the coordinator: PASS, 0 missed by the live counters and `fmp verify`; 180 s by the reviewer: the capture
  clean, one late show at 160 s failing the pacing verdict, Phase 6) and 1440p60-hdr for 180 s (PASS, 0 missed, `fmp verify`
  agreeing; the console's content was 30 fps, so 30 → 60). See "The runs".
- **The capture path is correct and low-latency by construction**: the driver writes into pinned memory the GPU copies
  from (no CPU copy); newest wins (never a queue: stale buffers are requeued unseen, a frame the pipeline has no room for is
  dropped at the source, counted); the only thread that waits for the GPU is the capture thread, polling an event (no
  context synchronise, nothing blocks another thread); a buffer goes back to the driver the moment its upload is done;
  nothing is allocated, freed or synchronised per frame (0 B on the capture thread and the consumer path, proven against an
  injected allocation). Measured cost (signal, P010 4K): upload 0.54 ms, cadence metric 0.10 ms, conversion 0.12 ms of GPU
  time; the capture thread has the frame judged 0.69 ms after dequeue.
- **Cadence detection works on the real signal**: on the PS5 home screen at 1440p P010 60 Hz the detector reads exactly
  the 30-in-60 pattern (every other capture new, repeats dropped, 30.00 fps), and in 4K P010 30 it keeps every frame. The
  thresholds come from the measured distributions (below). The kernel is bit-exact against a CPU reference; synthetic
  30-in-60, 40-in-120, dropped frames, a HUD-only change, noise and in-band repeats, PQ and SDR, go through the real kernel
  and tracker correctly. The plan's tracker backlog: four of the six items pass as acceptance cases or are decided, five
  known limits are pinned by tests (see "Cadence tracker backlog").
- **What the card does** (below): it takes CUDA pinned memory as USERPTR (and the two fallbacks), stamps frames on
  CLOCK_MONOTONIC at the start of the USB transfer with a jitter of 1.6 to 5.6 µs, delivers a whole frame over USB in
  25.4 ms (4K P010) to 3.7 ms (1080p NV12) after that stamp, drops nothing, never signals HDR (Rec. 709 for a PQ signal, in
  every mode), and in 4K P010 30 delivers 30 distinct frames a second of 30 fps content. Whether it ever blends, and which
  of each pair of 60 Hz frames it keeps, could not be determined without 60 fps motion on the console.

## How to run it

```sh
fmp=artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp
export FMP_VFX_SDK=$PWD/lib/VideoFX          # only for a binary outside the checkout

$fmp capture --list                          # devices, formats, sizes, rates, presets
$fmp capture --mode 4k30-hdr                 # 4K HDR 30 → 60 (asked of the card as 29.97: the console's rate, since 2026-11-19): VFG Medium 2x
$fmp capture --mode 4k30-hdr --fullscreen    # the same, borderless fullscreen on the window's output
$fmp capture                                 # also 4K HDR 30 → 60: no options = the largest 10-bit mode, taken as HDR10
$fmp capture --mode 1440p60-hdr              # 1440p HDR 60 → 120 (a 60 fps game; a 30 fps one becomes 30 → 60)
$fmp capture --mode 1440p60-hdr --fullscreen
$fmp capture --mode 1440p60-hdr --multiplier 4   # a 30 fps game in that signal to 120
$fmp capture --mode 4k30-hdr --duration-s 180 --csv run.csv --frames-csv frames.csv && $fmp verify run.csv
```

Window close, Escape, Ctrl+C and SIGTERM stop it with its summary; the device is released on every way out.

## What was built

```
src/FrameMotionPlus.Sources.V4l2/
  Interop/LibC.cs             open, close, ioctl, poll, mmap, munmap, eventfd, read, write (LibraryImport); errno names
  Interop/V4l2Types.cs        the V4L2 structures, ioctl numbers and constants (checked against videodev2.h by a test)
  V4l2Device.cs               an open device node: QUERYCAP, ENUM_FMT/FRAMESIZES/FRAMEINTERVALS, G/S_FMT, G/S_PARM,
                              REQBUFS, QUERYBUF, QBUF, DQBUF, STREAMON/OFF, SUBSCRIBE/DQEVENT, G/S_CTRL, G/ENUMINPUT
  CaptureDevices.cs           listing and describing devices (fmp capture --list), the default device
  CaptureFormat.cs            the mode choice from what a device offers (pure), applying it and reading back what was set
  CaptureColor.cs             V4L2 colour fields → matrix, range, transfer, primaries (videodev2.h's default rules), overrides
  CaptureBuffers.cs           USERPTR into CUDA pinned memory → USERPTR into registered memory → MMAP registered: the
                              first the driver accepts, proven by queueing every buffer
  CaptureSourceOptions.cs     options, the measured cadence thresholds
  CaptureStatistics.cs        counters, distributions, the frame log, why a capture ended, the signal state
  V4l2FrameSource.cs          the live IFrameSource: capture thread, upload, cadence, conversion, history, ending
native/kernels/cadence.cu     the cadence metric kernel; native/kernels/yuv.cu gains fmp_yuyv_to_rgb (packed 4:2:2)
src/FrameMotionPlus.Cuda/Kernels/CadenceGrid.cs, ImageKernels.cs   their wrappers
src/FrameMotionPlus.Pipeline/ CapturePlaybackSource and the session over any IPlaybackSource (below)
src/FrameMotionPlus.App/      fmp capture: Cli/CaptureCommand.cs, Capture/{CaptureOptions,CapturePrinter,CaptureFrameLog}.cs
tests/FrameMotionPlus.Sources.V4l2.Tests/   102 tests (below)
```

## The capture path

```
 driver (uvcvideo / vivid)                        capture thread "fmp-capture" (DedicatedThread, real-time asked for)
 writes the frame into a USERPTR buffer ──► poll(device, eventfd) ─► DQBUF; more ready? keep the newest, QBUF the rest
 (page-aligned cuMemHostAlloc memory)        │  (stale, counted)
                                             ▼
             copy stream: wait(staging's last conversion) → H2D copy (the whole frame, one cuMemcpyAsync)
                          → cadence kernel (luma vs the last unique frame's grid) → 16 B result to pinned host → event
                                             │ the thread polls the event (40 µs sleeps; bounded; Stop interrupts it)
                                             ▼
             QBUF the capture buffer at once (its upload is done) ─► CadenceTracker.Process(timestamp, metric, PQ?)
                repeat ─► nothing more (the reference grid stays the last unique frame's)
                new    ─► reference := this frame's grid; a free history buffer? (queue full or none: dropped, counted)
                          compute stream: wait(the buffer's last reader, GPU-side) → convert → Ready event
                          → publish SourceFrame { content time = driver timestamp, arrival = now }
```

- **Buffers.** (First established with a throwaway C probe, not in the repo, then by the source and its tests.)
  `CaptureBuffers` asks for 4 USERPTR buffers of `CudaPinnedBuffer.Allocate(..., alignment:
  Environment.SystemPageSize)` (the frame size rounded up to pages) and proves the mode by queueing all of them; a driver
  that cannot pin that memory (EFAULT/EINVAL on QBUF) falls back to anonymous page-aligned memory registered with
  `cuMemHostRegister`, then to MMAP buffers registered with `cuMemHostRegister`, saying which was refused and why. **The
  Elgato and vivid both accept all three; USERPTR into CUDA pinned memory is what they use** (the probe and the tests: every
  mode converts vivid's pattern bit-exactly).
- **Two staging buffers** on the device, alternating, so the upload of frame k+1 never waits for frame k's conversion (which
  waits, GPU-side, for its history buffer's last reader); frame k+2 waits for frame k's.
- **The cadence kernel runs on the copy stream** right behind the upload, so the capture thread waits for the upload and
  the metric at once (both are needed before it can decide), and the conversion is enqueued only for new pictures: a repeat
  costs an upload and the metric, nothing else.
- **Timestamps.** The driver's timestamp when the buffer flags say `V4L2_BUF_FLAG_TIMESTAMP_MONOTONIC` (both devices do;
  any other is replaced by the dequeue time and counted). A unique frame's content time is the timestamp of the capture where
  its picture first appeared (the tracker's), made strictly increasing (repairs counted: none seen).
- **Newest wins, nothing queues.** Stale buffers are requeued unseen; the output queue holds at most 2 frames for the
  pacing thread (which drains it at least every 2 ms); when it, or the history, has no room, the new frame is dropped at the
  source and counted. Nothing in the capture path waits for the consumer.
- **Colour.** From the V4L2 fields with videodev2.h's default rules (a YCbCr format's default quantization is limited), with
  `--hdr on|off|auto|10bit`, `--range`, `--matrix` overrides. The default (`10bit`): a 10-bit capture is HDR10 (PQ,
  BT.2020, the BT.2020 matrix) because the card does not signal it; 8-bit as the device says. Chroma siting is assumed
  left (V4L2 does not report it). Frames carry what their RGB pixels are (identity, full range, the content's primaries and
  transfer), as the file source's do.
- **Holds.** Repeats are not published, so a still picture (a menu, a pause, a sleeping console) is a gap: the scheduler's
  hold shows the last frame and stops generating. Start-up primes: until 8 frames are published (the scheduler's start-up
  frames), a repeat is published too when nothing has been for 100 ms, so the pipeline starts on a still picture (found when
  the console went to sleep: the pacing thread waited for start-up frames for ever).
- **Ending.** A device that goes away (POLLHUP, ENODEV), a streaming error (EIO, POLLERR with a failing DQBUF), a
  source-change event, or an upload that does not finish within 10 s end the capture with a `CaptureEndedException` naming
  the reason; the pipeline stops and reports it. A signal that stops is not an end: after 1 s without a frame the state is
  NoSignal (counted), frames resume by themselves and the first after the gap is a discontinuity; black frames are counted
  and the state says Black.
- **Release.** Stop: the thread (bounded, never waiting for the GPU), STREAMOFF. Dispose (a drained point): REQBUFS 0, close,
  free. `Abandon` (teardown with GPU work stuck): stop the thread, STREAMOFF, REQBUFS 0, close the device; nothing CUDA.
  `PlaybackSession.Abandon` calls it, so the device is free for another program even then (test below).

## The pipeline with a live source

`PlaybackSession` now takes either source (`PlaybackOptions.Capture` set: the path is the device). The live case:

- **Content time = capture timestamp**, no start offset (`PacingLoop.StartOffset` already returned 0 for a live source), no
  decode-ahead (the capture source's queue is 2 frames; the pacing loop holds 2 + `HistoryDepth`).
- **Arrival = the time the frame was published** (upload judged, conversion enqueued); the scheduler's D takes its
  arrival lag (p99 of arrival − content) on top of the hold and the generation lead, so D reflects the USB delivery and
  upload: 4K P010 D 49.1 ms = hold 16.7 + lead 0.6 + arrival 27.0 + alignment 4.9.
- **The summary's latency lines**: the latency generation adds (D less its arrival lag: hold + lead + alignment) against the
  design doc's model ΔL = d + (M−1)/M·T, and the measured time from capture timestamp to the frame shown.
- **Never slowed**: the pacing thread reads what is there; the source drops what the pipeline has no room for.
- Virtual clock refused for a capture (frames come when they come).

## What the card does (part 2)

| Question | Finding |
| --- | --- |
| Buffer modes | USERPTR into page-aligned `cuMemHostAlloc` memory: **accepted** (Elgato and vivid). USERPTR into anonymous memory registered with `cuMemHostRegister`: accepted. MMAP + `cuMemHostRegister` (no IOMEMORY flag needed): accepted. |
| Timestamp clock | `V4L2_BUF_FLAG_TIMESTAMP_MONOTONIC` + `TSTAMP_SRC_SOE`: CLOCK_MONOTONIC, the host time the first USB packet of the frame arrived (uvcvideo). vivid: MONOTONIC + EOF, stamped a frame period ahead of the dequeue. |
| Jitter (signal) | Interval sd 2.2 µs (4K P010 30), 1.6 to 2.5 µs (60 Hz modes); min/max within ±10 µs of 33.333 / 16.667 ms. |
| USB delivery (dequeue − timestamp, signal) | 4K P010 30: 25.4 ms. 1440p P010 60: 11.3 ms. 4K NV12 60: 14.6 ms. 1440p NV12 60: 6.1 ms. 1080p NV12 60: 3.7 ms. About 980 MB/s: each frame is transferred at the link's speed once it starts. Without a signal the card paces its frames over the period instead (1440p P010: 16.15 ms, 4K P010: 26.7 ms). This is latency after the stamp; the card's own HDMI-in to first-packet time is not measurable from the host. |
| Dropped or duplicated sequence numbers | None in any run (sequence numbers contiguous). |
| Start-up | First buffer 0.56 to 0.63 s after STREAMON; two all-zero frames; at 60 Hz a gap of about 0.78 s; then 2 (30 Hz) to 7 (60 Hz) frames flagged `V4L2_BUF_FLAG_ERROR` (skipped, counted); then the picture. |
| No signal | All-zero frames (black, counted) for about 2.9 s, then the card's own static "no signal" picture (brightest cell 1020.8 in P010). Frames keep coming at the negotiated rate. |
| Colour fields | sRGB / Rec. 709 / Rec. 709 / default quantization for every format and mode, P010 included. Never changed during this session; the console's HDR state was not changed (not ours to change), so whether the fields would follow it is not known — with this card's fields constant across formats, they very likely do not. |
| 2160p P010 30: distinct frames | Yes on the home screen: 308 of 309 captures new (metric p1 0.0139, p50 0.0210), at exactly 33.333 ms. |
| 2160p P010 30: blending | **Not determined.** The home screen's content is 30 fps (the 1440p60 capture shows each picture twice), so both 60 Hz copies the card chooses from are the same picture and a blend would be invisible. Needs 60 fps motion on the console (a 60 fps game, or a fast menu animation) captured in 2160p P010 30 and, separately, 2160p NV12 60. |
| Which of each pair it keeps | Not determinable with 30 fps content (both copies are identical). With 60 fps content the 30 fps capture's pictures would match every other 60 Hz capture; the phase relative to HDMI's frames cannot be seen from the host without a reference signal. |
| Cadence regularity at 30 | Regular: no repeated or skipped frame in 309, intervals 33.333 ms ± 10 µs. *Corrected 2026-11-19:* 309 frames (10 s) is too short to see it, but a card set to 30.000 repeats a frame every **33 s** with a PS5 (59.94 Hz): see the next row. |
| The capture rate (2026-11-19) | The card offers **29.970 and 59.940** beside 30.000 and 60.000 for these sizes (P010 2160p: 30 and 29.97 only; P010 1440p: 60, 59.94, 50, 30, 29.97; the 120 and 144 modes have no 119.88). UVC states the intervals in 100 ns units (29.97 is 333667/10000000); the app takes them as the exact 30000/1001. The card **free-runs on its own clock**: its frame period is 15 ppm short of nominal against CLOCK_MONOTONIC in every mode tried (30.000, 29.97, 59.94: −14.8 to −15.7 ppm), it does not lock to the input. At 30.000 with a 59.94 Hz console it runs out of new frames every 1/(30.000 − 29.97) ≈ 33 s and sends a repeat (measured: 32.80 to 32.90 s apart); at 60.000 every 16.7 s. The presets now ask for 29.97 and 59.94 (`--capture-fps` overrides): 0 repeats in 5 min at 2160p P010 29.97 and in 4 min at 1440p P010 59.94. The card is still about 15 ppm faster than the console, so a repeat remains about every **30 min at 29.97** (1 / (29.97 × 16 ppm)) and every 17 min at 59.94: predicted from the measured clocks, not yet seen in a run that long. Timestamp jitter at 29.97: sd 9 to 26 µs (1.6 µs at 30.000; a few delivery jitter pairs of ±0.2 ms), mean 33.366 ms. See docs/benchmarks/phase6-pacing.md, "The capture rate and variable refresh lost (2026-11-19)". |
| 1440p P010 60, home screen | 30 fps content in the 60 Hz signal: every other capture new (metric ≥ 0.0085 p1, p50 0.0138), the others repeats (p50 0.00054). In stretches of a second or two a small part of the screen changed every refresh (0.002 to 0.004 between the new pictures); with the final thresholds those go to the cadence and stay repeats. Detected: 30.00 fps. |
| 2160p NV12 60, home screen | The same 30-in-60 pattern (new p1 0.0077, between p50 0.0012); the 8-bit capture of the HDR signal is very dark (brightest cell 36). |
| A game | None was running. |

## Measured cost of the capture path

Signal, offscreen, the JIT build (`--generator none`; the cost is the source's, independent of the generator), 10 s each;
the 4K P010 row is also the NativeAOT binary's (no-signal run, same sizes):

| Mode | Frame | Upload GPU p50 / p99 | Cadence GPU p50 | Convert GPU p50 / p99 | Dequeue → judged p50 / p99 | USB delivery p50 | Timestamp → available p50 / p99 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2160p P010 30 | 24.9 MB | 0.538 / 0.678 ms | 0.102 ms | 0.122 / 0.139 ms | 0.686 / 0.860 ms | 25.41 ms | 26.11 / 26.53 ms |
| 1440p P010 60 | 11.1 MB | 0.277 / 0.327 ms | 0.091 ms | 0.056 / 0.063 ms | 0.407 / 0.461 ms | 11.31 ms | 11.73 / 11.84 ms |
| 2160p NV12 60 | 12.4 MB | 0.289 / 0.351 ms | 0.099 ms | 0.118 / 0.122 ms | 0.426 / 0.501 ms | 14.56 ms | 15.00 / 15.11 ms |
| 1440p NV12 60 | 5.5 MB | 0.175 / 0.188 ms | 0.088 ms | 0.055 / 0.060 ms | 0.313 / 0.340 ms | 6.08 ms | 6.41 / 7.01 ms |

Upload: about 46 GB/s (pinned, one copy). The capture thread's CPU: one poll wake per frame plus the event polling; the
dequeue → judged time is the upload plus the metric plus up to one 40 µs poll sleep. Timestamp → available is USB delivery
plus that. Capture thread allocations: 0 B (NativeAOT binary and the test host; the JIT `fmp` binary with dynamic PGO on
shows 64 B once, when tier-up re-binds a P/Invoke stub on the thread: the test hosts turn PGO off for the same reason).

## Cadence

**The kernel** (`native/kernels/cadence.cu`): cells of 4x4 luma samples summed exactly; tiles of 8x8 cells (32x32 pixels),
one block each; per tile floor(65536 · Σ|cur − ref| / (cells · 16)); the maximum over tiles (`atomicMax`), and the largest
cell sum (black detection). Integer only, order-independent: bit-exact against `CadenceReference` at 67x37 (partial tiles,
leftover columns and rows), pitched 640x360, P010 1366x766 and 3840x2160, YUYV 322x182, and one cell. 0.09 to 0.10 ms at
4K. The host divides by 65536 and the full-scale code (255 or 1023): the tracker's metric.

**Thresholds, per mode** (`CaptureSourceOptions.CadenceFor`; set after review from one-minute recordings of every frame's
metric with the console showing a bright, moving picture; the morning's numbers are kept below for what they show):

| Mode | True duplicates (a capture repeating the previous picture) | New pictures | Low / High |
| --- | --- | --- | --- |
| P010 2160p30 | 0.000132 (this pass), 0.00011 to 0.00012 (reviewer): one every 32.6 s (977 frames), strictly periodic | p0.1 0.0204, p1 0.0237 | **0.0003** / 0.0050 |
| P010 1440p60 | p50 0.000142, p99 0.000183, max 0.000206 (1,804 in a minute: 30 fps content) | p0.1 0.0135, p1 0.0151 | **0.0004** / 0.0050 |
| NV12 1440p60 and 2160p60 (20 s each) | p50 0.00048, p99 0.00066 to 0.00077, max 0.00075 to 0.00084 | p1 0.0127 and 0.0223 | **0.0016** / 0.0050 |

Low is about twice the largest true duplicate measured in the mode; High stays below every new picture's p1 seen in any
session (lowest 0.0085, on the dim screen in the morning). Between them the cadence decides (new only when due; at a 30 Hz
signal every capture is due). **The first pass's Low (P010 0.0010, NV12 0.0020) was too high**: its "repeat noise" (P010
1440p p50 0.0005, up to 0.002) was measured on the screen as it dimmed before rest mode, a slow fade that changes every
refresh, not noise; the review's and the coordinator's 4K30 runs then dropped as repeats frames with small real changes
at 0.0005 to 0.00099 (69 in the coordinator's run). With the console's true duplicates at 0.0001 to 0.0002 (P010), those
are now new. **The periodic duplicate at 4K30**: the card's 30.000 fps output drifts against the console's 60 Hz frames, and
about every 33 s it delivers the same 60 Hz frame twice (metric 0.00012, a repeat, dropped; the game frame it skipped is
lost before the app sees it). The tests model the card's duplicates (P010 ±1 code on 15% of samples: max 0.00016; NV12
on 25%: max 0.00074) and twice them, and noise into the band.

*The morning's recordings* (dim home screen, then fading): new pictures at 60 Hz p1 0.0085 (P010) and 0.0045 to 0.0077
(NV12); 2160p30 P010 every frame at least 0.0139; small parts animated every refresh 0.002 to 0.004.

**Synthetic sequences through the real kernel and tracker** (`CadenceTests`, 320x180 to 640x360, the card's noise level
modelled): 30-in-60 from both phases in P010 and NV12 (exactly the first copy of each picture, at its own capture time, 30.00
fps); 40-in-120 in NV12, YUYV and P010 (every third capture, 40.00 fps); a 60 fps game dropping one frame in ten (the
repeats are repeats); an 8x8-pixel HUD change of 100 (P010) or 40 (NV12) codes on a still picture (each change new);
noise at and at twice the card's level (never new, largest metric below Low); noise into the band on a learned 30-in-60
(left to the cadence: no wrong decision).

**Cadence tracker backlog** (plan, Phase 5), as acceptance cases with the capture thresholds (`CadenceBacklogTests`):

| Item | Result |
| --- | --- |
| 1a. 60 → 30 with every 8th new picture in-band | **Known limit, pinned**: the estimate stays above 31 fps (it froze at 50.5 in Phase 1). |
| 1b. learned 60, then 30 with in-band repeats | **Known limit, pinned**: the in-band repeats are due at 60 fps and taken as new. |
| 1c. starts with in-band repeats | With the card's measured noise (p99 0.0006 against Low 0.0010) repeats are below Low, and 30-in-60 is learnt from the start: **passes**. With repeats truly in-band from the first capture: **known limit, pinned** (no rate learnt). |
| 2. an 83 ms hitch every 4th frame | **Known limit, pinned**: the estimate saw-tooths (more than 5 fps peak to peak). |
| 3. gaps 1,1,1,1,3 read 60 | **Decided**: long gaps under a quarter of intervals are hitches, and the rate is the one between them (as the scheduler's holds treat them); pinned. |
| 4. recovery after an animated menu | **Passes**: within one rate window (33 unique frames) after play resumes. |
| 5. 57 fps in 60 Hz ripple | **Pinned as a bound**: within ±3% of 57. |
| 6. under 5 fps reports 0 | **Decided**, by design (pauses and menus); pinned. |

None of the pinned limits was changed (Core is out of scope here); 1a/1b matter only when in-band frames are common, which
on this card's noise they are not.

## The runs

**Made with the signal** (offscreen, real clock at a nominal 120 Hz, JIT build, before the thresholds were final):

| Run | Result |
| --- | --- |
| 4k30-hdr, blend, 6 s | 481 slots: 120 real, 121 generated, 240 repeated (stride 2: 60 images/s); **0 missed**; 188 captured, 188 unique; D 49.1 ms (hold 16.7 + lead 0.6 + arrival 27.0 + alignment 4.9); a real frame shown 49.2 ms after its timestamp; added by generation 22.2 ms against the model's 16.7 (difference: the stride's alignment 4.9 and the lead's margin). |
| 1440p60-hdr, VFG Medium, 8 s | 721 slots: 224 real, 137 generated (d p50 1.24, p99 1.30 ms), 360 repeated; **0 missed**; detected 30.00 fps; D 38.2 ms (hold 16.7 + lead 1.8 + arrival 11.8 + alignment 7.9); added by generation 26.4 ms against the model's 18.0. |

Both: GC 0/0/0, 0 B on the pacing, generator, present and control threads. The capture thread's 64 B in these runs had
two causes, both since dealt with: a static initialiser first run on the thread frames into the run (the sleep in its GPU
wait; now pre-rolled before the first frame), and the JIT host's dynamic PGO re-binding a P/Invoke stub at tier-up (none
in the NativeAOT binary; the test hosts turn PGO off). Every later run: 0 B.

**On the card after review** (windowed on HDMI-A-1 at 119.88 Hz, VFG Medium 2x, HDR10 swapchain, NativeAOT binary; the
console's picture bright and moving, 30 fps content):

| Run | Slots | Missed (live / `fmp verify`) | d p50 / p99 | D | Real frame shown after capture | Capture | Verdict |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 4k30-hdr, 60 s (coordinator) | 6,954 | 0 / 0 | 1.51 / 1.59 ms | 45.1 ms = hold 16.7 + lead 2.1 + arrival 26.3 | 61.8 ms | | PASS, 0 B, GC 0 |
| 4k30-hdr, 180 s (reviewer; `r4k.log`, `r4k.csv`, `r4k-frames.csv`, `r4k-verify.log` in the review's `scratchpad/work/`) | 21,340 | 4 / 5: one present shown a refresh late at 160.13 s, then 2 early, 1 untimed with its refresh taken; the verifier disagrees with the live counters on 1 slot (19302, wrong image) | 1.51 / 1.62 ms | 45.2 ms = hold 16.7 + lead 2.3 + arrival 26.2 | 61.9 ms (p99 66.4) | 5,408 captured, 5,403 unique, 5 true duplicates (0.000118), 0 dropped, 0 stale | FAIL on pacing (Phase 6 backlog: the hand-over window and the verifier/live disagreement); capture side clean, 0 B, GC 0 |
| 4k30-hdr, 30 s (this pass, after the fixes) | 3,358 | 0 / 0 (3,356 judged) | 1.51 / 1.57 ms | 45.1 ms = hold 16.7 + lead 2.2 + arrival 26.2 | 61.3 ms (p99 65.9) | 908 captured, 907 unique, 1 duplicate (0.000122) | PASS, 0 B, GC 0 |
| 1440p60-hdr, 180 s (this pass, after the fixes) | 21,340 | 0 / 0 (21,338 judged) | 1.32 / 1.39 ms | 30.4 ms = hold 16.7 + lead 1.9 + arrival 11.8 | 47.2 ms (p99 51.3) | 10,817 captured, 5,403 unique, 5,414 repeats dropped; detected 30.00 fps | PASS, 0 B, GC 0 |

Added by generation against the model: 18.8 ms vs ΔL 18.2 (4K, d p99 1.57, T 33.3), 18.6 vs 18.1 (1440p). The
remaining delay to light is the arrival lag (USB delivery of the frame, 25.4 / 11.3 ms) and the present lead (12.5 ms).
The 1440p run is 30 → 60, not 60 → 120: the console showed 30 fps content (a 60 fps game is needed for 60 → 120). Not
phase-locked to 119.88 Hz in either mode (Known issues).

**Without a signal** (published NativeAOT binary, VFG Medium, offscreen, 4 s): the pipeline starts on the card's black
frames (7 repeats primed), then holds (331 of 361 slots hold, nothing generated); capture thread 0 B; GC 0; PASS. On vivid
(published binary, VFG Medium): offscreen 640x360 NV12 30, 5 s: 361 slots, 0 missed, d p99 0.92 ms, 158 unique of 158, D
18.8 ms against the model's 17.4, 0 B on every thread, PASS. **Windowed** (480x270 window, which opened on the 119.88 Hz
output) with `--validate`, 4 s: 241 slots, 0 missed by the live accounting and by the telemetry verifier (239 judged,
agreeing), 509 of 509 shows timed by their first pixel, intervals sd 0.013 ms, **0 validation messages**, 0 B, GC 0, PASS.
YUYV 1280x720 at 60 offscreen, 4 s: PASS (every slot generated: vivid's 60 fps is not phase-locked to 120).

## Tests

`tests/FrameMotionPlus.Sources.V4l2.Tests` (108; categories `Gpu`, `Capture` (vivid), `Elgato`; skip, never fail, without
a GPU, vivid, the card or a picture on it; serial; concurrent GC, tiered PGO and tiering off as in the other hardware test
projects; builds for `win-x64`):

- **StructLayoutTests** (1): a C program compiled against `/usr/include/linux/videodev2.h` (and poll.h, fcntl.h,
  sys/mman.h, sys/eventfd.h, errno.h) prints 14 structure sizes, every declared field's offset and size, every constant of
  `V4l2Defs` and `LibC` and every ioctl number; each equals the C# value; reflection checks every field has a row.
- **NegotiationTests** (37): the mode choice on the Elgato's enumeration (default = 2160p P010 30, 59.94 ≠ 60, what is not
  offered is refused naming what is), colour mapping (the card's fields, the 10-bit default, overrides, videodev2.h's
  defaults), options and presets, usage errors, the help.
- **CadenceTests** (25) and **CadenceBacklogTests** (9): above.
- **ReviewRegressionTests** (2, the review's, adopted): a picture that goes still while the consumer is behind is still
  delivered (source level: the last frame read equals the input in all 230,400 pixels), and end to end with VFG and a
  2.5 s GPU stall during which the picture stops: the last new picture is published. Both failed before the fix (230,400 of
  230,400 pixels differing; the last new picture `Unique, DroppedBehind`, 537 repeats over 8.95 s, none published) and pass
  after it.
- **VividCaptureTests** (14): listing; negotiation logged and refused; **every buffer mode in NV12 and YUYV converting
  vivid's pattern bit-exactly against the CPU reference** (YUYV through a 4:2:0 image twice as tall, sited on the even
  rows); **newest wins** with a slow capture thread (stale buffers skipped, the newest processed, sequence jumps); **a
  consumer that stops reading never slows the capture** (frames dropped at the source, counted; fresh frames after); 
  timestamps monotonic on CLOCK_MONOTONIC and the content times; **ten open/stream/close cycles and five start/stops: file
  descriptors, threads and GPU memory back where they were, the device free after every close**; **vivid's simulated
  unplug** ends the capture as DeviceLost within a second, no hang, vivid reconnects; **no frames** (vivid dropping every
  buffer) reported as NoSignal, then resumed with a discontinuity; **an injected fatal streaming error** ends it as a stream
  error, an error-flagged buffer is skipped.
- **AllocationTests** (3): 0 B per frame on the capture thread (unique and repeat paths) and on the consumer path, exactly,
  and an injected 64 B allocation caught on each.
- **ElgatoTests** (3) and the card's CLI tests (2): each target mode negotiates as asked with USERPTR, frames at the
  signal's rate on CLOCK_MONOTONIC, no driver drops, not black, new pictures, costs bounded; `fmp capture --mode 4k30-hdr
  --offscreen` and a windowed 1440p60-hdr run. They skip when the card has no picture (they did in the first pass's runs,
  with the console asleep).
- **CaptureCliTests** (10 more of its 12), **GcConfigurationTests** (2): a capture from which no frame ever arrives ends at
  its `--duration-s`, failing with "No frames arrived from the source"; exit 69 with one line for a missing device, no GPU (hidden) and no display, out of process
  with `fmp simulate` still working; exit 2 for a mode the device does not offer; an offscreen and a windowed run on vivid;
  SIGINT stops a run with its summary and the device is free at once; **teardown that abandons GPU work (12 s of it on the
  ring writes' stream) still releases the device at once**.

**Runs.** The whole solution once after the last code change (`dotnet test FrameMotionPlus.slnx -c Release`,
`FMP_VFX_SDK` set, as a background job with nothing else running): Core 675, Platform 100 + 1 skipped (as before), Cuda 144
(143 before: the module theory has the new `cadence` module), Vulkan 97, Gpu 121, Vfx 92 + 1 skipped (as before),
Sources.File 154, Pipeline 112, all passing; Sources.V4l2 96 passed, 5 skipped (the card, no picture), 1 failed: the vivid
offscreen run's assertion of "60.00 fps", where vivid's coarse clock (15 to 18 ms steps) read 59.81. The assertion was
widened (a test-only change: about 60, every frame new), then the new suite ran twice more: 97 passed, 5 skipped, both
times. **After the review's fixes** (console on), the whole solution once more: Core 675, Platform 100 + 1 skipped, Cuda
144, Vulkan 97, Gpu 121, Vfx 92 + 1 skipped, Sources.File 154, **Sources.V4l2 108, none skipped** (the card's tests ran on
the console's picture), Pipeline 111 + 1 skipped (`A_present_lead_too_short_for_this_compositor_is_counted_as_the_pipelines`,
an environment skip of a windowed test: this compositor took presents handed over 1.5 ms before their refresh), no
failure; build 0 warnings; NativeAOT publish clean.

## Changes outside the new project

| Where | What and why | Test |
| --- | --- | --- |
| `native/kernels/cadence.cu` (new), `native/kernels/yuv.cu:122` (`fmp_yuyv_to_rgb`), `native/kernels/ptx/*` (regenerated by `build.sh`: `cadence.ptx` new, `yuv.ptx` gains the YUYV kernel, the other modules byte-identical) | The cadence kernel; YUYV conversion. | `CadenceTests.The_kernel_is_bit_exact_against_the_cpu_reference`, `VividCaptureTests.Every_buffer_mode_converts_the_pattern_bit_exactly`; Cuda's `KernelToolchainTests`, `KernelTests` (every module loads) |
| `src/FrameMotionPlus.Cuda/Kernels/ImageKernels.cs:16` (`cadence` module), `:57` (modules by name), `:272` (`EnqueueYuyvToRgb`), `:309` (`EnqueueCadenceMetric`); `Kernels/CadenceGrid.cs` (new); `FrameMotionPlus.Cuda.csproj:32` (IVT) | The wrappers. | as above |
| `src/FrameMotionPlus.Pipeline/PlaybackSource.cs:48` (`IPlaybackSource.Abandon`, default no-op), `:121` (`CapturePlaybackSource`), `:158` (`DistributionSummary`, `CaptureSummary`) | A capture as a playback source; the device released when teardown abandons the GPU. | `CaptureCliTests.An_abandoned_teardown_still_releases_the_device` |
| `src/FrameMotionPlus.Pipeline/PlaybackOptions.cs:60`, `:166` | `Capture` options; virtual clock refused for a capture. | `CaptureCliTests` |
| `src/FrameMotionPlus.Pipeline/PlaybackSession.cs:49`, `:107`, `:122`, `:152`, `:381`, `:406`, `:430`, `:480`, `:956`, `:1028` | The session over any `IPlaybackSource`: opening a capture (unavailable device → exit 69), its setup and summary, held capacity, stats after the stop, Abandon closing the device. | `CaptureCliTests`; Pipeline's existing 112 tests |
| `src/FrameMotionPlus.Pipeline/PlaybackTypes.cs:66` (`SourceDescription`), `:88` (`CaptureSetup`), `:115` (`Stream` now optional), `:256`, `:388` | Setup and report for either source; a capture-thread allocation fails the verdict. | as above |
| `src/FrameMotionPlus.Pipeline/FrameMotionPlus.Pipeline.csproj:16` | References the V4L2 project. | |
| `src/FrameMotionPlus.Pipeline/PacingLoop.cs:202`, `:744` (after review) | A run with a duration that never shows a frame ends at the duration counted from launch, failing with "No frames arrived from the source" (a capture with no buffers coming ran for ever). | `CaptureCliTests.A_capture_with_no_frames_ends_at_its_duration_saying_why`; Pipeline's 112 |
| `src/FrameMotionPlus.Pipeline/PlaybackSession.cs:425`, `:1043` (after review) | The setup shows the cadence thresholds resolved for the mode; an abandoned teardown says whether the capture device was released. | `CaptureCliTests` |
| `src/FrameMotionPlus.App/Play/PlayPrinter.cs:17`, `:48`, `:163`, `:176`, `:232` | The summary printer shared with capture (`PrintSummary(o, gate, r)`). | Pipeline's `CliTests` |
| `src/FrameMotionPlus.App/Cli/CommandDispatcher.cs:39`, `:80`, `:276`; `FrameMotionPlus.App.csproj:32` | `fmp capture`, its help; IVT for the tests. | `NegotiationTests.The_help_describes_capture` |
| `README.md`, `FrameMotionPlus.slnx` | Phase 5, `fmp capture`, the new projects. | |

No change to Core, Platform, Vulkan, CudaVulkan, Vfx or Sources.File. The capture source reuses `FrameHistory` from
Sources.File by reference (it loads nothing of FFmpeg).

## Fixes after review

- **Blocker: a dropped frame became the cadence reference** (`V4l2FrameSource.cs:964-993`). The reference grid advanced to
  a new picture before the source knew it had a history buffer for it; when that frame was dropped (the pipeline behind)
  and the picture then stayed still, every later capture repeated the dropped one, nothing was published and the display
  held an older picture until something moved (reachable: a 0.7 s GPU stall with VFG dropped 33 frames at 60 fps). Now the
  reference, the unique-interval statistics and the hold bookkeeping advance only once the frame has a slot and is
  published; a dropped new picture is judged new again at the next capture and published then (its content time is that
  capture's). Tests above.
- **Thresholds per mode, from true duplicates** (`CaptureSourceOptions.cs:30`, chosen at `V4l2FrameSource.cs:222`): see
  "Cadence".
- **EINTR** from QBUF, DQBUF and DQEVENT is retried (`V4l2Device.cs:365`); it used to end the capture as a stream error.
- **Abandon** no longer closes the descriptor when the capture thread did not stop within 2 s (it could still be inside an
  ioctl on that number): the device is then left open until the process exits, and the report says so
  (`V4l2FrameSource.cs:453`, `DeviceReleased` `:476`; `PlaybackSession.cs:1043`).
- **Dispose** synchronises the copy and compute streams before giving back and freeing the capture buffers
  (`V4l2FrameSource.cs:520`).
- **`--duration-s` with no frame ever** (`PacingLoop.cs:202`, `:744`): above.

## Deviations from the plan and the design doc

- **The cadence kernel runs on the copy stream**, not the compute stream: the capture thread needs the upload and the
  metric together before it can decide, and the compute stream may be held behind a history buffer's last reader.
- **Repeats are never converted** (only uploaded and measured): the conversion is enqueued for new pictures only.
- **USERPTR fallback order** adds "anonymous memory registered with CUDA" between the design doc's two modes (both cards
  took the first, so it is untested on hardware that needs it).
- **Start-up priming** (publishing up to 8 repeats at 100 ms intervals at the start) is new: without it a still picture
  never lets the scheduler start.
- **`fmp capture` with no options takes a 10-bit capture as HDR10** (`--hdr 10bit`): the design doc's "detect from V4L2
  with a manual override" cannot work on this card, which reports Rec. 709 for PQ.
- **Format changes end the capture** (a source-change event, or a device error); re-negotiating in place is not built
  (the plan allows stop or re-negotiate). uvcvideo sends no source-change events, so on the Elgato a console switching
  resolution or HDR is seen only as what the frames do.
- **YUYV is supported** (vivid's default; the card offers it too), at no cost to the other formats.

## What the plan and the design doc should say (not edited)

- Phase 5's "what has to be established": the buffer modes, the timestamp clock (SOE, first USB packet), the jitter, the
  USB delivery per mode and the HDR fields are now known (table above); blending and the kept copy are still open and
  need 60 fps motion.
- The latency model's capture term: USB delivery of a whole frame after the card's timestamp is 25.4 ms at 4K P010 and
  11.3 ms at 1440p P010 (about 1 GB/s), before any HDMI-in latency of the card: 4K30 HDR costs about 14 ms more than
  1440p60 HDR before frame generation starts.
- The cadence tracker backlog: items 3 and 6 decided, 4 passes, 1c passes with the card's noise, 1a/1b/1c-as-written/2/5
  pinned as known limits.
- The capture source needs start-up priming on a still picture (above), or the scheduler a start that does not need 8
  frames.

## Known issues

1. **Pacing on the 180 s 4K run** (the reviewer's): one present shown a refresh late at 160.13 s cascaded to 4 missed slots,
   and `fmp verify` disagreed with the live counters on one slot (a wrong image during the catch-up): the Phase 6 backlog
   (the hand-over window against the compositor's latch, items 1 and 2 of "Pacing backlog from step 4b"), not the capture.
   The 30 s 4K and 180 s 1440p runs here had none.
2. **Thresholds** come from two sessions of the PS5's own screens (a dim, fading home screen; a bright moving one): game
   HUDs, dark scenes, film grain and the console in SDR are unmeasured; NV12's floor is from 20 s of an HDR signal captured
   8-bit. `--frames-csv` records every frame's metric for re-tuning.
3. **The periodic duplicate at 4K30**: about every 33 s the card delivers one 60 Hz frame twice (its 30.000 fps output
   drifting against the console's 60 Hz); the app drops the duplicate as a repeat, but the game frame the card skipped there
   is lost before the app sees it.
4. **Not phase-locked on the 119.88 Hz output.** The console sends 60.00 Hz (timestamps 16.666 ms apart; the card's 2160p30
   is exactly half), so a capture drifts against HDMI-A-1's refreshes by 0.1% (DP-3 at 240.02 Hz likewise); a live source
   cannot be resampled, and a 120.00 Hz or VRR output would lock. Phase 6.
5. **60 → 120 at 1440p was not run with 60 fps content**: the console showed 30 fps content, so the 180 s 1440p60-hdr run
   was 30 → 60. Needs a 60 fps game.
6. **Blending and which 60 Hz copy the card keeps** in 2160p P010 30 are undetermined (needs 60 fps motion).
7. **The card's own latency** (HDMI in to its first USB packet) is not measurable from the host; reported latency starts at
   the timestamp.
8. **A format change on the card** (the console switching resolution or HDR) is not detected as such: uvcvideo sends no
   source-change event, the card keeps sending the negotiated format, and its colour fields never change. With `--hdr`
   fixed, a console switching between SDR and HDR is shown with the wrong transfer until restarted with the other `--hdr`.
9. **Not tested by the review or here**: closing the window or pressing Escape during a capture (the same session path as
   `fmp play`, which is tested), a real unplug of the card (vivid's simulated unplug is tested), and the 1440p60 mode
   beyond the suite's tests and the 180 s run above.
10. **The card's "no signal" picture** (static, near white) after about 3 s without input is taken as a still picture;
    only the all-zero frames before it count as black.
11. **Real-time scheduling** for the capture thread is asked for and refused here (no rtprio, no RLIMIT_NICE): it runs at
    nice 0.
12. **The pinned cadence-tracker limits** (above) are unchanged (Core).
13. **A dropped-and-re-judged picture's content time** is the capture where it was published, not where it first appeared
    (at most the drop's duration later; only after the pipeline fell behind).
14. **The JIT build of `fmp`** (not the NativeAOT binary or the test hosts) allocates 64 B once on the capture thread when
    dynamic PGO re-binds a P/Invoke stub at tier-up.

## What still needs the owner

- **End-to-end latency** with a photodiode or a 240 fps phone camera (the design doc's method: a flashing patch through
  the card), in 4k30-hdr → 60 and 1440p60-hdr → 120, against passthrough; the plan's manual gate "60 to 120 at 1440p within
  2 ms of the latency model".
- **A 30 fps game inside a 60 Hz signal detected**: play a 30 fps game with `fmp capture --mode 1440p60-hdr` and read
  "detected game rate" (the home screen is already detected as 30 fps).
- **How it looks**: 4K HDR 30 → 60 and 1440p60 → 120 with VFG, windowed and fullscreen, in a game.
- **A 60 fps game** in `--mode 1440p60-hdr` (60 → 120) and in `--mode 4k30-hdr` (blending, the kept copy).
- **60 fps motion in 2160p P010 30** (a 60 fps game) to settle blending and the kept copy, compared with 2160p NV12 60.

## Reproducing

```sh
sudo modprobe vivid                                  # optional: the tests' capture device without a card
dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64
fmp=artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp
$fmp capture --list
$fmp capture --mode 4k30-hdr --offscreen --duration-s 10 --frames-csv frames.csv   # costs and metrics per frame
$fmp capture /dev/video2 --size 640x360 --fps 30 --format nv12 --offscreen --duration-s 5   # vivid
dotnet test tests/FrameMotionPlus.Sources.V4l2.Tests -c Release
```
