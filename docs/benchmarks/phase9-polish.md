# Phase 9a and 9c: saved settings, live quality switching (and what was left for later)

Scope as set on 2026-11-18 (capture card first, forward progress over perfection). The session was cut short by the
owner at 22:37 ("I want to start using this at 12am, so no 2 hour test please"): the card, the GPU and the screen had to be
free from 23:30. So this phase delivers **saved settings and a bare `fmp capture`**, fully tested, plus two small
leftovers; the live quality switch and the soak instrumentation are **not built** and are listed under "Known issues /
not done" with what was found and the exact commands. The two-hour soak was the owner's own session on 2026-11-19 (section
1c).

Machine: RTX 5090, driver 620.70.06, Plasma 6.8.2 Wayland, DP-3 4K 240 Hz and HDMI-A-1 4K 119.88 Hz, Elgato 4K X on
`/dev/video0` with a PS5. Every run below used a scratch config directory (`XDG_CONFIG_HOME=<scratch>`), never
`~/.config/fmp`, and `--audio off` or a null sink created for the run and removed after it.

## 1. Saved settings

### What the owner can do

```sh
fmp settings set capture.mode 4k30-hdr     # checked exactly as --mode is; creates ~/.config/fmp/settings.json
fmp settings set audio.sink inzone         # --audio-sink
fmp settings set hdr.peakNits 1000         # --hdr-peak-nits
fmp capture                                # = fmp capture --mode 4k30-hdr --audio-sink inzone --hdr-peak-nits 1000
fmp settings show                          # every key, its value, and "from the file" or "default"
fmp settings unset audio.sink              # the default (your selected output) again
fmp settings path
fmp capture --mode 1440p60-hdr             # a typed option wins for this run; the saved mode is "not used"
fmp capture --no-settings                  # exactly as before this phase
```

### The model

- **Where:** `$XDG_CONFIG_HOME/fmp/settings.json`, else `~/.config/fmp/settings.json`; `%APPDATA%\fmp\settings.json` on
  Windows. Read by `fmp capture` and `fmp play` only, and only when it exists. `--settings <file>` reads another file,
  `--no-settings` none (both together is a usage error).
- **Precedence:** built-in defaults < the file < the command line. A saved value is turned into the option it stands for
  and handed to the command's own parser, so it is validated exactly as a typed option (same ranges, same messages). It is
  not used when the command line gives that option or one that conflicts with it: `--mode/--size/--fps/--format` against
  `capture.mode`; a device argument against `capture.device`; `--multiplier/--output-hz` against `generator.multiplier`;
  `--fullscreen/--windowed/--window/--offscreen` against `display.fullscreen`; `--audio off` (typed or saved) against
  `audio.sink/source/offsetMs`; a typed `--audio-sink/--audio-source/--audio-offset-ms` against a saved `audio.mode off`
  (they turn the sound on, as on the command line); `--gate` against the `generator.*` keys; `--list` against all.
- **Keys** (named after the options): `capture.device`, `capture.mode`, `generator.backend`, `generator.quality`,
  `generator.multiplier`, `display.clock`, `display.fullscreen` (true/false), `display.window` (WxH: the window's size when
  windowed; unlike `--window` it does not by itself choose a window, so a saved size never turns fullscreen capture into a
  window), `audio.mode`, `audio.sink`, `audio.source`, `audio.offsetMs`, `hdr.mode`, `hdr.peakNits`, `hdr.maxCll`,
  `hdr.maxFall`, `generator.vfxSdkDirectory` (as before: after `--vfx-sdk` and `$FMP_VFX_SDK`). `capture.*`, `audio.*`,
  `hdr.*` apply to `fmp capture` only.
- **Nothing applied unseen:** both commands print one start-up line, e.g.
  `settings    /home/…/fmp/settings.json: capture.mode=4k30-hdr, audio.sink=fmp-null-p9, hdr.peakNits=2000`, or with
  typed options `…: no value used from it; not used: capture.mode (--mode/--size/--fps/--format given), audio.sink (audio
  is off), hdr.peakNits (--hdr-peak-nits given)`, or `settings    none (no file at …)`.
- **The file:** `FmpSettings` extended additively (source-generated JSON, no reflection): a `version` (1; files without
  one read as 1, a newer version is refused with a clear message), and the sections `capture`, `audio`, `hdr` plus new
  nullable fields in `display` and `generator`; null means "not set". `generator.backend` now also accepts `auto` and
  `none`. The old scheduler/display/cadence/telemetry sections are unchanged and still apply through `--settings`.
- **Errors:** a malformed file is one usage error naming the file and the position
  (`Invalid settings '…': … LineNumber: 4 | BytePositionInLine: 0.`); a bad value names the key and the option's own
  message (`capture.mode: Option '--mode' expects one of 4k30-hdr, …, got '8k240'.`); a key this build does not know is
  reported on the start-up line and by `show` and skipped (a second, lenient source-generated context reads the file;
  the strict reader used by `fmp settings --validate` and `fmp simulate --settings` is unchanged).
- **`set`/`unset`:** validate first, keep every other key, create the directory, write a temporary file, check that it
  loads, rename it over the old one. A malformed existing file is reported and never overwritten. Comments in a
  hand-edited file are not kept by `set`/`unset`; since the hardening pass they say so (`note: the file had comments; …`).
