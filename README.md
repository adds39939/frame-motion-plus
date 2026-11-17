# Frame Motion Plus

Live frame generation for a PS5 (capture card first, then video files and PS Remote Play): unique
frames are interpolated on the GPU with NVIDIA Maxine Video Frame Generation (VFG) in CUDA and
presented with Vulkan, paced by a scheduler that works from display times. Linux first (KDE Plasma
Wayland, RTX 50 series); Windows and macOS are design targets.

- Design (what and why): [`Live Frame Generation App Implementation Plan.md`](Live%20Frame%20Generation%20App%20Implementation%20Plan.md)
- Build phases (order, projects, gates): [`docs/implementation-plan.md`](docs/implementation-plan.md)

## Status

Phase 1 (solution foundation and pure core) is in place: the platform-neutral core with the six
backend interfaces, frame and colour types, the display-time scheduler, the cadence tracker, lock-free
rings, telemetry, settings, and the `fmp simulate` command.

Phase 2 (GPU foundation) is joined: CUDA driver-API bindings and kernels, the Vulkan presenter with its
exportable presentation ring, the CUDA/Vulkan bridge that imports the ring and its two timeline
semaphores into CUDA, the platform clock and threads, and the `fmp pattern` and `fmp bench jitter`
commands. Results: [`docs/benchmarks/phase2-interop.md`](docs/benchmarks/phase2-interop.md),
[`docs/benchmarks/phase2-jitter.md`](docs/benchmarks/phase2-jitter.md). There is no capture, file or
VFG code yet.

## Layout

```
src/FrameMotionPlus.Core/         pure managed core: interfaces, scheduler, cadence, rings, telemetry, settings, simulation
src/FrameMotionPlus.App/          the `fmp` NativeAOT executable
src/FrameMotionPlus.Platform/     CLOCK_MONOTONIC clock, absolute-deadline waits, dedicated real-time threads, GC helpers
src/FrameMotionPlus.Cuda/         CUDA driver-API bindings, streams, events, external memory/semaphores, kernels, Repeat/Blend generators
src/FrameMotionPlus.Vulkan/       device selection, exportable presentation ring, presenter, SDL3 window, offscreen target
src/FrameMotionPlus.CudaVulkan/   the IGpuBridge: the presenter's ring and timelines imported into CUDA (Linux, opaque FDs)
native/kernels/, native/shaders/  CUDA kernel and shader sources with the generated PTX and SPIR-V checked in
tests/FrameMotionPlus.Core.Tests/ xUnit v3 tests of the core and the CLI (always run)
tests/FrameMotionPlus.Platform.Tests/ tests of the platform layer and `fmp bench jitter` (Linux-specific ones skip elsewhere)
tests/FrameMotionPlus.Cuda.Tests/   CUDA bindings, kernels against CPU references, generators (category Gpu)
tests/FrameMotionPlus.Vulkan.Tests/ ring, presenter, offscreen and windowed presents under the validation layer (Gpu, Display)
tests/FrameMotionPlus.Gpu.Tests/    CUDA/Vulkan interop end to end and `fmp pattern` (Gpu, Display)
```

Hardware tests carry the category `Gpu` (NVIDIA GPU, CUDA driver, Vulkan with the validation layer) or
`Display` (a Wayland session and SDL3 as well) and skip, never fail, when that is missing, so hosted CI
stays green. Vulkan tests run under the validation layer and fail on any message.

## Build, test, publish

Requires the .NET 10 SDK (see `global.json`). NativeAOT publishing on Linux also needs `clang` and
`zlib` development headers.

```sh
dotnet build FrameMotionPlus.slnx -c Release
dotnet test FrameMotionPlus.slnx -c Release
dotnet publish src/FrameMotionPlus.App -c Release -r linux-x64
```

Build output goes to `artifacts/`; the native binary is
`artifacts/publish/FrameMotionPlus.App/release_linux-x64/fmp`. Warnings are errors, and package
versions live only in `Directory.Packages.props`.

## Simulating the scheduler

`fmp simulate` runs the real scheduler against a simulated source and display (null generator and
presenter), prints slot counts and the measured added latency, and compares it with the design doc's
latency model ΔL = d + (M − 1)/M · T.

```sh
fmp simulate --source-fps 60 --display-hz 120                  # 2x, fixed refresh
fmp simulate --source-fps 30 --display-hz 120 --clock vrr      # 4x, VRR slot clock
fmp simulate --source-fps 59.94 --display-hz 120 --csv run.csv # non-integer ratio, telemetry CSV
fmp simulate --source-fps 30 --display-hz 120 --d-ms 10        # multiplier fallback (d > T/M)
fmp simulate --drop-rate 0.05 --hold-at-s 5 --hold-for-s 3     # dropped frames and a long hold
fmp --help
```

