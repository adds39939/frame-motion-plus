# Phase 8 (part 1): capture-card audio

Scope (`docs/implementation-plan.md`, Phase 8, its capture-card audio only; design doc "Audio sync"): `fmp capture` plays
the PS5's sound from the capture card's USB audio device, delayed so it is heard when its picture is seen. The delay is
measured, not configured: it follows the video latency the pipeline measures (a real frame's first pixel less its
capture timestamp), smoothed so it never warbles, with a manual trim on top. Remote Play, file audio and the rest of
Phase 8 are not part of this.

Machine: as Phase 6 (RTX 5090, KDE Plasma 6.8.2 Wayland, Elgato 4K X on `/dev/video0`, the PS5 on and on its home
screen, its sound quiet: about −48 dBFS rms). PipeWire 1.6.11 with WirePlumber 0.5.19. Sound to the default sink, the
GPU's HDMI output to an LG TV (`alsa_output.pci-0000_01_00.1.hdmi-stereo`). The INZONE H9 II headset was in a Discord
call throughout and was not touched. No RTKit: every thread of `fmp`, PipeWire's client data thread included, is
SCHED_OTHER at nice 0 (checked with `ps -L`). `fmp` is the NativeAOT binary.

## Summary

| | |
| --- | --- |
| What works | `fmp capture` plays the card's sound by default (`--audio auto`: when the capture device has an audio source; `vivid` has none and runs silent), delayed by the measured video latency, to **the default output the owner selected** (followed when they select another) or to `--audio-sink` (any sink, the headset too, by name or a unique part of a name or description); `--audio on/off`, `--audio-source`, `--audio-offset-ms`; `--list` shows each device's audio source and the sinks, marking the selected default |
| Where the sound can go | **only to the device in force** (section 2, "Where the sound goes"): the playback stream is pinned and guarded; if the device goes away the sound is muted, said once, and resumes when it is back; never moved by PipeWire's fallback; a link anywhere else is muted and cut |
| API | **libpipewire bound directly with `LibraryImport`, no C shim** (section 1); every structure, constant and the one SPA pod proven against the system headers |
| The audio path's own latency (HDMI sink) | capture **8.00 ms** + sink **10.67 ms**; the least total delay it can hold **26.4 ms**, below the lowest video latency (about 32 ms, 1440p fullscreen) |
| Delay achieved, 10 minutes `1440p60-hdr --windowed` | video latency 40.69 ms (mean over its window), sound 40.72 ms: **+0.03 ms**; error rms 0.20 ms; 0 underruns, overruns, resyncs, jumps |
| Delay achieved, 60 s `4k30-hdr --windowed` | video 55.73 ms, sound 55.76 ms: **+0.03 ms**; rms 0.16 ms; 0 underruns |
| Clock drift | corrected continuously: over 10 minutes the ratio averaged −36.3 ppm against the clocks' measured difference of −35.0 ppm; no dropout or repeat; ratio always within ±2,000 ppm (3.5 cents) |
| Faults | sink switched mid-run, source gone and back, PipeWire absent, `vivid`: no crash, no hang, sound back on target, nothing left in PipeWire after any run (section 5) |
| The video | unchanged with audio on (latency within noise, 0 missed, 0 B on every pipeline thread, GC 0 in the window); the audio callbacks allocate exactly 0 B (225,710 cycles in 10 minutes) |
| Not measured | the card's own audio-versus-video alignment and the TV's processing: the timestamps cannot see them; the owner checks by ear and eye once and trims (section "How the owner checks sync") |

The owner's commands:

```sh
fmp capture --mode 4k30-hdr                         # with sound, as before otherwise
fmp capture --mode 4k30-hdr --audio-offset-ms 20    # the sound 20 ms later (if it comes before the picture)
fmp capture --mode 4k30-hdr --audio off             # picture only
fmp capture --mode 4k30-hdr --audio-sink inzone     # to the INZONE headset (any sink, by a part of its name); else your selected default output
fmp capture --list                                  # devices, the audio source of each, the sinks
```

## 1. The API: libpipewire, bound directly, no shim

The design doc suggested PipeWire through a small C shim because its API is macro- and inline-heavy. On this machine
none of that is needed, and the stricter constraints (NativeAOT with no IL warnings, no native build step, timing on
`CLOCK_MONOTONIC`, devices by name, a clean end) point the same way:

| Candidate | What it gives | Against it |
| --- | --- | --- |
| **libpipewire-0.3 via `LibraryImport`** (chosen) | The stream API is real functions (`pw_stream_new`, `_connect`, `_dequeue_buffer`, `_queue_buffer`, `_get_time_n`); this PipeWire (1.6.11) also **exports** the core and registry methods its headers declare inline (`nm -D` shows `pw_core_get_registry`, `pw_core_sync`, `pw_registry_add_listener`, `pw_core_create_object`, `pw_registry_bind`); the metadata listener is `pw_proxy_add_object_listener`. Timing straight from the graph: `pw_time.now` is `CLOCK_MONOTONIC`, with the delay to the device, what is queued and buffered. Devices by `node.name` (`target.object`). PipeWire's own thread loop and data thread. | The format needs one SPA pod: built by hand (168 bytes) and compared **byte for byte** with `spa_format_audio_raw_build`'s output by the layout test. A libpipewire that still has those only as header inlines is refused: audio is off with that reason (every symbol is checked before any call). |
| libpulse (served by pipewire-pulse) | Plain C functions; latency queries | An extra hop (the pulse protocol to pipewire-pulse, its own buffering); `pa_timing_info` timestamps are `gettimeofday`; it needs a main loop and callbacks anyway. Less precise timing for the same machinery. |
| ALSA through PipeWire's plugin (`pipewire:NODE=…`) | Plain C, blocking reads and writes, `snd_pcm_status` timestamps | Blocking I/O on threads of our own, which cannot be real-time here (`ulimit -r` 0); the plugin adds its own buffer; timing through two layers. |
| ALSA hardware (`hw:3,0`) | The device directly | Conflicts with PipeWire, which owns the devices; no way to reach the HDMI sink. |

Every structure (`pw_time`, `pw_buffer`, `spa_buffer`, `spa_data`, `spa_chunk`, `spa_dict`, `spa_hook`, the four event
tables), every constant, every property key and the format pod are checked against `/usr/include/pipewire-0.3` and
`spa-0.2` by `StructLayoutTests` (gcc; skipped where the headers are missing, as on CI). It caught one real error: I had
`pw_time` as 72 bytes; it is 64.

Two findings about the stack that shaped the design:

- **WirePlumber drops a stream with `node.dont-fallback` on the card.** A capture stream with `target.object` set to the
  Elgato's source and `node.dont-fallback=true` streams for one cycle and is then destroyed with "defined target not
  found" by a rescan (WirePlumber 0.5.19; the same stream on another source keeps going). Without it, WirePlumber would
  move the stream to the default source (a microphone) if the card went away. So the capture stream uses
  `node.dont-reconnect=true` (linked once, never moved), and the supervisor mutes the sound unless the capture stream is
  linked to exactly the intended source node, and cuts the stream if it is ever linked to anything else.
- **The client's data thread is not real-time here.** There is no RTKit and `ulimit -r` is 0, so PipeWire's
  `module-rt` cannot raise it: `data-loop.0` runs SCHED_OTHER, nice 0. The design copes (a ring with milliseconds of
  slack, a margin in the minimum delay); a late callback would show as "late sink cycles" in the summary (none seen).

## 2. How it works

```
 Elgato USB audio ─► PipeWire source (driver, 256-frame cycles) ─► fmp capture stream ─┐  data thread
                                                                                       ▼
      ring (1.37 s of F32 stereo, lock-free, one writer, one reader) + capture timeline (seqlock)
                                                                                       │
 HDMI sink (driver) ◄─ fmp playback stream ◄─ 32-tap sinc resampler at ratio r ◄───────┘  data thread
        │                                         ▲
        ▼ LG TV                      PI controller on the measured delay (control thread gives the target)
```

- **Timestamps.** A capture buffer's first frame was captured at its cycle time less the stream's `pw_time.delay`
  (and any converter buffering): 384 frames (8.0 ms) on the card, its 256-frame quantum plus PipeWire's 128-frame
  headroom for a USB device. A frame written in a playback callback is heard at the cycle time plus `delay` (256 frames
  on the HDMI sink), what is queued and buffered, **plus one cycle**: pw_stream gives the graph the buffer queued in the
  *previous* callback first. That last cycle is not documented as such; it was measured (section 5): without it a
  loopback through two null sinks came out exactly two cycles (10.67 ms) long. With the playback graph at 128 frames
  and the capture graph at 256, putting the cycle on the playback side leaves 0.26 ms of error, where putting it on the
  capture side would leave 2.67 ms.
- **The delay measured.** For each output block: the time its first frame will be heard less the capture time of the
  input frame the resampler is reading (from the newest capture timeline: frames written and the capture time of their
  end). That is the total delay, capture to ear, against which everything is controlled.
- **The target.** The video latency the pipeline measures, a real frame's first pixel less its capture timestamp,
  smoothed (`LatencyFollower` on the control thread: a running mean for the first 5 s, then an exponential average with
  a 5 s time constant; one frame counts for at most 20 ms of deviation), plus `--audio-offset-ms`. The control thread
  hands it over through `IAudioDelay.SetTargetDelay` (a volatile store) whenever it moves. If the path cannot go that low
  (its capture and sink latency, a block of lookahead for the resampler and a 2 ms margin: 26.4 ms on the HDMI sink), the
  sound plays as early as it can and the summary says by how much it lags.
- **Following without warble.** A PI controller (1 ms of error → 1,000 ppm; integral 0.1 /s², overdamped) on the error
  low-passed over 0.5 s, the target low-passed over 1 s and its rate of change fed forward. The ratio stays within
  **±2,000 ppm of 1 (3.5 cents at most)** and changes by at most 2,000 ppm per second; a change of target is a slow glide
  of at most 2 ms per second. The first video measurement, and any error over 15 ms for 0.3 s (or 100 ms for three
  blocks, as after a sink switch), is corrected by moving the read position with a 20 ms equal-power cross-fade instead.