- **Tests never touch the owner's file:** every test process sets `XDG_CONFIG_HOME` and `APPDATA` to a temporary
  directory of its own before any test runs (`tests/Shared/IsolatedSettings.cs`, a module initializer compiled into every
  test project by `Directory.Build.props`); child processes inherit it. `Tests_never_see_the_owners_config_directory`
  checks it.

### Verified on the published binary (scratch config directory)

| Run | Result |
| --- | --- |
| `fmp settings set capture.mode 4K30-HDR`, `audio.sink fmp-null-p9`, `hdr.peakNits 2000`, `generator.quality HIGH`, `audio.offsetMs 12.5`; `show`; `unset` ×2 | saved normalised (`4k30-hdr`, `High`); `show` lists each "from the file"; `unset` removed only those keys; `set hdr.peakNits 50` refused, exit 2, file unchanged |
| bare `fmp capture --windowed --duration-s 20` with that file and a null sink | start-up line `capture.mode=4k30-hdr, audio.sink=fmp-null-p9, hdr.peakNits=2000`; 3840x2160 P010 at 30; sound to the null sink, A/V offset +0.06 ms; 0 missed, GC 0/0/0, **PASS**, latency 58.2 ms. (A first attempt ended FAIL: the window changed output mid-run, "NewOutput", 4 misses during a refresh-rate change and verifier disagreements: the known window-moving issue, Phase 4b backlog item 8, not the settings) |
| `fmp capture --mode 1440p60-hdr --hdr-peak-nits 1000 --audio off --windowed` with the same file | the command line won: 2560x1440, metadata 1,000 nits, audio off; the line lists the three saved values as not used and why; PASS |
| malformed / bad value / unknown keys / `--settings` with `--no-settings` | one error with file and line; `capture.mode: Option '--mode' expects …`; unknown keys listed by `show`; usage error |
| **no settings file:** `fmp capture --mode 4k30-hdr --audio off --windowed --duration-s 30`, before (HEAD 9020bc1) and after | before: latency 56.4 ms (p99 57.2), 0 missed, GC 0, PASS; after: 55.4 ms (p99 58.4), 0 missed, GC 0, PASS; the only new line in the output is `settings    none (…)` |

### Tests

`dotnet test FrameMotionPlus.slnx -c Release`, once, whole solution, 22:46 to 22:56: all green. Core 705 (681 + 24 new:
`UserSettingsTests`, `LenientSettingsTests`), Platform 100 + 1 skip, Audio 71, Vulkan 108, Gpu 129, Sources.V4l2 121,
Pipeline 142, Vfx 92 + 1 skip, Cuda 144, Sources.File 154. The new tests also passed twice on their own. Zero warnings;
NativeAOT publish clean (no IL warnings); the App builds for `-r win-x64`.

## 1b. A 10-minute soak on the card (not the two-hour one)

`fmp capture --mode 4k30-hdr --window 960x540 --duration-s 600 --audio-sink fmp-soak-p9` (a null sink created for it and
removed after), final binary, no settings file, **the card held 22:56:26 to 23:06:29**; resources sampled from outside
every 30 s (`/proc/<pid>/status`, `/proc/<pid>/fd`, `nvidia-smi`):

| | Result |
| --- | --- |
| Verdict | PASS; 600 s played, 143,532 slots measured on the 240 Hz output, 0 missed (the verifier agrees); teardown clean |
| GC | measured window 0/0/0 (whole run 1/1/1, all before the measured window); every thread 0 B allocated |
| Latency / drift | real frame shown 56.0 ms after capture (p99 58.2); D 48.1 ms at the end, window min 47.2, p50 49.3, max 51.7 |
| Audio | 600 s played; A/V offset 0.00 ms (min −0.52, max +0.23); drift correction −25.9 ppm against a measured clock difference of −28.2 ppm; underruns 0, overruns 0, resyncs 0; callbacks 0 B |
| Descriptors, threads | 120 and 21 from the first sample to the last |
| GPU memory (this process) | 2,991 MiB from the first sample to the last |
| RSS | 476.9 MB → 488.1 MB in 571 s, in steps of about 2 MB roughly every 200 s after the first minute (about +35 MB/h if it kept on; managed heap not growing: GC 0). **Open:** whether it levels off needs the two-hour run |

## 1c. Two-hour soak (the owner's session, 2026-11-19)

The owner played for 2 h 16 min through the app (`fmp capture`, 4K HDR 30 → 60, VFG, fullscreen, **variable refresh on
the 119.88 Hz output HDMI-A-1**, sound to the headset) while streaming it on Discord, with the Discord patch of
`PipeWireAudioDelay` (phase8-audio.md, "Screen shares and recorders") in the binary. Resources sampled from outside.

