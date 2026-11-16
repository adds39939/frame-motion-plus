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
rings, telemetry, settings, and the `fmp simulate` command. There is no GPU, capture or display code
yet.

## Layout

```
src/FrameMotionPlus.Core/         pure managed core: interfaces, scheduler, cadence, rings, telemetry, settings, simulation
src/FrameMotionPlus.App/          the `fmp` NativeAOT executable
tests/FrameMotionPlus.Core.Tests/ xUnit v3 tests of the core and the CLI (always run)
```

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
