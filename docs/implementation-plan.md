# Frame Motion Plus: phased implementation plan

This turns the design in [`Live Frame Generation App Implementation Plan.md`](../Live%20Frame%20Generation%20App%20Implementation%20Plan.md)
(the "design doc") into build phases for a C# / .NET 10 solution. The design doc stays the source of
truth for *what* and *why*; this file says *in what order*, *in which project*, and *how each phase is
proven*.

## Development machine (probed 2026-11-15)

| Item | State | Consequence |
| --- | --- | --- |
| .NET SDK | 10.0.114 | Target `net10.0`, NativeAOT for the app |
| GPU / driver | RTX 5090, 620.70.06 | `libcuda.so.1`, `libnvcuvid`, `libnvidia-present` present |
| Vulkan | 1.4, `VK_EXT_present_timing`, `present_wait2`/`present_id2`, `fifo_latest_ready`, `hdr_metadata`, `swapchain_colorspace`, external memory/semaphore FD, `KHR_display`, `acquire_drm_display` | Everything the design doc's pacing and HDR sections rely on is exposed |
| Desktop | Plasma 6.8.2 Wayland; DP-3 4K 240 Hz, HDMI-A-1 4K 120 Hz; HDR on, VRR automatic | Real presentation, HDR and VRR tests can run here |
| FFmpeg | 9.0.2 (libavcodec 63) with `cuda` hwaccel | FFmpeg.AutoGen 9.x |
| SDL3 | 3.4.20 | System library, C# bindings |
| clang / LLVM | 23.1.5 with the `nvptx64` target | CUDA kernels compile to PTX without the CUDA toolkit |
| CUDA toolkit (`nvcc`, NVRTC) | **Not installed** | Use the driver API only; kernels via clang NVPTX |
| NVIDIA VFX SDK (Maxine VFG) | 1.3.0.0, unpacked under `lib/` (git-ignored, never committed) | Phase 3 is unblocked. See "VFX SDK layout" below |
| Capture card | **No `/dev/video*`** | **Blocks the Phase 5 hardware gate.** `vivid` kernel module is available as a stand-in (`sudo modprobe vivid`) |
| sudo | Needs a password | Agents cannot install system packages or load kernel modules |

### VFX SDK layout

The SDK is proprietary and is not redistributed with the repo. Locally it sits in `lib/`:

```
lib/VideoFX/                         SDK Core 1.3.0.0 (nvidia/maxine/vfx_sdk_core:1.3.0.0_linux)
  include/                           nvVideoEffects.h, nvCVImage.h, nvCVStatus.h
  lib/                               libVideoFX.so, libVideoFXLocal.so, libNVCVImage.so
  external/cuda/lib/                 bundled CUDA 12 runtime, NPP, cuDNN
  features/nvvfxvideoframegeneration -> ../../nvvfxvideoframegeneration
lib/nvvfxvideoframegeneration/       VFG feature 1.3.0.0: libnvVFXVideoFrameGeneration.so + header
```

A throwaway C smoke test (not in the repo) confirmed the SDK loads and interpolates on this machine
with `LD_LIBRARY_PATH` set to `VideoFX/lib`, `VideoFX/external/cuda/lib` and the feature's `lib`.
**Preliminary** numbers from it, on synthetic frames, wall-clock around `NvVFX_Run` + stream sync,
explicit timestep mode. Phase 3 replaces these with proper measurements:

| Resolution | Low p50 / p99 (ms) | Medium p50 / p99 (ms) | High p50 / p99 (ms) |
| --- | --- | --- | --- |
| 720p | 0.29 / 0.71 | 1.08 / 1.77 | 5.62 / 6.61 |
| 1080p | 0.33 / 0.98 | 1.12 / 1.80 | 5.72 / 6.99 |
| 1440p | 0.45 / 0.98 | 1.17 / 1.89 | 5.81 / 7.08 |
| 2160p | 0.62 / 1.30 | 1.36 / 2.02 | 6.15 / 7.37 |

RGB10A2 (`NVCV_RGB10A2` + `NVCV_P32`) costs the same as RGBA8. Cost is almost flat in resolution, not
linear as the design doc assumed, so 4K at Medium looks viable and High fits a 60 fps source. Also seen:
`NVVFX_OUTPUT_IMAGE` can be rebound after load without error, and load takes well under a second.

## Solution layout

Projects are created in the phase that first needs them.