| | Result |
| --- | --- |
| Duration | 8,148.6 s played, 8,146.5 s measured after 2 s of warm-up; stopped by the user; teardown clean |
| RSS | 481–493 MB at 118 minutes (476.9 → 488.1 MB in the 10-minute run of 1b): the creep levelled off, it was warm-up |
| Threads, descriptors | 21 and 120, unchanged |
| GPU memory (this process) | 3,069 MiB |
| GC | measured window 0/0/0 (one collection in the whole run, after the stop); 0 B allocated on the pacing, generator, present, control and capture threads |
| Capture | 244,488 frames, 0 dropped by the driver |
| HDR | followed: 244,258 frames read 10-bit, 0 read 8-bit, 0 switches |
| Audio | 8,148.6 s to the headset with a Discord recorder attached: error mean 0.01 ms, rms 0.27 ms; underruns 0, overruns 0, resyncs 0; drift correction −22.0 ppm against a measured clock difference of −25.2 ppm; re-locks 2 (path changes 1); no "linked to another node" line |
| Latency | a real frame shown 48.7 ms after its capture timestamp (p99 57.2) |
| Slots | 487,483 measured (243,826 real, 243,657 generated, 0 repeated); 59.8 presents/s; intervals mean 16.711 ms, p99 17.140 ms |
| Missed slots | 37,201 by the definition (first pixel more than half the shortest refresh period, 4.17 ms, after the slot's time): **the live counters and the verifier agreed on every one of the 277,908 slots the verifier held** (its buffer: the first 77 minutes; 3,929 missed there); the 209,575 after it were the live counters' alone. Of the 37,201 nearly all were labelled the pipeline's "late present" because hand-overs ran 0.43–0.56 ms before their time against an expected display delay of 0.85 ms; they were handed over in time, the display was late (fixed since: phase6-pacing.md, Known issues). With a p99 interval of 17.14 ms, at most about 1% of the intervals can be uneven, so almost all of these late slots came in runs of consecutive late slots shown at an even cadence: a steady extra latency of 4–8 ms while it lasted, not a judder (inferred from the summary; no CSV of the run). The TV itself, measured afterwards with a steady 29.97 fps source under variable refresh, made none: 0 late in 23,014 slots, first pixel at most 3 ms after the hand-over; the owner's session had a Discord screen share running, the leading suspect (phase6-pacing.md, Known issues 3b) |
| Generated slots skipped | 767 in 8,146 s (one every 10.6 s; variable refresh, the next real frame came too late for them: known) |
| Verdict as printed | FAIL, for bookkeeping only: the 209,575 slots beyond the verifier's buffer were counted as disagreements. Fixed: they are now reported as the live counters' alone, and a run without a duration reserves 4 hours of rows |

## 2. Small leftovers

- **H ignored on 8-bit captures:** H now acts only on a P010 capture (`PlaybackSession.cs`, `CaptureIsTenBit`); it used to
  turn an NV12 capture's SDR codes into PQ. The `FMP_CAPTURE_SWITCH_EVERY_S` diagnostic is unchanged.
- **Wording:** that a PS5's SDR arrives as 8-bit samples is now stated as assumed and not yet seen
  (`SampleDepthProbe.cs`, `CaptureColor.cs`, the setup line, `--help`, README).
- **Keys:** in-window keys already acted only on key-down without repeat, with or without modifiers (`SdlWindow.cs`);
  `--help` and the README now say so and list them (Esc, H, A). They are not yet printed once at start (no Q key either).

## 3. Direct display (read-only probe, nothing acquired)

`vulkaninfo` in this session: `VK_KHR_display` enumerates both outputs (`XG32UCWMG (DP-4)` and `LG TV SSCR2 (HDMI-0)`, the
driver's own connector names), and `VK_EXT_acquire_drm_display` and `VK_EXT_direct_mode_display` are present. KWin
advertises `wp_drm_lease_device_v1` (`/dev/dri/card1`) but offers **no connectors** for lease (it lists only non-desktop
outputs). So a direct-display mode is not reachable from inside the desktop session; to try one the owner would switch to a
text console (Ctrl+Alt+F3, log in; KWin gives up DRM master while its VT is in the background), run the app there on the
HDMI-A-1 TV (VRR, HDR) or DP-3, and switch back with Ctrl+Alt+F1/F2. No mode is built for it yet.

## 4. Live quality switching (Phase 9c, 2026-11-19)

Built at night with the PS5 off and the owner asleep: every run below is `fmp play` of a looping 4K HDR10 file (the dim
29.97 fps clip `c2160p2997-dim.mp4`, PQ/BT.2020, ordinary video) or a 1440p version of it, published NativeAOT binary,
scratch `XDG_CONFIG_HOME`, `--no-settings`, no sound. Switches were asked for by the new diagnostic
`FMP_QUALITY_SWITCH_EVERY_S=<s>` (what Q does; nobody at the keyboard). `fmp capture` uses the same session code but was
not run with Q on the card.

### What the owner can do

- **Q in the window** (`fmp play` and `fmp capture`; key-down, no repeat) switches VFG between Medium and High (between
  the start quality and High, or Medium when started with `--quality high`). One line per switch:
  `quality     >>> NOW HIGH (Q pressed) <<<  the generator switched between two frames, no load; the picture now runs
  about 5 ms later at 4K (High's longer generation time); Q switches back` (capitals since the hardening pass, so a stray
  Q in fullscreen is seen in the terminal; preformatted: the control thread allocates nothing). A start-up `keys` line lists the keys (capture: Esc, H, A, Q; play: Esc, Q), and a `quality` line
  says what the second instance cost or why Q does nothing. `--generator blend|repeat|none`, `--no-quality-switch` and a
  failed second load answer Q with one line (`quality     Q ignored: the blend generator has one quality`).
- **`--quality-switch` / `--no-quality-switch`** (both commands; both together is a usage error) and the saved setting
  **`generator.qualitySwitch`** (`fmp settings set generator.qualitySwitch false`). **Default: on.**
- The summary lists every switch: when, from → to, the generator thread's switch call, how long it was prepared and what D
  did meanwhile, the estimate it restarted from, and the slots around it (from the first slot decided while it was
  prepared to 2 s after its first slot at the new quality: slots, missed, late, generated slots skipped, fallbacks); then
  each quality's real-frame latency, d and D outside the switches; the quality at the end.

### How it works

- **Both instances loaded at start** (`PlaybackSession.Open`, a drained point): `VfgGenerator.Prepare` of the other
  quality after the first `Configure`. During playback the switch is `VfgGenerator.TryConfigurePrepared`: no load, no CUDA
  call, the active instance and its configuration published as one object (unchanged since Phase 3).
- **Where the switch lands:** the pacing thread owns it (`QualitySwitchControl`). A request (Q, any thread) is first
  *prepared* on the scheduler (`FrameScheduler.PrepareGenerationTimeChange`): for a dearer quality D is budgeted for the
  new cost at once and rises at its normal slew (50 ms/s, about 100 ms for High's +5 ms); once D is there the decision
  lead is raised in steps of a quarter refresh per decision, so no decision finds its new time already past. Then it is
  *committed*: the scheduler's generation-time estimate starts again from a figure for the new quality (what that quality
  measured the last time it ran, else the start-up table: Medium 1.6 ms, High 6.5 ms), and a `WriteKind.Quality` job is
  queued behind every write already handed out. The generator thread switches when it reaches that job, between two
  writes: slots decided before the commit are generated at the old quality with the old lead, every later slot at the new
  quality with the new lead. A cheaper quality is ready at once; D then falls at its slow slew (5 ms/s). A second press
  before an up-switch is committed calls it off (counted); the latest wish wins. If no commit is possible within 1 s it
  is made anyway (counted as "made before D was ready"; never seen).
- **The first pair after a switch is generated normally** by the new instance: VFG keeps no state across runs (the same
  pair gives the same output whatever ran before, Phase 3 "Output rebinding"), so no real frame is substituted and nothing
  is counted for it. Generation times still in flight from the old quality are tagged with the switch epoch and dropped
  before they reach the new estimate (2 to 5 per run of 8 switches).
- **Budget:** with Q on the bridge is budgeted for High (192 CUDA operations per write; lead limit still 5).
- **A failed second load** (video memory short, or any exception; injected in a test) keeps one instance: the start-up line
  says `Q does nothing: the High instance could not be loaded at start (…); one instance kept`, Q answers with one line.
- Q during the pre-roll or the variable-refresh probe is applied at the first pacing turn (playback starts at the other
  quality); during an H colour switch nothing interacts (the generator's format does not change with the colour); on a
  hold or still picture the switch is committed (no generation runs to hurt); at teardown a queued switch is executed or
  dropped harmlessly. Both instances are destroyed by `VfgGenerator.Dispose` as before.

### Cost, measured

**Start-up and GPU memory** (warm CUDA JIT cache; the context's free memory around each load, and `nvidia-smi`):

| Size (RGB10A2) | First instance (Medium) | Second instance (High) | Both |
| --- | --- | --- | --- |
| 3840x2160 | loaded 23.1 ms, warmed up 11.3 ms, 1,864 MiB | loaded 43.8–47.8 ms, warmed up 45.0–46.8 ms (**+89 to 94 ms**), **+1,540 MiB** (once 1,604) | 3,404 MiB |
| 2560x1440 | loaded 22.6 ms, warmed up 9.9 ms, 858 MiB | loaded 47.0 ms, warmed up 43.1 ms (**+90 ms**), **+1,286 MiB** | 2,144 MiB |

With an empty JIT cache (a new driver) High's first load is slower (114 ms at 1080p in Phase 3), once.

**An idle second instance does not change Medium** (fullscreen on DP-3, variable refresh, 30 s, same file):

| | d p50 / p99 | Real frame shown after content time (p99) | D p50 | Missed | GC | Verdict |
| --- | --- | --- | --- | --- | --- | --- |
| one instance (`--no-quality-switch`) | 1.422 / 1.496 ms | 19.564 ms (19.950) | 18.745 ms | 0 | 0/0/0 | PASS |
| two instances, High unused (default) | 1.432 / 1.497 ms | 19.585 ms (19.690) | 18.751 ms | 0 | 0/0/0 | PASS |

**Switching every 5 s, 42 s** (8 switches each; final build):

| | Medium: latency (p99), d p50/p99, D | High: latency (p99), d p50/p99, D | Around the 8 switches | Run |
| --- | --- | --- | --- | --- |
| fullscreen, variable refresh (DP-3) | 19.496 ms (19.630), 1.417/1.486 ms, 18.690 ms | 24.284 ms (24.630), 6.153/6.394 ms, 23.440 ms | **0 missed, 0 late, 0 generated slots skipped, 0 fallbacks** | 2,399 slots, 0 missed, GC 0, PASS |
| window 1600x900, fixed 240 Hz (stride 4) | 27.019 ms (27.340), 1.622/1.639 ms, 20.480 ms | 31.851 ms (35.100), 6.823/6.996 ms, 24.740 ms | **0 missed, 0 late** | 9,602 slots, 0 missed, GC 0, PASS |

So High costs about **+4.8 ms of latency** (its +4.7 ms of d, through the lead and D). Per switch: the generator
thread's call **1.9 to 3.2 µs**, no load, no CUDA call, no GPU memory; an up-switch is prepared for 73 to 95 ms (fixed) or
174 to 188 ms (variable refresh: D's slew, then the lead's steps) while D rises from 18.7 to 23.4–23.7 ms, invisible but
for the latency rising; a down-switch is committed within 0.2 to 15 ms and D falls back over about a second.

The first version committed as soon as D was ready, with the lead raised in one step: in 7 up-switches it skipped 1
generated slot (variable refresh) and made 1 late slot plus 1 miss (fixed 240 Hz), the slots whose decision time the
raised lead put in the past. The lead now rises in steps before the commit (above); the unit test
`Committing_at_once_without_raising_D_first_would_skip_slots` keeps the difference visible.

**50+ switches, memory and allocation:** 1440p offscreen on the real clock at 120 Hz, a switch every 0.2 s for 16 s:
**80 switches**; this process's GPU memory 3,208 MiB at every sample from the first second to the last, the machine back
to its 1,609 MiB after exit; 0 B allocated on every pipeline thread, GC 0/0/0, 0 missed, teardown clean, PASS. (A JIT
build's first switch allocated 424 B on the generator thread, first-use work; the generator thread's pre-roll now calls
the switch once, to the quality it has.)

**Default on, because** the added start-up is about 90 ms (under the 100 ms asked), the idle instance changes neither
Medium's d nor its latency beyond noise, and GPU memory is the only cost: **+1.5 GB at 4K, +1.3 GB at 1440p** (the owner's
4K session goes from about 3.1 to about 4.6 GB of the RTX 5090's 32 GB). `--no-quality-switch` or
`generator.qualitySwitch false` keeps one instance.

**For the owner's command** (`fmp capture --mode 4k30-hdr`, no settings file, no new option) the only differences are
those: about 90 ms more start-up and 1.5 GB more GPU memory, both on the new `quality` start-up line, a `keys` line, and
the bridge budget printed as 192 operations per write (its lead limit is 5 either way). Not run on the card tonight.

### Tests

- Unit (no GPU): `QualitySwitchControlTests` (Pipeline, 9: a model generator whose cost follows the commits in queue
  order against the real scheduler; up and down with no late or skipped slot in variable and fixed refresh; the naive
  commit does skip; presses taken back, rapid presses, a press before playback, the estimate restarting from what the
  quality measured, allocation-free polling); `GenerationTimeChangeTests` (Core, 2: the tuner's restart; D raised before
  the commit and the lead after it).
- Hardware: `QualitySwitchPlaybackTests` (Pipeline): VFG switched Medium → High → Medium mid-run on the virtual clock
  with the true middle frame of every pair known: every presented frame checked, the generated frames beat the blend, High's
  d over 1.5 times Medium's, 0 misses, 0 fallbacks (Vfx); a second instance whose load throws leaves one and plays on
  (Vfx); a model generator on the real clock: Q before playback and mid-run, the switch calls on the generator thread in
  order, frames keep coming, nothing missed (Gpu).
- CLI: `Quality_switching_is_an_option_of_both_commands_and_a_saved_setting` (Core): default, both options, contradiction,
  the setting's check, apply and override, `show`, `--help`.

## 5. Whole-solution test runs (Phase 9c)

**What was wrong.** Seven test assemblies queued behind the cross-process machine lock (`tests/Shared/GpuMachineLock.cs`:
Cuda, Gpu, Pipeline, Sources.File, Sources.V4l2, Vfx, Vulkan); **Core, Platform and Audio did not**. So in a
`dotnet test FrameMotionPlus.slnx` run Core's scheduler sweeps and stress tests (every core busy) and Platform's timer
tests ran at the start beside whichever hardware suite held the lock, and the Audio suite (PipeWire loopback through a
delay line on a thread that is not real-time) always ran beside one, which is how the real-clock windows of
`SimulatedDisplayTests` (latch 1.9–2.7 ms, lead 3.9–4.7 ms), `PipeWireLoopbackTests` and `ReviewWindowRegressionTests`
failed in whole-solution runs and passed alone.

**Fix.** Every one of the ten test assemblies now holds the same lock (Audio, Core and Platform compile it in too), so
none runs beside another; the wait limit is 30 minutes (the last of ten can wait longer than the old 10). Core takes 4 s,
so the wall time barely moves. CI is unaffected (no GPU or audio server: those tests skip, the lock is held briefly).
`WindowedTests`' zero-miss tests now use the rule their sibling `A_short_starting_lead_adapts_and_the_misses_stop` uses:
skipped only when every miss counted as the pipeline's is cleared by its own timestamps (handed over at least 1 ms before
the latch, or within 4 refreshes of a miss of the display's); any other miss fails (`WindowedTests.JudgeMisses`).

**Results** (`dotnet test FrameMotionPlus.slnx -c Release`, three times in a row, 05:22 to 05:54; each run 10 min 20 s,
about what one took before):

| Run | Result |
| --- | --- |
| 1 | all green: Core 708, Platform 100 + 1 skip, Audio 75, Cuda 144, Vulkan 108, Gpu 129, Vfx 92 + 1 skip, Sources.File 154, Pipeline 156 + 1 skip, Sources.V4l2 86 + 35 skips |
| 2 | all green (Vfx 93 + 1 skip: the new Vfx test was in from run 2) |
| 3 | **one failure: `SinkGuardTests.A_pulseaudio_monitor_of_the_stream_is_left_alone` (Audio)**; everything else green |

The Pipeline skip in every run is `WindowedTests.A_present_lead_too_short_for_this_compositor_is_counted_as_the_pipelines`
(the compositor took the presents handed over 1.5 ms before their refresh: nothing late to judge; its own rule, unchanged).
`SimulatedDisplayTests`, `PipeWireLoopbackTests`, `WindowedTests` and `ReviewWindowRegressionTests` passed in all three.

**The remaining failure is not a timing one and not this phase's** (no Audio source changed): afterwards the test failed
alone too, 4 of 7 times, then passed 12 times in a row. Each failure had the `parec --monitor-stream` node at a recycled
low id (127; the run-3 one at 129) that the **test's own registry connection** reported as not announced, while the guard's
connection had classified the same node as a recorder ("1 recorder(s) take a copy of the sound (parec,
Stream/Input/Audio): left alone"); a `pw-mon` trace showed a pulse client object holding id 127 being removed and made
again around the moment parec's node was created. So it looks like a node id recycled while a registry still held (or
received late) a removal for the id's previous object: `PwConnection` keys everything by id and its `global_remove` drops
whatever has that id. If the guard's own connection ever met the same, it would see a link to an unannounced node, which
it treats as wrong (mute and cut), a real risk for a screen share's stream; matching objects by `object.serial` (which
PipeWire never reuses) would remove it. Not fixed here: the test now says which peers it saw, how its registry described
them and whether the recorder was announced later (`SinkGuardTests.cs`, `Rig.Describe`), so the next failure tells.

Two more runs afterwards (06:01 to 06:22): **run 4 failed one test, run 5 was all green.** Run 4's failure was
`DecodeTests.A_buffer_is_not_overwritten_until_its_release_point(releaseWithReader: False)` (Sources.File, running alone
under the lock): its *control* case expects the decoder to overwrite a released buffer within a 300 ms GPU delay kernel,
and once the decoder's work did not get the GPU in time. The real case (released with the reader's signal: never
overwritten early) is unchanged; the control now takes up to three tries (`DecodeTests.cs`, `DelayedCopySeesTheFrame`),
passed 3 of 3 alone afterwards. Then two more whole-solution runs with that change (06:23 to 06:44): **all green**.
So runs 5, 6 and 7 are three in a row with no failure (10 min 20 s each); the Audio test above did not fail again in
them, but it is not fixed.

## 6. Hardening pass (2026-11-19)

Leftovers of the two reviews, each done as far as it was contained and testable.

**The audio guard reads the registry again before cutting for a node it does not know** (`PipeWireAudioDelay.Examine`,
`PwConnection.ResyncRegistryLocked`). A playback peer the registry has not announced used to be cut at once (mute,
destroy, re-make: a screen share's link broken with it). Now, when the only wrong peers are unannounced ones (an output
device other than the chosen sink is still cut at once, without a resync), the guard keeps the mute, destroys its registry
proxy, makes a fresh one, and waits for a core round trip (at most 250 ms, `ResyncTimeoutMs`; the loop thread runs while
it waits, the audio callbacks never take the loop lock) so the fresh registry has announced everything; the rebuilt model
replaces the old one only then (readers see the old one meanwhile), and the peers are classified again. A recorder after
all: the sound resumes, nothing cut; still unknown, or an output device: cut and re-made as before. One resync per
unannounced peer while it stays linked, at most one per look. The old registry proxy's hook memory is kept until the
connection is disposed (the library may touch it until the proxy is freed). Measured: the resync took **1.4, 1.4 and
1.6 ms** (three runs of the new test); **worst case muted: 250 ms (the round-trip limit) plus the fade-out (one block) and
the delay line's restart with its 10 ms fade-in**; when the server does not answer in time the fresh model is used anyway
and fills in as the late announcements arrive. The summary's audio line adds `linked to a node not announced Nx (…; M of
them a recorder, not cut)` when it happened.

**What the registry handler's catch swallows is counted** (`PwConnection.HandlerErrors`, the metadata handler's too):
`registry objects lost to a handler error: N` on the summary's audio line when N > 0, and the first one's type and
message once on stderr (`fmp: audio: the PipeWire registry handler failed on an object (…)`), said by the supervisor
outside the loop lock.

**Registry event log for tests**: `PwConnection.TryConnect(…, registryLogCapacity)` keeps a ring of the last N
registry events (`+id Node name (class)`, `+id Link a->b`, `+id Client`, `-id`, resyncs, handler errors), off in the app
(capacity 0; the guard's through the internal `AudioDelayOptions.RegistryLogCapacity`). `SinkGuardTests` keep 8,192 on
both connections and the recorder tests dump both logs (the lines about the peers and the playback node, then the last 60
of each) when they fail, so the next intermittent failure explains itself.

**The cause of the test's missed node was not found.** Reading `OnGlobal` again: nothing in the node path can throw on a
well-formed announcement (the name and class are copied before anything is parsed), and both connections receive the
same ordered stream from the server. One untested hypothesis remains: PipeWire keeps a client's per-object permissions
by global id, so a permission entry left for a recycled id could hide the new object from one client but not another (the
link, at a fresh id, stays visible); a fresh registry would then not announce it either, and the guard would cut as before.
Not reproduced in this pass (the monitor test passed in every run here).

Tests (`SinkGuardTests`, null sinks only): a recorder whose first announcement the guard's connection drops (the test
seam `HideNodeOnceForTests`) → one resync, classified a recorder, 0 wrong links, no restart, not muted, clicks on the
chosen sink and to the recorder afterwards; a recorder the guard never knows (`HideNodeAlwaysForTests`) → resync, then
cut (wrong links ≥ 1), the sound back on the chosen sink; a link by hand to another null sink → cut with **no** resync; the
classifier's new fields (output device, unannounced ids) without a server.

**Saved settings.** `fmp settings > file` used as a settings file now runs as no file: the file model's backend
default is `auto` (the command line's), and a saved `generator.backend: auto` or `generator.quality: medium` (the two keys
the model always holds) is not counted as saved. A file written by an older `fmp settings` (with `"vfg"`) still forces
VFG: it cannot be told from a chosen `vfg`. A failure after the file was applied (no such device, a mode the device does
not offer, the generator unavailable, a V4L2 error) is followed by `fmp: capture: saved settings used
from <file>: capture.device=/dev/video99, … (fmp settings show lists them; --no-settings runs without the file)`, and a
usage error from the combined command line carries the same text in parentheses; the start-up order is unchanged when
nothing fails. `set`/`unset` on a file with comments say `note: the file had comments;
set and unset rewrite it without them` (comments are detected with the JSON reader's own tokens, not by searching for
`//`). Verified on the AOT binary with a scratch `XDG_CONFIG_HOME` (no `~/.config/fmp` created).