`--check` makes the command exit non-zero unless the run meets the design doc: no missed slot, content
regression, causality or timestep error on any run, and on ideal inputs also the {k/M} timestep pattern,
the real:generated ratio and the latency band from the latency model. Wake-up lateness, stalls, ingest
jitter, rate changes and decode-ahead delivery can be injected (`fmp --help`). `fmp settings` prints the
default settings JSON; `fmp settings --validate <file>` checks a settings file and exits 1 if it has unknown,
duplicate or null properties or out-of-range values (exit codes: 0 success, 1 check failed, 2 usage error,
70 internal error).

## Measuring pacing jitter

`fmp bench jitter` is the design doc's runtime-jitter measurement. A dedicated pacing thread waits for each
output slot's decision time on `CLOCK_MONOTONIC` and then does the real per-slot work: it feeds synthetic frame
arrivals from a second dedicated thread (through the real SPSC ring) to the real scheduler, makes the decision and
writes a telemetry record. It reports wake lateness (actual wake minus requested deadline) and per-slot work time
(p50 to max), missed slots, GC collections and hot-thread allocations. Slots the loop came too late for count in
the percentiles too. The same loop with the scheduler work removed runs in alternating blocks with it, so the OS's
timer jitter can be told apart from what our managed code adds. It exits 1 if p99 wake lateness exceeds the gate
(0.5 ms by default), if any slot is missed (`--max-missed` sets a budget), or on any collection or hot-thread
allocation. Run it on the
published binary; results for this machine are in [`docs/benchmarks/phase2-jitter.md`](docs/benchmarks/phase2-jitter.md).

```sh
fmp bench jitter                                   # 120 Hz, clock_nanosleep wait, 10 s measured per loop
fmp bench jitter --rate 240 --wait hybrid --duration-s 60 --csv jitter.csv  # sleep, then spin the last 200 us
fmp bench jitter --rt --pin-core 4                 # ask for SCHED_FIFO and a core; prints what was granted and why not
```

Real-time scheduling is optional and never fatal: the threads report what they obtained, and when `SCHED_FIFO` is
refused they print what would allow it (an `rtprio` limit, `CAP_SYS_NICE`, or rtkit).

## Showing the test pattern

`fmp pattern` runs the Phase 2 pipeline end to end: CUDA draws a test pattern (colour bars, a moving
box, a ramp and a 32-bit frame counter; 8-bit SDR or 10-bit PQ) straight into a presentation-ring slot,
the slot's timelines order everything GPU-side, and the Vulkan presenter shows it at the display's fixed
refresh, from a dedicated thread paced on `CLOCK_MONOTONIC`. `--blend` puts a 2x generator in the loop:
real frames alternate with `BlendGenerator` output at t = 0.5, written straight into the ring.
`--offscreen` needs no display: the target is the pattern's size and every frame is read back and
compared bit for bit with a CPU reference, so the command doubles as an automated check. It prints
frames presented, dropped and skipped, presents in flight, the GPU time of the pattern, blend and
present work, CPU time per frame, and allocations and collections during the run.

```sh
fmp pattern                                         # 2560x1440 8-bit in a 960x540 window for 5 s (Esc closes)
fmp pattern --format 10 --blend --validate          # 10-bit (HDR10 swapchain where offered), 2x blend, validation layer
fmp pattern --offscreen --size 3840x2160 --frames 600 --validate   # headless, every frame verified
fmp pattern --offscreen --no-verify --frames 1200   # headless without readback, to measure costs alone
fmp pattern --offscreen --unpaced --no-verify --duration-s 3   # stress: no pacing, back-pressure alone
```

It also shows how the presenter learns that a present was shown (and warns when a window cannot cap
presents in flight), how many writes the producer had handed out that the GPU had not finished (the
bridge holds it back at a limit of 2 × the ring's slots), any present request the presenter rejected,
and whether the bridge drained and freed its import at teardown. `--unpaced` drops the demo's pacing
and its wait for a free image, so only the bridge's back-pressure and the presenter's own rules keep
the loop going (frames may be held back or dropped there; nothing may be rejected). It exits 1 on any
failure, a dropped or held-back frame offscreen (paced), a rejected present request, writes signalled
out of order, an import not freed after teardown, a validation message (with `--validate`) or a
managed allocation on the present thread after the warm-up, and 69 with a one-line message when no
usable NVIDIA GPU, Vulkan device, Wayland session or validation layer is found. Ctrl+C or SIGTERM stops
the run cleanly with its verdict (a second one ends it at once). The bridge needs Linux; on other
systems the command says so.