```
FrameMotionPlus.slnx
Directory.Build.props        net10.0, nullable, TreatWarningsAsErrors=true, analyzers, AOT analysis
Directory.Packages.props     central package management: every package version lives here
nuget.config                 nuget.org only, sources cleared, package source mapping
global.json                  pins the .NET 10 SDK band
src/
  FrameMotionPlus.Core/          pure, no OS or GPU calls: the six interfaces, frame/colour types,
                                 scheduler, cadence tracker, SPSC rings, telemetry, settings   (P1)
  FrameMotionPlus.App/           NativeAOT executable `fmp`: CLI, pipeline assembly, threads   (P1)
  FrameMotionPlus.Cuda/          CUDA driver-API bindings, wrappers, kernels,
                                 Repeat and Blend generators                                  (P2)
  FrameMotionPlus.Vulkan/        device selection, CUDA<->Vulkan bridge and presentation ring,
                                 presenter, SDL3 window, HUD                                  (P2)
  FrameMotionPlus.Vfx/           NvCVImage / NvVFX bindings, VfgGenerator                     (P3)
  FrameMotionPlus.Sources.File/  FFmpeg demux + NVDEC                                         (P4)
  FrameMotionPlus.Sources.V4l2/  capture card                                                 (P5)
  FrameMotionPlus.PipeWire/      audio delay line, ScreenCast capture                         (P8)
native/
  kernels/                       CUDA kernel sources and generated PTX                        (P2)
  shim/                          small C shim for PipeWire's macro-heavy API                  (P8)
tests/
  FrameMotionPlus.Core.Tests/    always run
  FrameMotionPlus.Gpu.Tests/     hardware-gated (see Testing)
docs/
  implementation-plan.md         this file
  benchmarks/                    measured results that gates are judged against
```

## Conventions for every phase

- **Build:** `net10.0`, latest C#, nullable enabled, `TreatWarningsAsErrors=true`, .NET analyzers on,
  `IsAotCompatible=true` on libraries so trim/AOT warnings fail the build. No per-project package versions.
- **Interop:** source-generated `LibraryImport` and function pointers only. No `DllImport`, no reflection,
  nothing that breaks NativeAOT.
- **Hot path** (anything run per frame): preallocated structs and native memory; no LINQ, closures,
  boxing, `async`, `params`, string formatting or logging allocations. Hot paths get a test asserting
  zero bytes allocated.
- **Time:** one clock domain. Every timestamp is `long` nanoseconds on `CLOCK_MONOTONIC`.
- **Platform split:** Core never references a platform project. Linux-only code lives behind the six
  interfaces (`IFrameSource`, `IFrameGenerator`, `IPresenter`, `IGpuBridge`, `IAudioDelay`, `IClock`).
- **Testing:** xUnit v3. Unit tests always run. Hardware tests carry a category and *skip* (never fail)
  when their requirement is missing, so hosted CI stays green:
  `Gpu` (NVIDIA GPU + `libcuda`), `Vfx` (VFX SDK), `Display` (Wayland session), `Capture` (a V4L2 device).
- **CI:** GitHub Actions builds and tests on Linux and Windows from Phase 1; Linux also does the
  NativeAOT publish.

## Workflow for every phase

1. Implementation by Opus 5.5 sub-agents, working from this plan and the design doc.
2. Build, tests and the phase's automated gate are run and the output captured.
3. An independent sub-agent reviews the phase's diff with no knowledge of the implementer's reasoning.
4. Review findings are fixed and re-verified.
5. Report to Adam: what was built, test and gate results, review findings and their resolution,
   deviations, anything still open.
6. On approval: commit, push to `master`, start the next phase.

Gates are split into **automated** (verified by the agents on this machine) and **manual** (need Adam,
hardware or eyes). A phase report states which manual gates are still outstanding.

## Phases

### Phase 1: Solution foundation and pure core

*Design doc: architecture, frame pacing maths, automated tests.*

- Solution scaffold: `.slnx`, `Directory.Build.props`, `Directory.Packages.props`, `nuget.config`,
  `global.json`, `.editorconfig`, `.gitignore`, `.gitattributes`, README, CI workflow.
- **Core abstractions:** the six interfaces; `GpuFrame`, `GpuSignal`, `PixelFormat` (RGBA8, BGRA8,
  RGB10A2), `MemoryDomain`, `ColorMetadata` (primaries, transfer, matrix, range, mastering display,
  content light level), `FrameGenCapabilities`, `FrameGenConfig`. 10-bit is representable from day one.