- **Clock drift.** The card's and the sink's crystals differ; the integral term learns the difference and the ratio
  averages to it, so the delay holds indefinitely with no dropouts or repeats.
- **Faults.** Lost capture cycles (a gap in the capture timestamps of 1.5 to 200 ms) are filled with silence, so the
  timeline stays true; a longer gap is a discontinuity. An underrun (the data does not reach the end of the block) plays
  what is there fading out, then silence until the data reaches the target again (a resync, faded in). An overrun (the
  playback side stalled past the ring) resyncs. Mute fades out over a block.
- **Threads and the video.** The two process callbacks run on PipeWire's data thread, touch only the delay line, take
  no lock and allocate nothing (`UnmanagedCallersOnly`; each counts its own allocations after a 200-call warm-up, and
  the summary prints the total). A supervisor thread (50 ms sleeps, allocation-free unless the graph changed) watches
  the links, mutes unless the capture is linked to the intended source, captures again when the source comes back,
  follows sink changes and tears down. Nothing on a video path waits for audio: the control thread's only call is the
  volatile store, the session calls `Stop` (which returns at once) as playback ends, and `fmp capture` disposes the
  audio after the video, with a bounded 2 s wait.
- **Where the sound goes** (after the safety review). The sink is resolved once at the start: `--audio-sink` (a node
  name, else the one sink whose name or description contains it, ignoring case; several matches are refused and listed),
  or the default output the owner selected (`default.configured.audio.sink`, WirePlumber's current default only if there
  is no selection). The playback stream is created with `target.object=<that sink>`, `node.dont-reconnect`,
  `node.dont-move` and `node.dont-fallback`. Verified on WirePlumber 0.5.19 with silent probe streams and null sinks:
  without `dont-reconnect` WirePlumber writes `target.node=-1` for a stream that lands on the default (its
  `checkFollowDefault`) and from then on ignores the stream's own target and follows *its* default, which drops a device
  whose route is unavailable (`default-nodes/rescan.lua`) and falls back down a list that ends at the headset; with
  `dont-reconnect` it does not. A per-stream target in the metadata is ignored (`dont-move`); `pipewire-pulse` refuses the
  desktop's per-application move outright ("Invalid argument"), with or without `dont-move`, as long as `dont-reconnect`
  is set, so that move cannot be honoured without giving up the pin. When the sink goes, the stream ends in error and is
  linked nowhere; the supervisor makes it again when a sink of that name is back. Behind all of that, the guard mirrors
  the capture side: **silence unless the playback stream feeds the chosen sink and no other output device** (changed
  2026-11-19, see "Screen shares" below: it was "exactly the chosen sink"); a link to another output device (another
  sink, a null sink, a duplex device, a link made by hand) or to a node the registry has not announced mutes at once and the stream is cut and made again (back-off 250 ms doubling to 2 s); the sound being muted for that
  reason and resuming are each said once on stderr. Without `--audio-sink`, when the owner selects another default output
  mid-run the stream is re-pinned there, guarded the same way (the line mutes and fades in on target). WirePlumber's own
  fallback default is never followed.
- **`IAudioDelay`.** The interface was enough; only its documentation was made precise (additively): `SetTargetDelay`
  takes the video latency to match, the implementation adds its own trim and accounts for its own path; `Stop` returns
  at once; `Dispose` waits, bounded. No member was added or changed.

## 3. The audio path's latency, stage by stage (measured)

From PipeWire's timing on the running streams (`pw_time`, mean over the run) plus the one cycle measured in section 5;
48 kHz, 256-frame cycles (`node.latency=256/48000`, asked for by both streams):

| Stage | HDMI sink (LG TV) | What it is |
| --- | --- | --- |
| Capture | **8.00 ms** (384 frames) | the age of a cycle's first frame when the cycle starts: the 256-frame quantum plus PipeWire's 128-frame headroom for a USB capture device |
| Sink | **10.67 ms** (512 frames) | the HDMI sink's delay to the device (256 frames, no headroom on PCI HDA) plus the cycle a queued buffer waits (256) |
| Least the path can hold | **26.37 ms** | both of the above, a block and the resampler's lookahead (5.69 ms), and 2 ms of margin for a late callback |
| Delay line (what fmp adds) | the rest: 37.9 ms at 4K (56.6 − 18.7), 14 to 18 ms at 1440p | the ring: chosen so the total is the video latency |

The INZONE H9 II headset (USB) was in a Discord call and no stream was started on it for measurement. PipeWire's own
report for it (read-only, `pw-cli enum-params … Latency`): 128 frames of headroom on top of the quantum, so its sink stage
would be 13.33 ms and the least delay 29.03 ms. The first 10-minute run confirms that figure: its playback stream ended up
on the headset (section 4) and the reported sink latency and least delay moved to 13.33 and 29.03 ms there. The TV's own
processing of the sound (and of the picture) is not in any of these numbers.

## 4. The delay achieved against the video latency

