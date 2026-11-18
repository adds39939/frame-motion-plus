# Phase 7: HDR pass-through, done correctly

Scope (`docs/implementation-plan.md`, Phase 7, as set by the owner on 2026-11-18): "We don't need SDR-to-HDR. Just display
what the PS5 is currently outputting, as long as HDR is correct." So: the HDR path right and proven from capture to
swapchain, an SDR signal shown as SDR, no conversions between the two (no tone mapping, no SDR placed in HDR, no expansion),
nothing for Windows, file playback kept working.

Machine: RTX 5090 (driver 620.70.06), KDE Plasma 6.8.2 (KWin 6.8.2) Wayland, DP-3 (3840x2160 at 240 Hz, primary) and
HDMI-A-1 (3840x2160 at 119.88 Hz), both with HDR and wide colour gamut on and VRR automatic; Elgato 4K X on
`/dev/video0` with the PS5 on, in a game (Demon's Souls), **sending HDR throughout this work** (its samples carried 10
bits in every frame captured, 2026-11-18 evening). `fmp` is the NativeAOT binary. Display settings were not changed.

## Summary

| | |
| --- | --- |
| HDR path, capture to swapchain | **proved bit-exact** for real frames: P010 (limited range, BT.2020 matrix, 4:2:0) → the capture's conversion kernel → RGB10A2 → ring → presenter → HDR10 ST 2084 target, read back: 0 pixels differ from the exact CPU reference on four HDR patterns at 1920x1080 (`fmp hdr-pattern --check`, 27 checks, all pass) |
| 10 bits kept | yes: 831 distinct grey levels of a 1024-code ramp reach the screen image, the same 831 that limited-range 10-bit luma carries; no 8-bit stage anywhere (an 8-bit one would leave at most 256) |
| VFG keeps transfer and range | yes: a frame generated between two identical HDR frames is identical inside every feature (0 codes off; 2 at most on the ramp's one-code steps), mean signed difference −0.003 to +0.002 codes over the frame; 97.7 to 99.6% of pixels identical, the rest on a few high-contrast edges (VFG's own, up to 29 codes) |
| Output colour space | follows the content: **HDR10** (`A2B10G10R10` / `HDR10_ST2084`) for PQ, **SDR** (`B8G8R8A8` or, for 10-bit SDR, `A2B10G10R10` / `SRGB_NONLINEAR`) for SDR. Before: chosen by the ring's bit depth (10-bit was always PQ) |
| Scaling | **in linear light** for PQ (decode, bilinear, encode; within 0.57 codes of a double-precision CPU reference), pixel for pixel when sizes match (fullscreen 4K: exact), bars at code 0 (true black). Before: bilinear on PQ code values: an edge between black and 10,000 nits weighted 1/8 showed **0.6 nits instead of 1,250** |
| HDR metadata | **sent** (`VK_EXT_hdr_metadata`): the file's mastering display and light levels; for the capture card (which carries none) BT.2020/D65, 0.0001 to 1000 nits, MaxCLL 1000, settable with `--hdr-peak-nits`, `--hdr-max-cll`, `--hdr-max-fall`. Before: none. The driver hands it to KWin (seen on the wire, section 3) |
| What KWin does | per the Wayland colour-management protocol and what KWin and the driver report (section 3): the window is PQ / BT.2020 with the default **203-nit reference white**, perceptual intent; KWin anchors reference whites, so PQ is shown scaled by the output's "SDR brightness" / 203 (2.17x on DP-3, 2.46x on HDMI-A-1) and compressed above the output's peak (1000 / 800 nits). Not measured with a meter |
| Following the console | nothing is signalled (V4L2 colour fields constant, no source-change events, the card's UVC extension unit shows no signal state to plain reads). **Detector**: a P010 capture follows the bit depth of its samples (10 bits HDR10, 8 bits SDR), on by default (`--hdr follow`). The HDR side is measured (every HDR frame read 10-bit, three sessions); **the SDR side is not** (the console stayed in HDR). Manual switch: **H** in the window, A to follow again; the command-line overrides `--hdr on/off` |
| Switch cost | measured on the card, windowed: 7 and 8 switches in 20 s runs, **0 missed slots**, each output rebuild **at most 0.66 ms**, no request refused; no VFG change needed (the ring's format does not change), so no second VFG instance is kept |
| No regression | 30 s windowed, before / after: 4k30-hdr **54.02 / 54.25 ms** (p99 58.0 / 58.2), 1440p60-hdr **39.12 / 39.33 ms** (p99 43.0 / 43.7); 0 missed, 0 B, GC 0 in all four. Fullscreen 4k30-hdr: variable refresh chosen, HDR10 swapchain, 46.6 ms (Phase 6: 46.6) |
| HDR vs SDR latency | 1440p60, same output, back to back: d **1.478 vs 1.472 ms** (no added d); total 40.4 vs 35.6 ms, the difference being the card's USB delivery of twice the bytes (11.6 vs 6.2 ms) |

## Corrections from the independent review (2026-11-18)

The review approved this phase and corrected four statements made elsewhere in this document. Where they disagree,
this section is the one to believe.

1. **The metadata now sent changes how KWin shows the picture; the "2.2 to 2.5 times brighter" figure is wrong.**
   From KWin's source (master, not the installed 6.8.2; not measured with a meter): the reference white is anchored
   to the output's "SDR brightness" as section 3 says, but a tone mapper then runs over the whole range, a Reinhard
   curve keyed on MaxCLL (else the mastering peak, else 10,000 nits), with a clamp above the stated peak. On DP-3
   that puts 203-nit white at about 226 nits before this phase (no metadata, so 10,000 assumed) and about 297 now
   (1,000 stated): about 1.46 times at reference white, not 2.17. Content at 1,000 nits goes from about 414 nits to
   1,000, and anything above 1,000 clamps to the panel's peak. So the picture is **brighter than before this phase,
   and highlights above the stated peak clip**. The game on the card during the review peaked at 2,100 to 2,400 nits
   in luma (under 0.005% of pixels above 1,000 in the frames sampled). `--hdr-peak-nits` moves the curve: at 2,000,
   white is about 254 nits, 1,000 nits about 641, and nothing clips up to 2,000; at 10,000 the picture is as it was
   before this phase. The intended way to settle it is the PS5's own "Adjust HDR" screens run through the app
   (section 6), so the console's peak matches what the chain shows; or set `--hdr-peak-nits` to the console's
   calibrated peak. PQ files without mastering metadata get the same 1,000-nit default. "Cannot be changed from
   Vulkan" holds for the reference white only.
2. **"0 missed slots" on a switch was measured windowed only.** In fullscreen variable refresh a colour switch can
   cost a late present: 5 late slots over 19 switches in 12 s at 1440p60 (`VulkanPresenter.cs:663-675`). Harmless
   for a console that switches rarely.
3. **That a PS5's SDR arrives as 8-bit samples in P010 is an assumption, not a measurement.** It was traced to the
   `ffmpeg` NV12 fallback (section 4), and no real SDR signal has been seen. If the card does not zero the low bits
   for SDR, follow never switches, which is the behaviour before this phase; H switches by hand. The comments in
   `SampleDepthProbe.cs:20-24` and `CaptureColor.cs:24-30` and the help text state it as fact.
4. **The "PQ codes kept" lines of `fmp hdr-pattern --check` cannot fail** (`HdrCheck.cs:110` passes a constant).
   The conversion matrix is still covered by the conversion check and a unit test, and the review's five mutations
   (BT.2020 coefficient, limited-range offset, code-value scaling, metadata peak, SDR/HDR selection) each made the
   check fail.

Also found, not fixed:

- Offscreen colour switches allocate on the present thread (`OffscreenTarget.cs:157`: 12,960 B over 72 switches), so
  a switching `fmp capture --offscreen` run ends with verdict FAIL. The swapchain path allocated nothing over 132.
- H and A are bare keys (`SdlWindow.cs:187-192`): a stray H in the focused window turns HDR off and stops following,
  with the notice only on stdout; H also toggles an 8-bit NV12 capture into PQ.
- Follow shows up to three frames in the wrong colour when a signal returns after the card's own still, if that
  still reads 8-bit (`SampleDepthProbe.cs:98`).
- H on a still picture is announced at once but only shown at the next new frame (`V4l2FrameSource.cs:1004, 1118`).
- The switch notice goes stale after 64 switches (`PlaybackSession.cs:885-895`).
- Two of about ten windowed runs without a switch had a short episode of missed slots (5 and 4); reruns were clean.
  It looks like the known desktop disturbance but was not compared against the previous commit.

## 1. The path, stage by stage: before and after

| Stage | Before (Phase 6) | Now | Proof |
| --- | --- | --- | --- |
| V4L2 colour fields | sRGB / Rec. 709 / Rec. 709 / default for every format and mode, P010 included (the card signals nothing) | unchanged (the card's); a P010 capture is taken as HDR10 by default and follows the signal's bit depth (section 4) | Phase 5 table; this session |
| YCbCr → R'G'B' (`native/kernels/yuv.cu`) | exact integer arithmetic: limited range 64..940 / 64..960 expanded to 0..1023, BT.2020 non-constant-luminance matrix for HDR10, BT.709 for SDR, bilinear chroma upsampling with the siting (left assumed), rounded half up, clamped | **unchanged: it was already right**. Super-white and sub-black are clamped deliberately: in PQ, luma above 940 is above 10,000 nits (nothing legal there); in SDR, above 940 is above reference white, which an sRGB swapchain clamps anyway; below 64 is below black. Out-of-range chroma (illegal YCbCr) clamps per channel | 0 of 2,073,600 pixels differ from the exact CPU reference on every pattern (`--check`) |
| Precision | 10-bit P010 → RGB10A2 throughout | unchanged; now proven on the screen image | 831 grey levels end to end |
| Frame history, ring | RGB10A2 (10-bit captures), RGBA8 (8-bit) | unchanged; frames now carry the content's colour (PQ or SDR, metadata) and present requests carry the colour of their newest real frame (`PacingLoop.cs:568`) | |
| VFG | RGB10A2 in and out (`NVCV_RGB10A2`, P32); generates in the source's transfer (PQ code values) | unchanged | generated vs real: identical inside features, bias ≤ 0.003 codes |
| Ring → staged image | R32 word copy, exact | unchanged | presented real frames bit-exact |
| Output transform | by ring bit depth: 10-bit = PQ always, 8-bit = SDR always | **by the content's colour**: PQ → HDR10 pass-through, SDR → SDR pass-through (any bit depth); the placeholder PQ→SDR stays only for a display without HDR10 (`PresentPass.cs:133`) | `HdrPresentTests`, `--check` "SDR, 10-bit" / "8-bit" |
| Scaling | hardware bilinear on code values (PQ too) | **PQ in linear light**: four texels fetched, PQ decoded, weighted as the hardware does, encoded (`present.frag:56`); SDR unchanged (code values, as is usual for SDR video); identity when the frame fits exactly | ≤ 0.57 codes from the CPU reference at 1.33x, 0.5x and 1.3x |
| Swapchain | `A2B10G10R10` / `HDR10_ST2084` for any 10-bit ring | HDR10 for PQ content; SDR (`SRGB_NONLINEAR`, 10-bit format for 10-bit content) for SDR; switches while presenting (section 4) | capture and play runs |
| HDR metadata | none | `vkSetHdrMetadataEXT` on every HDR10 swapchain, and again when the content's metadata changes (`SwapchainTarget.cs:392`) | read back from the structure passed; seen on the Wayland wire |
| Bars | cleared to (0, 0, 0) | unchanged: code 0 is true black in PQ (0 nits) and in SDR | checked on every scaled read-back |

Chroma siting: the card makes its own 4:2:0 from what the console sends, and does not say where it puts chroma. Left
(MPEG-2/H.264/HEVC's default) is assumed; BT.2020 material is often top-left. The difference is half a chroma row
vertically, visible at most as a faint colour fringe on a sharp coloured edge; it cannot be settled without a known chroma
pattern from the console.

## 2. The numbers (`fmp hdr-pattern --check`, published binary, 1920x1080, VFG Medium)

Each pattern is encoded as a console sends it (P010, limited range, BT.2020 matrix, 4:2:0), uploaded, converted by the
capture's kernel, written into the ring as a real frame and as a VFG frame between two identical frames, presented by the
same presenter as playback (following the frames' colour) to an offscreen HDR10 target and read back.

| Check | Ramp | Peak | Bars | Near-black |
| --- | --- | --- | --- | --- |
| Conversion vs exact CPU reference | 0 px differ | 0 | 0 | 0 |
| Intended PQ codes vs the 10-bit YCbCr round trip (the signal's own quantisation) | ≤ 1 code; ≤ 3.65% luminance from 0.05 nits up | ≤ 1 code, ≤ 0.89% | ≤ 1 code, ≤ 0.94% | ≤ 1 code; ≤ 0.001 nits below 0.05 nits |
| Presented real frame vs conversion | 0 px differ | 0 | 0 | 0 |
| VFG frame: inside features / mean signed / largest (edges) | 2 / −0.0006 / 29 codes | 0 / +0.0021 / 21 | 0 / −0.0027 / 25 | 0 / 0.0000 / 5 |
| VFG frame: pixels identical | 97.66% | 99.56% | 99.21% | 99.63% |

Scaling against a CPU reference that filters in linear light (every third row checked; tolerance 1 code):

| | worst vs linear-light reference | what averaging code values (Phase 2's presenter) would have shown at the worst pixel |
| --- | --- | --- |
| peak 1920x1080 → 2560x1440 | 0.52 codes | 666 codes off: 0.6 nits instead of 1,250 |
| peak → 960x540 | 0.00 | 0 (features on even pixels, exact 2:1) |
| peak → 2496x1404 (pillarboxed on 2880x1404) | 0.52 | 667 codes off: 0.5 nits instead of 1,154 |
| ramp → 2560x1440 / 960x540 / 2496x1404 | 0.57 / 0.56 / 0.56 | up to 603 codes off |

Bars were code 0 in every scaled read-back. HDR metadata handed to Vulkan (read back from the `VkHdrMetadataEXT` passed):
BT.2020 primaries, D65 white, mastering 0.0001 to 1000 nits, MaxCLL 1000, MaxFALL not stated: as intended; with
`--hdr-peak-nits 800 --hdr-max-cll 700 --hdr-max-fall 250`, those (test). SDR: a P010 BT.709 picture on the same RGB10A2
ring switched the output to SDR (10-bit images, no metadata) and was presented bit-exact; the next PQ frame switched it
back to HDR10 with metadata; an NV12 BT.709 picture on an RGBA8 ring went to an SDR target, every code as converted.

Offscreen presenter tests (`HdrPresentTests`): every one of the 1024 codes in each channel unchanged on an HDR10 target;
PQ scaled in linear light at 4x, 1.5x, 2.5x (pillarboxed) and 0.5x within 1 code of the reference and never outside the
codes it is made from (no ringing); an edge between 0 and 1,000 nits upscaled 4x shows 375 and 625 nits at 3/8 and 5/8;
SDR 8- and 10-bit pass through code for code; the peak and near-black patches keep their codes; 12 switches between SDR and
HDR10 through a presenter that follows frame colours, each frame on the right output, no request rejected.

The cost of linear-light scaling: the present pass at 2560x1440 → 1856x1044 takes 25.6 µs of GPU (p50; 7.5 µs before,
hardware bilinear). Unscaled (fullscreen 4K) it is the plain texel fetch as before.

## 3. What KWin does with the surface (as far as could be established)

What the compositor reports (`wayland-info`, `wp_color_manager_v1` version 3; `kscreen-doctor -o`):

| | DP-3 (240 Hz) | HDMI-A-1 (119.88 Hz) |
| --- | --- | --- |
| HDR, wide colour gamut | enabled | enabled |
| Output's preferred description to clients | BT.2020 container, gamma 2.2, luminances min 0, max 1000, **reference 440** cd/m²; target gamut about P3; max_cll 1000, max_fall 265 | BT.2020, gamma 2.2, min 0, max 800, **reference 500**; target about P3; max_cll 800, max_fall 500 |
| `kscreen-doctor`: SDR brightness / peak | 440 nits / EDID 1317, overridden with 1000 | 500 nits / EDID unknown, overridden with 800 |
| Rendering intents offered | perceptual, relative, absolute, relative + BPC | same |

What the NVIDIA driver tells KWin about our HDR10 swapchain (`WAYLAND_DEBUG=client fmp hdr-pattern bars --hdr-peak-nits
800`, our own process): `create_parametric_creator`, `set_primaries_named(bt2020)`, `set_tf_named(st2084_pq)`,
`set_max_cll(800)`, `set_mastering_display_primaries(BT.2020, D65)`, `set_mastering_luminance(0.0001, 800)`, no
`set_luminances` (so the PQ defaults: 0.005 to 10,000, reference white 203), no `set_max_fall` (0 = not stated is left
out), `set_image_description(…, perceptual)`. So the metadata reaches KWin as sent; an SDR swapchain gets no description
(sRGB).

What follows from the protocol (`color-management-v1.xml`, set_luminances: "Compositors should make sure that all content is
anchored, meaning that an input signal level of 'reference_lum' on one image description and another input signal level of
'reference_lum' on another image description should produce the same output level"):

- A PQ window's 203 nits are shown at the output's reference: 440 nits on DP-3, 500 on HDMI-A-1. Everything in the
  window is scaled with it in linear light (2.17x and 2.46x), so **PQ levels are not shown as coded** with these settings;
  above the output's peak (1000 / 800) KWin compresses (its own curve; the window's MaxCLL / mastering peak tell it how
  bright the content gets).
- With the output's SDR brightness at 203 nits the anchoring is 1:1 and PQ is shown as coded up to the output's peak. That
  also dims the desktop's SDR content; it is the owner's choice (not changed here).
- The app cannot ask for anything else through Vulkan: the driver owns the surface's colour-management object (a second
  one from the app is a protocol error), and Vulkan has no way to state a reference white or an intent.
- An SDR window (SDR swapchain) is the desktop's SDR: shown at the SDR brightness, as every SDR window is.

Not established (needs a colorimeter, or KWin's debug output, which needs a KWin restart): the light actually emitted; KWin's
compression curve; whether a fullscreen PQ window is scanned out directly or composited (with reference 440 ≠ 203 a
conversion is needed, so most likely composited; the latency measurements do not show a difference either way); what the
monitors themselves do with the PQ signal KWin sends them (their own tone mapping above their peak).

## 4. Following the console

**What the card shows about the console's output** (the console was not switched: not ours to change):

- V4L2 colour fields: sRGB / Rec. 709 / Rec. 709 / default in every format and mode, whatever the console sends (Phase 5).
- Events: `uvcvideo` offers no source-change events; the card keeps the negotiated format.
- The card's UVC extension unit (`{961073c7-49f7-44f2-ab42-e940405940c2}`, unit 4, 17 controls), read with plain GET
  requests only: selectors 3 and 4 return a static device-information block (unchanged between reads); the others refuse a
  read (they are a vendor command channel that needs a write first). Nothing about the input signal is readable this way;
  writing to an undocumented vendor channel was not tried (it could change the card's state).
- The picture itself:
  - **Sample depth.** Every HDR frame captured in P010 used the two low bits of the 10-bit codes: this evening 907 of 908
    (4k30) and 1,812 of 1,814 (1440p60) frames read 10-bit (the rest black); this morning's HDR runs show it too (their
    brightest-cell means fall in sixteenths, impossible with codes in steps of 4). A PS5 sends SDR in 8 bits (unless its
    "Deep Colour Output" sends more), which the card would carry in P010 with the low bits zero. That is the detector built
    (`SampleDepthProbe.cs`): 64x32 luma samples read on the CPU from the capture buffer while the GPU uploads it (no added
    latency: dequeue → upload judged 0.672 vs 0.663 ms before at 4K), black left out; "8-bit" needs no low bit set over at
    least 16 distinct codes (a 10-bit picture would do that by chance about once in 4^16), "10-bit" low bits on at least
    1/16 of the samples; three such frames in a row switch (the first conclusive frame of a run decides alone). **Not
    verified: what an SDR signal from this console looks like in P010** (the console stayed in HDR). If the card or the
    console sends SDR with the low bits in use, the detector reads it as HDR10 (the old behaviour) and H is the way.
    Caution for anyone repeating this: the ffmpeg CLI cannot capture this card's P010 ("Unsupported" in its format list)
    and silently captures NV12 instead; its "P010" output then has every code a multiple of 4, which first looked like an
    8-bit SDR signal here.
  - **Levels.** In P010 the brightest 4x4 cell of the HDR game sat at code 791 (about 2,000 nits PQ); the console's
    HDR runs earlier in the day at 595 to 843. An SDR signal's whites sit at 940. A dark SDR scene and a dim HDR one overlap, so levels alone
    cannot decide; they are not used.
  - **The card's 8-bit formats**: on the same HDR signal its NV12 picture's brightest cell is 235.8, SDR white: the card fits
    HDR into SDR itself for NV12, so the `-sdr` modes are right to show SDR.
- A switch of the console's HDMI output probably shows as a few black or no-signal frames (a re-sync); that is counted
  (black frames, no-signal episodes) but does not say which way it went.

**What is implemented.** `--hdr follow` (the default for P010: the `-hdr` modes and no options) starts as HDR10 and follows
the sample depth; `--hdr on|off|auto|10bit` fix the colour for the run. In the window, **H** switches between SDR and HDR10
by hand at the capture's next frame and stops following; **A** follows again. A switch changes the conversion (BT.2020 ↔
BT.709 matrix), the cadence thresholds and the frames' colour; the first frame in the new colour is marked a discontinuity
(never interpolated with the one before) and a format change; the presenter, following the frames' colour, rebuilds its
output in the other colour space at that frame, once the frames in flight are done (requests are refused until then, never
waited for), with the pipelines for both prepared at the start. One line is printed per switch, and the summary lists them
with their cost.

**No second VFG instance.** A P010 capture stays RGB10A2 in both colours (10-bit SDR keeps its precision on an SDR swapchain
in `A2B10G10R10`), an NV12 one RGBA8 in both: the generator never changes format, so nothing is reloaded and nothing more is
kept in memory (a VFG Medium instance is 0.5 to 1.9 GiB).

**Switch timings** (published binary, `FMP_CAPTURE_SWITCH_EVERY_S`, which asks for what H does; windowed; the window was on
the 240 Hz output):

| Run | Switches | Missed slots | Output rebuild (worst) | Wait for frames in flight | Requests refused | Other |
| --- | --- | --- | --- | --- | --- | --- |
| 1440p60-hdr, 20 s, every 3 s | 7 | 0 (verifier agrees) | 0.616 ms | 0 | 0 | 26 slots held at the switch discontinuities (no generated frame across a switch) |
| 4k30-hdr, 20 s, every 2.5 s | 8 | 0 (verifier agrees) | 0.658 ms | 0 | 0 | 0 B on every thread, GC 0, PASS |
| vivid NV12, offscreen session, 4 switches (test) | 4 | — | — | — | 0 rejected | output SDR at the end |
| offscreen check, P010 SDR then PQ | 2 | — | 2.7 ms (offscreen images) | 0 | 0 | |

The first-pixel report of the last present on each old swapchain is lost (8 reports in the 8-switch run), so those few
slots are "unjudged", not missed.

## 5. Latency: no regression, and HDR against SDR

30 s windowed runs of the published binary on the console's HDR game, before (HEAD) and after; the window opened on
HDMI-A-1 (119.88 Hz) in all four:

| Mode | Latency before (p99) | After (p99) | Missed | Allocated | GC |
| --- | --- | --- | --- | --- | --- |
| 4k30-hdr | 54.02 ms (58.01) | 54.25 ms (58.21) | 0 / 0 | 0 B / 0 B | 0 / 0 |
| 1440p60-hdr | 39.12 ms (43.04) | 39.33 ms (43.74) | 0 / 0 | 0 B / 0 B | 0 / 0 |

Fullscreen, 4k30-hdr, 9 s on DP-3: variable refresh chosen, HDR10 swapchain with metadata, 46.56 ms (p99 46.80), 0 missed,
shown pixel for pixel (3840x2160 on a 3840x2161 surface: one bar row). Phase 6 measured 46.6.

HDR against SDR at the same size and rate (1440p60 P010 and NV12, 20 s each, back to back, both on DP-3 at 240 Hz):

| | HDR (P010 → RGB10A2) | SDR (NV12 → RGBA8) |
| --- | --- | --- |
| d (VFG Medium, GPU) p50 / p99 | 1.478 / 1.528 ms | 1.472 / 1.516 ms |
| added by generation (hold + lead + alignment) | 16.98 + 2.01 + 3.65 = 22.64 ms | 16.68 + 2.02 + 2.78 = 21.48 ms |
| arrival lag (USB delivery) | 12.21 ms (11.58) | 6.77 ms (6.19) |
| a real frame shown after its capture timestamp | 40.44 ms | 35.59 ms |

No added d. The alignment term depends on where the source's phase falls against the refreshes in each run; the rest of the
difference is the card's USB delivery of twice the bytes per frame.

## 6. How the owner checks HDR by eye

1. **Patterns on screen** (each 10 s in a window by default; Escape ends it; keep fullscreen runs of `peak` short):
   - `fmp hdr-pattern ramp --fullscreen`: every PQ code left to right, ticks at 0.01 … 10,000 nits. Correct: smooth, no
     bands; black at the left; brightening up to the display's peak (or KWin's compression), flat beyond. Steps or
     banding in the dark third would mean a lost bit.
   - `fmp hdr-pattern peak`: patches 100 … 10,000 nits, each with an inner square 25% brighter. Correct: each patch
     brighter than the one before up to the display's limit; the inner square vanishes where the display (or KWin) clips.
   - `fmp hdr-pattern bars`: BT.2020 bars at 203 nits and at 1,000 nits. Correct: saturated (deeper than sRGB red and
     green), the top row at about the brightness of SDR white (with KWin's SDR brightness at 203 nits; with 440 it is
     brighter, section 3), no hue shift between the rows.
   - `fmp hdr-pattern black --fullscreen`: near-black steps. Correct: the background truly black (on an OLED, off); the
     first steps may vanish, the rest distinct.
2. **Against the PS5 plugged straight into the monitor.** Run the same scene both ways, the console's HDR settings
   unchanged. Expect the same colours and contrast; brightness matches only with KWin's SDR brightness at 203 nits for that
   output (otherwise the app's picture is brighter by that ratio, and highlights compressed by KWin). A washed-out, grey
   picture means PQ shown as SDR (press H); a dark, harsh picture with blinding whites means SDR shown as PQ (press H).
3. **The PS5's own "Adjust HDR" screens through the app** (Settings → Screen and Video → Video Output → Adjust HDR): on each
   of the three screens the symbol should disappear at a setting close to the one it needs with the monitor direct. With
   KWin's SDR brightness above 203, expect the symbol on screens 1 and 2 to disappear at a lower setting (KWin's scaling lifts
   it into the compressed range): a sign of the anchoring above, not of the app. Calibrating the PS5 through the app makes the
   console target what KWin then shows; set `--hdr-peak-nits` to the peak that calibration lands on.
4. `fmp hdr-pattern --check` at any time: the numbers above, in 2 s.

## Tests

New: `HdrPresentTests` (Vulkan, 10: codes, linear-light scaling at four ratios, a bright edge, SDR 8/10-bit, metadata,
following frame colours with 12 switches, peak and near-black patches), `HdrFollowTests` (Sources.V4l2, 6: the depth probe
and follower, the two colours and metadata options, CLI, a vivid capture switched by request, a whole vivid session switched
four times while playing), `HdrPatternTests` (Gpu, 8: PQ arithmetic and the patterns, the YCbCr encoding against the
capture's reference, `fmp hdr-pattern --check` in-process, the metadata options, usage errors). Changed: the 10-bit scaled
present test now expects linear light; the reconfigure test expects 10-bit SDR content to pass through to SDR; the capture
CLI tests expect `follow` and the new help and start-up lines.

Whole solution, once (`dotnet test FrameMotionPlus.slnx -c Release`): Core 681, Platform 100 + 1 skip, Audio 70 of 71,
Cuda 144, Vulkan 108, Gpu 129, Vfx 92 + 1 skip (the "no setting" lookup test skips with `$FMP_VFX_SDK` set), Sources.File
154, Pipeline 141 of 142, Sources.V4l2 121. The two failures were timing tests outside this phase, both passing on their
rerun: `WindowedTests` (10 late slots the display caused, the known intermittent case) and the audio loopback's click
delay at 35 ms (37.7 ms worst; it ran alongside the Pipeline tests). The new classes passed twice more on their own.

## Changes outside the obvious

- `src/FrameMotionPlus.Core/Frames/ColorMetadata.cs:90`, `:145`: `MasteringDisplay.Bt2020D65`, `ColorMetadata.Hdr10WithPeak`;
  `ColorMetadata.Hdr10` now carries BT.2020/D65 1000-nit mastering and MaxCLL 1000, MaxFALL not stated (was P3/D65,
  1000/400). `src/FrameMotionPlus.Core/Abstractions/IPresenter.cs`: `PresenterConfig.OutputColor` documented as the
  content's colour.
- `src/FrameMotionPlus.Pipeline/PacingLoop.cs:568`: each present request carries its newest real frame's colour.
- `src/FrameMotionPlus.Pipeline/PlaybackSession.cs:275`: the presenter follows frame colours; `:123` `RequestCaptureColor`
  (H/A keys and tests); `:649` the pre-roll's requests carry the source's colour.
- `src/FrameMotionPlus.Vulkan/Windowing/SdlWindow.cs`, `SdlNative.cs`: H and A key events.
- `src/FrameMotionPlus.App/Cli/CaptureCommand.cs`: `FMP_CAPTURE_SWITCH_EVERY_S` diagnostic (in `--help`, beside
  `FMP_AUDIO_LOG`).

## What `docs/implementation-plan.md` and the design doc should change (not edited)

- Phase 7, "Following the console": drop "both VFG instances (RGBA8 and RGB10A2) stay loaded": a switch never changes the
  ring's format (P010 stays RGB10A2, 10-bit SDR included), so no VFG switch is needed (measured: rebuild ≤ 0.66 ms, 0 missed).
- Phase 7: record that KWin anchors a PQ window's 203-nit reference white to the output's SDR brightness, so "PQ code values
  untouched" holds to the swapchain, not to the panel, unless the owner sets SDR brightness to 203.
- Design doc, "HDR input": the card's detection is by sample depth (unverified for SDR) with a manual switch; "swap on the
  first frame of the new format" is what is built, without a black flash (no frames dropped at a switch).
- Design doc, "HDR output": "metadata ... the display's peak when the source has none" became settable defaults
  (`--hdr-peak-nits`), since the console's calibration, not the display, decides its peak.
- Phase 5's "Found" list: the ffmpeg CLI cannot capture this card's P010 (it falls back to NV12); the card's NV12 of an HDR
  signal is already SDR.

## Known issues / not done

1. **SDR detection unverified.** The sample-depth rule for SDR has not seen an SDR signal from this console (it was in HDR
   throughout). Check once: put the console in SDR (or a game without HDR with HDR set to "On when supported"), run
   `fmp capture --mode 4k30-hdr --windowed`, and read the summary's "sample depth of the frames": if 8-bit frames appear and
   the colour line says SDR, the detector works; if they read 10-bit, follow cannot see SDR on this setup and H is the way.
2. **KWin's anchoring** (section 3): PQ is shown scaled by the output's SDR brightness / 203 and compressed above its peak.
   Not fixable from Vulkan; the owner can set SDR brightness to 203 nits on the gaming output. Unmeasured without a meter.
3. Requests made before the capture's next frame collapse into the last (two quick presses of H before a frame arrives
   count once).
4. Chroma siting of the card's 4:2:0 is assumed (left); there is no `--siting` option on `fmp capture` (the source has the
   override).
5. HLG files are shown on an SDR output (HLG's backward-compatible picture); converting HLG to PQ is not built.
6. The PQ→SDR placeholder for a display without HDR10 stays a placeholder (deferred with the conversions).
7. VFG on a high-contrast edge of a static HDR picture changes a few pixels by up to 29 codes (0.3 to 2.3% of pixels);
   transfer and range are kept (section 2).
8. `FMP_CAPTURE_SWITCH_EVERY_S`, like `FMP_PLAY_FAULT`, is a diagnostic compiled into the release binary.
9. Fullscreen direct scanout of the PQ window, the panels' own tone mapping, and the light output are not established.