**Test lock** (`tests/Shared/GpuMachineLock.cs`): what it says goes to stderr and to xunit's diagnostic messages, and a
lock not obtained in 30 minutes now **fails** the assembly's tests with the reason instead of running them unlocked.
Proved by holding the lock with `flock` and `FMP_TEST_LOCK_WAIT_S=3` (an override for trying it): the test failed with
`Assembly fixture type 'GpuMachineLock' threw in its constructor … the machine-wide test lock (/tmp/frame-motion-plus-gpu-tests.lock)
was not obtained in 0.1 min: …`. The "waiting" notice is shown only where diagnostic messages are on.

**High's generation-time seed** (`PlaybackSession.SeededGenerationTime`): the other quality's estimate starts from its
own instance's warm-up at load (8 back-to-back runs at the real size): the mean run plus a quarter, kept between half and
twice the table's figure (a cold first run must not make the scheduler give up generating). At 4K here High warmed up at
5.7–5.8 ms a run (45.3–46.7 ms for 8), so the seed is 7.2 ms against the measured windowed p99 of 6.996 ms (the table's
6.5 ms was below it). No warm-up measured (the instance was already loaded): the table. In the switch model a High
costing 9.5 ms (a slower GPU) made 1 late and 1 skipped slot at the first up-switch with the table seed, and none with
the warm-up seed (8 runs at 7.6 ms). The start quality's estimate is unchanged.