The video latency is the pipeline's own measurement, `latency` in the summary (a real frame's first pixel less its
capture timestamp, mean over the measured window). The audio delay is measured per block from the timestamps as described
in section 2. "Offset" is audio delay less video latency: positive means the sound comes later than its picture (the trim
was 0 in every run). Both exclude everything before the host's timestamps and after the first pixel and the sink (section 6).

| Run (sink: HDMI) | Output, clock | Video latency, mean (p99) | Sound's delay, mean (low-passed min to max) | Offset vs the video's mean | Error rms | Drift applied / clocks' difference | Underruns, overruns, resyncs, jumps | Callbacks |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| **10 min `1440p60-hdr --windowed`**, final code | DP-3 240 Hz, fixed refresh, phase-locked (the home screen's 30 fps) | 40.69 (42.90) | 40.72 (34.99 to 42.19) | **+0.03 ms** | 0.20 ms | −36.3 / −35.0 ppm | 0, 0, 0, 0 | 0 B in 112,854 + 112,856 |
| **60 s `4k30-hdr --windowed`**, final code | DP-3 240 Hz, fixed, phase-locked | 55.73 (57.73) | 55.76 (54.05 to 56.88) | **+0.03 ms** | 0.16 ms | −17.0 / −8.2 ppm (60 s: the integral still learning) | 0, 0, 0, 0 | 0 B |
| 10 min `1440p60-hdr --windowed`, first version (follower 2 s, no feed-forward) | HDMI-A-1 119.88 Hz, fixed, not phase-locked | 39.93 (54.42) | 38.91 (34.56 to 48.51) | −1.02 ms | 1.63 ms | −31.2 / −34.2 ppm | 0, 0, 0, 0 | 0 B in 112,913 + 112,915 |
| 60 s `4k30-hdr --windowed`, before the settled re-lock | DP-3 240 Hz | 56.47 (58.57) | 56.57 (54.78 to 62.67) | +0.10 ms | 0.75 ms | | 0, 0, 0, 0 | 0 B |
| 60 s `1440p60-hdr --windowed`, sink switched twice (section 5) | DP-3 240 Hz | 31.79 (33.85) | 31.77 (27.38 to 36.31) | −0.02 ms | 1.51 ms | | 0, 0, 0, 0 | 0 B |

The first 10-minute run is the one whose stream was moved to the headset part-way (below); its larger error is the
first version's slower following of a strongly wandering latency (p99 54 ms on the unlocked 119.88 Hz output), since
improved: the follower now averages over 5 s, the target's rate of change is fed forward (the integral learns only drift),
the line moves once more to the settled latency 5 s after start (the start-up transient visible in the 60 s 4K run above
is gone in the final one), and a change in the path's latency is put right with a cross-fade. The two "final code" runs
are the code as delivered.

Fullscreen (variable refresh) was not run with sound: the owner was on a call at the desk and a fullscreen run would have
covered the screen. The sound follows whatever the pipeline measures, so there it would follow the 32 ms (1440p) or 47 ms
(4K) of Phase 6; the least delay (26.4 ms) is below both.

Notes on these runs:

- **Following a wandering video latency.** In a window on fixed refresh the video's latency is not steady: the present
  lead adapts and the 60 Hz source beats against the 119.88 Hz output, so the smoothed latency wanders between about 35
  and 50 ms over tens of seconds (first 10-minute run). The sound follows it; the low-passed delay stays within a
  millisecond or two of it, and the ratio never leaves its ±2,000 ppm bound (3.5 cents). Phase-locked on the 240 Hz
  output (the final runs) the wander is a slow 2 to 3 ms saw-tooth, followed within ±500 ppm; under variable refresh
  (Phase 6: p99 within 0.3 ms of the mean) it is steadier still.
- **Drift.** In the first 10-minute run the ratio averaged −31.2 ppm; the clocks measured independently against
  `CLOCK_MONOTONIC` over the same window (the card from its capture timestamps, the sink from its heard times) differed by
  −34.2 ppm: the correction is the clocks' difference to within 3 ppm, and the delay held for 10 minutes with no
  underrun, overrun, resync or gap. The clock figures differ from run to run by tens of ppm (−80 to +10 ppm for the card,
  −125 to +10 for the sink over 30 to 60 s windows): PipeWire's clock estimates take a while to settle, which is why both
  are measured over the summary's window only.
- **The sink changed during the first 10-minute run.** About 150 s in, the playback stream moved from the HDMI sink to the
  INZONE headset without the selected default changing. The safety review found why such a move could happen at all:
  the playback stream then had no `node.dont-reconnect`, so WirePlumber had set `target.node=-1` on it and made it
  follow WirePlumber's own default, which drops a sink whose route is briefly unavailable (the display showed a hiccup at
  that moment) and falls back down a list that ends at the headset; even `--audio-sink` did not prevent that. Fixed:
  the stream is pinned and guarded (section 2). The delay line followed the move cleanly (the sink latency rose by
  2.67 ms, no underrun).
