# Live Frame Generation App: Implementation Plan

Nov 15, 2026 · @Adam

## Summary

Build it in C# (.NET 10, NativeAOT), with CUDA doing all processing and Vulkan doing only presentation. Use VFG in explicit timestep mode, driven by a scheduler that works from display times. Expect roughly 13 to 16 ms of added latency for 60 to 120 at 1440p, and 21 to 24 ms for 30 to 60.

- **VFG supports multi frame generation** up to 8x, so 30 to 120 and 40 to 120 are in scope. The per-frame budget, not the SDK, sets the limit.
- **4K60 to 120 probably won't fit** on a 5090 at Medium. Run the PS5 at 1440p for frame generation. Phase 0 confirms this with real measurements.
- **The capture card path comes first:** pinned V4L2 buffers, a GPU cadence detector to find real frames inside the PS5's fixed-refresh signal, and no queued frames anywhere.
- **HDR in and out is v1 scope:** 10-bit PQ from capture to swapchain, plus tone mapping or SDR-in-HDR when source and display differ. The pipeline carries 10-bit from Phase 1.
- **Pacing uses `VK_EXT_present_timing`** (NVIDIA Linux driver 595+, KWin commit-timing in Plasma 6.8), with VRR as the lowest-latency clock mode.
- **NVIDIA Smooth Motion is a second backend:** almost free to add, 2x only, and a useful reference to A/B VFG against.
- **Cross-platform comes from six interfaces** around a pure core. Windows reuses VFG; macOS needs a different generator.

## Goals, non-goals and constraints

**Goals**

- Take a PS5 feed from a capture card (primary), PS Remote Play, or a video file, and present it with frame generation.
- Support 2x (30 to 60, 60 to 120) and multi frame generation (30 to 120, 60 to 180/240) where the GPU budget allows.
- HDR in and HDR out: HDR10 from the capture card, files and Remote Play; HDR10 output on an HDR display, with correct handling when source and display disagree (tone mapping, or SDR inside an HDR output).
- Priorities, in order: performance, latency, image quality. Every design choice below is judged against those three.
- Frame generation method is selectable. NVIDIA Maxine VFG is the first and only backend for v1.
- Linux first (Arch, KDE Plasma Wayland, RTX 5090). Windows and macOS are design targets, not v1 deliverables.

**Non-goals for v1**

- Hooking games or injecting into a process. The app only sees finished frames.
- Extrapolation (generating frames ahead of the newest real frame). VFG only interpolates; extrapolation is a future backend.
- Recording or streaming the output.
- AMD/Intel GPUs. The abstraction allows them later.

**Hard constraints from the hardware and SDK**

- VFG has no spatial scaling: previous, current and generated frames must be the same size. Upscaling, if any, is a separate stage.
- PS5 output over HDMI is fixed-refresh (60 Hz or 120 Hz). A 30 fps game arrives as a 60 Hz signal with every frame repeated, so the app must find the real frame boundaries itself.
- Capture cards strip VRR, and the PS5 needs HDCP turned off (Settings, System, HDMI) before most cards will capture.

## What NVIDIA VFG gives us