**Q in fullscreen**: there is no periodic status line or overlay to put the quality in, so the switch line is now in
capitals with its cost: `quality     >>> NOW HIGH (Q pressed) <<<  …; the picture now runs about 5 ms later at 4K …;
Q switches back`. No overlay was built.

**Test runs**: the Audio project five times in a row, all green (77 each, 53 s; the recorder count's last one-line
refinement came after them); then the whole solution once, all green: Core 711, Platform 100 + 1 skip, Audio 77, Cuda
144, Vulkan 108, Gpu 129, Vfx 93 + 1 skip, Sources.File 154, Pipeline 158 + 1 skip, Sources.V4l2 86 + 35 skips.

## Known issues / not done

1. **The two-hour soak**: done as the owner's session (1c); the RSS creep of 1b levelled off. Nothing in the summary
   measures RSS, GPU memory or descriptors yet (see 3): they were sampled from outside. To repeat it silently on the card:
   ```sh
   pactl load-module module-null-sink sink_name=fmp-soak     # prints a module number
   fmp capture --mode 4k30-hdr --window 960x540 --duration-s 7200 --audio-sink fmp-soak > soak.log 2>&1 &
   while sleep 60; do ps -o rss=,nlwp= -p $(pgrep -n fmp); ls /proc/$(pgrep -n fmp)/fd | wc -l; \
     nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader | grep $(pgrep -n fmp); done > soak-res.log
   pactl unload-module <module number>                        # afterwards
   ```
   Judge: GC 0 in the measured window, the latency line at the end against the start, audio drift and underruns, RSS,
   GPU memory and descriptor counts flat after the first minutes, a clean exit (PASS, teardown clean).