- **No effect on the video.** 30 s `1440p60-hdr --windowed`, audio on against `--audio off`, same binary, back to back:
  latency 35.1 against 34.8 ms (p99 35.6 / 35.2; run to run noise is about ±0.5 ms), 0 missed slots in both, 0 B on every
  pipeline thread, the capture thread 0 B, GC 0/0/0 in both. The control thread's CPU per slot: 16.9 against 18.7 µs.
  In the final 10-minute run the video had 2 missed slots of 143,533 (the pipeline's, "early"; the first one had 6 of
  71,690, 3 of them the display's), within the handful the owner accepts; Phase 6 measured a 1440p60 *file* at 0 on this
  output, so whether the audio threads cost those 2 is not established.

## 5. Robustness and tests

| Run (published binary) | Result |
| --- | --- |
| Sink switched part-way: 60 s 1440p; at 22 s only fmp's playback stream moved (per-stream metadata) to a null sink made for the test, at 37 s back to the default | both moves reported (`now playing to …`), the null sink's lower latency taken in; 0 underruns, overruns, resyncs; mean offset −0.02 ms against the video's mean; default sink and volumes untouched; the null sink removed after |
| Source gone and back: 50 s 1440p, `--audio-source` a silent virtual source made for the test, removed at 20 s, made again at 30 s | `the audio source … is gone`: muted; `is back`: captured again (the same node name, a new node), playing on target within a second; 0 underruns |
| Sink guard, published binary: 40 s 1440p with `--audio-sink` a null sink made for the test, removed at 15 s and made again at 25 s | the stream on exactly that sink (`pw-dump`: `target.object` it, `dont-reconnect`, `dont-move`, `dont-fallback`, no metadata on it); sink removed: `the sink … is gone: the sound is muted until it is back`, `the sound is muted`, and no fmp playback node existed for the 10 s (WirePlumber ended the stream; it is not re-made while the sink is missing), so nothing was linked to the HDMI sink or anywhere else; sink back: re-made, linked to it alone, `the sound resumes`; 0 underruns, PASS. The same check against the real HDMI sink named explicitly (60 s 1440p): linked only to it at 20 s and 50 s, no metadata, offset +0.03 ms against the video's mean, 0 underruns |
| Sink guard, tests on null sinks (`SinkGuardTests`) | the named sink removed: muted, said, linked to nothing (a sampler every 20 ms saw no link to any other node), a bystander sink recorded nothing; a sink of that name back: the sound resumes there, said once. A per-stream target in the metadata (`target.object` to another sink and `target.node=-1`): ignored, still only on the chosen sink. A link made by hand (`pw-link`) to another sink: muted at once, cut, made again to the chosen sink; at most one click (the supervisor's 50 ms look) reached the other sink |
| `vivid` (no sound card) | `audio off: no audio source goes with /dev/video2`, the video runs, PASS; with `--audio on` the same plus a WARNING, PASS |
| Every run here | no fmp node left in PipeWire after it (`pw-cli ls Node`); the callbacks allocated 0 B |

The loopback through PipeWire (`PipeWireLoopbackTests`): two null sinks the test creates on its own connection; a click
train played into A, the delay line from A's monitor to B, a recorder on B's monitor. Every click arrives exactly once,
and the delay measured from the clicks (B's capture time against A's heard time, from the streams' own timing) is the
target to within **0.26 ms** at targets of 60 and 35 ms (`target 60: 60.171 to 60.402 ms`). The recorder runs B's graph at
128 frames and A's at 256: that is how the playback-side cycle was located (without it: 10.67 ms long; with it on the
capture side instead it would be 2.67 ms off).

Tests (`tests/FrameMotionPlus.Audio.Tests`, **71**, all passing three times on this machine after the review's fixes; on
CI the layout test and the PipeWire tests skip, everything else runs). The whole solution, once, with `FMP_VFX_SDK` set
(the test binaries run from a build directory outside the checkout): Core 681, Platform 100 + 1 skipped, Audio 71, Gpu 121,
Pipeline 142, Sources.V4l2 115, Sources.File 154, Vulkan 98, Vfx 92 + 1 skipped (the check that only applies without
`FMP_VFX_SDK`), Cuda 144; no failure. `ReviewRegressionTests.Pipeline_stall_then_still_picture_end_to_end` now skips
without the SDK, as the Pipeline VFG tests do. Every test run of `fmp capture` on a real device passes `--audio off`:
tests play sound only to null sinks they create.

- **Resampler:** the kernel is an exact unit impulse at whole frames and has unit DC gain at every phase; ratio 1 on a
  whole frame copies bit for bit; resampled sines match the ideal (about −79 dB error at 1 kHz: the window's ripple, a
  static gain ripple of ±3·10⁻⁴; above 55 dB at 10 and 15 kHz); a ratio ramp across a block and a cross-faded jump leave
  no step; gain ramps; the ring wraps.
- **Controller (against a model of the line):** converges and learns drifts of ±500, 80 and 0 ppm; the ratio stays in its
  bound and slew; follows a moving target; one jump for a large sustained error, within three blocks for a huge one, none
  for a single wild measurement.
- **Delay line (a simulated card and sink around the real line, deterministic):** the delay measured from clicks against
  their true capture times equals the target to within one frame (21 µs); the trim moves it both ways; a target below the
  path's minimum plays at the minimum and reports the shortfall; a 6 ms change of target is followed with no step in a
  sine and no jump, and lands on target; **±500 ppm of drift on either clock with ±0.2 ms of jitter on every timestamp:**
  clicks within 0.25 ms of target after 3 minutes, no dropout, the mean ratio equal to the clocks' ratio within 10 ppm;
  ±0.5 ms of jitter keeps the ratio within 300 ppm; a lost capture block is filled and changes nothing; the source
  stopping fades out and comes back on target; a 2 s playback stall overruns and resynchronises; a 300 ms stall and a
  3 ms change of sink latency are put right at once; mute fades out and back; the clocks are measured.
- **No allocation per block:** exactly 0 bytes over 20,000 blocks through every path (playing, jump, underrun and
  resync, gap, mute). Also: nothing from before a long outage is played on resume (the ring is cleared); silent until
  the first video latency; the cross-fade is equal-gain and never louder than its input.
- **Interop layouts:** every structure, constant, key and the format pod against the system headers (section 1).
- **PipeWire loopback** (above), a missing source refused in one line, the graph snapshot.
- **Where the sound goes** (`SinkGuardTests`, section 5) and the sink matching (a name, a unique part of a name or
  description, several refused and listed).
- **CLI:** the four options, their errors and combinations, the help; audio off and a missing source leave the video
  running with one reason; the video latency follower (running mean, then 5 s; a 400 ms hitch moves it by under 0.1 ms).

## 6. What could not be measured

- **The card's own audio-versus-video alignment.** The video timestamp is when a frame's first USB packet reached the
  host; the audio's is when its samples did. If the Elgato buffers sound and picture differently between its HDMI input
  and USB, that difference is in the sound's offset and nothing on the host can see it. It needs a sound and a flash at the
  same instant on the console (a PS5 menu click and its highlight with a camera and a microphone, or a photodiode and a
  microphone). Not done: I had no way to make the console do something on cue, and the owner was not available to.
- **The TV's processing.** The picture goes PC → TV over HDMI (latency in the TV's picture mode, game mode or not) and the
  sound too (the TV's audio path, possibly with its own delay compensation). Unknown; the trim is for this.
- **Anything in the room**: the speakers' distance (about 3 ms per metre).
- **The design doc's gate, A/V offset under 10 ms,** is therefore established only up to the host's timestamps: there the
  offset is within about ±1 ms of zero on average (section 4). Whether the console's sound reaches the ear within 10 ms of
  its picture reaching the eye depends on the two unknowns above and needs the owner's eyes and ears, or a camera.

## How the owner checks sync and trims it

The timestamps put the sound on the picture as the host sees both. What they cannot see is checked once by eye and ear:

1. `fmp capture --mode 4k30-hdr` (or `1440p60-hdr`) as usual. The setup prints the source, the sink and the latencies;
   the summary's `audio` lines say what was achieved.
2. Find something with a sharp sound tied to a visible event: the PS5 menu's click as the highlight moves (press left
   and right on the home screen repeatedly), a gunshot and its muzzle flash, a footstep, a ball hitting a wall. Watch
   and listen for a minute.
3. If the sound comes **before** the picture, make it later: `--audio-offset-ms 20`, then try 10 or 30. If it comes
   **after**, make it earlier: a negative value, down to what the path allows (the summary then says the sound lags by
   the shortfall). Positive is later. Step by 10 ms; people notice sound early from about 20 to 45 ms and late from
   about 60 to 125 ms, so a few milliseconds either way is not audible.
4. The value then holds for that setup (the TV and its picture mode, the sink). The video latency itself is followed
   automatically: fullscreen and windowed, 4K and 1440p need no different trim.

If the sound is also heard another way (the TV on the card's HDMI passthrough, a headset on the DualSense), that sound
is not delayed and arrives about one video latency before the picture: mute it there, or it will sound like an echo.

## Screen shares and recorders (2026-11-19)

Found in the owner's 2 h 16 min session while streaming on Discord: Discord's Linux client takes an application's sound
by listing the sink inputs (PulseAudio API) and linking a recording stream to the application's playback node (seen in the
graph: `discord_capture`, `media.class = Stream/Input/Audio`, `media.name = "game capture"`,
`application.process.binary = Discord`, `target.object` = fmp's playback node, linked from `fmp-audio-playback`'s
outputs). The guard took that for a wrong link: it muted, cut and re-made the stream, Discord linked again, and it looped
(no sound for the owner or the stream). The rule now (`PipeWireAudioDelay.ClassifyPlaybackPeers`), for each node the
playback stream's outputs are linked to:

| Peer | Treated as |
| --- | --- |
| the chosen sink | linked: the sound plays |
| another output device: `Audio/Sink*` (a sink, a null sink, an equaliser's or EasyEffects' virtual sink, a combined sink), `Audio/Duplex` | **wrong**: muted at once, cut, the stream made again to the chosen sink (as before) |
| a node the registry has not announced | **wrong** as well (where the sound goes is unknown). The registry announces a node before any link to it (the server registers a node before its ports and links), so this is no race with a recorder appearing. Since the hardening pass (phase9-polish.md, section 6) the guard first mutes and reads the whole registry again (a fresh registry proxy and a round trip, at most 250 ms; 1.4–1.6 ms measured), then judges the peer again: a recorder after all is left alone and the sound resumes with nothing cut; still unknown, it is cut as before. An output device is cut at once, with no resync |
| any other node: a capture stream (`Stream/Input/Audio`: a screen share's sound, OBS, `pw-record` aimed at the stream, `parec --monitor-stream`), a virtual source | a recorder taking a copy: left alone. Said once on stderr ("N recorder(s) take a copy of the sound (...): left alone") |

A recorder attaching changes nothing on the sound's path to the sink: no mute, no re-made stream, no re-lock beyond what
a change of the graph's quantum makes the delay line do (the owner's run: re-locks 2, path changes 1). The playback stream
also carries `application.process.id` and `application.process.binary`, by which a screen share matches a stream to the
process it shares.

**Each recorder attaching or leaving changes the path** (measured after review): a `pw-record` attach raised the path
latency by 2.7 ms; 40 attach/detach cycles in 5 s gave 80 path changes and 40 re-locks (cross-faded), with 0 mutes,
restarts, underruns or wrong links. A share that is toggled often would be heard as cross-fades. Recorder lines may fill
at most 48 of the summary's 64 event lines, so a later wrong-link line is always kept.

**"Recorder" is wider than recording streams:** any node that is not `Audio/Sink*` or `Audio/Duplex` and is announced is
left alone: a virtual microphone (`Audio/Source/Virtual`, confirmed in review) and, by reading, nodes without a media
class (JACK clients) too. Something has to link to fmp's stream for that: a patchbay restoring saved connections
(qpwgraph, Helvum), or OBS's application-audio capture with monitoring on, could then pass the sound on to an output.
The fix is the same as for the loopback below: bind stream peers and read `node.link-group` (and what they feed).

**Not caught, deliberately for now:** a loopback or filter whose capture half is a plain stream and whose playback half
plays to another sink (`pw-loopback`, `module-loopback`, a filter-chain with a `Stream/Input/Audio` capture) linked to
fmp's stream by hand or by its target: its two halves are not linked in the graph, and the registry's global properties
do not carry `node.link-group` (checked on PipeWire 1.6: only `client.id` and `factory.id` are shared, and a recorder
such as Discord's shares its client with the application's own voice playback, so the client cannot be the test).
Catching it needs each stream peer bound to read its `node.link-group` from the node's info; someone has to set such a
loopback up on fmp's stream on purpose. Filters that show themselves as a sink are output devices and are caught.

Tests (real server, null sinks only, `SinkGuardTests`): a capture stream aimed at the playback node → the clicks keep
arriving on the chosen sink, the recorder receives them, 0 wrong links, no restart, not muted; `parec --monitor-stream`
on the stream's sink-input index → the monitor receives the clicks (peak above half the click height), 0 wrong links, not
muted; a recorder and a hand-made link to another null sink at once → muted and cut (wrong links ≥ 1), back on the chosen
sink only, at most one click on the other sink; the rule itself without a server (chosen, other sink, duplex, virtual
sink, unannounced node, recorder, recorder plus another sink, the chosen sink gone).

## Known issues / not done

1. **Card and TV alignment unmeasured** (section 6): the owner should check by eye and ear once and set
   `--audio-offset-ms`; the design doc's 10 ms gate is open until then.
2. **Fixed after review: the sound could move to a device nobody chose.** The playback stream had no `node.dont-reconnect`,
   so WirePlumber set `target.node=-1` on it within seconds (`checkFollowDefault`) and from then on ignored its target,
   `--audio-sink` included, and followed WirePlumber's effective default, which drops a sink whose route is unavailable
   (`default-nodes/rescan.lua`; the HDMI hiccup at 150 s in the first run) and falls back down to the INZONE headset, the
   owner's call headset; and the app played at unity wherever it landed. Now the stream is pinned and guarded (section 2).
   Not exercised: a real change of the owner's selected default mid-run (it would change the owner's setting), so the
   re-pin is tested only as far as the code path shared with the sink coming back.