VFG supports multi frame generation up to 8x, and its explicit timestep mode is the feature the whole pacing design is built on. Key facts from the [VFG filter docs](https://docs.nvidia.com/maxine/vfx/latest/Filters/VideoFrameGeneration.html):

- **Interpolation only.** Each `NvVFX_Run()` produces one frame strictly between the previous (t=0) and current (t=1) frame. No extrapolation.
- **Two modes.** Multiplier mode takes M from 2 to 8 and an index 1..M-1. Explicit timestep mode takes any t in (0, 1). Setting the multiplier resets the timestep, so it must be set before every run.
- **Quality modes.** Low, Medium (default), High. Set before `NvVFX_Load()`; changing it means reloading the effect.
- **Formats.** RGBA/BGRA 8-bit, or RGB10A2 packed in 32 bits. All three images must use the same family. RGB10A2 is our HDR path.
- **Memory.** CUDA device memory or mapped pinned host memory. Batch size 1 only.
- **Output buffer is reused every run**, so it must be consumed or copied before the next run. This matters for multi frame generation.
- **Shot changes.** Automatic detection is on by default. A manual flag bypasses interpolation and copies the current frame.
- **GPU support.** Ada and Blackwell on Linux and Windows x86-64, plus Hopper on Linux. Your 5090 qualifies.

**Published performance (Medium model, 2x, 1280x720, t=0.5)**

| GPU | Platform | Latency per generated frame |
| --- | --- | --- |
| RTX 4090 | Windows | 1.32 ms |
| RTX 5090 | Windows | 1.90 ms |
| L40 | Linux | 1.29 ms |
| B40 | Linux | 1.15 ms |

Sources: [Windows](https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/PerformanceReference.html), [Linux](https://docs.nvidia.com/maxine/vfx/latest/LinuxVFXSDK/PerformanceReference.html). NVIDIA only publishes 720p Medium numbers. The 5090 being slower than the 4090 looks like a measurement artefact, so treat 1.15 to 1.90 ms as the 720p range.

**Estimated cost at PS5 resolutions** (my linear-in-pixels extrapolation; Phase 0 replaces these with measurements):

| Resolution | Pixels vs 720p | Estimated ms per generated frame |
| --- | --- | --- |
| 1080p | 2.25x | 2.6 to 4.3 |
| 1440p | 4x | 4.6 to 7.6 |
| 2160p (4K) | 9x | 10.4 to 17.1 |

Unlike in-game frame generation, the GPU is not rendering the game. The whole 5090 is available to VFG, which helps throughput but not the per-frame cost above.

## Latency model

Interpolation always costs latency: a generated frame between N and N+1 cannot exist until N+1 has arrived, and N+1 must then wait for its generated frames to be shown first. With source frame interval T, multiplier M and per-frame generation time d, the added latency on every real frame is:

```latex
\Delta L = d + \frac{M-1}{M}\,T \qquad \text{subject to} \qquad d \le \frac{T}{M}
```

The constraint says each generated frame must finish within one output interval, or the next one is late. If d exceeds T/M the scheduler must add delay or drop to a lower multiplier.

**Estimated added latency on top of the capture path** (d from the 1440p and 1080p estimates above):

| Mode | M | T (ms) | Hold (ms) | Per-frame budget T/M (ms) | Fits at | Added latency (ms) |
| --- | --- | --- | --- | --- | --- | --- |
| 60 to 120 | 2 | 16.7 | 8.3 | 8.3 | 1080p, 1440p (not 4K) | 11 to 13 (1080p), 13 to 16 (1440p) |
| 30 to 60 | 2 | 33.3 | 16.7 | 16.7 | up to 4K (4K marginal) | 21 to 24 (1440p), 27 to 34 (4K) |
| 40 to 120 | 3 | 25.0 | 16.7 | 8.3 | 1080p, 1440p | 21 to 24 (1440p) |
| 30 to 120 | 4 | 33.3 | 25.0 | 8.3 | 1080p, 1440p | 30 to 33 (1440p) |
| 60 to 240 | 4 | 16.7 | 12.5 | 4.2 | 1080p, borderline | 15 to 17 (1080p), needs a 240 Hz panel |

Presentation adds up to one refresh on a fixed-refresh display, and close to zero with VRR or timed presents. The capture card adds its own hardware latency, which we measure rather than assume.

What this means in practice:

- **60 to 120 is the sweet spot.** It costs roughly half the latency of 30 to 60, because the source interval is half as long.
- **Run the PS5 at 1440p for frame generation.** 4K60 to 120 likely blows the 8.3 ms budget on a 5090 at Medium. 4K30 to 60 is the only 4K mode that plausibly fits.
- **On a 120 Hz panel** (your 5K2K ultrawide), 60 to 120, 40 to 120 and 30 to 120 are the useful modes. 240 needs a faster display.
- **Latency levers we control:** a smaller d (lower resolution, Low mode), no queued frames anywhere, VRR or timed presents, and auto-tuning the pipeline delay to the measured p99 of d rather than a fixed worst case.

## Architecture overview

&#91;embedded content: pipeline architecture · CUDA processing, Vulkan presentation, CPU scheduler\]

Frames flow top to bottom. The scheduler on the right only moves timestamps and timesteps, never pixels, and the presentation ring is the single CUDA/Vulkan boundary. Capture, scheduling and present each run on their own OS thread, connected by lock-free single-producer rings.

## Input sources

All sources produce the same thing: GPU-resident frames with a content timestamp, colour metadata and a "new frame" flag. Capture card first, then file, then Remote Play.

### Capture card (V4L2), primary

- **Formats.** Use uncompressed NV12, YUYV or P010 (10-bit HDR). Never MJPEG: it needs a decode step and adds latency. Some USB cards silently fall back to MJPEG at 4K on a 5 Gbps link, so log the negotiated format.
- **Buffers.** `V4L2_MEMORY_USERPTR` with 3 to 4 buffers allocated by `cudaHostAlloc`. The driver writes each frame straight into pinned memory, and we DMA it to the GPU with no extra CPU copy. Fallback: `MMAP` buffers registered with `cudaHostRegister`.
- **Dequeue thread.** Dedicated thread, real-time priority, blocking `VIDIOC_DQBUF`. Take the driver timestamp (`CLOCK_MONOTONIC`). If several buffers are ready, keep the newest and requeue the rest.
- **Upload and convert.** `cudaMemcpyAsync` on a copy stream, then one CUDA kernel: chroma upsample, YUV to RGB with the right matrix and range, pack to RGBA8 or RGB10A2.
- **Cadence detection.** A second kernel compares the new frame against the last unique frame on a downsampled luma grid and takes the max tile difference, so small HUD changes still count. Repeats are dropped. A real frame's timestamp is when it first appeared. This recovers 30 fps games inside a 60 Hz signal, 40 fps inside 120 Hz, and dropped frames.
- **Holds.** If no new frame arrives within about 2x the expected interval (menus, pause, loading), stop generating and show the last frame. Interpolating across a long hold produces garbage.
- **Hardware notes.** USB cards (Elgato 4K X, HD60 X) work through `uvcvideo`. PCIe cards such as the Elgato 4K Pro can capture 4K60 HDR as P010, but Linux driver support varies by model. Magewell lists Linux V4L2/ALSA support.

### Video file

- FFmpeg demux plus NVDEC (`hwaccel cuda`), so decoded NV12/P010 frames land directly in CUDA memory and go through the same convert kernel.
- Not latency critical: decode ahead, use the High model, time everything from PTS. Variable frame rate content works naturally with timestep mode.
- Audio is the master clock for file playback.

### PS Remote Play

chiaki-ng is the Linux client. It is [AGPL-3.0](https://github.com/streetpea/chiaki-ng), PS5 streams top out at 1080p60, and PS5 HDR streaming uses H.265.

- **v1: capture the chiaki-ng window** through the PipeWire ScreenCast portal (DMA-BUF into Vulkan, then copied into CUDA). Simple and keeps the licences separate, but assume it is SDR only. Costs chiaki-ng's own presentation plus compositor latency, roughly one extra frame.
- **Phase 5, needed for HDR: embed libchiaki** for the session and decode H.264/HEVC ourselves with NVDEC straight into CUDA. Lowest latency and keeps 10-bit HDR. Linking it makes the app AGPL, and we must forward gamepad input ourselves.
- **Jitter.** Network frames arrive unevenly. Use stream PTS, not arrival time, for timesteps. A small adaptive jitter buffer trades latency for smoothness.
- The PipeWire source also makes the app work on any window, Lossless Scaling style, for free.

## GPU pipeline and CUDA/Vulkan interop

CUDA does all processing; Vulkan only presents. There is exactly one boundary between them, the presentation ring, and no CPU waits on the hot path.

**Domains**

- **CUDA (processing):** upload, colour convert, cadence detection, frame history, VFG. VFG wants a pitch-linear CUDA pointer, so keeping everything before it in CUDA avoids extra hops.
- **Vulkan (presentation):** scaling to the display, SDR/HDR output transform, HUD, swapchain or direct display.

**The presentation ring**

1. Allocate 4 to 6 `VkBuffer`s in Vulkan with `VkExportMemoryAllocateInfo` (opaque FD).
2. Import each into CUDA with `cudaImportExternalMemory` and `cudaExternalMemoryGetMappedBuffer`, then wrap the pointer in a preallocated `NvCVImage` (RGBA8 or RGB10A2, explicit pitch).
3. VFG writes generated frames straight into a ring slot by rebinding `NVVFX_OUTPUT_IMAGE` per run. If the SDK does not allow rebinding, copy device-to-device instead, which costs tens of microseconds at 1440p.
4. Real frames are copied into the ring the same way, so the presenter only ever reads ring slots.

**Synchronisation**

- One exported Vulkan timeline semaphore, imported into CUDA as a timeline semaphore. CUDA signals value k when slot k is written; the Vulkan present pass waits on k.
- A second timeline goes the other way: Vulkan signals when it has finished reading a slot, and CUDA waits on it before reusing that slot.
- The CPU only enqueues work and picks timestamps. It never blocks on the GPU between capture and present.

**Streams and devices**

- Three CUDA streams: copy (H2D upload, uses the copy engine), compute (convert, cadence), and a high-priority stream for VFG (`cudaStreamCreateWithPriority`), passed to VFG via `NVVFX_CUDA_STREAM`.
- Pick the Vulkan physical device whose `deviceUUID` matches the CUDA device UUID, so both APIs are on the same GPU.
- Frame history keeps the last 2 to 3 unique RGB frames in CUDA memory as VFG inputs.

**Presenter pass**

Copy the ring buffer into an optimal-tiling image (about 0.1 ms at 4K), then one compute or fragment pass that scales to the output size, applies the output colour transform, draws the HUD and writes the swapchain image.

**Future backends**

A Vulkan-native generator (for example RIFE on compute shaders) would run in the presentation domain. Each generator declares its memory domain, and the pipeline inserts a bridge only where domains differ.

## Frame generator abstraction and the VFG backend

The scheduler decides *when* and *at what timestep*; a generator only answers "make the frame at t between these two". That split is what keeps backends swappable.

```csharp
public interface IFrameGenerator : IDisposable
{
    FrameGenCapabilities Capabilities { get; } // max multiplier, arbitrary t, extrapolation, formats, memory domain
    void Configure(in FrameGenConfig config);   // size, format, quality; may rebuild engines off-thread

    // GPU-async: enqueue only, never waits. Signals 'done' when output is written.
    void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done);

    void NotifyDiscontinuity();                 // source switch, seek, detected cut
}
```

`GpuFrame` is a plain struct: memory domain, pointer or buffer handle, size, pitch, format, colour metadata, content timestamp. No allocations per frame.

**Backends**

- `VfgGenerator`: v1, CUDA domain.
- `RepeatGenerator`: shows the nearest real frame. Baseline for A/B tests and the fallback when VFG cannot keep up.
- `BlendGenerator`: linear blend. Debug baseline for pacing work before VFG is wired in.
- Later: RIFE (TensorRT on NVIDIA, ncnn-Vulkan elsewhere), an optical-flow backend, an extrapolation backend for a latency-neutral mode.

**VFG backend details**

- Create the VFG effect, set `NVVFX_CUDA_STREAM`, input width/height and the model mode, then `NvVFX_Load()`.
- Use explicit timestep mode for everything (`FrameMultiplier = 0`, then `Timestep = t` before each run). Even 2x and 4x are just t = 0.5 or t = 0.25/0.5/0.75 from the scheduler's point of view.
- Bind `NVVFX_INPUT_IMAGE_0`/`_1` to history slots and `NVVFX_OUTPUT_IMAGE` to the target ring slot. All `NvCVImage` wrappers are preallocated.
- Leave automatic shot-change detection on. Also set the manual shot-change flag on source switches and seeks, then clear it.
- Changing quality mode or resolution means a reload. Build the new instance on a background thread and swap it in between frames, so the change never causes a hitch.
- Engine build on first load can be slow and is undocumented. Load and warm up with dummy frames at startup, before the first real frame.

**NVIDIA Smooth Motion as a second backend**

Worth adding, as a cheap baseline and a fallback, not as the main path. On Linux it is the driver's `VK_LAYER_NV_present` implicit layer, enabled with `NVPRESENT_ENABLE_SMOOTH_MOTION=1`. It works on any x86-64 Vulkan app, RTX 50 since the 575 driver and RTX 40 since 580.82.07. It injects one inferred frame between each pair of presented frames.

- **Why it fits:** our presenter is a Vulkan app, so we set the variable before creating the Vulkan instance and present only unique real frames at their content cadence. The driver doubles them. Almost no code.
- **Why it is not the primary:** 2x only, so no 30 to 120 or 40 to 120. No timestep control, so it cannot fix 59.94 vs 60 Hz drift or uneven game frame times. Its latency and pacing are a black box we cannot measure from inside, and its behaviour with HDR and with our own present timing is untested.
- **How it plugs in:** it is not an `IFrameGenerator`, because generation happens inside present. Model it as a presenter mode with generator = none, flagged in capabilities. Switching it on or off means recreating the Vulkan instance, because the layer is chosen at load.
- **Where it goes in the plan:** Phase 1, straight after first light. It gives an end-to-end frame generation result early and a fixed reference to A/B VFG against.

## Frame pacing and presentation on Linux

The scheduler works from display times, not source frames: for each output slot it computes the content time to show, then asks for the frame at that exact timestep. VFG's explicit timestep mode makes this possible.

**The scheduling rule**

```latex
\tau = V - D, \qquad t = \frac{\tau - ts_N}{ts_{N+1} - ts_N}
```

- V is the target display time of the next output slot, D is the pipeline delay, and ts are content timestamps of unique real frames.
- If t is near 0 or 1 (within a small epsilon), show the real frame. Otherwise generate at t.
- This one rule covers 2x, 3x, 4x, non-integer ratios (30 to 144), a 59.94 Hz source on a 120.00 Hz panel, and uneven game frame times. No special cases.
- D is auto-tuned: the hold time from the latency model plus the measured p99 of d plus a small margin. It moves slowly so pacing stays smooth.

**Two clock modes**

- **VRR (preferred, lowest latency).** Output slots follow the source: V = ts + D + kT/M. The display refreshes when we present. Needs a VRR display and compositor support, which KWin has on NVIDIA Wayland.
- **Fixed refresh.** Output slots are the panel's vblanks. Integer ratios give the lowest D. Non-integer ratios push D towards T + d, because the smallest generated t gets close to 0.

**Hitting the target time**

- **`VK_EXT_present_timing`** is the main tool. NVIDIA's Linux driver supports it (the [595 beta](https://phoronix.com/news/nvidia-595.45.04-linux-beta) onwards, with Wayland and direct display fixes since). It lets us request an absolute present time and read back when the first pixel actually went out, which feeds back into D and vblank phase.
- **Wayland side.** KWin has fifo-v1 since 6.4 and merged commit-timing in time for [Plasma 6.8](https://phoronix.com/news/KDE-KWin-Wayland-Commit-Timing). Fullscreen with a compatible format gets direct scanout, so the compositor adds nothing.
- **Fallback without present timing:** `VK_KHR_present_wait2`/`present_id2` for feedback, `FIFO_LATEST_READY` or `MAILBOX`, and sleeping until just before the target with `clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME)`.
- **One clock everywhere.** V4L2 timestamps, CUDA event times (converted once) and present timing all map to `CLOCK_MONOTONIC`, so there is no clock-domain drift to manage.
- At most 2 swapchain images in flight. Nothing queues behind the frame we just presented.

**Presentation modes**

- **Windowed fullscreen on Wayland** (default). SDL3 window and Vulkan surface. Force `SDL_VIDEO_DRIVER=wayland`.
- **Direct display (kiosk), later.** `VK_EXT_acquire_drm_display` plus `VK_KHR_display`, run from a TTY or via a DRM lease. Bypasses the compositor entirely and gives exact vblank control. Best latency, at the cost of the desktop.
- **Tearing mode (opt-in).** `IMMEDIATE` plus tearing control for the absolute minimum latency. Off by default because it fights the even pacing frame generation is for.

## HDR input and output

HDR in and HDR out are v1 requirements, so the pipeline is 10-bit end to end from Phase 1. PQ-encoded RGB10A2 runs from ingest to swapchain. The one open question is how well VFG interpolates PQ content, and Phase 0 tests that first.

**Source and output combinations**

| Source | Display mode | What the app does |
| --- | --- | --- |
| HDR10 | HDR | Native path: PQ RGB10A2 through VFG, HDR10 swapchain, source metadata passed through |
| HDR10 | SDR | Generate in PQ, then tone map to BT.709 in the presenter |
| SDR | HDR | Place SDR in the HDR output at a user-set paper white (default 203 nits); optional SDR-to-HDR expansion later |
| SDR | SDR | 8-bit RGBA through VFG, BT.709 out |

Generation always happens in the source's own colour space. Any conversion happens once, after generation, so VFG's input stays consistent and the work stays off the latency-critical step.

**HDR input**

- **Capture card:** 10-bit P010 from an HDR-capable card. Detect HDR from the V4L2 format's colourspace and transfer function (BT.2020, SMPTE 2084), with a manual override for cards that don't report it. Read mastering display and content light level metadata where the driver exposes it; otherwise use defaults.
- **Mid-session switching:** the PS5 flips between SDR and HDR (system menus vs games). Keep both VFG instances (RGBA8 and RGB10A2) loaded, swap on the first frame of the new format, and recreate the swapchain colour space without a black flash where the driver allows.
- **Files:** HDR10 metadata from FFmpeg frame side data. HLG is converted to PQ at ingest. Dolby Vision profile 8 plays its HDR10 base layer; profile 5 is out of scope for v1.
- **Remote Play:** HDR needs the libchiaki plus NVDEC path (HEVC Main10). Treat PipeWire window capture as SDR, which is why the libchiaki work is in Phase 5.

**Processing in HDR**

- Keep PQ encoding through ingest and VFG. VFG only accepts RGBA8 or RGB10A2, so generating in linear light isn't an option.
- Phase 0 checks VFG on PQ content specifically: specular highlights, dark gradients, banding.
- The cadence detector's threshold is tuned separately for PQ code values.
- The presenter scales HDR in linear light (decode PQ, scale, re-encode). The cost is negligible next to VFG.

**HDR output**

- Swapchain `A2B10G10R10_UNORM_PACK32` with `VK_COLOR_SPACE_HDR10_ST2084_EXT`, metadata via `VK_EXT_hdr_metadata`: the source's metadata, or the display's peak when the source has none.
- Plasma's HDR mode must be on. The app checks whether the surface offers the HDR10 colour space and falls back to tone-mapped SDR output if it doesn't.
- **NVIDIA Linux status.** `VK_EXT_hdr_metadata` works on Wayland since 580.94.11. The HDR swapchain colour space has needed the `VK_hdr_layer` on NVIDIA; recent drivers fix crashes with Vulkan HDR on the colour-management-v1 protocol, which suggests native support is arriving. Check `vulkaninfo` on your driver and keep `VK_hdr_layer` as the fallback.
- HDR in direct display mode is unverified and gets checked in Phase 6.
- The HUD is drawn in output space at a fixed, moderate brightness so it isn't blinding in HDR.

**Later: SDR-to-HDR expansion.** Maxine TrueHDR, from the same SDK, converts SDR to HDR10; NVIDIA lists 0.168 ms at 720p on a 5090. It would run on the CUDA side after VFG, before the ring.

**SDR correctness**

- Get the matrix and range right: BT.709 for SDR, limited vs full range per the PS5's RGB range setting. Read it from V4L2 colourspace fields, with a manual override and a test pattern to verify.
- Upsample chroma once, at ingest, with a better-than-nearest filter.

**Scaling**

- Generate at source resolution, then scale on present. Scaling first would multiply VFG's cost.
- A 16:9 PS5 image on a 21:9 panel is pillarboxed. Use a Lanczos or bicubic scaler.
- Optional later: Maxine Video Super Resolution as the scaler. NVIDIA lists 0.64 ms for 720p to 1440p on a 5090 in its streaming mode.

## Audio sync

Audio must be delayed by exactly the video pipeline's latency, or lip sync and hit sounds land early. The delay is measured, not configured.

- **Capture card:** read the card's audio device through PipeWire, pass it through a delay line, play it to the default sink. The delay tracks the measured capture-to-photon video latency (capture timestamp to present feedback), smoothed so it never warbles.
- **Remote Play:** the same delay line on the decoded stream audio.
- **Files:** audio is the master clock; video is scheduled against it. Files can decode and generate ahead, so D only matters for sync, not responsiveness.
- **If you listen through the TV or a headset on the controller instead,** audio will lead video by the pipeline latency. The app should say so rather than pretend to fix it.

## Language and stack decision: C# vs Rust

Use C# on .NET 10 with NativeAOT. The hot path is GPU work; per output frame the CPU only issues a few dozen API calls, and a zero-allocation C# loop does that with no GC activity. Rust's advantages are real but don't change performance, latency or image quality here.

| Concern | C# (.NET 10, NativeAOT) | Rust |
| --- | --- | --- |
| GC and jitter | No collections if the hot loop allocates nothing; NativeAOT removes JIT and tiering hitches | No GC at all |
| Vulkan | [Vortice.Vulkan](https://www.nuget.org/packages/vortice.vulkan) 3.2.x, net9/net10, current headers, VMA included | ash, always current |
| CUDA and VFG | Hand-written `LibraryImport` for the CUDA driver API subset and the small VFG C API | cudarc plus bindgen for VFG |
| V4L2 | Hand-written ioctl structs (small) | v4l crate |
| PipeWire | Tiny C shim; the API is macro and inline heavy | pipewire-rs |
| FFmpeg | FFmpeg.AutoGen | ffmpeg-next / rsmpeg |
| Window, input, surface | SDL3 bindings (Linux, Windows, macOS) | SDL3 or winit |
| Familiarity | Your strongest language; existing zero-alloc patterns | Slower to start |

**How the C# version stays deterministic**

- Hot loop rules: preallocated structs and native memory, no LINQ, closures, boxing or async state machines per frame, `[SkipLocalsInit]`, function pointers, source-generated P/Invoke.
- GC settings: workstation GC, `SustainedLowLatency` during playback. Gate: zero gen0 collections in a 10-minute steady-state run (EventPipe counters).
- Dedicated OS threads for capture, scheduling and present, not the thread pool.
- One small native shim library (C) for PipeWire and anything else macro-heavy.

**When to switch.** If Phase 0 shows more than 0.5 ms p99 jitter on the pacing thread that comes from the runtime, move only the scheduler and present loop into a small Rust or C module behind the same interface. Everything else stays in C#.

## Cross-platform and future backends

The core (scheduler, pipeline graph, telemetry, settings) has no OS calls. Everything platform-specific sits behind six interfaces: `IFrameSource`, `IFrameGenerator`, `IPresenter`, `IGpuBridge`, `IAudioDelay`, `IClock`. CI builds Linux and Windows from day one, even while Windows backends are stubs.

| Platform | Capture | Frame generation | GPU bridge | Present |
| --- | --- | --- | --- | --- |
| Linux (v1) | V4L2, PipeWire, FFmpeg/NVDEC | VFG, Smooth Motion | CUDA to Vulkan via opaque FDs | Vulkan on Wayland, direct display |
| Windows | Media Foundation, FFmpeg/NVDEC | VFG (same API on Windows x64) | CUDA to Vulkan via NT handles | Vulkan, later DXGI flip model |
| macOS | AVFoundation, FFmpeg/VideoToolbox | Needs a non-NVIDIA backend | None, single Metal domain | Metal or MoltenVK |

**Frame generation beyond VFG**

- **RIFE.** Open model, works on finished frames only. TensorRT on NVIDIA, ncnn-Vulkan on AMD/Intel, CoreML or Metal on macOS. The most portable second real backend.
- **MetalFX frame interpolation** is built for games that supply depth and motion vectors, so it is a poor fit for captured video unless it proves usable with colour alone.
- **Extrapolation.** A future latency-neutral mode that predicts ahead of the newest frame instead of waiting for the next one. More artefacts, no hold time.
- **Frame repeat and blend** stay as permanent baselines for testing.

## Instrumentation, latency measurement and testing

Telemetry is built in from the first milestone, because every later decision (D, quality mode, resolution, backend) is tuned against measured numbers.

**Per-frame timeline** (all `CLOCK_MONOTONIC`, written to a lock-free ring):

capture timestamp, dequeue, upload done, convert done, VFG start and end (CUDA events), present submitted, first pixel out (present timing feedback). Shown live in a HUD (frame-time graph, d, D, dropped and repeated frames, detected game frame rate) and exportable to CSV.

**End-to-end latency**

- Source: a PC's HDMI output showing a flashing patch, through the capture card into the app.
- Measure with a photodiode on the panel, or film the passthrough display and the app side by side with a 240 fps phone camera.
- Compare: passthrough display, app with frame generation off, app with VFG at each mode, app with Smooth Motion.

**Image quality**

- Feed real 120 fps footage, drop every other frame, generate the missing ones, and score them against the dropped originals (PSNR, SSIM, FLIP). Repeat per quality mode, for SDR and HDR.
- Keep a set of PS5 clips that stress interpolation: fast camera pans, HUD-heavy scenes, particles, scene cuts.

**Pacing**

- Frame-to-frame display interval variance and missed slots, from present feedback.

**Automated tests**

- Scheduler maths as pure functions with simulated clocks: 30-in-60, 40-in-120, 59.94 on 120, random drops, long holds.
- Cadence detector against synthetic sequences with known repeats.
- Two-hour soak: no drift, stable memory, zero GC collections in steady state.

## Milestones with acceptance criteria

Phase 0 is a go/no-go on VFG cost at real resolutions; everything after it assumes the numbers hold. Each phase ends at a measurable gate.

0. **Feasibility spike.** VFG SDK on Arch; benchmark d at 1080p, 1440p and 4K for Low/Medium/High, single 0.5 timestep and 4x; engine build time and VRAM. Push a PQ RGB10A2 clip through VFG. Prove Vulkan buffer export, CUDA import, VFG output into it, Vulkan present. Test output-image rebinding. Measure NativeAOT hot-loop jitter.
   - Gate: 1440p Medium p99 d under 7 ms; interop works; runtime jitter under 0.5 ms p99. If 1440p fails, target 1080p or Low mode.
1. **File playback, first light.** FFmpeg/NVDEC, convert kernel with 8-bit and 10-bit outputs, a ring that holds RGB10A2 from day one, repeat and blend generators, Vulkan presenter at fixed refresh, HUD and telemetry. Then Smooth Motion mode, then VFG 2x.
   - Gate: 1440p60 file to 120 Hz with zero missed slots over 10 minutes; Smooth Motion vs VFG A/B works.
2. **Capture card.** V4L2 with pinned USERPTR buffers, newest-wins dequeue, cadence detection, hold handling.
   - Gate: end-to-end latency measured; 60 to 120 at 1440p within 2 ms of the latency model; a 30 fps game in a 60 Hz signal is detected correctly.
3. **Pacing engine.** Display-time scheduler on explicit timesteps, VRR mode, present timing, auto-tuned D, 3x and 4x.
   - Gate: 30 to 120 and 40 to 120 with output interval standard deviation under 0.5 ms; a 59.94 source on a 120 Hz panel shows no periodic judder over 10 minutes.
4. **HDR.** A v1 requirement. Completes the source and output matrix: HDR detection, metadata passthrough, HDR to SDR tone mapping, SDR inside an HDR output, and switching mid-session when the PS5 toggles HDR.
   - Gate: HDR test patterns correct; a PS5 HDR game looks right on both HDR and SDR displays; an SDR/HDR switch recovers within one frame with no black flash; no added latency versus SDR.
5. **Audio and Remote Play.** PipeWire audio delay line; chiaki-ng window capture through the ScreenCast portal (SDR), then libchiaki with NVDEC for HDR Remote Play.
   - Gate: measured A/V offset under 10 ms; Remote Play 1080p60 to 120 works, in HDR on the libchiaki path.
6. **Latency extras and polish.** Direct display mode, hitch-free quality switching, settings UI.
   - Gate: direct display measurably beats Wayland fullscreen end to end.
7. **Portability groundwork.** Windows capture and presenter backends; the VFG path runs on Windows.
   - Gate: same file-playback test as Phase 1 passes on Windows.

## Risks and open questions

The biggest risk is VFG's cost at 1440p and above; the second is that the Linux SDK is unsupported for local use on GeForce.

| Risk | Impact | Mitigation |
| --- | --- | --- |
| VFG cost scales worse than linearly | 60 to 120 infeasible at 1440p | Phase 0 gate; 1080p or Low mode; Smooth Motion fallback |
| Linux SDK is server-oriented and unsupported for local use | Breakage after driver or SDK updates | Pin driver and SDK versions; Windows build as an escape hatch |
| VFG quality on PQ HDR content unknown | HDR artefacts | Test in Phase 0 before anything is built on it; use the High model for HDR if needed |
| HUD and UI artefacts | Visible warping on overlays | Inherent without game data; shot-change detection; per-game quality mode |
| NVIDIA Wayland HDR and present timing still maturing | Pacing or HDR bugs | Feature-detect at runtime; VK\_hdr\_layer; direct display mode |
| Capture card quirks (MJPEG fallback, UVC bugs, HDCP) | Extra latency or no picture | Log negotiated format; recommend known-good cards; HDCP off on the PS5 |
| Licensing | Distribution limits | VFG under NVIDIA's licence and Open Model License; libchiaki would make the app AGPL |

**Open questions**

- Can `NVVFX_OUTPUT_IMAGE` be rebound per run, or must we copy out of a fixed buffer?
- How long does the first VFG load take, and is the engine cached between runs?
- Does Smooth Motion respect our present-timing targets, or does it override them?
- Does your capture card deliver P010 at 1440p60 HDR on Linux, and what is its own latency?
- Does your panel's VRR range cover 60 to 120 Hz, so the VRR clock mode is usable?

## Sources

- [NVIDIA VFX SDK: Video Frame Generation](https://docs.nvidia.com/maxine/vfx/latest/Filters/VideoFrameGeneration.html)
- [VFX SDK Performance Reference (Windows)](https://docs.nvidia.com/maxine/vfx/latest/WindowsVFXSDK/PerformanceReference.html)
- [VFX SDK Performance Reference (Linux)](https://docs.nvidia.com/maxine/vfx/latest/LinuxVFXSDK/PerformanceReference.html)
- [VFX SDK: Get Started on Linux](https://docs.nvidia.com/maxine/vfx/latest/LinuxVFXSDK/GetStartedonLinux.html)
- [NGC: Video Frame Generation collection](https://catalog.ngc.nvidia.com/orgs/nvidia/maxine/collections/nvvfxvideoframegeneration/-)
- [VFX-SDK-Samples: VideoFrameGenerationEffectApp](https://github.com/NVIDIA-Maxine/VFX-SDK-Samples)
- [NVIDIA Vulkan developer driver release notes](https://www.developer.nvidia.com/vulkan-driver)
- [Phoronix: NVIDIA 595 beta adds VK\_EXT\_present\_timing](https://phoronix.com/news/nvidia-595.45.04-linux-beta)
- [Phoronix: KWin merges commit-timing](https://phoronix.com/news/KDE-KWin-Wayland-Commit-Timing)
- [KDE bug 491219: fifo-v1 in KWin](https://bugs.kde.org/show_bug.cgi?id=491219)
- [GamingOnLinux: NVIDIA 575 adds Smooth Motion on Linux](https://www.gamingonlinux.com/2025/04/nvidia-575-51-02-beta-driver-brings-nvidia-smooth-motion-support-to-linux-geforce-rtx-5060-series-announced)
- [NVIDIA 580.82.07: Smooth Motion for RTX 40](https://ubuntuhandbook.org/index.php/2025/09/nvidia-580-82-07-added-smooth-motion-for-rtx-40-series-gpus/)
- [Korz: NVIDIA and HDR on Wayland (VK\_hdr\_layer)](https://korz.dev/drafts/nvidia-wayland-hdr-bazzite-flatpak/)
- [Phoronix: NVIDIA 615.78.08](https://www.phoronix.com/news/NVIDIA-615.78.08-Linux)
- [chiaki-ng](https://github.com/streetpea/chiaki-ng)
- [Elgato 4K Pro capture formats](https://help.elgato.com/hc/en-us/articles/23479165158029)
- [Vortice.Vulkan on NuGet](https://www.nuget.org/packages/vortice.vulkan)
- [Apple WWDC25: MetalFX frame interpolation](https://developer.apple.com/videos/play/wwdc2025/211)