- **Scheduler:** the display-time rule (τ = V − D, t = (τ − tsₙ)/(tsₙ₊₁ − tsₙ)), epsilon snap to real
  frames, fixed-refresh and VRR slot clocks, unique-frame timeline, hold detection (no new frame within
  ~2× the expected interval stops generation), multiplier fallback when d > T/M, pipeline-delay tuner
  (hold + p99(d) + margin, slew-limited), latency model calculator.
- **Cadence tracker:** the CPU half of cadence detection: takes a per-frame max-tile-difference metric,
  classifies unique vs repeat with hysteresis, estimates the game frame rate, separate SDR and PQ thresholds.
- **Infrastructure:** lock-free SPSC ring, per-frame timeline telemetry ring, CSV export, allocation-free
  rolling percentile estimator, `IClock` with a simulated clock, settings model (source-generated JSON).
- **App:** `fmp` NativeAOT executable with `fmp simulate`, which drives the scheduler from a simulated
  source and clock with null generator and presenter and writes telemetry CSV.
- **Tests:** scheduler simulations for 30-in-60, 60 to 120, 40-in-120, 30 to 120, 59.94 on 120.00,
  non-integer 30 to 144, random drops, uneven frame times and long holds; latency model against the
  design doc's table; ring stress test; percentile and tuner tests; zero-allocation assertions.
- **Automated gate:** zero-warning build; all tests pass; `dotnet publish` NativeAOT for `linux-x64`
  succeeds with no trim/AOT warnings; `fmp simulate` output matches the latency model.

### Phase 2: GPU foundation (CUDA, Vulkan, the presentation ring)

*Design doc: GPU pipeline and CUDA/Vulkan interop; Phase 0 items that do not need VFG.*

- **CUDA:** driver-API bindings and wrappers: context, three streams (copy, compute, high-priority),
  device and pinned host memory, events for timing, module/kernel launch, external memory and external
  semaphore import.
- **Kernel toolchain:** kernels compiled with clang's NVPTX backend to PTX, embedded as resources,
  generated PTX checked in so a build needs no GPU toolchain. First kernels: test patterns (SDR and PQ),
  blend, copy/pack. Fallback if clang PTX proves unworkable: the `cuda` pacman package.
- **Vulkan:** physical device matched to the CUDA device by UUID; presentation ring of 4 to 6 exported
  `VkBuffer`s imported into CUDA; two timeline semaphores (CUDA signals written, Vulkan signals released);
  presenter pass (ring slot to image, scale, write swapchain); SDL3 window on Wayland at fixed refresh;
  offscreen target with readback for tests. RGBA8 and A2B10G10R10 ring formats.