3. **The client data thread is not real-time** (no RTKit here). No late cycle was seen in any run, but a heavily loaded
   CPU could make the sink play silence for a cycle (counted as "late sink cycles"). Installing `rtkit` (or the
   `realtime-privileges` group the video side also wants) would give PipeWire's `module-rt` the priority it asks for.
4. **The 256-frame quantum is fixed** (5.3 ms cycles). It keeps the least delay (26.4 ms on HDMI) below the lowest video
   latency (about 32 ms fullscreen at 1440p). If another stream on the same sink asks for a smaller quantum the path gets
   faster; a larger one (a sink forced to 1,024) raises the least delay to about 60 ms, and the summary would report the
   shortfall. Not configurable from the command line.
5. **Following the video's wander costs some pitch movement in a fixed-refresh window**: in the final 10-minute run the
   ratio ranged from −1,598 to +1,145 ppm (under 3 cents) as the window's latency moved between 34 and 42 ms; in the
   first version, on the unlocked 119.88 Hz output, it reached its 2,000 ppm bound (3.5 cents) for seconds at a time.
   Below what is audible, but a window is not the best way to play; fullscreen (variable refresh) is steady.
6. **No reconnect after PipeWire restarts**: if the PipeWire server goes away the sound stops for the rest of the run
   (`the PipeWire connection was lost`); the video carries on.
