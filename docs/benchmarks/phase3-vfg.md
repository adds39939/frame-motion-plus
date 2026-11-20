# Phase 3: VFG backend and the feasibility gate (`fmp bench vfg`)

Gate (`docs/implementation-plan.md`, Phase 3, and the design doc's milestone 0): **1440p Medium p99 d under 7 ms**,
and **VFG writes into the Vulkan-exported ring and the result presents**. Manual gate: Adam looks at the PQ dumps.

Machine: RTX 5090, driver 620.70.06 (CUDA driver API 13.5), Plasma 6.8 Wayland, DP-3 4K 240 Hz and HDMI-A-1 4K
120 Hz, both HDR. NVIDIA VFX SDK 1.3.0.0 with the Video Frame Generation feature 1.3.0.0 under `lib/` (never
committed). .NET 10.0.114 SDK; `fmp` is the NativeAOT binary from `dotnet publish src/FrameMotionPlus.App -c Release
-r linux-x64`. Conditions of every published-binary run are recorded below each table (`uptime`, `nvidia-smi`): the
desktop was in use but idle (Discord and Steam open, no call, no game, no browser video); the GPU sat at P3, 0 %
utilisation, 2.4 GB used by other processes before each run. The test-host numbers quoted in "Answers" come from the
JIT test host (`tests/FrameMotionPlus.Vfx.Tests`, `CharacterizationTests` and friends) on the same machine.

## Verdict

**The gate is met by a factor of almost six: 1440p Medium p99 d = 1.21 ms (gate 7 ms), and VFG writes straight into
the presentation ring and presents.**

- **VFG's cost is almost flat in resolution** (the design doc assumed linear in pixels): Medium 1.14 / 1.19 / 1.37 ms
  p50 at 1080p / 1440p / 2160p, Low 0.35 / 0.47 / 0.64 ms, High 5.69 / 5.77 / 6.10 ms; p99 within 0.05 to 0.4 ms of
  p50; RGB10A2 costs the same as RGBA8; t = 0.25/0.75 cost the same as t = 0.5. **4K60 → 120 fits at Medium with
  6.9 ms to spare**, and every 4x mode (30 → 120, 60 → 240) fits at Medium up to 4K. High fits a 30 → 120 or 60 → 120
  budget (8.33 ms) at 1080p and 1440p, is marginal at 4K (p99 6.5 ms idle, up to 7.9 ms with a browser active), and
  never fits 60 → 240 (4.17 ms).
- **Output rebinding works per run**: `NVVFX_OUTPUT_IMAGE` is pointed at a different ring slot every run (one
  `NvVFX_SetImage`, under a microsecond), with any pitch and offset a slot has; no device-to-device copy is needed.
  `fmp pattern --vfg` runs real frames and VFG frames alternately into the ring, offscreen and windowed, under the
  validation layer: 0 messages, 0 dropped, 0 rejected, 0 B allocated per frame, every real frame bit-exact and every
  generated frame closer to the pattern's true middle frame than the blend (box band error 0.05 codes vs 1.6).
- **`NvVFX_Run` blocks its caller while VFG's automatic shot-change detection is on** (the SDK default and the design
  doc's choice): until the work queued on VFG's stream before it *and* the generation itself have finished, spinning a
  CPU core meanwhile; the `async` argument changes nothing. With detection off it only enqueues (40 µs), but an
  unreported cut then comes out as a morph of the two shots. So `IFrameGenerator.Enqueue`'s "enqueue only, never
  waits" does **not** hold for VFG as configured; Core now says so (`FrameGenCapabilities.EnqueueMayBlock`), and Phase 4
  runs VFG on a generator thread of its own with detection on (Answers, 1).
- **`NvVFX_Load` and `NvVFX_DestroyEffect` synchronise the whole CUDA context** and block every other thread's CUDA
  calls meanwhile, like `cuMemFree` in Phase 2. Runs do neither. So a VFG instance can only be built or destroyed at a
  drained point; switching between instances already loaded (SDR/HDR) is free.
- **First load** is 8 to 48 ms with a warm CUDA JIT cache (per mode: Low ~7, Medium ~21, High ~42 ms), and 114 to
  217 ms per mode the first time on a machine (empty `~/.nv/ComputeCache`). Nothing else is cached; no engine files are
  written. VRAM per instance: 140 MB to 1.9 GB (1440p Medium 858 MiB).
- **PQ content interpolates as well as SDR** except for small, fast, independently moving specular highlights, where
  VFG smears (highlight error 83 codes vs the blend's 397, PSNR 38 dB against 48 to 63 dB elsewhere, Medium). 10-bit precision
  survives: no banding in ramps or dark gradients. **High is not simply better than Medium**: around those highlights it
  disturbs the dark background widely (40 % of the shadow pixels off by more than 4 codes, against 4 % for Medium), and
  it trails Medium on a detailed pan. Frames for Adam to inspect are in `out/vfg-quality/`, `pq-specular/e-vfg-high.png`
  first (see "PQ").
- **Recommended defaults**: Medium at the source's own resolution, up to 4K. High is an option, not an upgrade: it
  costs 4.6 ms more latency, is marginal at 4K 120 Hz, and is worse than Medium on bright highlights over dark
  backgrounds and on detailed pans; Low only where nothing else fits (it is visibly softer: 36 dB against Medium's 43 to
  46 dB on the moving box). Details at the end.

## What was built

```
src/FrameMotionPlus.Vfx/
  Interop/NvCVTypes.cs        NvCV_Status, NvCVImage (64-byte struct), pixel formats, component types, constants
  Interop/VfxNative.cs        LibraryImport bindings (NvVFX_*, NvCVImage_Init, NvCV_GetErrorStringFromCode) and
                              VfxFrameCalls, function pointers for the five per-frame calls (see "Allocation")
  Interop/VfxSelectors.cs     effect and parameter selectors, encoded once as null-terminated UTF-8 in native memory
  Interop/VfxLibraries.cs     loading the SDK from a directory, dependencies first, by absolute path; the resolver
  Interop/VfxLog.cs           the SDK's own log (warnings, errors) captured into exception messages
  VfxSdkLocator.cs            where the SDK is: option, $FMP_VFX_SDK, settings, a checkout's lib/VideoFX, /usr/local/VideoFX
  VfxSdk.cs                   TryLoad / IsLoaded / Version / MissingFiles / EnsureLoaded
  VfxException.cs             VfxException (call, NVCV status name, SDK description, SDK log), VfxUnavailableException
  VfgEffect.cs                one loaded VFG instance on one stream; per-run rebinding; VfgMode, VfgInstanceKey
  VfgGenerator.cs             IFrameGenerator; VfgOptions, VfgLoadReport
src/FrameMotionPlus.App/
  Cli/VfgBenchCommand.cs, Vfg/VfgBench*.cs, Vfg/VfgReport.cs, Vfg/VfgContent.cs, Vfg/SyntheticTexture.cs   fmp bench vfg
  Cli/VfgQualityCommand.cs, Vfg/VfgQuality.cs, Vfg/QualityScenes.cs, Vfg/PngWriter.cs                     fmp bench vfg-quality
  Pattern/*                   fmp pattern --vfg (and InterpolationScore in PatternVerifier)
src/FrameMotionPlus.Core/Abstractions/DiscontinuityLatch.cs   "the next pair" of NotifyDiscontinuity, shared by every generator
tests/FrameMotionPlus.Vfx.Tests/   92 tests, category Vfx; skip without CUDA or the SDK
```

The API later phases use:

```csharp
// Find and load the SDK (once per process); false with a one-line reason when it is not there.
VfxSdk.TryLoad(out string? reason, string? explicitDirectory = null, string? settingsDirectory = null);
VfxSdkLocator.Resolve(explicitDirectory, settingsDirectory) -> VfxSdkLocation { Directory, FeatureDirectory, Source }

public sealed class VfgGenerator : IFrameGenerator
{
    VfgGenerator(CudaContext context, VfgOptions? options = null); // VfgOptions: AutomaticShotChangeDetection (true),
                                                                   // WarmupRuns (8), MaxLoadedInstances (2),
                                                                   // StreamPriority (High), Stream (caller's, optional)
    CudaStream Stream { get; }            // every wait, run, span and done signal goes here; VFG's NVVFX_CUDA_STREAM
    FrameGenCapabilities Capabilities { get; } // EnqueueMayBlock = AutomaticShotChangeDetection (Answers, 1)
    void Configure(in FrameGenConfig c);  // instant if prepared; otherwise loads + warms up (a drained point only);
                                          // reloads a faulted instance
    bool TryConfigurePrepared(in FrameGenConfig c); // switch only if loaded: never loads, safe during playback
    VfgLoadReport Prepare(in FrameGenConfig c); // load another instance without activating it (drained point)
    bool IsPrepared(in FrameGenConfig c);
    void ReleaseInactive();               // destroy prepared instances (drained point)
    void Enqueue(in GpuFrame prev, in GpuFrame curr, float t, in GpuFrame output, in GpuSignal done);
    void NotifyDiscontinuity();           // every call with the next pair: manual shot-change flag (outputs curr)
    bool IsFaulted { get; }               // an SDK failure: Enqueue throws until Configure reloads
    bool TryReadGpuTime(out long ns);     // d of each Enqueue, oldest first, non-blocking
    VfgLoadReport LastLoad { get; }       // create, load and warm-up times, device bytes, first in process
    long Runs, ShotChangeRuns, RealFrameCopies, Loads, DroppedGpuTimes { get; }
}
```

`Enqueue` follows the Phase 2 generators: it validates every frame and signal first (nothing is enqueued for a call
that fails on its arguments), waits GPU-side on `prev.Ready`, `curr.Ready` (if different) and `output.Ready`, opens
a GPU span, points the three image descriptors at the frames (handing a descriptor to the SDK only when its address
or pitch changed), sets `FrameMultiplier = 0` (which resets the timestep) and `Timestep = t`, sets the manual
`ShotChange` flag for every call with the pair that follows a `NotifyDiscontinuity` (Core's "next pair", below),
calls `NvVFX_Run`, clears the flag (its status checked too), closes the span and signals `done`. VFG refuses t = 0
and t = 1 (`NVCV_ERR_PARAMETER`, "TIMESTEP must be in (0.0, 1.0)"); for those two the
generator copies the real frame, so the IFrameGenerator contract (t in [0, 1]) holds. Capabilities: max multiplier
8, any t, no extrapolation, no scaling, RGBA8/BGRA8/RGB10A2, CUDA device memory. `Configure` loads the instance for
(size, format, mode) and warms it up with 8 runs on dummy frames (varied t and one shot change), because the first
runs allocate (see Answers, 3); a configuration whose instance is loaded already is switched to without any CUDA call.

**Failure (review fix).** A call that throws produced nothing and signalled nothing (Core's contract now says so for
every generator: if others wait on `done`, the caller signals it itself). If the SDK call itself failed, the active
instance is **faulted**: `IsFaulted` is true, every later `Enqueue` throws `InvalidOperationException` naming the
remedy, `TryConfigurePrepared` and `IsPrepared` refuse it, and `Configure` with the same configuration (or `Prepare`)
destroys and reloads it, a drained-point operation like any load. Before the fix, `Configure(same)` was a no-op and the
instance stayed broken. `A_failed_run_faults_the_instance_until_it_is_loaded_again` covers it.

**A switch that never loads (review fix).** `TryConfigurePrepared(config)` switches only to an instance that is
loaded and not faulted and returns false otherwise, without loading: a pipeline that loaded its instances at start-up
uses it during playback and can rely on no `NvVFX_Load` (which synchronises the context, "Reload and swap") happening
there. `A_prepared_only_switch_never_loads`.

## Loading the SDK without `LD_LIBRARY_PATH`

Measured with `LD_DEBUG=libs` (glibc's loader trace) on a small C program and confirmed in the C# loader:

- `libVideoFX.so` (the dispatcher) opens its backend with `dlopen("libVideoFXLocal.so")`, a **bare name**. It does not
  look next to itself. (It also tries `libVideoFXTriton.so` the same way when its `NvVFX_ConfigureLogger` is called:
  that library needs a gRPC client the SDK does not ship, so the dispatcher prints "Cannot load libVideoFXTriton.so"
  to stderr. The logger is therefore configured through `libVideoFXLocal.so` directly, which does the same job
  silently.)
- `libVideoFXLocal.so` opens the feature with `dlopen("libnvVFXVideoFrameGeneration.so")`, again a **bare name**, and
  tries nothing else (one search, in the system directories). The strings `features` and `bin` in it are presumably
  the Windows layout's; on Linux the feature must be findable by the loader.
- Their dependencies are plain `DT_NEEDED` sonames: `libcudart.so.12`, `libnppc/ial/icc/idei/ig.so.12`, `libcudnn.so.9`
  (which has `RUNPATH $ORIGIN` for its own sub-libraries), `libNVCVImage.so` (whose only `RPATH` is the build machine's
  `/usr/local/cuda-12.8/targets/x86_64-linux/lib`), and the driver's `libcuda.so.1`.

glibc satisfies a bare-name `dlopen` or a `DT_NEEDED` entry with an object **already loaded under that soname**, whatever
`LD_LIBRARY_PATH` says. So `VfxLibraries` loads, with `NativeLibrary.Load` by absolute path and in this order:
`external/cuda/lib/libcudart.so.12`, `libnppc.so.12`, `libnppial.so.12`, `libnppicc.so.12`, `libnppidei.so.12`,
`libnppig.so.12`, `libcudnn.so.9` (each from the SDK's `external/cuda/lib`, or by soname from the system if the SDK
directory has no `external/`), then `lib/libNVCVImage.so`, `lib/libVideoFXLocal.so`, `lib/libVideoFX.so`, and
`features/nvvfxvideoframegeneration/lib/libnvVFXVideoFrameGeneration.so`. The LD_DEBUG trace of the loaded program shows
every one of the SDK's lookups resolved to these objects and only the driver's own optional `libnvcuextend` searched
in `/usr/lib`. `LD_LIBRARY_PATH` could not be used anyway: the loader reads it once, at process start. The test
`The_sdk_loads_from_its_directory_alone_dependencies_first` checks the order and that every path is absolute and
inside the SDK directory, in a test host started without `LD_LIBRARY_PATH`; the published `fmp` runs the same way.

The directory comes from, first set wins: `--vfx-sdk <dir>`, `$FMP_VFX_SDK`, `generator.vfxSdkDirectory` in a settings
file (new, additive Core setting), `lib/VideoFX` of the repository checkout containing the program (not one found from
the current directory: review fix, native code is not loaded relative to the working directory), `/usr/local/VideoFX` (NVIDIA's installer default). A directory missing the SDK's files is refused before
anything is loaded, with the missing files named; once loading starts the outcome is final for the process (libraries
cannot be unloaded safely). `VfxSdk.IsLoaded` turns true only after the version is read and the SDK's log is set up
(review fix: it was published before them), and registering the `DllImport` resolver no longer takes the load lock
(review fix: a binding first resolved on another thread while a load held the lock, from inside a static constructor,
could deadlock with it). Without the SDK or the GPU every VFG command exits 69 with one line, e.g.
`fmp: bench vfg needs the NVIDIA VFX SDK with Video Frame Generation: VFX SDK at /x (from $FMP_VFX_SDK) is incomplete:
lib/libNVCVImage.so, … not found` or `fmp: bench vfg needs an NVIDIA GPU with the CUDA driver: cuInit:
CUDA_ERROR_NO_DEVICE (no CUDA-capable device is detected)`. On Windows the loader looks for `NVCVImage.dll` and
`NVVideoEffects.dll` in the directory; that path compiles but is untested (Phase 10).

## The benchmark matrix

`fmp bench vfg` (published NativeAOT binary), full matrix: 600 generated frames per cell after 30 of warm-up (on top
of the generator's own 8 warm-up runs at load), between neighbouring frames of the test pattern (frames 4i, the box
moving 4 × W/240 px per source frame: 32 px at 1080p, 40 px at 1440p, 64 px at 2160p), each run's output into the
next of four buffers (rebound every run, as into ring slots), paced as in the pipeline at 120 Hz output: 2x runs one
frame at t = 0.5 every 16.7 ms, 4x three frames at t = 0.25/0.5/0.75 in the three slots after each real frame.
Automatic shot-change detection on. d is the GPU span of each run (CUDA events around the work on VFG's stream);
"host" is the wall time of the `Enqueue` call. Every 20th output was read back: all 1,080 checked outputs were genuine
interpolations (none a copy of a neighbour) and every one was closer to the true intermediate frame than the blend
(PSNR over the moving band, last column). Times in ms.

| Size | Mode | Format | M | d p50 | p90 | p99 | max | host p50 | host p99 | load | warm-up | VRAM MiB | VFG / blend dB |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1080p | Low | RGBA8 | 2x | 0.35 | 0.37 | 0.41 | 0.42 | 0.34 | 0.48 | 10.1 | 3.9 | 140 | 36.3 / 22.5 |
| 1080p | Low | RGBA8 | 4x | 0.35 | 0.37 | 0.41 | 0.42 | 0.34 | 0.48 | | | | 31.1 / 23.3 |
| 1080p | Medium | RGBA8 | 2x | 1.14 | 1.15 | 1.16 | 1.18 | 1.13 | 1.20 | 22.2 | 8.2 | 502 | 46.0 / 22.5 |
| 1080p | Medium | RGBA8 | 4x | 1.13 | 1.15 | 1.17 | 1.30 | 1.12 | 1.19 | | | | 46.0 / 23.3 |
| 1080p | High | RGBA8 | 2x | 5.69 | 5.71 | 5.92 | 6.31 | 5.68 | 5.76 | 49.1 | 42.1 | 1192 | 50.5 / 22.5 |
| 1080p | High | RGBA8 | 4x | 5.69 | 5.71 | 5.73 | 6.31 | 5.68 | 5.76 | | | | 49.3 / 23.3 |
| 1080p | Low | RGB10A2 | 2x | 0.35 | 0.37 | 0.41 | 0.86 | 0.34 | 0.48 | 7.3 | 2.6 | 140 | 40.5 / 27.6 |
| 1080p | Low | RGB10A2 | 4x | 0.35 | 0.37 | 0.41 | 0.43 | 0.34 | 0.48 | | | | 35.1 / 28.4 |
| 1080p | Medium | RGB10A2 | 2x | 1.14 | 1.15 | 1.17 | 1.64 | 1.13 | 1.20 | 20.8 | 8.2 | 502 | 50.0 / 27.6 |
| 1080p | Medium | RGB10A2 | 4x | 1.13 | 1.15 | 1.16 | 1.18 | 1.12 | 1.19 | | | | 49.8 / 28.4 |
| 1080p | High | RGB10A2 | 2x | 5.70 | 5.72 | 5.78 | 6.33 | 5.69 | 5.77 | 43.1 | 42.1 | 1192 | 53.6 / 27.6 |
| 1080p | High | RGB10A2 | 4x | 5.70 | 5.72 | 5.94 | 6.34 | 5.69 | 5.77 | | | | 54.5 / 28.4 |
| 1440p | Low | RGBA8 | 2x | 0.47 | 0.48 | 0.49 | 0.51 | 0.44 | 0.52 | 7.0 | 3.4 | 206 | 36.9 / 22.8 |
| 1440p | Low | RGBA8 | 4x | 0.47 | 0.48 | 0.50 | 0.60 | 0.44 | 0.51 | | | | 31.9 / 23.6 |
| **1440p** | **Medium** | **RGBA8** | **2x** | **1.19** | **1.20** | **1.21** | **1.22** | **1.16** | **1.23** | **21.3** | **8.6** | **858** | **43.6 / 22.8** |
| **1440p** | **Medium** | **RGBA8** | **4x** | **1.19** | **1.20** | **1.21** | **1.23** | **1.16** | **1.23** | | | | **43.9 / 23.6** |
| 1440p | High | RGBA8 | 2x | 5.77 | 5.79 | 5.80 | 6.61 | 5.75 | 5.83 | 43.0 | 42.7 | 1288 | 47.4 / 22.8 |
| 1440p | High | RGBA8 | 4x | 5.78 | 5.79 | 5.84 | 6.51 | 5.75 | 5.82 | | | | 46.8 / 23.6 |
| 1440p | Low | RGB10A2 | 2x | 0.46 | 0.48 | 0.49 | 0.51 | 0.44 | 0.52 | 7.0 | 3.4 | 206 | 41.2 / 27.9 |
| 1440p | Low | RGB10A2 | 4x | 0.47 | 0.47 | 0.49 | 0.51 | 0.44 | 0.51 | | | | 36.0 / 28.7 |
| **1440p** | **Medium** | **RGB10A2** | **2x** | **1.18** | **1.19** | **1.21** | **1.23** | **1.16** | **1.24** | **21.3** | **8.6** | **858** | **48.5 / 27.9** |
| **1440p** | **Medium** | **RGB10A2** | **4x** | **1.19** | **1.20** | **1.21** | **1.34** | **1.16** | **1.23** | | | | **48.2 / 28.7** |
| 1440p | High | RGB10A2 | 2x | 5.78 | 5.79 | 5.81 | 6.36 | 5.76 | 5.83 | 42.5 | 42.6 | 1288 | 52.2 / 27.9 |
| 1440p | High | RGB10A2 | 4x | 5.78 | 5.80 | 6.12 | 6.39 | 5.76 | 6.08 | | | | 53.7 / 28.7 |
| 2160p | Low | RGBA8 | 2x | 0.64 | 0.66 | 0.67 | 1.09 | 0.59 | 0.66 | 7.2 | 4.7 | 382 | 35.7 / 22.5 |
| 2160p | Low | RGBA8 | 4x | 0.64 | 0.65 | 0.67 | 1.14 | 0.58 | 0.64 | | | | 30.8 / 23.4 |
| 2160p | Medium | RGBA8 | 2x | 1.37 | 1.38 | 1.40 | 1.86 | 1.31 | 1.38 | 21.5 | 10.4 | 1864 | 43.0 / 22.5 |
| 2160p | Medium | RGBA8 | 4x | 1.36 | 1.38 | 1.55 | 1.82 | 1.31 | 1.38 | | | | 43.2 / 23.4 |
| 2160p | High | RGBA8 | 2x | 6.10 | 6.12 | 6.37 | 6.77 | 6.05 | 6.13 | 45.2 | 45.1 | 1542 | 45.5 / 22.5 |
| 2160p | High | RGBA8 | 4x | 6.11 | 6.12 | 6.14 | 6.75 | 6.06 | 6.12 | | | | 45.0 / 23.4 |
| 2160p | Low | RGB10A2 | 2x | 0.64 | 0.65 | 0.67 | 1.12 | 0.58 | 0.66 | 7.1 | 4.7 | 382 | 40.0 / 27.6 |
| 2160p | Low | RGB10A2 | 4x | 0.64 | 0.65 | 0.67 | 1.12 | 0.58 | 0.65 | | | | 34.9 / 28.4 |
| 2160p | Medium | RGB10A2 | 2x | 1.37 | 1.38 | 1.39 | 1.87 | 1.31 | 1.39 | 21.4 | 9.9 | 1864 | 47.3 / 27.6 |
| 2160p | Medium | RGB10A2 | 4x | 1.37 | 1.38 | 1.41 | 1.87 | 1.31 | 1.37 | | | | 47.4 / 28.4 |
| 2160p | High | RGB10A2 | 2x | 6.11 | 6.12 | 6.18 | 6.75 | 6.05 | 6.13 | 44.1 | 45.0 | 1542 | 49.7 / 27.6 |
| 2160p | High | RGB10A2 | 4x | 6.12 | 6.13 | 6.48 | 6.75 | 6.06 | 6.40 | | | | 50.6 / 28.4 |

Gate line printed by the run: `gate PASS: 1440p Medium p99 d 1.21 ms at worst (RGBA8 4x) over 4 cell(s), gate <= 7 ms`,
exit 0. Conditions: 13:18:29 load average 0.63, GPU 0 %, P3, 2380 MiB used before; 13:23:45 load average 0.52 after.
The 4x cells reuse the 2x cell's instance (same size, format and mode), so load, warm-up and VRAM appear once.

**Re-run after the review fixes** (published binary from the final code, 14:57, load average 1.6, GPU 7 %, P1; Chromium,
Discord and the compositor open, using 660 MiB of GPU memory between them): `fmp bench vfg --size 1440p,2160p --mode
medium`, both formats, 2x and 4x. d p50 / p99: 1440p 1.18 to 1.20 / 1.21 to 1.22 ms, 2160p 1.36 to 1.38 / 1.38 to 1.61
ms, host p99 1.22 to 1.38 ms, VRAM 858 / 1864 MiB, every checked output genuine and closer than the blend; `gate PASS:
1440p Medium p99 d 1.22 ms at worst (RGBA8 4x)`, exit 0. Within 0.01 ms of the first matrix at p50.

With detection off the bench now synchronises often enough for the mode (review fix): every 16 runs for Low and
Medium, every 5 for High (≈ 900 queued operations at most, under the stream's ~1,024), where it used to be every 16
for all, so an unpaced High run could fill the queue and make the call itself wait. The paced detection-off cells
re-run at 15:00 are unchanged (1440p High d 5.69 / 5.75 ms, host p50 0.81 ms; Medium host 0.09 ms); unpaced, 1440p
High's host time is 0.37 / 0.41 ms (p50 / p99).

Reading it:

- **The host time equals d** (host p50 0.02 to 0.06 ms below d): with automatic shot-change detection on, the call
  returns when the generation is done (Answers, 1).
- **p99 sits 0.02 to 0.4 ms above p50 and the maximum within 0.85 ms.** The output checks make some slots late (a
  4K read-back of three frames takes longer than a slot, so the next runs start late and back to back: the "late"
  counts of 20 to 40 per 600 in the 2160p cells are these, not VFG); unpaced, where no slot can be late, the spread is
  the same (below).
- **Quality modes differ much more in cost than resolution does**: High is 4.5 to 5× Medium, Low 30 to 45 % of it.
  On the moving box High scores 1.8 to 5.5 dB over Medium and Low 6.7 to 9.7 dB under it (and Low loses another
  5 dB at t = 0.25/0.75, the only mode that does).
- VRAM is the context's free-memory drop over load and warm-up: per instance 140 / 206 / 382 MiB (Low), 502 / 858 /
  1864 MiB (Medium), 1192 / 1288 / 1542 MiB (High) at 1080p / 1440p / 2160p. Medium at 4K is the largest.

Other runs of the same binary (all exit 0, every checked output genuine):

| Run | What changes | Result (d p50 / p99, ms; host p50 where it differs) |
| --- | --- | --- |
| `--format 8 --multiplier 2 --unpaced` | back to back, the GPU never idles | 1080p Low/Medium/High 0.32/0.34, 1.11/1.13, 5.69/6.05; 1440p 0.44/0.46, **1.17/1.18**, 5.77/6.13; 2160p 0.62/0.63, 1.35/1.36, 6.11/6.34: within 0.03 ms of paced, so the GPU's clocks at 120 Hz pacing cost nothing measurable |
| `--size 1440p,2160p --format 8 --multiplier 2 --scd off` | automatic shot-change detection off | 1440p Low/Medium/High 0.42/0.44, **1.15/1.17**, 5.69/5.71 with host p50 **0.08, 0.09, 0.79 ms**; 2160p 0.60/0.62, 1.32/1.35, 6.01/6.08 with host 0.08, 0.08, 0.78 ms. d is 0.04 to 0.09 ms lower (no host round trip inside the run); the call only enqueues (High's ~170 operations take 0.8 ms to enqueue) |
| `--content pan --size 1440p,2160p --mode medium,high` | a textured pan, motion everywhere (24/32 px per pair) | 1440p Medium 1.19/1.22, High 5.83/6.17; 2160p Medium 1.36/1.41, High 6.14/6.22, both formats alike; VFG 50.6 to 55.4 dB against the truth vs the blend's 17.5 to 17.7 dB |
| `--video lavfi:mandelbrot=size=2560x1440:rate=60` | a zooming fractal decoded by the ffmpeg CLI | 1440p Low 0.47/0.50, Medium 1.19/1.22, High 5.78/5.81 |
| `--video lavfi:testsrc2=size=3840x2160:rate=60` | ffmpeg's moving test source | 2160p Medium 1.36/1.50 (RGBA8), 1.37/1.39 (RGB10A2), High 6.12/6.23, 6.12/6.20 |
| `--rate 240 --multiplier 4 --size 1440p,2160p --format 8` | 60 → 240: a generated frame every 4.17 ms | 1440p Low 0.46/0.48, Medium 1.18/1.19, High 5.78/6.12; 2160p 0.63/0.65, 1.36/1.39, 6.11/6.42. High at 6 ms cannot keep a 4.17 ms slot: every one of its 600 runs started late |

Content makes no difference to the cost: the pattern, a full-frame pan, a zooming fractal and ffmpeg's test source give
the same d to within 0.05 ms. Each run's conditions are in `nvidia-smi` and `uptime` snapshots taken just before it
(13:24 to 13:29, load average 0.3 to 0.6; the GPU utilisation readings of 35 to 87 % at those moments are our own
previous run finishing, the GPU was idle between runs). The "late" slots in the pan runs (120 to 480 per 600) are the
bench's own output checks: drawing the pan's true frame on the CPU at 4K takes longer than many slots; d is a GPU span
and is not affected.

## Against the design doc's estimates and latency model

| | Design doc estimate (Medium) | Measured Medium p50 / p99 | Measured High p50 / p99 | Measured Low p50 / p99 |
| --- | --- | --- | --- | --- |
| 1080p | 2.6 to 4.3 ms | 1.14 / 1.17 | 5.69 / 5.94 | 0.35 / 0.41 |
| 1440p | 4.6 to 7.6 ms | 1.19 / 1.21 | 5.78 / 6.12 | 0.47 / 0.50 |
| 2160p | 10.4 to 17.1 ms | 1.37 / 1.55 | 6.11 / 6.48 | 0.64 / 0.67 |

The estimate scaled NVIDIA's 720p figure linearly with pixels; the cost is nearly flat instead (2160p Medium is 1.2×
1080p for 4× the pixels), so the estimate is 2 to 4× too high at 1080p and 8 to 12× at 4K. The preliminary smoke-test numbers in
the plan (wall clock, synthetic frames) were close: Medium 1.17 / 1.89 at 1440p there against 1.19 / 1.21 here; their
p99 was inflated by host timing.

Per-frame budget T/M and the latency model ΔL = d + (M − 1)/M · T with the measured p99 of d (the scheduler's D is
tuned to p99 d plus a margin):

| Mode | T/M budget | Fits at Medium | Fits at High | ΔL at 1440p Medium | ΔL at 2160p Medium | Design doc said |
| --- | --- | --- | --- | --- | --- | --- |
| 60 → 120 (2x) | 8.33 ms | all sizes (≤ 1.6 ms) | 1080p, 1440p (≤ 6.2 ms); marginal at 4K (below) | 1.2 + 8.3 = 9.5 ms | 1.4 + 8.3 = 9.7 ms | 13–16 ms at 1440p, not 4K |
| 30 → 60 (2x) | 16.7 ms | all | all | 1.2 + 16.7 = 17.9 ms | 18.1 ms | 21–24 ms (1440p), 27–34 ms (4K) |
| 40 → 120 (3x) | 8.33 ms | all | 1080p, 1440p; marginal at 4K | 1.2 + 16.7 = 17.9 ms | 18.2 ms | 21–24 ms (1440p) |
| 30 → 120 (4x) | 8.33 ms | all | 1080p, 1440p; marginal at 4K (p99 6.5 ms idle; 6.7 to 7.9 ms, max 8.2 ms with a browser active) | 1.2 + 25.0 = 26.2 ms | 26.6 ms | 30–33 ms (1440p) |
| 60 → 240 (4x) | 4.17 ms | all | **none** | 1.2 + 12.5 = 13.7 ms | 14.1 ms | 15–17 ms at 1080p, borderline |

So **4K60 → 120 is feasible at Medium** (the design doc expected it not to fit and told Adam to run the PS5 at 1440p),
4x modes fit at every size at Medium, and High fits every 120 Hz mode at 1080p and 1440p but is **marginal at 4K**: on
the idle desktop its p99 was 6.5 ms against the 8.33 ms slot, but the reviewer, with a browser active, measured p99
6.7 to 7.9 ms and a maximum of 8.2 ms; 4K High at 120 Hz cannot be relied on. High never fits 60 → 240. Against the
design doc's table, the added latency **at 1440p** drops by 3 to 7 ms, all of it from d (2x rows use the 2x cells'
p99, 3x and 4x rows the 4x cells'); the doc's one 4K figure (30 → 60, 27–34 ms) drops by 9 to 16 ms, and its other
rows gave no 4K figure because it expected 4K not to fit. The "Run the PS5 at 1440p for frame generation" advice and the
"4K60 to 120 probably won't fit" summary line no longer hold.

## Answers (they decide how Phase 4 is built)

### 1. Is `NvVFX_Run` synchronous?

**It depends on automatic shot-change detection, and the `async` argument does nothing.**

- **Detection on (the default):** `NvVFX_Run` returns only when the work queued on VFG's stream before it *and* the
  generation have finished, give or take the last ~15 µs of GPU work. 1080p Medium, 100 runs each in the test host:
  host time of `Enqueue` p50 **1.100 ms**, GPU span p50 **1.115 ms**, thread CPU time p50 **1.098 ms** (it spins).
  Behind a GPU-side wait that holds VFG's stream for 300 ms, the call took **300.8 ms**. In the matrix above the
  host time tracks d in every cell (host p50 = d p50 − 0.02 to 0.06 ms; High at 4K: 6.05 ms in the call).
- **It is not a context-wide synchronise:** with 300 ms of unrelated work queued on another stream, a run took
  1.1 to 1.2 ms. It waits for its own stream only, the way a read-back of a flag from the device would.
- **Detection off:** the call only enqueues: host p50 **0.04 ms** (test host, 1080p Medium; GPU 1.084 ms) and 0.08 to
  0.09 ms (published binary, 1440p and 2160p Low and Medium, `--scd off` above), 0.78 ms for High (which enqueues about
  170 operations); behind the 300 ms gate it returned in 0.0 ms.
- `async = 1` and `async = 0` measured identically (C probe, 720p: host 1.073 ms both, GPU 1.10 ms both).
- With the primary context's scheduling set to `CU_CTX_SCHED_BLOCKING_SYNC` before the SDK first uses it, the same
  blocking call sleeps instead of spinning: thread CPU time 0.06 ms instead of 1.07 ms for about 40 µs more wall time
  (my C probe, 720p); the reviewer's independent probe found 0.11 ms instead of 1.16 ms of CPU for about 0.08 ms more
  wall time. `CudaContext.RetainPrimary(device, CudaSchedulingMode.BlockingSync)` already offers this.

**So `IFrameGenerator.Enqueue`'s "enqueue only, never waits" does not hold for VFG with detection on**: the call
blocks for d (0.35 to 6.1 ms, measured above) plus however long the GPU-side waits it enqueued first take, which
includes `output.Ready`, the presenter's release of the target ring slot. That is now part of the Core contract
(review fix): `FrameGenCapabilities.EnqueueMayBlock` says so (true for VFG with detection on, false with it off and for
the CUDA generators), and `IFrameGenerator.Enqueue` documents what a pipeline must then do.

**Why not simply turn detection off.** With detection off the call only enqueues (40 to 90 µs, 0.8 ms for High), but a
hard cut that nobody reports through `NotifyDiscontinuity` is then interpolated like motion and comes out as a morph of
the two shots, neither real frame: the pattern cut to an unrelated texture at t = 0.5 gave a frame 35 to 74 codes (mean
per channel) from the previous shot and 39 to 70 from the next in Low, Medium and High
(`CharacterizationTests.Without_detection_an_unreported_cut_is_morphed`), where detection on outputs a real frame
unchanged (5 below). The output is closer to the plain blend of the two shots (30 to 41 codes) than to either; the
reviewer found the same failure mode independently. Phase 4
is file playback and has no cut detector (the capture path's cadence kernel is Phase 5; a demuxer's keyframes are not
scene cuts), so detection off would put those morphs on screen at every cut.

**Decision for Phase 4 (the coordinator's, recorded here): VFG keeps automatic shot-change detection on, and the
pipeline runs it on a generator thread of its own**, because `Capabilities.EnqueueMayBlock` is set:

- The scheduler hands the generator thread (prev, curr, t, slot, done) through an SPSC ring and never calls VFG
  itself; the generator thread blocks for d plus any wait for its inputs or slot, so the work must be handed over at
  least d + margin before the slot's display time, which the scheduler's D (≥ p99 d + margin) already provides.
- The present and pacing threads never call a blocking generator and never wait on the generator thread; the
  generator thread never does anything whose effect would release the slot or write the inputs it waits for.
- Retain the primary context with `BlockingSync`, so the generator thread sleeps instead of burning a core while it
  is blocked (CPU per run from about 1.1 ms to about 0.1 ms, for 0.04 to 0.08 ms more latency per run).
- One consequence to plan for: with detection on, every run waits for everything queued before it on its stream, so
  VFG never runs more than one frame ahead; the ring's lead comes from the copies of real frames.

**Later option, not before the app has its own cut detector:** detection off with cuts reported through
`NotifyDiscontinuity` (the manual flag skips the model and costs 10 µs). The call then only enqueues, keeps the original
contract, could run on the pacing thread, and saves a core. It depends on a cut detector at least as good as VFG's
(the Phase 5 cadence kernel's per-frame difference metric is the candidate).

**`fmp pattern --vfg` is not a model for the pipeline.** The demo calls VFG on its present thread with detection on
(it prints so): at a lead of one write the slot's release was submitted long before, so it cannot deadlock, but the
present thread is held for d. Produce-and-present on the present thread: p99 1.5 to 1.8 ms at Medium and 6.2 ms with a
6.6 ms maximum at High in my runs ("VFG through the real ring"); the reviewer saw p99 1.7 ms at Medium and 6.7 ms with a
7.3 ms maximum at High, inside an 8.33 ms slot. The pipeline must not do this.

### 2. Does VFG do all its work on the stream it was given?

**Yes.** With detection off, the generator's stream held back 300 ms by a GPU-side wait, a consumer on another stream
that waits on `done` and copies the output reads the finished frame; the control, the same copy without the wait,
reads the marker the output held before (`GeneratorTests.Done_is_signalled_after_the_output_is_written`, both
outcomes asserted). A run enqueued behind the gate does not start until the gate opens (`Enqueue` returned in 0 ms,
the output appeared after 300 ms), and each of `prev.Ready`, `curr.Ready` and `output.Ready` is honoured with a
control that drops it and then sees the stale input or the overwritten slot (`The_ready_signals_of_inputs_and_output_are_waited_for`).
So ordering waits and signals on `generator.Stream` is valid and `done` after `Run` is after the write. With detection
on the same consumer reads the finished frame as well (the call itself returns only after the gate).

### 3. Memory, other threads and the stream queue

- **Runs neither allocate nor free once warmed up.** At 1080p Medium a load took 392 MiB and the **first four runs
  another 110 MiB** (lazy allocations: hence the warm-up at load); the next 200 runs, including shot changes, changed
  the free memory by 0.0 MiB and `nvidia-smi`'s figure for the process not at all (1028 → 1028 MiB, 12 runs of the
  test). Free memory is device-wide: in one of the review's whole-solution runs it fell by 2 MiB during those 200 runs,
  and in a focused run by 0.5 MiB with the process's own figure unchanged, which is the desktop's other GPU clients;
  the test now decides by the process's own figure (exact to the 2 MiB page) and falls back to the device-wide one only
  when `nvidia-smi` cannot tell. The leak test (3 cycles × 6 loads of three configurations) left the context's reference
  count, `nvidia-smi`'s figure for the process (502 → 502 MiB) and the free memory unchanged.
- **Runs do not block other threads.** Another thread recording an event and launching a kernel every millisecond
  saw a worst call of 0.01 ms while runs went on, with or without detection.
- **`NvVFX_Load` and `NvVFX_DestroyEffect` do**, like Phase 2's `cuMemFree`: with 300 ms of unrelated work queued in
  the context, the load took **296.4 ms** and the other thread's worst call was **279.8 ms**; destroy 282.5 ms and
  279.1 ms (`CharacterizationTests.Load_and_destroy_synchronise_the_context_and_runs_do_not`). So both belong to a
  drained point ("Reload and swap" below).
- **Stream operations per run**, from the ~1,024-operation queue limit (stream gated, detection off, runs enqueued until
  the call blocks; a single memset fills it after 1,021): **Low 28.4, Medium 32.9, High 170.2** operations per run,
  the same at 1080p and 2160p. A VFG write into the ring adds the three waits, the two span records and the signal:
  about 34 / 39 / 176 operations. The bridge's default `OperationsPerWrite` (16) is far too low for VFG: with High,
  `MaxLead` must be at most 5 (960 / 176). `fmp pattern --vfg` declares 192 operations per write and a lead of 4.
- **With detection on, a waiting run does not fill the queue**: the call does not return until the stream reaches
  it, so at most one run is ever queued. That is safe for the queue limit but means the producer blocks instead of
  being held back by `TryAcquireSlot`. With detection off a run behind a stalled wait does count against the limit:
  about 30 Medium or 5 High runs fill it, after which the call blocks.

### 4. First load, caching, VRAM, two instances

- **Load time** (create + `NvVFX_Load`, published binary, the first configuration of each kind in the process):
  with a warm CUDA JIT cache Low **7 to 10 ms**, Medium **16 to 22 ms**, High **41 to 50 ms**, independent of size and
  format; warm-up (8 runs) 3 to 45 ms more. **Cold** (an empty `$CUDA_CACHE_PATH`): the first load of each *mode* is
  slower, **114 ms (Low), 217 ms (Medium), 114 ms (High)** at 1080p, after which the other sizes and formats of that
  mode load warm (7 / 20 / 42 ms). With caching disabled (`CUDA_CACHE_DISABLE=1`) every first load per mode in a
  process pays it again (82 / 193 / 108 ms at 1440p). The first load in a process pays no further one-time cost: 8.2 ms
  warm, against 7 to 10 ms for later Low loads.
- **What is cached and where**: only the CUDA driver's JIT cache (the SDK ships PTX for some kernels that the driver
  compiles for this GPU): 4.2 MB in 51 files after all nine (size, mode) combinations, in `~/.nv/ComputeCache` or
  `$CUDA_CACHE_PATH`. No TensorRT engine or other file is written anywhere: the header documents a TRT-RTX cache under
  `ModelDir/cache`, but this feature needs no `ModelDir` (its model is inside its 216 MB library) and a search of
  `~/.cache`, `~/.nv` and the SDK directory after the runs found nothing new. On this machine `~/.nv/ComputeCache` already holds 232 MB; the driver evicts beyond
  `CUDA_CACHE_MAXSIZE`, so a launcher may want to raise that to keep VFG's kernels warm.
- **VRAM per instance** (load + warm-up, the context's free memory): Low 140 / 206 / 382 MiB, Medium 502 / 858 /
  1864 MiB, High 1192 / 1288 / 1542 MiB at 1080p / 1440p / 2160p, the same for RGBA8 and RGB10A2.
- **Two instances** (RGBA8 and RGB10A2 at 1080p Medium, as the design doc wants for SDR/HDR) load side by side in one
  context, on one stream, each 392 + 110 MiB (the process: 1,550 MiB in `nvidia-smi` including the test's buffers), and
  run alternately with correct results (`Steady_runs_do_not_allocate_and_two_instances_share_a_context`). Two loaded
  instances at 4K Medium would take 3.7 GB.

### 5. Shot changes

- **Manual flag** (`NotifyDiscontinuity`): the output is **the current frame, bit for bit**, and the model is skipped:
  GPU time **0.011 ms** against 1.115 ms for a normal run at 1080p. The generator clears the flag after each run (its
  status checked) and sets it again for every run of the same pair.
- **"The next pair", one rule for every generator (review fix).** The first version applied a notification to one
  call: 4x across a cut then gave curr, prev, curr with detection on (the picture stepped back and forth) and curr then
  two morphs with it off, and a t = 0 call used the notification up. Now Core's `DiscontinuityLatch` binds a
  notification to the pair of the next call, identified by its frames (each frame's pixel address and content time), and
  every call with that pair is discontinuous until a call with another pair: VFG runs it with the manual flag, the blend
  and repeat generators copy curr; t = 0 shows prev (it is that frame) and does not end it. So across a cut every
  generated frame is the new shot, and the picture switches once. `FrameScheduler` never interpolates across a
  discontinuity it is told about (it shows real frames and reports `SlotReason.Discontinuity`), so this is for a pair a
  pipeline enqueues across a cut regardless; it calls `NotifyDiscontinuity` right before that pair. Tests:
  `A_discontinuity_covers_every_frame_of_the_pair_across_the_cut` (Vfx, 2x and 4x, detection on and off),
  `A_discontinuity_covers_every_frame_of_the_next_pair` (Cuda, blend and repeat, 2x and 4x), `DiscontinuityLatchTests`
  (Core). With the latch mutated back to the old one-call flag, 11 of them fail.
- **Automatic detection on a real cut** (the test pattern followed by an unrelated texture): the output is **a real
  frame, unchanged: the previous one for t = 0.25 and 0.5, the current one for t = 0.75**, and the whole model runs
  first (1.131 ms). That is consistent with the blocking in 1: the SDK runs the model, reads its cut decision back to
  the host, and on a cut copies the nearer real frame over the output.
- No false cut was seen in any run of this phase: none of the 1,080 checked outputs of the matrix, nor the 2,000+
  generated frames scored by `fmp pattern --vfg`, was a copy.
- **Later (2026-11-20): fast, uneven motion is taken for a cut.** In a game, foliage rushing past and fast driving lost
  their generated frames (every one a copy of a real frame); measured from about 48 px a frame of unevenly moving detail at
  4K, while a uniform pan of 384 px a frame is still interpolated. `fmp capture` now runs with detection off by default
  (`--scd on|off` on both commands, `fmp play` keeps it on): [shot-change.md](shot-change.md).

### 6. t near 0 and 1; changing the mode

- **VFG refuses t = 0 and t = 1** (`NVCV_ERR_PARAMETER`, SDK log "NVVFXVIDEOFRAMEGENERATION_TIMESTEP must be in (0.0,
  1.0), got 0.000000"); `VfgGenerator` answers those with a copy of the real frame.
- **Close to the ends it is accurate on detailed content**: on a 40-px pan of a detailed texture (truth at every
  pixel of the motion), VFG scored 50.8 to 53.0 dB at t = 0.025, 0.05, 0.95 and 0.975, against 26.8 to 32.7 dB for the
  nearest real frame, and 53.7 to 58.4 dB in the middle (`Timestep_sweep_near_zero_and_one`, RGBA8 and RGB10A2; a 10-px
  pan: 64.5 / 65.3 dB at t = 0.1 / 0.2). There is no t at which snapping to the real frame would be better.
- **On large textureless motion it does not follow t near the ends**: the test pattern's flat box moving 40 px, whose
  centre should sit at 204.5, 209.5, … 234.5 px for t = 1/8 … 7/8, sat at 210.1, 210.7, 214.6, 219.8, 224.4, 229.3,
  231.8: within 0.3 px of the truth for t = 3/8 … 3/4, but at t = 1/8 where t = 0.27 belongs, and at t = 7/8 where
  t = 0.81 does (at t = 0.01 the box is ~10 px off with a faint ghost). It never moves backwards. So the scheduler's
  epsilon snap to real frames costs nothing on detailed content and helps a little on flat motion; it need not be large.
- **The quality mode cannot change without a reload**: setting `Mode` on a loaded effect is accepted, but every later
  run fails with `NVCV_ERR_INITIALIZATION` ("Feature not initialized") until `NvVFX_Load` runs again (42 ms at
  640x360). The same holds for `NVVFX_CUDA_STREAM`. Both are fixed per instance; `VfgGenerator` keys instances by
  (size, format, mode).

### Output rebinding (the design doc's open question)

**`NVVFX_OUTPUT_IMAGE` (and the inputs) can be pointed at different memory every run, and that is how the generator
works**: four outputs in turn, each receiving exactly its run's frame and no other buffer touched
(`Each_run_writes_into_the_output_it_is_given_and_nowhere_else`, outputs pre-filled with a marker); ring slots with an
odd width and a tight, only 4-byte aligned pitch (1277 px, 5108 B), and an output view 16 B into each row of a padded
allocation with guard rows, all give the same frame as an ordinary pitched buffer, and no guard byte changes
(`Pitches_and_offsets_of_ring_slots_are_honoured_and_nothing_outside_is_written`). One `NvVFX_SetImage` costs well
under a microsecond (C probe: 0.4 µs for the first call, 1,000 more averaged under 0.05 µs); the generator calls it only
when an address or pitch changed. No device-to-device copy fallback was needed. VFG has no state across runs either:
the same pair gives the same output whatever ran before (same test).

**The SDK keeps the descriptor's pointer, it does not copy it** (review finding; nvVideoEffects.h says `NvVFX_SetImage`
takes a shallow copy, and the first version's comment repeated that). Rewriting the output descriptor in place,
without calling `NvVFX_SetImage` again, moved the next run's output to the new buffer; the reviewer's C probe also saw a
descriptor on the stack make the next run fail. The generator was safe only because its three descriptors live in
native memory allocated with the effect and freed after `NvVFX_DestroyEffect`; that is now its stated invariant
(`VfgEffect`'s remarks: allocated with the effect, never moved, rewritten only for the next run after the previous
`NvVFX_Run` returned, which captures the addresses when it enqueues). `The_sdk_reads_the_descriptor_it_was_given_at_every_run`
pins the SDK's behaviour; with `Bind` mutated to an ephemeral stack descriptor (what the header's comment would allow),
54 of the 91 Vfx tests of the time fail, the load and warm-up included.

### Allocation on the per-frame path

`Enqueue` allocates nothing: `AllocationTests` measures three windows of 120 calls each (288 VFG runs with the output
rebound every call and the inputs swapped, 18 with the manual shot-change flag, 72 copies at t = 0 or 1, 3 waits and
a signal per call, every GPU span read back) at exactly **0 bytes**, with detection on and off, and asserts each of
those counts so it cannot pass by doing nothing. With a 24-byte allocation injected into the run path it reports
6,912 B (288 × 24) and fails, as do the `fmp pattern --vfg` tests (1,560 B). Selectors are encoded once at start-up.
Finding this took one detour worth recording: in the JIT test host the first version allocated 88 B once, about a
second into playback. The runtime **re-binds a `LibraryImport` P/Invoke when its stub is re-compiled at a higher tier,
and each binding hands the `DllImport` resolver a freshly allocated library name** (40 + 48 B for the two library
names, traced with a stack dump inside the resolver). The five per-frame calls therefore go through function
pointers taken once at load (`VfxFrameCalls`), which never involve the resolver; the `LibraryImport` declarations stay
the bindings for everything else. NativeAOT has no tiers, so a published binary is not exposed to this; it measured
0 B in every run above. The reviewer confirmed the mechanism: with the five calls put back on `LibraryImport`, 88 B
appeared in 6 of 9 runs of `AllocationTests`, in 0 of 6 with `DOTNET_TieredCompilation=0` and 0 of 4 with
`DOTNET_TieredPGO=0`. The CUDA, Vulkan and Platform hot paths use the same `LibraryImport` + resolver pattern (never seen
to fire: 0 failures in 5 whole-solution and 13 focused review runs), so every hardware test project now runs its host
with dynamic PGO off (`<TieredPGO>false</TieredPGO>`, Cuda, Vulkan, Gpu, Vfx and Platform tests, with a comment saying
why and a test that the switch reaches the host).

## VFG through the real ring (`fmp pattern --vfg`)

`fmp pattern --vfg` is the Phase 2 pipeline with VFG as the generator: source frame j is pattern frame 2j (so the
true middle of every pair, frame 2j + 1, is known), real frames are copied into the ring and VFG writes its frame at
t = 0.5 straight into the next slot, all on one stream (VFG runs on the ring stream, `VfgOptions.Stream`), so the
ring's *written* timeline is signalled in order. Slots come from `TryAcquireSlot`; the bridge is configured for VFG's
operation count (192 per write, lead 4). Offscreen, real frames are verified bit-exact (pattern and frame counter) and
each generated frame is scored over the moving box band against the true frame: it must be closer to it than the
blend of its neighbours, differ from both neighbours (not a copy), and leave the static bars intact (no sampled row
off by more than 0.4 % of full scale on average). Published binary, validation layer with synchronization checks:

| Run | Presented | Dropped / rejected | Generated frames genuine | Box band error, VFG / blend (codes) | d p50 / p99 | Allocated | Validation |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 2560x1440 RGBA8, 120 Hz, 1200 frames | 1200 | 0 / 0 | 595 of 595 (5 across the box's wrap not scored) | 0.046 / 1.617 | 1.18 / 1.20 ms | 0 B | 0 messages |
| 2560x1440 RGB10A2 | 1200 | 0 / 0 | 595 of 595 | 0.117 / 3.609 | 1.18 / 1.20 ms | 0 B | 0 |
| 3840x2160 RGB10A2, 600 frames (3 runs) | 600 | 0 / 0 | 298 of 298 | 0.118 / 3.850 | 1.37 / 1.39 ms | 0 B | 0 |
| 2560x1440 RGB10A2, High | 600 | 0 / 0 | 298 of 298 | 0.085 / 3.609 | 5.79 / 5.86 ms | 0 B | 0 |
| 2560x1440 RGBA8, unpaced, 2000 frames | 2000 (403/s) | 0 / 0 | 992 of 992 | 0.047 / 1.617 | 1.18 / 1.20 ms | 0 B | 0 |
| window 2560x1440 RGBA8 on the 240 Hz output, 3 s | 721 of 721 slots | 0 / 0 | (not read back) | | 1.18 / 1.20 ms | 0 B | 0 |
| window RGB10A2 (HDR10 swapchain), 3 s | 711 (10 slots skipped) | 0 / 0 | | | 1.19 / 1.20 ms | 0 B | 0 |
| window RGB10A2, no validation, 3 s | 717 (4 skipped) | 0 / 0 | | | 1.17 / 1.19 ms | 0 B | – |

Conditions: 13:30 to 13:31, load average 0.5 to 0.7, the GPU idle between runs. Re-run after the review fixes
(14:58, load average 0.6, Chromium and Discord open): 1440p RGBA8 and RGB10A2 1200 frames, 4K RGB10A2 600 frames,
both windows: the same results (595 of 595 and 298 of 298 genuine, 0 dropped, 0 rejected, 0 B, 0 validation messages;
2 and 5 slots skipped in the windows), the present thread's produce-and-present p99 1.57, 1.57, 1.73 ms offscreen and
1.56, 1.59 ms windowed. The run with the SDK directory only in a settings file (`--settings`, `$FMP_VFX_SDK` unset)
found and used it. Every run's bridge lead stayed at 1
and nothing was held back, the teardown drained and freed the import, and no write was signalled out of order.
Windowed, skipped slots (no free swapchain image within half a period) are the demo's clock-paced FIFO, as in Phase 2:
0 to 3 % there too. The present thread's CPU time per slot rises to p50 0.7 to 1.4 ms, p99 1.5 to 1.8 ms (6.2 ms with
High), because with detection on the generated slot's `Enqueue` blocks for d (Answers, 1); it fits a 240 Hz period
at Medium.

One check was tightened while measuring: the first 4K 10-bit run had one generated frame whose worst sampled bar row
was off by 1.07 codes on average, in 10-bit, against a threshold of 1 code that was meant for 8-bit (VFG changes static
content by about a code in 10-bit; the box band was fine: 0.108 against the blend's 3.85). The threshold is now 0.4 % of
full scale in both formats (1 code 8-bit, 4 codes 10-bit), and that run's failure message, which was formatted on the
present thread, allocated 800 B; failures are now recorded as values and described after the loop. Three further 4K
10-bit runs passed with 0 B.

## PQ and 10-bit (`fmp bench vfg-quality`)

`fmp bench vfg-quality` (published binary, 1920x1080, t = 0.5, each scene a pair with known motion and its exact true
middle frame; errors in code values against the truth, PSNR peak 255 or 1023, the right-edge columns where new content
enters excluded). Frame dumps (16-bit PNG; PQ images tagged BT.2020 + ST 2084 with a cICP chunk, so an HDR-aware viewer
on an HDR display shows them as HDR10) and an index of what each shows:
**`/mnt/disk1/dev/repos/frame-motion-plus/out/vfg-quality/index.md`** (82 MB in all; `out/` is git-ignored). Run at
13:31, load average 0.6, the GPU otherwise idle; re-run after the review fixes at 14:57 (load average 1.7, GPU 0 %
before the run, Chromium, Discord and the compositor open) with identical numbers in every cell, and the shadow columns
added to the report and to `index.md`. The photo is a Plasma system wallpaper
(`/usr/share/wallpapers/Next/contents/images/5120x2880.png`), decoded by ffmpeg to 16-bit RGB.

| Scene | Low VFG / blend dB | Medium VFG / blend dB | High VFG / blend dB | Notes |
| --- | --- | --- | --- | --- |
| sdr-pattern (box band) | 38.4 / 25.6 | 48.0 / 25.6 | 57.0 / 25.6 | |
| sdr-pan (24 px) | 50.4 / 17.5 | 58.5 / 17.5 | 53.2 / 17.5 | max error 14 codes |
| sdr-photo (a wallpaper, 24 px pan) | 52.8 / 30.7 | 56.3 / 30.7 | 62.5 / 30.7 | errors only at sharp edges |
| pq-photo (same, graded to ~4000-nit highlights) | 55.6 / 36.5 | 61.3 / 36.5 | 65.2 / 36.5 | highlight MAE 10.8 vs blend 268 codes |
| pq-pattern (1000-nit box over 10 nits) | 43.7 / 30.7 | 51.9 / 30.7 | 64.6 / 30.7 | highlight MAE 0.75 vs 30.8 |
| pq-ramp-pan (every PQ code; the 0–10000 nit wedge) | 58.1 / 36.8 | 62.8 / 36.8 | 64.1 / 36.8 | 758 of 758 codes in the ramp window kept |
| **pq-specular** (60 highlights 600–10000 nits, own motion, over 0.05–2 nits) | **32.2** / 29.6 | **38.4** / 29.6 | **37.4** / 29.6 | Medium: highlight MAE **82.7** vs 397 codes, max 867, p99.9 155. **Shadows** (truth ≤ 1 nit): mean error Low 2.16, Medium **1.17**, High **5.53**, blend 14.37 codes; pixels off by > 4 codes Low 8.1 %, Medium **4.4 %**, High **40.5 %**, blend 86.5 % |
| pq-dark-gradient (0.002–3 nits, a 20-nit disc moving) | 60.6 / 37.1 | 60.9 / 37.1 | 63.0 / 37.1 | shadow MAE 0.17 vs 0.66 codes; 102 of 101 codes; ×4 share 0.25 |

What it shows:

- **PQ interpolates as well as SDR**, and slightly better in PSNR terms (61.3 dB on the HDR-graded photo against 56.3
  on the same photo in SDR, Medium), since PQ codes are perceptually spaced and a 10-bit code is a quarter of an 8-bit
  one. RGB10A2 costs the same (matrix).
- **10-bit precision survives; no banding.** In the ramp window VFG kept all 758 distinct codes of the truth, in the
  dark gradient 102 of the truth's 101, and the share of codes that are multiples of 4 is 0.25 in both, as for
  genuine 10-bit data (an 8-bit internal path would give 1.0). The error maps of the gradient show only the moving
  disc's edge. Shadow errors are a fraction of a code.
- **Specular highlights are the weak spot.** Small bright highlights moving differently from the background are
  placed roughly right but smeared or doubled (one 3-px highlight came out as two stacked, dimmer blobs), lose peak
  brightness, and leave faint striped halos in the background around them (`pq-specular/f-error-*.png`). VFG still
  beats the blend by 3 to 9 dB. This is the kind of content (sparks, glints, small lights) where a PS5 game will show
  VFG's artefacts most; the PQ encoding is not the cause (the 1000-nit box and the HDR photo's highlights interpolate
  cleanly).
- **High is worse than Medium on that scene, and in the way that shows most** (review finding, reproduced from my own
  dumps): its highlights are a little closer (MAE 69 vs 83 codes) but it **corrupts the dark background widely**, where
  Medium disturbs it only around the highlights. Over the shadow pixels (truth ≤ 1 nit) High's mean error is 5.53
  codes against Medium's 1.17, and **40.5 % of them are off by more than 4 codes, against 4.4 % for Medium** (Low 8.1 %,
  the blend 86.5 %). On an HDR display that is a mottled, shifting near-black across the frame, not a halo; see
  `pq-specular/e-vfg-high.png` against `b-truth.png` and `e-vfg-medium.png`, and `f-error-high.png` against
  `f-error-medium.png` (the index's "What to look at first" points there). The other dark scenes do not show it
  (pq-dark-gradient: High 0.33, Medium 0.17 codes, 0.0 % over 4 codes), so it is tied to bright highlights over dark.
- **Low is clearly worst** (8 to 10 dB under Medium on the pattern and the pan, 3.5 to 6 dB on the photos). **High leads
  Medium** by 4 to 13 dB on the pattern and the photos but **trails it on the detailed pan** (53.2 vs 58.5 dB; max
  error 10 vs 14 codes, but more error overall) and on the specular scene's shadows. So the Medium default stands on
  quality as well as cost: High is a different trade-off, not an upgrade.
- Nothing else looked wrong in the images: errors concentrate on sharp edges (the photo's star, the wedge's steps,
  the box's edges) and are a few codes.

**Manual gate (outstanding): Adam to look at the dumps**, especially `pq-specular/e-vfg-high.png` (High's dark
background) and `e-vfg-medium.png` (highlight smear and halos), and `pq-dark-gradient/` and `pq-ramp-pan/` (banding),
on the HDR display.

## Reload and swap: what is implemented and what is deferred

The design doc wants a quality or resolution change built on a background thread and swapped in between frames.
Measured (Answers, 3): `NvVFX_Load` waits for all work queued in the CUDA context and blocks every other thread's
CUDA calls while it waits (296 ms behind 300 ms of unrelated queued work; another thread's event record and kernel
launch blocked 280 ms); `NvVFX_DestroyEffect` does the same (283 ms / 279 ms). A load on a background thread during
playback would therefore stall the pipeline's own enqueues for as long as the pipeline's queued work takes plus the
load (8 to 48 ms warm, up to 217 ms cold), which is a visible hitch, not a background build. Setting the mode or the
stream of a loaded instance is accepted but every later run fails (`NVCV_ERR_INITIALIZATION`) until it is loaded again.

Implemented now:

- **Instances keyed by (size, format, mode), kept loaded side by side** (`MaxLoadedInstances`, 2 by default for SDR and
  HDR), all on the generator's one stream, so `done` ordering never changes. `Configure` to a loaded instance is a
  pointer swap: no CUDA call, no allocation, safe between frames during playback (tested: three switches in under
  5 ms, the generated frames correct after them). This is the design doc's "keep both VFG instances loaded, swap on the
  first frame of the new format".
- **Loading is explicit and drained-point only**: `Configure` of an unloaded configuration and `Prepare` load and warm
  up synchronously; `ReleaseInactive` and `Dispose` destroy. Each is documented as a drained point (production
  stopped, ring drained), never on a pacing thread. The generator never frees on its own except when a load evicts the
  least recently used instance beyond the limit, which happens inside that (drained) load.

Deferred to the pipeline (Phase 4), and why: building a new instance *while playback continues* is not possible in the
shared context without a hitch (above). Phase 4 should load at start-up every instance the session can need (SDR and
HDR at the source size and the chosen mode, possibly the next mode down for the multiplier fallback: about 2.1 GB at
1440p for Medium and Low in both formats), and treat a size or mode change as a drained-point reconfigure (stop producing,
drain the ring, `Configure`, warm-up included, resume), exactly as the Phase 2 bridge does for a ring change. Whether
a second CUDA context could host background loads is unexplored: frames would then have to cross contexts, which the
pipeline avoids by design.

## Changes outside the new projects

All additive; the review-fix items are marked.

- **Core, review fix (items 1 and 2)** (`src/FrameMotionPlus.Core/Abstractions/`):
  - `FrameGenTypes.cs`: `FrameGenCapabilities.EnqueueMayBlock` (bool, false by default), true when `Enqueue` may
    block its caller until the frame is generated, including until the GPU waits it enqueued first are met.
  - `IFrameGenerator.cs`: `Enqueue` no longer promises "never waits"; it says what a pipeline must do for a blocking
    generator (a dedicated generator thread; never the present or pacing thread; never a thread whose later action
    writes the inputs or releases the output), and that a call that throws produced nothing and did not signal `done`.
    `NotifyDiscontinuity` defines "the next pair" (identified by its frames, every call with it, prev at t = 0 and curr
    for every t > 0, ended by a call with another pair).
  - `DiscontinuityLatch.cs` (new): that rule, lock-free and allocation-free, used by every generator.
  - Tests: `tests/FrameMotionPlus.Core.Tests/Abstractions/DiscontinuityLatchTests.cs` (new),
    `AllocationTests.Discontinuity_latch_allocates_nothing`.
- **Cuda, review fix (item 1)**: `src/FrameMotionPlus.Cuda/Generators/CudaFrameGenerator.cs`: the blend and repeat
  generators use `DiscontinuityLatch` instead of a one-call flag (a failed call no longer needs to restore it), and
  report `EnqueueMayBlock = false`. Tests in `tests/FrameMotionPlus.Cuda.Tests/GeneratorTests.cs`:
  `A_discontinuity_covers_every_frame_of_the_next_pair` (replaces `Discontinuity_outputs_the_current_frame_once`,
  which asserted the one-call behaviour) and `A_failed_enqueue_keeps_the_discontinuity_for_the_rest_of_the_pair`;
  `AllocationTests` alternates its pairs so the latch is exercised.
- **Test hosts, review fix (item 6)**: `<TieredPGO>false</TieredPGO>`, with a comment, in the Cuda, Vulkan, Gpu, Vfx and
  Platform test projects, and a test in each that the switch reaches the host (`Test_host_runs_without_dynamic_pgo`);
  the generated runtimeconfig of each has `"System.Runtime.TieredPGO": false`. Core.Tests calls no native code and
  keeps the default.
- `src/FrameMotionPlus.Cuda/FrameMotionPlus.Cuda.csproj:23-27`: `InternalsVisibleTo` for `FrameMotionPlus.Vfx` and
  `FrameMotionPlus.Vfx.Tests`. `VfgGenerator` validates frames and signals before enqueueing anything exactly as
  `CudaFrameGenerator` does (`FrameChecks`, `CudaStream.ValidateCudaSignal/WaitValidated/SignalValidated`, which are
  internal); its tests read the stream's path counters and the context's reference count.
- `src/FrameMotionPlus.Core/Settings/FmpSettings.cs:270-275` and `:278-282`: `GeneratorSettings.VfxSdkDirectory`
  (nullable string, null by default; an empty string is reported invalid). Test:
  `tests/FrameMotionPlus.Core.Tests/Settings/SettingsTests.cs:196-211`, `The_vfx_sdk_directory_defaults_to_null_and_round_trips`.
- `src/FrameMotionPlus.App/Cli/CommandDispatcher.cs`: `bench` gets the error writer (for the one-line exit-69
  message); help text for `bench vfg`, `bench vfg-quality`, `pattern --vfg` and `pattern --settings`.
- `src/FrameMotionPlus.App/Cli/BenchCommand.cs`: routes `vfg` and `vfg-quality`.
- `src/FrameMotionPlus.App/Pattern/*`, `Cli/PatternCommand.cs`: the `--vfg` mode (above). Review fixes: the demo
  consults `Capabilities.EnqueueMayBlock` instead of the generator's type and prints that it calls a blocking generator
  on its present thread, which a pipeline must not do; `--settings <file>` (with `--vfg`) applies the settings file's
  SDK directory in the usual lookup order.
- `FrameMotionPlus.slnx`, `.gitignore` (`/out/` for frame dumps), `README.md`.

## Recommendation for defaults

- **Quality: Medium.** It fits every mode the design doc lists at every size up to 4K with at least 6.8 ms to spare
  in 2x and 4x at 120 Hz, and 2.6 ms at 60 → 240; it beats Low by 7 to 10 dB on the moving box and the pan. High beats
  it by 2 to 5 dB on the box in the matrix (up to 13 dB on the quality scenes' pattern and 4 to 6 dB on the photos),
  but is worse on the detailed pan (53.2 vs 58.5 dB) and corrupts dark backgrounds around bright highlights (40 % of
  shadow pixels off by more than 4 codes against Medium's 4 %), which is exactly HDR game content.
- **Resolution: the source's own, up to 4K.** Since d barely grows with size, there is no reason to ask the PS5 for
  1440p for frame generation's sake: 4K60 → 120 at Medium adds 1.4 ms p50 / 1.6 ms p99 of d.
- **High** only as an explicit choice, not as "better quality": it fits 30 → 60 at any size and 30 → 120 / 60 → 120
  at 1080p and 1440p, is marginal at 4K 120 Hz (p99 6.5 ms idle, 6.7 to 7.9 ms with a browser active, max 8.2 ms,
  against 8.33), and never fits 60 → 240. It costs 4.6 ms more latency than Medium on every real frame, and its dark
  background errors around highlights (above) argue against it for HDR content until Adam has looked at the dumps.
- **Low** only as the multiplier fallback's last step or for > 240 Hz output; it is visibly softer.
- **Load both SDR and HDR instances at start-up** at the session's size and mode (≈ 1.7 GB at 1440p Medium, 3.7 GB at
  4K Medium), plus Low if the fallback is wanted; all three modes at 4K would take 3.7 GB per format.
- **Shot-change detection: on** (the design doc's choice), with VFG on a generator thread of its own because
  `Capabilities.EnqueueMayBlock` is true. Detection off only once the app has its own cut detector (Answers, 1).
- **Pipeline changes this phase found** (for Phase 4): the generator thread (Answers, 1); the bridge's
  `OperationsPerWrite` at least 176 for VFG High (Answers, 3); loads and destroys only at drained points, switches
  during playback with `TryConfigurePrepared` ("Reload and swap"); `BlockingSync` context scheduling for the generator
  thread; `NotifyDiscontinuity` right before the pair across a cut.

## What the plan and the design doc should say now (not edited here)

- Design doc summary and "Latency model": "4K60 to 120 probably won't fit" and "Run the PS5 at 1440p for frame
  generation" are contradicted: 4K Medium is 1.37 ms p50. The estimate table (2.6–4.3 / 4.6–7.6 / 10.4–17.1 ms) and
  the added-latency table should take the measured d (about 1.1 to 1.4 ms Medium, 5.7 to 6.1 ms High).
- Design doc, "VFG backend details": "leave automatic shot-change detection on" has a cost the doc does not mention
  (NvVFX_Run blocks the caller for the queued work and d); "build the new instance on a background thread and swap it
  in" is not possible without a hitch because `NvVFX_Load` synchronises the context; the stream and mode are fixed at
  load. The open questions on rebinding and engine caching are answered (rebinding works; only the CUDA JIT cache).
- Plan, Phase 4: the bridge's `OperationsPerWrite` for a VFG producer; VFG on a generator thread of its own
  (`EnqueueMayBlock`), detection on until the cadence kernel can serve as a cut detector; load all needed instances at
  start-up and switch with `TryConfigurePrepared`; `BlockingSync`.
- Plan, "VFX SDK layout": the loading order above replaces "with LD_LIBRARY_PATH set to …".

## Not done, uncertain or worth knowing

- **Background reload during playback is not implemented**, by design (above): it cannot be done without a hitch in
  the shared context on this SDK and driver. Prepared-instance switching is implemented and tested.
- **`Enqueue` blocks with detection on.** Now part of the Core contract (`FrameGenCapabilities.EnqueueMayBlock`); the
  decision for Phase 4 is a generator thread of its own with detection on (Answers, 1). Not worked around inside the
  generator.
- **A notification binds to the next pair enqueued**, whatever it is: if a pipeline calls `NotifyDiscontinuity` at a
  cut and then never enqueues the pair across it (the scheduler shows real frames across a cut it knows of), the next,
  continuous pair is treated as the cut and shows curr for its generated frames. Core's documentation says to call it
  right before enqueueing the pair across the cut; Phase 4 follows that, and a generator cannot tell the two cases
  apart without knowing the source timeline.
- **The cut decision of VFG's automatic detection** was seen on one synthetic cut (pattern → texture) only. How often it
  fires on real game content (flashes, fast pans, UI transitions) is unmeasured; none of this phase's synthetic motion
  triggered it.
- **Quality numbers are from synthetic and wallpaper content**, not game footage or real 120 fps video (the design
  doc's harness of dropping every other frame of real footage is Phase 4's). The PQ "real footage" is an SDR wallpaper
  graded to HDR on the CPU (BT.709 primaries carried in the BT.2020 container, no gamut mapping), not native HDR video;
  none was available on this machine.
- **VRAM figures are the context's free-memory drop**, so another process allocating at the same moment could shift
  one; the leak test cross-checks with `nvidia-smi`'s per-process figure (unchanged over 18 loads).
- **Windows**: the Vfx project and the App compile for `win-x64`; the Windows loader path (`NVCVImage.dll`,
  `NVVideoEffects.dll`) is written but has never run.
- **The JIT-host re-binding allocation** (above): fixed for the VFG calls by function pointers, and for every hardware
  test host by turning dynamic PGO off (`<TieredPGO>false</TieredPGO>` in the Cuda, Vulkan, Gpu, Vfx and Platform test
  projects, each with a test that the switch reaches its host). The CUDA, Vulkan and Platform bindings keep the
  `LibraryImport` + resolver pattern; the published NativeAOT binary binds once and is unaffected. Core.Tests calls no
  native code and is unchanged.
- The bench's own output checks make some paced slots late at 4K (a 4K read-back of three frames takes longer than a
  slot). d is a GPU span and is not affected, but the runs right after a check are issued back to back, not paced.
  `--verify-every 0` turns the checks off.
- All published-binary numbers were measured with the binary built from the final code except the matrix and the
  follow-up runs, which ran on the build from 30 minutes earlier: the code paths they measure did not change after it
  (the later changes were to `VfgEffect.Dispose`'s context handling and to `fmp pattern --vfg`'s 10-bit threshold and
  failure message).

## Reproducing

```sh
fmp bench vfg --csv vfg.csv                                   # the full matrix (about 6 minutes)
CUDA_CACHE_PATH=$(mktemp -d) fmp bench vfg --size 1440p --frames 20   # cold first loads
fmp bench vfg --unpaced --format 8 --multiplier 2              # back to back
fmp bench vfg --scd off --size 1440p,2160p --format 8 --multiplier 2  # detection off: the call only enqueues
fmp bench vfg --content pan --size 1440p,2160p --mode medium,high --multiplier 2
fmp bench vfg --video lavfi:mandelbrot=size=2560x1440:rate=60 --size 1440p --format 8 --multiplier 2
fmp pattern --vfg --offscreen --validate --format 10 --frames 1200  # VFG into the ring, every frame checked
fmp pattern --vfg --validate --format 10 --duration-s 3            # windowed
fmp bench vfg-quality                                          # PQ/SDR scores and frame dumps in out/vfg-quality
dotnet test tests/FrameMotionPlus.Vfx.Tests -c Release         # the measurements in "Answers" (category Vfx)
```