- `RepeatGenerator` and `BlendGenerator` (CUDA domain) behind `IFrameGenerator`.
- **Decide here, with the real APIs in hand:** how a generator on its own CUDA stream waits for its
  inputs (the source frame's upload/convert and the ring slot being free). Phase 1 left
  `IFrameGenerator.Enqueue` exactly as the design doc has it and did not put a ready-signal on
  `GpuFrame`; if stream ordering alone is not enough, this phase changes Core to carry one.
- `fmp pattern` (CUDA test pattern through the ring to a window) and `fmp bench jitter`
  (NativeAOT hot-loop jitter on a dedicated thread).
- **Tests (`Gpu`, `Display`):** CUDA memory round trip; kernel load and run; CUDA writes a pattern into
  an exported buffer and Vulkan reads it back bit-exact, both formats; semaphore handshake in both
  directions; validation layer reports nothing.
- **Automated gate:** interop round trip passes; `fmp pattern` runs clean under the validation layer;
  runtime jitter under 0.5 ms p99 (design doc Phase 0 gate).

### Phase 3: VFG backend and the feasibility gate

*Design doc: Phase 0 remainder, VFG backend details. This is the go/no-go on VFG cost.*
The SDK is found at run time through a configurable SDK directory (default `lib/VideoFX` in a dev
checkout); nothing from `lib/` is copied into build output or committed.

- `NvCVImage` / `NvVFX` bindings; `VfgGenerator`: explicit timestep mode, own CUDA stream, preallocated
  image wrappers, manual shot-change flag on discontinuities, warm-up at startup, background reload and
  swap for quality or resolution changes.
- `fmp bench vfg`: 1080p / 1440p / 2160p × Low / Medium / High × (t = 0.5, 4x); p50/p99 of d from CUDA
  events; first-load time, engine caching, VRAM. Results written to `docs/benchmarks/`.
- Answer the design doc's open questions: can `NVVFX_OUTPUT_IMAGE` be rebound per run to a ring slot;
  how long is first load and is the engine cached.
- PQ RGB10A2 clip through VFG with frame dumps for inspection.
- **Automated gate:** 1440p Medium p99 d under 7 ms; VFG writes into the Vulkan-exported ring and the
  result presents. If 1440p fails, the report recommends 1080p or Low mode and later phases retarget.
- **Manual gate:** Adam looks at the PQ dumps for highlight, gradient and banding artefacts.

### Phase 4: File playback, first light

*Design doc: milestone 1.*

- FFmpeg demux + NVDEC (`hwaccel cuda`) sharing the app's CUDA context; convert kernel
  (NV12/P010 to RGBA8/RGB10A2, chroma upsample, matrix and range); frame history; PTS timing.
- Full pipeline assembly: source, scheduler and present threads joined by SPSC rings; generator
  selectable (repeat, blend, VFG 2x); fixed-refresh presenter.
- HUD (frame-time graph, d, D, dropped/repeated, detected frame rate) and telemetry CSV export.
- Smooth Motion presenter mode (`NVPRESENT_ENABLE_SMOOTH_MOTION=1` before instance creation), A/B against VFG.
- Quality harness: 120 fps footage, drop alternate frames, regenerate, score PSNR/SSIM against originals.
- File audio (audio as master clock) arrives with Phase 8; until then file video is timed from PTS.
- **Automated gate:** 1440p60 file to 120 Hz with zero missed slots over 10 minutes; zero gen0
  collections in that run; quality harness produces scores for each generator.
- **Manual gate:** Smooth Motion vs VFG A/B looks right.

### Phase 5: Capture card (V4L2)

*Design doc: milestone 2.* **Prerequisite for the hardware gate: a capture card, PS5 HDCP off.**

- V4L2 ioctl bindings with struct layouts checked against the kernel headers; `USERPTR` buffers from
  `cudaHostAlloc` with `MMAP` + `cudaHostRegister` fallback; real-time dequeue thread, newest-wins;
  upload and convert (adds YUYV); cadence kernel (max tile difference on a downsampled luma grid) feeding
  the Phase 1 tracker; hold handling; negotiated-format logging and MJPEG refusal.
- **Automated gate:** cadence detection correct on synthetic 30-in-60, 40-in-120 and dropped-frame
  sequences run through the real kernel; capture pipeline runs against `vivid` when loaded.
- **Manual gate:** end-to-end latency measured; 60 to 120 at 1440p within 2 ms of the latency model;
  a 30 fps game inside a 60 Hz signal is detected.
- **Cadence tracker backlog from the Phase 1 review** (accepted as non-blocking there; these become
  acceptance cases once the real metric kernel and a capture card set the thresholds):
  1. The estimate is frozen while in-band frames (metric between the low and high thresholds) arrive
     at least once per 8 unique frames. 60 to 30 fps with every 8th new frame in-band froze at
     50.53 fps; a learned 60 fps followed by 30 fps with in-band repeats stays at 60 and takes every
     capture as new; a source that starts with in-band repeats never learns a rate.
  2. One long gap in every 3 to 4 frames that is at least 5× the short gap (60 fps with an 83 ms hitch
     every 4th frame) makes the estimate saw-tooth 30/48/30. After a held-share reseed, do not hold gaps
     up to the largest gap just accepted.
  3. Long gaps under 25% of intervals are reported as hitches, not cadence: gaps 1,1,1,1,3 read 60.00
     against a true mean of 42.86. Decide whether that is the wanted meaning of "game frame rate".
  4. Animated menus (three or more irregular sub-200 ms gaps) can reseed the estimate; it recovers
     within 32 unique frames after play resumes.
  5. Cadences whose cycle is longer than 16 frames (57 fps in a 60 Hz signal) ripple by about 3%.
  6. A cadence slower than the 200 ms cap (under 5 fps) reports 0 by design.

### Phase 6: Pacing engine

*Design doc: milestone 3.*

- `VK_EXT_present_timing` targets and feedback; VRR clock mode; live auto-tuned D; 3x and 4x;
  fallback path (`present_wait2`/`present_id2`, `FIFO_LATEST_READY`, `clock_nanosleep` absolute);
  at most two swapchain images in flight; multiplier fallback when VFG cannot keep up.
- **Automated gate:** 30 to 120 and 40 to 120 with output interval standard deviation under 0.5 ms from
  present feedback; 59.94 source on 120 Hz with no periodic judder signature over 10 minutes.
- **Scheduler tuning backlog from the Phase 1 review** (accepted as non-blocking there; resolve against
  real hardware here):
  1. An animated menu (irregular 40 to 95 ms gaps) re-seeds the source interval and raises D to
     ~80 to 90 ms; it then slews down at 5 ms/s (~14 s). Jump D at hold boundaries or after a re-seed.
  2. Generation back-off after a slow-d period is long (resumes 133 s after d recovers from a 200 s
     slow period). The failed-probe test compares against 1.25× the warm-up value, not the budget.
  3. A warm-up d above the budget is never re-examined (warm-up 12 ms, real 3 ms: no generation).
  4. `fmp simulate --check` reports a failure on near-1:1 non-integer ratios (59.94 to 60, 110 to 120)
     where declining to generate is the intended behaviour.
  5. Constant wake lateness above the 5 ms clip makes every generated slot late or skipped.
  6. 110-on-120 with drops and jitter still cycles generation on and off (no misses).
  7. Wake lateness is charged against the GPU budget: 30 to 120 at d = 6 ms with 2 ms wake jitter
     drops to 2x although 6 ms fits the 8.33 ms budget.
  8. Bursts of three dropped frames re-seed each time (D bumps of ~6 ms, no misses).
  9. 1:1 passthrough with 1 ms timestamp jitter repeats ~19% of slots at D = margin.
  10. 24 to 60 and 50 to 120 on fixed refresh can show no real frames at an unlucky phase.
  11. VRR accepts source rates up to 2% over the panel maximum, so the presenter must drop rather than
      queue early presents, or feed present lag back to the scheduler.
  12. Disturbance sweep gaps: it does not assert skipped or late counts, allows 2 misses per event,
      treats "generating" as generated ≥ 0.3 × real, and has no near-1:1 non-integer ratio.

### Phase 7: HDR

*Design doc: milestone 4.*

- HDR detection from source metadata with manual override; metadata passthrough; HDR10 swapchain with
  `VK_EXT_hdr_metadata`; HDR to SDR tone mapping; SDR placed in HDR at a set paper white; linear-light
  scaling; both VFG instances loaded for mid-session SDR/HDR switching; HUD brightness in HDR.
- **Automated gate:** HDR test patterns numerically correct on the offscreen target; no added d versus SDR.
- **Manual gate:** PS5 HDR game looks right on HDR and SDR displays; SDR/HDR switch recovers in one
  frame with no black flash.

### Phase 8: Audio and Remote Play

*Design doc: milestone 5.*

- PipeWire shim; audio delay line tracking measured video latency; file audio as master clock.
- chiaki-ng window capture through the ScreenCast portal (SDR).
- libchiaki + NVDEC for HDR Remote Play. **Linking libchiaki makes the app AGPL: needs Adam's decision
  before any code is written.**
- **Gates:** measured A/V offset under 10 ms; Remote Play 1080p60 to 120 works (manual, needs a PS5).

### Phase 9: Latency extras and polish

*Design doc: milestone 6.*

- Direct display mode (`VK_KHR_display` + `VK_EXT_acquire_drm_display`), hitch-free quality switching,
  settings UI, two-hour soak (no drift, stable memory, zero steady-state collections).
- **Manual gate:** direct display measurably beats Wayland fullscreen end to end.

### Phase 10: Windows portability

*Design doc: milestone 7.* Cannot be verified on this machine beyond the CI build; needs a Windows box.

- Media Foundation capture, NT-handle CUDA/Vulkan bridge, Windows presenter, VFG on Windows.
- **Gate:** the Phase 4 file-playback test passes on Windows.

## Deviation from the design doc's ordering

The design doc starts with a feasibility spike (its Phase 0). Here that spike is split: the parts that
need no VFG (interop proof, runtime jitter) are Phase 2, and the VFG measurements are Phase 3. The pure
core comes first because it needs no hardware or SDK and everything else builds on it. The go/no-go
still lands before any pipeline code depends on VFG, and the preliminary numbers above already point
to "go".
