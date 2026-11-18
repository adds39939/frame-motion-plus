# Phase 9a: saved settings (and what was left for later)

Scope as set on 2026-11-18 (capture card first, forward progress over perfection). The session was cut short by the
owner at 22:37 ("I want to start using this at 12am, so no 2 hour test please"): the card, the GPU and the screen had to be
free from 23:30. So this phase delivers **saved settings and a bare `fmp capture`**, fully tested, plus two small
leftovers; the live quality switch, the soak instrumentation and the two-hour soak are **not built or run** and are listed
under "Known issues / not done" with what was found and the exact commands.

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
  hand-edited file are not kept by `set`.
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

## Known issues / not done

1. **The two-hour soak has not been run** (the owner needed the machine at midnight); only the 10-minute run in 1b, whose
   RSS crept up about 2 MB every 200 s. Nothing in the summary measures RSS, GPU memory or descriptors yet (see 3). For
   the owner to run another time, silent, on the card:
   ```sh
   pactl load-module module-null-sink sink_name=fmp-soak     # prints a module number
   fmp capture --mode 4k30-hdr --window 960x540 --duration-s 7200 --audio-sink fmp-soak > soak.log 2>&1 &
   while sleep 60; do ps -o rss=,nlwp= -p $(pgrep -n fmp); ls /proc/$(pgrep -n fmp)/fd | wc -l; \
     nvidia-smi --query-compute-apps=pid,used_memory --format=csv,noheader | grep $(pgrep -n fmp); done > soak-res.log
   pactl unload-module <module number>                        # afterwards
   ```
   Judge: GC 0 in the measured window, the latency line at the end against the start, audio drift and underruns, RSS,
   GPU memory and descriptor counts flat after the first minutes, a clean exit (PASS, teardown clean).
2. **Live quality switching (Q) is not built.** What is known: `VfgGenerator` already supports loading a second instance
   at a drained point (`Prepare`) and switching to it during playback without any CUDA call (`TryConfigurePrepared`,
   published whole between two `Enqueue` calls, covered by `A_prepared_only_switch_never_loads`); loading during
   playback is not an option, because `NvVFX_Load` synchronises the whole CUDA context and stalls the capture's uploads
   and the generator for the load and warm-up (Phase 3: High loads in about 42 ms warm, 114 to 217 ms cold, and an
   instance takes 140 MB to 1.9 GB). The plan: load the other instance at start-up behind an opt-in option (so the default
   `fmp capture --mode 4k30-hdr` keeps its start-up time and memory), Q on the control thread calling
   `TryConfigurePrepared`, the bridge budgeted for High's 192 operations per write (lead still 5), the scheduler's d
   estimate following the step within two samples and D rising at 50 ms/s (about 90 ms for High's +4.6 ms), so latency
   about +5 ms on High. The switch cost was not measured on hardware.
3. **Soak figures in the summary are not built** (RSS, GPU memory per process via NVML, descriptor and thread counts at
   start, after the warm-up and at the end, growth per hour, latency drift by minute). A first allocation-free sampler
   (procfs with `pread`, `getdents64`, NVML process lists) was written and removed unfinished when the session was cut;
   the existing summary already carries GC counts for the measured window, D and latency at the end, and the audio drift
   correction, underruns, overruns and the delay's excursions.
4. Keys are not printed once at start; no Q key.
5. Leftovers not done: the switch notice going stale after 64 switches (`PlaybackSession.cs`, the source keeps only the
   first 64 in its log); the HDR check's "PQ codes kept" line that cannot fail (`HdrCheck.cs:110`, needs a measured
   tolerance); offscreen colour switches allocating on the present thread (`OffscreenTarget.cs:157`).
6. `fmp capture --list` with `--settings` is refused ("--list takes no other option"), as before.
7. Direct display: only the probe above.

## What should change in `docs/implementation-plan.md` (not edited here)

Under Phase 9: saved settings done (CLI, no window); hitch-free quality switching, the soak instrumentation and the
two-hour soak still open; the direct-display finding above (KWin leases no desktop connector: a TTY is needed).

## Found by the independent review (2026-11-18)

Approved with no must-fix. Fixed afterwards: a saved `capture.device` or `generator.vfxSdkDirectory` starting with `-`
is refused (it would have been read as an option), and `fmp settings set` gives a plain error when the config directory
cannot be created. Not fixed:

- `fmp settings > file` used as a settings file forces VFG: the printed defaults contain `generator.backend: "vfg"`
  and `quality: "Medium"`, which then count as saved (`FmpSettings.cs` against `UserSettings.cs:61`, default `auto`).
- A saved value that fails after parsing does not name its key or file: a saved `/dev/video99` gives `no such capture
  device /dev/video99` with no hint that it came from the file (`CaptureCommand.cs:180` prints the settings line only
  after setup succeeds).
- `set` on a symlinked `settings.json` replaces the link with a regular file; a symlinked directory is fine.
- `set` and `unset` drop comments from a hand-edited file without saying so; unknown keys are kept.
- No lock and no fsync on write: two `set`s at once can lose one update.
- An existing but unreadable settings file stops a capture with one error before any device is opened.
- JSON errors give a zero-based line number and .NET type names.
- Test isolation redirects `XDG_CONFIG_HOME` for the whole test process, so PipeWire or Vulkan loader configuration
  under `~/.config` would be ignored by tests on a machine that has any.