2. **Live quality switching**: built (section 4). Not run on the capture card (the PS5 was off); a hold on the card while
   switching, and Q on the owner's TV under variable refresh, are untested on hardware.
3. **Soak figures in the summary are not built** (RSS, GPU memory per process via NVML, descriptor and thread counts at
   start, after the warm-up and at the end, growth per hour, latency drift by minute). A first allocation-free sampler
   (procfs with `pread`, `getdents64`, NVML process lists) was written and removed unfinished when the session was cut;
   the existing summary already carries GC counts for the measured window, D and latency at the end, and the audio drift
   correction, underruns, overruns and the delay's excursions.
4. Keys: printed once at start (`keys` line); Q added.
5. Leftovers not done: the switch notice going stale after 64 switches (`PlaybackSession.cs`, the source keeps only the
   first 64 in its log); the HDR check's "PQ codes kept" line that cannot fail (`HdrCheck.cs:110`, needs a measured
   tolerance); offscreen colour switches allocating on the present thread (`OffscreenTarget.cs:157`).
6. `fmp capture --list` with `--settings` is refused ("--list takes no other option"), as before.
7. Direct display: only the probe above.
8. **The audio guard's loopback hole** (phase8-audio.md, "Screen shares and recorders"): not done in Phase 9c (time).
9. **`SinkGuardTests.A_pulseaudio_monitor_of_the_stream_is_left_alone` failed intermittently** (section 5) and the cause
   is still unknown (section 6: the id-reuse ordering fault was refuted in review; a stale per-id permission is an
   untested hypothesis). The guard no longer cuts for a node it does not know without reading the registry again, and the
   test now dumps both connections' registry events when it fails. Not reproduced in this pass.