7. **The headset's latency was not measured with a stream** (it was in a call); only PipeWire's report and the first
   10-minute run's accidental move to it.
8. **A libpipewire that exports the inline core and registry methods is required** (1.6.11 here does). An older one
   that has them only as header inlines (I expect Ubuntu 24.04's 1.0 to be one; not checked) leaves audio off with that
   reason; calling those few methods through the interface tables would lift it.
9. **One GC at the end of each 10-minute run** (whole run 1/1/1, measured window 0/0/0, and the window ends at the stop):
   so after playback had stopped, most likely the end-of-run verification over 72,000 to 144,000 rows; not during
   playback, and not in any shorter run. Not investigated further.
10. **The desktop's per-application "move to" is refused** for the stream (`pipewire-pulse`: "Invalid argument", because of
    `node.dont-reconnect`, which the pin needs). Change the output with the selected default device (followed) or
    `--audio-sink`.
11. **No persistent `audio.sink` / `audio.offsetMs` setting.** Settings are read only from `--settings <file>` (there is no
    default settings location), so a persistent choice needs that first; the selected default output already persists.
12. **`--audio auto` takes any sound card on the capture device's USB device**: a webcam with a microphone would be played.
13. **`Report`/`Describe` can lock a loop being destroyed** if the 2 s teardown join timed out (`PipeWireAudioDelay.cs`,
    `Report` and `Describe`); `PwConnection` leaks a hook when the metadata is bound again (`BindMetadata`) and treats a
    negative `pw_core_sync` as answered (`RoundtripLocked`).