10. Quality switching, not done or not measured: Q on the capture card (PS5 off); switching during a hold or a cut on the
   card; Q under variable refresh on the owner's 119.88 Hz TV (only DP-3 was used); the per-switch window counts the live
   accounting's misses, not the verifier's; a switch made while the scheduler makes no decisions (a long hold) waits 1 s
   and is then committed without D ready (counted, never seen).

## What should change in `docs/implementation-plan.md` (not edited here)

Under Phase 9: saved settings done (CLI, no window); the two-hour soak done (the owner's session, 1c); hitch-free
quality switching done (section 4: Q, on by default, +90 ms start-up and +1.5 GB at 4K, 0 missed around 16 switches;
the whole solution's test assemblies all queue behind one machine lock, section 5); the soak instrumentation still open; the direct-display finding above (KWin leases no desktop connector: a TTY is needed).

## Found by the independent review (2026-11-18)

Approved with no must-fix. Fixed afterwards: a saved `capture.device` or `generator.vfxSdkDirectory` starting with `-`
is refused (it would have been read as an option), and `fmp settings set` gives a plain error when the config directory
cannot be created. Not fixed:

- `set` on a symlinked `settings.json` replaces the link with a regular file; a symlinked directory is fine.
- `set` and `unset` still drop comments from a hand-edited file (they now say so; section 6). Fixed in section 6: the
  printed defaults forcing VFG, and a saved value that fails after parsing not naming its key and file (`fmp play` does
  not tag its errors that way yet: only `fmp capture` does).
- No lock and no fsync on write: two `set`s at once can lose one update.
- An existing but unreadable settings file stops a capture with one error before any device is opened.
- JSON errors give a zero-based line number and .NET type names.
- Test isolation redirects `XDG_CONFIG_HOME` for the whole test process, so PipeWire or Vulkan loader configuration
  under `~/.config` would be ignored by tests on a machine that has any.

## Found by the independent review of live quality switching (2026-11-19)

Approved with no must-fix. Measured back to back on the published binary, the idle second instance changed nothing:
fullscreen variable refresh 19.60 / 19.52 ms without it and 19.52 / 19.50 ms with it; 0 missed, 0 B, no collections.
The bridge budget printed as 192 operations is a declared figure only: the lead limit is 5 either way. Fixed
afterwards: the windowed tests' skip rule could clear every miss when no slot made its refresh. Not fixed:

- **Recycled node ids and the audio guard:** the intermittent failure of `SinkGuardTests.A_pulseaudio_monitor_of_
  the_stream_is_left_alone` did not reproduce in 106 attachments with every registry event logged (1,810 id reuses,
  none mis-tracked), and the suspected ordering fault is contradicted by that evidence. The cause of the original
  failures is still unknown. Done in section 6: a resync before cutting for an unannounced peer, the swallowed
  handler errors counted, the test's registry dump. A node hidden from the guard by the server itself (if that is the
  cause) would still be cut after the resync.
- Done in section 6: the test lock's fallback (now a failure with its reason), High's seed (from its warm-up).
- **Video memory short:** a second load that fails falls back to one instance; one that succeeds and starves a
  later allocation would break a start that one instance survived (not tested; nor a cold CUDA JIT cache).
- **Q in fullscreen** still shows nothing on the screen (no overlay): the terminal line is now in capitals with the
  cost (section 6).
- 60 fps at 4x on fixed 240 Hz drops to stride 2 with 6 starved slots at an up-switch (simulated).
- Not exercised: Q on the capture card, during a hold or a cut, and H while a switch is pending.

## Found by the independent review of the hardening pass (2026-11-19)

Approved with no must-fix. Corrections and what was done about them:

- **The registry re-read can wait about 1 s, not 250 ms, when the server does not answer.** The wait used
  (`pw_thread_loop_timed_wait`) counts in whole seconds and the deadline is only checked on wake-up: against a
  stopped server the re-read returned after 1,001 ms three times out of three. Where this document or
  `phase8-audio.md` says "at most 250 ms", read "about 1 s if the server has stalled; 0.4 to 1.6 ms measured
  otherwise". Not changed: an absolute-deadline wait (`pw_thread_loop_timed_wait_full`) would honour 250 ms.
- **Fixed afterwards: a re-read that timed out no longer lets the guard conclude anything.** The reviewer showed by
  reading that the incomplete model could hold the link to the chosen sink but not yet a link to another output
  device, so the sound could resume until the late link arrived. Now, when the server did not answer in time, the
  stream is cut and made again, as an unannounced peer always was before the re-read existed.
- One retired listener hook per re-read is kept until the connection is disposed (about 64 bytes each).