14. **WirePlumber's saved state holds test sinks from before this fix**: `~/.local/state/wireplumber/stream-properties`
    has 76 `fmp-test-*` (plus `fmp_test_*`, `fmp-probe*`, `fmp-review-src`) entries (volumes of the test sinks, the
    review's and my probes). Not edited (the owner's file); the test sinks and streams now carry
    `state.restore-props=false` and `state.restore-target=false`. They can be deleted from that file with WirePlumber
    stopped, or left (they apply only to nodes with those names).
15. **What the plan's Phase 8 also lists is not started:** file audio as the master clock, Remote Play (chiaki-ng capture,
    libchiaki), and the PipeWire ScreenCast source.

Found by the re-check of the output-device fix, not fixed:

- If the selected default output is not present at the start (plausibly the HDMI output with the TV off), the sound
  starts on the system's current default instead, pinned there, and moves to the selected one when it appears. The
  start-up line says so. Read from the code, not reproduced.
- **Sound for a Discord screen share: verified by the owner's ears only** (2 h 16 min to the headset with Discord's
  recorder attached: no "linked to another node" line, 0 underruns, re-locks 2 (path changes 1)). The tests above use
  a capture stream and `parec --monitor-stream` in Discord's place; Discord itself was not run here.
- A loopback or filter with a plain-stream capture half, or any other non-sink node (a virtual microphone, a JACK
  client, a patchbay's restored link to something that plays on), linked to fmp's stream and passing the sound to
  another output, is not caught by the guard (see "Screen shares and recorders").
- A node announcement lost on the guard's connection no longer cuts a recorder's link straight away (a resync first:
  phase9-polish.md, section 6), but the sound is muted for the resync (1.4–1.6 ms measured, at most 250 ms plus the
  fades), and a node the server never shows this client would still be cut. Why the test connection once missed a
  `parec` node is unknown; registry handler failures are now counted in the summary.
- Each recorder attach or detach is a path change and usually a cross-faded re-lock: a share toggled often is heard.
- On a change of the selected default, a spurious "linked to another node" line and a wrong-link count follow the
  "your selected default output is now…" line, because the old stream's link is judged against the new sink.
- If no real frame is ever shown with timing, the sound stays silent for the whole run and the summary says only
  "0.0 s played", with no line naming the cause.
- The summary's "played" seconds looked high for a run that included a 4.7 s loss of the sink (22.2 s of 27 s).
- That the desktop's "set default" writes `default.configured.audio.sink` (through pipewire-pulse) was not verified by
  changing the owner's setting.

## Suggested changes to the plans (not made here)

- `docs/implementation-plan.md`, Phase 8: "PipeWire shim" → "libpipewire bound directly (recent versions export the inline core
  and registry methods; no shim)"; `native/shim/` in the layout is not needed; the project is `FrameMotionPlus.Audio`, not
  `FrameMotionPlus.PipeWire` (the ScreenCast capture can live there or in its own project). Record the capture-card audio
  as done with the gate "A/V offset under 10 ms" open pending the owner's check (section 6).
- Design doc, "Language and stack decision", PipeWire row: "Tiny C shim" → "direct `LibraryImport`; one SPA pod built by
  hand and checked against the C builder". "Audio sync": add that the measured delay excludes the card's and the TV's
  internal delays, hence the trim.
