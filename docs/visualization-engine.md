# Visualization Engine Architecture

> **Status: foundation document, partially superseded.** The D3D11 → D3D9Ex → WPF D3DImage bridge is replaced by a WinUI 3 SwapChainPanel with a composition swap chain and a dedicated render thread (ADR-002). The sub-10 ms latency figure is restated as a measurable one-refresh target with look-ahead compensation (ADR-012). Analysis, feature extraction, shader and quality-scaling sections remain valid; presets are data-driven per ADR-009. The next section is **as built** and supersedes the HLSL sketches further down, which describe constant buffers the engine does not have. See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

## Preset format and constant-buffer contract (as built, E4-S3)

A preset is a directory: `preset.json` plus the HLSL it names. The core scans one preset root at renderer creation — `MPCORE_PRESET_ROOT` when set, otherwise `presets/` beside `mpcore.dll` — and compiles a preset's shader only when it is selected. Nothing is compiled ahead of time and nothing is cached on disk; `D3DCompile` on a preset of this size costs single-digit milliseconds.

The core also carries **one preset compiled into it**, `builtin-bars`. It is always first in the catalogue and is what a renderer starts on, so a missing or empty preset root costs the user a choice rather than a picture — and `mp_renderer_set_preset` always has a previous preset to fall back to. It is not one of the four built-ins of ADR-009; those are files, and E4-S4/E4-S5 write them.

```json
{
  "schema": 2,
  "id": "spectrum-bars",
  "name": "Spectrum Bars",
  "shader": "spectrum-bars.hlsl",
  "vertex_entry": "VSMain",
  "pixel_entry": "PSMain",
  "topology": "trianglelist",
  "vertex_count": 6,
  "instance_count": 64,
  "clear": [0.04, 0.04, 0.06, 1.0],
  "parameters": [
    { "name": "gain", "default": 1.0, "min": 0.0, "max": 4.0 }
  ]
}
```

`id` and `name` are required; everything else has a default. `id` is 1–63 bytes of `[A-Za-z0-9._-]` and `name` at most 127, because both have to fit `mp_preset_info`. `shader` must name a file beside `preset.json` — absolute paths and anything containing `..` are refused rather than resolved, because a preset is data and will one day be data a user downloaded. At most 16 parameters; a `default` outside `min`..`max` is clamped to it. A manifest that does not parse, or whose shader is not there, is skipped with a warning in the log: one bad preset must not cost the user the others.

Every preset sees the same resources and declares none of its own:

```hlsl
cbuffer Frame : register(b0) {
    float4 viewport;   // x = width px, y = height px, z = 1/width, w = 1/height
    float4 timing;     // x = seconds since the renderer started, y = seconds since the last frame,
                       // z = frame index, w = mp_analysis_frame.sequence (0 when nothing has played)
    float4 level;      // x = rms, y = peak, z = spectral centroid Hz, w = harmonic ratio
    float4 counts;     // x = onset (0 or 1), y = band count, z = spectrum bins, w = waveform samples
    float4 bands[3];   // the 10 octave bands in .x .y .z .w order; the last two floats are unused
    float4 params[4];  // the preset's own parameters, in the order preset.json declares them
    float4 theme[4];   // schema 2: the shell's theme, primary / secondary / accent / background, each RGBA
};
Buffer<float> Spectrum : register(t0);   // 1024 magnitudes, full-scale sine = 1.0
Buffer<float> Waveform : register(t1);   // 512 mono samples, the newest hop
```

What those numbers are, now that E4-S2 fills them (they were zero when this contract was written): `level.z` is the magnitude-weighted mean frequency of the spectrum in Hz, 0 in silence. `level.w` is one minus the spectral flatness — 0 to 1, how much of the spectrum is tone rather than noise, about 1.0 for a sine and 0.15 for white noise; it is not a count of harmonics, so an inharmonic bell reads high too. `counts.x` is 1 on the hop a transient was detected in and 0 for the three hops after it, so one drum hit is one flag; its resolution is the hop, meaning "somewhere in the 10.67 ms this frame covers". `bands` are ten octaves centred on the ISO 31.5 Hz–16 kHz series, partitioning the spectrum between them and scaled the same way the spectrum is: a full-scale sine inside a band reads 1.0 there. They are not additive — a tone on a band edge splits between two bands in quadrature — so a preset that sums them to get a level should use `level.x` instead.

Both SRVs are bound to the vertex and the pixel stage, so a preset may drive geometry or colour from either. The spectrum and waveform are buffers rather than `cbuffer` arrays because HLSL packs a float array one value per `float4` register: 1024 bins would cost 16 KB of constant buffer to carry 4 KB of data. The renderer polls `mp_analysis_try_get_latest` once per frame and only re-uploads the two buffers when the sequence has moved; `b0` is written every frame because time has.

The contract is the versioned part: `schema` is **2** as of E4-S6, and a preset that names a schema this build does not know is refused rather than guessed at. Adding a field to the end of `b0` is a schema bump, not a silent change.

**`theme` is the renderer-wide palette (E4-S6), and appending it is what made the schema 2.** `mp_renderer_set_theme` carries four RGBA colours — primary, secondary, accent, background, in `mp_theme_colors`' own order — and they reach every preset here, unchanged by a preset switch. That is the difference from a parameter: a parameter belongs to one preset and returns to its default when another is selected, while the theme outlives both, because the shell's background gradient does not change colour when someone picks a different visualizer. Channels outside 0..1 are clamped; a channel that is not a finite number is `MP_E_INVALID_ARG` and nothing changes. Until the shell sets one every channel is zero, so **alpha 0 is how a preset reads "no theme"** — the same shape as `art_*`'s −1. Sixteen floats are *not* sixteen more entries in `param_values_`: those are independent relaxed atomics and four colours stored through them could be read half-applied, so the theme crosses to the render thread the way the analysis override does, as one atomic generation the render thread compares per frame with the lock taken only on the frame after a change.

**Schema 1 still loads, and that is the point of the rule at the top of `mpcore.h` applied to a different contract.** Nothing before `theme` moved, so a schema 1 preset's shader declares a shorter `cbuffer` block and reads exactly the bytes it always read out of a buffer that is merely longer. `mpcore.tests`' `solid-green` and `solid-blue` fixtures stay at schema 1 and keep drawing, which is the proof; the four shipped presets are at schema 2 and their golden images are unchanged at max channel delta 0 over 230 400 pixels, which is the other half of it — appending to `b0` disturbed no picture. The four do not *read* `theme` yet: what E4-S6 owed them is the field and the palette in it, and repainting a preset from the theme would change what each one looks like, which is a preset decision and not this story's.

**Error reporting.** `mp_renderer_set_preset` compiles on the *calling* thread, and hands the render thread either a finished preset or nothing at all. A shader that does not compile therefore returns `MP_E_D3D` with the compiler's own first diagnostic in `mp_last_error` — file, line, error code and message — while the preset that was already drawing keeps drawing. On the managed side that is `PresetCompilationException.CompilerMessage`, which is what the preset switcher shows.

## The presets that ship (as built, E4-S4 and E4-S5)

All four of ADR-009's built-ins, in `presets/` at the top of the repository, all written against the contract above.

| id | name | draws | parameters (default, range) |
| --- | --- | --- | --- |
| `spectrum-bars` | Spectrum Bars | one instanced quad per bar over a logarithmic slice of the 1024-bin spectrum | `bars` (64, 8–128), `smoothing` (0.35, 0–1), `colour` (0, 0–2), `gain` (1.0, 0.25–4) |
| `waveform` | Waveform | a ribbon through the 512-sample mono waveform, one instanced quad per segment, thickened in pixels along the segment normal | `points` (192, 16–256), `smoothing` (0.2, 0–1), `colour` (0, 0–2), `gain` (1.0, 0.25–4), `thickness` (2.5 px, 1–8) |
| `radial-spectrum` | Radial Spectrum | one instanced wedge per ray around a hub, over the same logarithmic slice of the spectrum, fitted to the minor screen dimension so it stays circular | `rays` (72, 12–128), `smoothing` (0.35, 0–1), `colour` (0, 0–2), `gain` (1.0, 0.25–4), `hub` (0.22, 0.05–0.5 of the half-height) |
| `ambient-glow` | Ambient Glow | one screen-covering quad; three soft lobes of light in the pixel shader, one per octave-band group, coloured from the album art palette when there is one | `spread` (0.6, 0.25–1.2), `smoothing` (0.4, 0–1), `colour` (0, 0–2), `gain` (1.0, 0.25–4), `glow` (1.0, 0.25–**1.0**), `art_primary` / `art_secondary` / `art_accent` (−1, −1–16777215) |

**Ambient Glow is one quad and not three, because the pipeline sets no blend state.** Overlapping geometry overwrites rather than adds — which is why the Waveform's soft edge is a lerp toward its clear colour rather than a fade to transparent — and a wash has to be additive to be a wash. So its `instance_count` is 1 and the whole picture is pixel-shader arithmetic, which makes its cost per-pixel rather than per-primitive.

**The album art palette reaches Ambient Glow as parameters, not as a theme (AC-124).** `art_primary`, `art_secondary` and `art_accent` each carry one sRGB colour packed as `r*65536 + g*256 + b` — exact in float32, so the preset stays deterministic and its golden image stays reproducible — and a negative value, which is the default, means "no art". One float per colour rather than three is deliberate: parameters are independent relaxed atomics read once a frame, so three channels set in sequence could be read half-applied. `AmbientGlowPalette` in `Tunqio.Core` turns E3-S7's `ArtPalette` into those three values and sets them through `IVisualizationHost.SetParameter`; what is still missing is the caller, because nothing in the shell attaches the visualizer to Now Playing yet. This is deliberately **not** `mp_renderer_set_theme`, which E4-S6 has since implemented as the renderer-wide theme described above: that is four colours reaching every preset and the shell's own gradient through a new field in `b0`, and the two remain different things - the theme is the window's palette and `art_*` is one preset's colour source. AC-124 asks for one preset's colour *source*, which this contract already expresses. "When available" is handled twice — `−1` for a track with no art, and a per-colour fallback in the shader for a palette too dark to glow, since a near-black sleeve quantises to near-black entries and amplifying one of those is not a glow but amplified quantisation noise.

A preset's `instance_count` is fixed in its manifest, so `bars`/`points`/`rays` is a *maximum* the manifest declares (128, 256 and 128) and the parameter selects how many of those instances draw: an instance past the count emits six coincident vertices and rasterises nothing. `colour` is the colour **source** in all four, rounded to a mode — 0 position (along the spectrum, the ribbon's own excursion, around the wheel, or the lobe's own place in the ramp), 1 loudness (`level.y`), 2 spectral centroid (`level.z`, log-mapped over 50 Hz–12 kHz). Ambient Glow's `glow` is the one parameter whose range is not symmetric about its use: it tops out at its default of 1.0 and can only attenuate, because 1.0 is where the flash measurement is taken and a parameter must not be able to push a preset past the accessibility contract.

**`smoothing` is spatial, not temporal, and that is a limitation of the contract rather than a choice.** In Spectrum Bars and Radial Spectrum it widens a bar's or a ray's slice into its neighbours and slides the reading from that slice's peak toward its mean; in Waveform it is the width of a box filter along the hop; in Ambient Glow it slides a band group's reading from its peak toward its mean. What a visualizer usually means by smoothing — an attack/decay envelope — needs state that survives between frames, and a preset has none: `b0` and the two SRVs are the whole surface and there are no UAVs. Temporal smoothing would have to be the renderer's, applied to the spectrum before it is uploaded, which would make it a property of the renderer rather than of one preset. It is not in this story.

**All four presets are deterministic while something is playing.** None reads `timing` on the `timing.w > 0` branch, so the picture is a pure function of (preset, parameters, `mp_analysis_frame`, size) — which is what lets `native/mpcore.tests/fixtures/golden/*.png` be checked-in images rather than screenshots. The clock is used only by the idle animation behind `timing.w == 0`, where there is no spectrum to draw, and every idle animation runs below 0.4 Hz.

**All four are flash-safe by construction, and it is measured.** The accessibility contract (`docs/ui-screens-and-flows.md`) is that a preset never flashes above 3 Hz full-field luminance change; the analysis stream runs at ~93 Hz, so what a preset must avoid is changing the *field* that much, not changing quickly. Three of them bound the flashing *area*: in Spectrum Bars brightness is a function of screen height rather than of position within a bar, so the lower field stays near the clear colour whatever the audio does; the Waveform is a line a few pixels thick; in Radial Spectrum brightness falls with screen radius, so the bright part is a small disc around the hub whose size does not depend on the audio at all and what the music changes is how far the dim outer part of each ray reaches. Ambient Glow cannot use that argument — it covers the whole field — and uses the other one: the wash is saturated per channel and then scaled, so the brightest colour it can emit is 0.0742 relative luminance against the 0.10 WCAG 2.3.1 counts as a flash, whatever the audio does and whatever the album art is.

Measured between digital silence and full scale in every bin, which bounds any pair of real consecutive frames, at the 640×360 the test renders:

| preset | flashing area (25% allowed) | mean full-field luminance change (0.10 threshold) | largest single-pixel change |
| --- | --- | --- | --- |
| `spectrum-bars` | 7.02% | 0.0275 | 0.9211 |
| `waveform` | 1.11% | 0.0029 | 0.4546 |
| `radial-spectrum` | 1.72% | 0.0064 | 0.5298 |
| `ambient-glow` | 0.00% | 0.0255 | 0.0723 |

The Waveform's is a line of a fixed pixel thickness, so a taller field makes it smaller still. Ambient Glow's zero is the reason `measure_flash` reports the largest single-pixel change as well as the area: a zero area with no reading beside it is an absence, not a measurement.

**Parameter discovery is not on the ABI yet.** `mp_preset_info` carries `id` and `name` only, so a settings page cannot learn that `spectrum-bars` has a `bars` in 8–128 without being told out of band; `mp_renderer_set_param` takes a name and a value and that is the whole surface. E4-S9's settings screen needs the metadata, and adding it is an ABI minor. It matters more now than it did at E4-S4: Ambient Glow's three art parameters are set by code rather than by a person, and a settings page that listed every declared parameter would offer a user three raw packed integers to type.

**A golden image is only as sensitive as the picture's dynamic range.** Ambient Glow's whole picture lives in the bottom eighth of the byte range, because flash safety caps every channel it can emit at 0.28 of full scale. A 1% change to that cap moves 146443 of 230400 pixels but each by a single byte, which passes the 6-of-255 per-channel tolerance and passed the 0.5 mean tolerance the other three use — a golden test that could not fail, found by perturbing a constant rather than by reasoning, exactly as E4-S4 found that 320×180 was too small. Its mean bound is therefore 0.06, which catches the smallest perturbation tried by 2.4× while the real comparison reads 0.0000 in Debug, Release and ASan alike.

The visualization engine provides real-time audio-reactive graphics through D3D11/WPF integration, achieving sub-10ms latency from audio sample to visual update while maintaining 60fps performance.

## Audio-reactive theming (as built, E4-S6)

The window's background follows the music, and the same four colours go to the renderer. The whole mapping lives in `Tunqio.Core.Visualization` — `ReactiveThemeEngine`, `ReactiveContrast`, `ReactiveTheming` — which targets plain `net8.0` and knows nothing about a window, so the colours are tested by feeding synthetic `AnalysisFrame`s in and reading `ReactiveThemePalette`s out. What the shell adds is `ReactiveThemeController` (the 30 Hz poll and the three off switches) and `ReactiveThemeLayer` (a Composition linear gradient on a sprite visual behind every panel).

**The mapping.** Spectral centroid, log-mapped over 50 Hz–12 kHz — the same range the presets' `colour = 2` mode uses, so a preset and the window agree about what "bright" means — sweeps the hue 120° from indigo to amber. One minus spectral flatness sets saturation, so a tone is more coloured than noise. RMS, read as a meter reads it (−45 to −6 dBFS), sets lightness. The album art's dominant hue is blended in at 0.45 on the unit circle, and a near-grey sleeve is ignored because the hue of a grey is whatever survived quantisation.

**The smoothing is exponential in wall-clock time**: `alpha = 1 - exp(-dt / tau)`, so ten steps of 10 ms land where one step of 100 ms does and the setting means the same thing on a fast machine and a slow one. `ui.reactiveSmoothing` 0..1 maps onto a time constant of 0.5 s to 4 s (0.15, the default, is 1.025 s). The floor is not zero and it is a measurement rather than taste: at 0.25 s the light theme moved 0.1201 of relative luminance in a third of a second, over WCAG 2.3.1's 0.10, and the same figure at 0.5 s with the light theme's chroma reined in is 0.0713. Hue is smoothed as (cos, sin) rather than as degrees, because a scalar EMA between 350° and 10° sweeps the long way through every other colour.

**`mp_analysis_frame.discontinuities` deliberately changes nothing, and the design is what makes that safe.** The count moving means the analysis restarted and anything computed *across* frames is not continuous. So the engine computes nothing across frames: its target is a pure function of one frame — no running maximum, no flux, no beat history — and the only thing it carries is the colour currently on screen. "Start again" on that state would mean throwing away what is being displayed and snapping to the new target, which is the full-field step the smoothing exists to prevent. The count and the number of restarts are surfaced on `ReactiveThemeEngine` so the decision is visible rather than silent, and a test pins it: the same frames with the count moving every 40 of them produce a byte-identical colour path.

**The contrast guarantee is a bound, not a search.** WCAG contrast is a ratio of two relative luminances and relative luminance is a linear combination of linearised channels, so "text reads on this background at 4.5:1" is exactly "the background's luminance is outside an interval computed from the text's". Multiplying the linear channels by `k` multiplies luminance by `k` and mixing toward white by `t` moves it to `L + t(1 - L)`, both exactly, so `ReactiveContrast.Constrain` is closed form: it lands *on* the bound rather than near it, and scaling in linear light keeps the chromaticity the music chose. Converting back to bytes rounds toward the safe side — down when darkening, up when lightening — so quantisation cannot undo it.

That is what lets AC-126 be checked rather than sampled. `Constrain` is a function of a colour and a set of foregrounds and of nothing else — not of which surface, not of how the mapping arrived there — so every reactive surface of a theme is covered by walking the whole range once against that theme's foreground set. The test walks all 16 777 216 sRGB colours for each theme; the worst result is 4.5000:1 and there are no violations, and the same sweep with the guarantee removed finds 24 929 936 of 33 554 432 pairs under 4.5:1 in the dark theme, so it is a check that can go red. The foreground tokens on reactive surfaces are opaque by decision: an alpha'd foreground resolves against the very colour that is moving, and a bound on the background alone cannot then guarantee the pair.

**Stopping.** `ui.reactiveTheming` off, Windows asking for reduced motion (`UISettings.AnimationsEnabled`) and a high-contrast theme each stop it and put the static theme back. The accessibility change event stops it where it is raised — measured at 0 ms on the injected clock — and the 30 Hz poll re-reads both switches anyway, so a notification that is missed or marshalled late costs at most one poll interval, measured at 33.333 ms. The event alone would not be enough: a paused player publishes no frames, and a theming that only woke on a frame would sit on the last chord's colour for as long as the music stayed paused.

## Adaptive quality (as built, E4-S7)

`mp_renderer_set_quality` is implemented (ABI 0.14) over a controller in `native/mpcore/src/render/quality.h`.
The controller is a pure object — it takes a clock reading and a frame cost and answers with a tier — which is
what lets AC-129's "without oscillating" be measured against a synthetic series rather than waited for on a
machine somebody else is also using. `mp_render_stats` grew a tail carrying what it decided and what it decided
it on; the diagnostics overlay shows both.

**The lever is render scale, and it is the only one.** The story asked for render scale, update rate and preset
complexity. T-148 measured the four shipped presets on WARP and only one of them can exhaust a rasteriser at
all: `ambient-glow` at 150 fps against 1331–2596 for the other three, because it is the only one whose cost is
per-pixel rather than per-primitive. A lever measured in pixels is therefore the only one that helps the preset
that needs helping — fewer bars would save nothing on the wash. Update rate is not a lever at all here:
presenting less often does not make a frame cheaper, and a tier that deliberately halves the frame rate reads,
to any controller that measures the frame interval, as a tier that is failing its budget. Preset complexity has
no representation in the preset schema and would need a schema 3 bump to buy nothing on the preset in trouble.

The tiers draw at 1.0, 0.75 and 0.5 of the panel. The back buffer stays the panel's size and the picture is
drawn into a rectangle of it, which the compositor stretches out — `IDXGISwapChain2::SetSourceSize` on the
flip-model swap chain that is already there. That is one call: no intermediate render target, no upscale pass,
no sampler, nothing new for AC-118's device-reference count to see, and going back up costs nothing because the
buffers never changed size. The preset sees the rendered size in `b0.viewport`, not the panel's, because the
Waveform's thickness and the Radial Spectrum's hub are in pixels and a half-scale picture drawing a half-width
line the compositor then doubles would be a tier that changed the composition rather than the resolution.

**The cost signal is a GPU timestamp pair, not the frame interval.** The frame-to-frame interval is not a
measurement of how much work a frame is: with vsync it is pinned to the refresh whether the GPU is idle or
drowning, so a controller reading it would be blind on exactly the path the product ships. A
`D3D11_QUERY_TIMESTAMP` pair inside a disjoint query, read two frames behind so `GetData` never stalls the
loop, is the work itself. WARP supports them, and the tests print which source was used. The interval is the
fallback for a device that will not make the queries, and `mp_render_stats.cost_source` says which is in force.
Two things a first cut of this got wrong and the tests caught: a frame cheaper than the timestamp clock can
resolve comes back as two equal stamps, and reporting that as "no reading" rather than as "the smallest cost
there is" stops the controller for ever; and an always-disjoint device has to count as a miss, or the fallback
never fires.

**The hysteresis is a prediction, not a threshold pair, and T-148 is why.** The sketch further down this file
drops above 20 ms, raises below 14 ms and refuses to decide twice inside two seconds. A cooldown bounds how
*fast* a controller oscillates, not *whether* it does: if the cost at High is over the drop threshold and the
cost at Medium is under the raise threshold, that controller alternates for ever, one visible step every two
seconds. Whether that trap is reachable depends on the ratio between the cost at one tier and the next, and
that ratio is a property of the preset — 1.78 for a per-pixel one going 0.75 → 1.0, about 1.0 for a
per-primitive one. A raise threshold tuned on the second is tuned on the wrong preset. So the raise rule asks
about the tier it is thinking of moving to rather than the one it is on:

> raise only when `smoothed_cost × gain(tier → tier+1) ≤ budget × 0.8`

`gain` starts at the analytic worst case (the area ratio, which is the pure per-pixel case and an upper bound
for any preset) and is replaced by the ratio the controller measured last time it crossed that boundary. A
per-primitive preset teaches it 1.0 within one transition and gets a responsive controller; `ambient-glow`
teaches it 1.78 and gets a cautious one. Neither is tuned by hand. Measured, in `[quality]`: two controllers at
Low both showing 10.00 ms, well inside a 16.667 ms budget and inside the 13.33 ms a threshold pair would raise
on — the per-primitive one climbs to High and the per-pixel one stays at Low, and both are right.

The prediction is a guarantee only while the estimate holds, so there is a second bound that does not depend on
it: **a raise undone inside ten seconds doubles the wait before the next raise across that boundary**, capped
at sixty seconds and charged per boundary rather than per raise. That makes the number of tier changes in a
window logarithmic in the window instead of linear in it, whatever the cost series does — which is what AC-129
is actually measured as. On the trap series above (18 ms at High, 10.13 at Medium, against 16.667) over two
minutes: the controller as shipped changes tier **once** and stays at Medium; with the prediction removed but
the escalation kept, **11 times**, exactly the derived bound; with both removed — the sketch — **65 times**. On
300 s of a machine flipping between 40 ms and 12 ms every 8 s, which no predictor can be right about: **8**
changes with the escalation and **75** without, against a derived bound of 34.

T-148's other consequence is that the windows are in seconds and not in frames. `ambient-glow` produces 150
samples a second on WARP and `radial-spectrum` 2287, so a dwell counted in frames would be a dwell 15 times
longer in seconds on one of them — while AC-128 and AC-129 are both stated in seconds. Hence
`alpha = 1 - exp(-dt / tau)` for the smoothing, the same form E4-S6 uses, and dwells in wall-clock seconds.
Measured: the time to reach Low differs by 31.3 ms (1.6%) between those two sampling rates, against the 40 ms
that six decision boundaries quantised to one frame at 150 Hz allow.

The numbers: budget one refresh at 60 Hz, EMA time constant 0.25 s, 0.2 s settle after a change (during which
samples are discarded and after which the first sample becomes the EMA outright rather than being blended with
the previous tier's), 0.75 s over budget before a drop, 2.0 s under the predicted budget before a raise.

**`quality_changes` counts the controller's own decisions.** A tier the caller pinned is not one, and a preset
switch or a resize does not clear it — the first would let a person's choices bury the signal, the second would
let a switch hide thrashing it may itself have started. What a preset switch and a resize *do* clear is
everything the controller learned about the cost model, because it is a different cost model: T-148 measured
the difference between two of these presets at 10 to 17 times, and carrying a gain estimate across that is
worse than having none.

**How the on-WARP half is tested without asserting a frame rate.** Whether WARP misses 60 fps at 1080p depends
on who else is using the CPU — the same preset has been observed at 150 fps idle, 51.9 under a concurrent build
and 21.4 on a shared runner (T-150) — so a test that needs it to miss is a test that goes red when a machine is
busy. Instead the test *measures* what this machine costs at each tier and then sets a budget strictly between
two of those measurements, so the drop is forced on any machine at any load while every frame time the
controller reads is real. On this dev machine that reads: `ambient-glow` at 1920×1080 on WARP, from a timestamp
pair, 6.99 ms at High, 4.99 at Medium (1.40×) and 2.10 at Low (3.33×) — which is also the proof that the lever
does what the preset's shape says it should. AC-128 reaches Low in 1.74 s of a four-second bound; AC-129, with
the surface cut to a sixteenth of the pixels and the budget untouched, climbs back to High in 4.77 s of ten, in
exactly two changes, and holds it.

## D3D11/WPF Integration Strategy

### DXGI Surface Sharing Implementation

The core challenge of integrating hardware-accelerated D3D11 rendering with WPF involves sharing GPU textures between rendering contexts without performance penalties.

```cpp
class D3D11WPFBridge {
private:
    ID3D11Device* d3dDevice_;
    ID3D11DeviceContext* d3dContext_;
    IDirect3DDevice9Ex* d3d9Device_;
    ID3D11Texture2D* sharedTexture_;
    IDXGISurface* dxgiSurface_;

public:
    HRESULT Initialize(HWND hwnd, int width, int height) {
        // Create D3D11 device and context
        CreateD3D11Device();

        // Create shared texture with specific bind flags
        D3D11_TEXTURE2D_DESC textureDesc = {
            .Width = width,
            .Height = height,
            .MipLevels = 1,
            .ArraySize = 1,
            .Format = DXGI_FORMAT_B8G8R8A8_UNORM,  // WPF-compatible format
            .SampleDesc = {1, 0},
            .Usage = D3D11_USAGE_DEFAULT,
            .BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            .CPUAccessFlags = 0,
            .MiscFlags = D3D11_RESOURCE_MISC_SHARED
        };

        HRESULT hr = d3dDevice_->CreateTexture2D(&textureDesc, nullptr,
                                               &sharedTexture_);

        // Get shared handle for WPF consumption
        IDXGIResource* dxgiResource;
        hr = sharedTexture_->QueryInterface(&dxgiResource);
        HANDLE sharedHandle;
        hr = dxgiResource->GetSharedHandle(&sharedHandle);

        // Create D3D9Ex wrapper for WPF D3DImage
        CreateD3D9ExSurface(sharedHandle);

        return hr;
    }

    void Present() {
        // Render to D3D11 shared texture
        RenderVisualization();

        // Flush D3D11 context to ensure GPU completion
        d3dContext_->Flush();

        // WPF will read from shared surface via D3DImage
        NotifyWPFUpdate();
    }
};
```

### WPF Integration via D3DImage

```csharp
public class D3DVisualizationControl : D3DImage, INotifyPropertyChanged
{
    private IntPtr sharedSurfaceHandle;
    private D3D9ExSurface d3d9Surface;

    public void UpdateSharedSurface(IntPtr handle)
    {
        sharedSurfaceHandle = handle;
        CreateD3D9Surface();

        // Set WPF back buffer to shared D3D9 surface
        SetBackBuffer(D3DResourceType.IDirect3DSurface9, d3d9Surface.NativeSurface);
    }

    private void CreateD3D9Surface()
    {
        // Create D3D9Ex device wrapper
        var d3d9Ex = new Direct3DEx();
        var device = new DeviceEx(d3d9Ex, 0, DeviceType.Hardware, IntPtr.Zero,
                                CreateFlags.HardwareVertexProcessing |
                                CreateFlags.Multithreaded |
                                CreateFlags.FpuPreserve,
                                presentParams);

        // Open shared surface using handle from D3D11
        d3d9Surface = new D3D9ExSurface(device, sharedSurfaceHandle);
    }
}
```

## Real-Time Audio Analysis Pipeline

### FFT Processing Architecture

The visualization engine processes audio through a dedicated analysis pipeline optimized for real-time performance:

```cpp
class AudioAnalyzer {
private:
    static constexpr size_t FFT_SIZE = 1024;
    static constexpr size_t OVERLAP_SIZE = FFT_SIZE / 2;
    static constexpr float SAMPLE_RATE = 48000.0f;
    static constexpr float UPDATE_RATE = 86.0f;  // ~11.6ms intervals

    // FFT processing context (Intel IPP or FFTW3)
    IppsFFTSpec_R_32f* fftSpec_;
    float* workBuffer_;
    float* windowFunction_;  // Pre-computed Hann window

    // Ring buffer for overlapped processing
    CircularBuffer<float> inputBuffer_;
    CircularBuffer<SpectrumData> outputBuffer_;

public:
    void ProcessAudioBlock(const float* samples, size_t sampleCount) {
        // Add samples to input buffer
        inputBuffer_.Write(samples, sampleCount);

        // Process overlapped FFT windows
        while (inputBuffer_.AvailableRead() >= FFT_SIZE) {
            ProcessFFTWindow();
        }
    }

private:
    void ProcessFFTWindow() {
        float timeData[FFT_SIZE];
        float freqData[FFT_SIZE / 2 + 1];

        // Read windowed audio samples
        inputBuffer_.Read(timeData, FFT_SIZE);

        // Apply Hann window to reduce spectral leakage
        ippsWinHann_32f_I(timeData, FFT_SIZE);

        // Perform forward FFT
        ippsFFTFwd_RToCCS_32f(timeData, freqData, fftSpec_, workBuffer_);

        // Extract visualization features
        SpectrumData spectrum = ExtractFeatures(freqData);

        // Write to output buffer for UI consumption
        outputBuffer_.Write(spectrum);

        // Advance input buffer by overlap amount
        inputBuffer_.Advance(OVERLAP_SIZE);
    }
};
```

### Feature Extraction for Visualization

The analyzer extracts multiple audio characteristics for different visualization purposes:

```cpp
struct SpectrumData {
    float spectralCentroid;     // Brightness: weighted mean frequency
    float rmsAmplitude;         // Overall energy level
    float harmonicRatio;        // Harmonic content vs. noise
    float frequencyBands[6];    // Octave band energy distribution
    float peakFrequency;        // Dominant frequency component
    uint64_t timestamp;         // Sample-accurate timing
};

SpectrumData AudioAnalyzer::ExtractFeatures(const float* spectrum) {
    SpectrumData data = {};
    data.timestamp = GetAudioSampleTime();

    // Calculate spectral centroid (brightness measure)
    float weightedSum = 0.0f, magnitudeSum = 0.0f;
    for (size_t i = 1; i < FFT_SIZE / 2; ++i) {
        float magnitude = std::abs(spectrum[i]);
        float frequency = (i * SAMPLE_RATE) / FFT_SIZE;

        weightedSum += magnitude * frequency;
        magnitudeSum += magnitude;
    }
    data.spectralCentroid = weightedSum / (magnitudeSum + 1e-10f);

    // Calculate RMS amplitude from time domain
    float sumSquares = 0.0f;
    for (size_t i = 0; i < FFT_SIZE; ++i) {
        sumSquares += timeData[i] * timeData[i];
    }
    data.rmsAmplitude = std::sqrt(sumSquares / FFT_SIZE);

    // Extract octave band energy distribution
    ExtractOctaveBands(spectrum, data.frequencyBands);

    // Calculate harmonic-to-noise ratio
    data.harmonicRatio = CalculateHarmonicRatio(spectrum);

    return data;
}
```

### Octave Band Analysis

```cpp
void AudioAnalyzer::ExtractOctaveBands(const float* spectrum,
                                     float* bands) {
    // Define octave band boundaries (Hz)
    const float bandBoundaries[] = {
        20.0f,    // Sub-bass
        60.0f,    // Bass
        250.0f,   // Low midrange
        2000.0f,  // Midrange
        6000.0f,  // High midrange
        20000.0f  // Presence
    };

    for (int band = 0; band < 6; ++band) {
        float lowFreq = (band == 0) ? 0 : bandBoundaries[band - 1];
        float highFreq = bandBoundaries[band];

        // Convert frequency to FFT bin indices
        int lowBin = static_cast<int>((lowFreq * FFT_SIZE) / SAMPLE_RATE);
        int highBin = static_cast<int>((highFreq * FFT_SIZE) / SAMPLE_RATE);

        // Sum energy in frequency range
        float energy = 0.0f;
        for (int bin = lowBin; bin <= highBin && bin < FFT_SIZE / 2; ++bin) {
            energy += spectrum[bin] * spectrum[bin];
        }

        bands[band] = std::sqrt(energy / (highBin - lowBin + 1));
    }
}
```

## Audio-Reactive UI System

### HSL Color Mapping

The UI system maps audio features to visual properties through HSL color space manipulation:

```cpp
class AudioColorMapper {
private:
    // Exponential moving average for temporal smoothing
    struct EMA {
        float alpha = 0.15f;  // Smoothing factor
        float value = 0.0f;

        void Update(float newValue) {
            value = alpha * newValue + (1.0f - alpha) * value;
        }
    };

    EMA spectralCentroidEMA_;
    EMA rmsAmplitudeEMA_;
    EMA harmonicRatioEMA_;

public:
    HSLColor MapAudioToColor(const SpectrumData& data) {
        // Update exponential moving averages
        spectralCentroidEMA_.Update(data.spectralCentroid);
        rmsAmplitudeEMA_.Update(data.rmsAmplitude);
        harmonicRatioEMA_.Update(data.harmonicRatio);

        // Map spectral centroid to hue (200Hz-8kHz → 240°-60°)
        float normalizedCentroid = std::clamp(
            (spectralCentroidEMA_.value - 200.0f) / 7800.0f, 0.0f, 1.0f);
        float hue = 240.0f - (normalizedCentroid * 180.0f);  // Blue to yellow

        // Map RMS amplitude to saturation (0-1 with 0.2 floor)
        float saturation = std::clamp(
            rmsAmplitudeEMA_.value * 0.8f + 0.2f, 0.2f, 1.0f);

        // Map harmonic ratio to lightness (more harmonics = warmer)
        float lightness = std::clamp(
            0.5f + (harmonicRatioEMA_.value - 0.5f) * 0.3f, 0.3f, 0.8f);

        return HSLColor{hue, saturation, lightness};
    }
};
```

### Performance-Optimized UI Updates

To maintain 60fps performance while processing audio-reactive updates:

```cpp
class VisualizationRenderer {
private:
    static constexpr float UPDATE_THRESHOLD = 5.0f;  // HSL units
    HSLColor lastColor_;
    std::atomic<bool> updatePending_{false};

public:
    void UpdateVisualization(const SpectrumData& audioData) {
        HSLColor newColor = colorMapper_.MapAudioToColor(audioData);

        // Only update if significant change occurred
        if (HSLDistance(newColor, lastColor_) > UPDATE_THRESHOLD) {
            QueueUIUpdate(newColor);
            lastColor_ = newColor;
        }
    }

private:
    void QueueUIUpdate(const HSLColor& color) {
        if (!updatePending_.exchange(true, std::memory_order_acquire)) {
            // Dispatch to UI thread via WPF Dispatcher
            wpfDispatcher_->BeginInvoke([=]() {
                ApplyColorToUI(color);
                updatePending_.store(false, std::memory_order_release);
            });
        }
    }

    void ApplyColorToUI(const HSLColor& color) {
        // Convert HSL to RGB for WPF brush creation
        RGBColor rgb = HSLToRGB(color);

        // Create hardware-accelerated gradient brush
        auto gradientBrush = std::make_shared<LinearGradientBrush>(
            Color::FromRgb(rgb.r * 0.8f, rgb.g * 0.8f, rgb.b * 0.8f),  // Darker
            Color::FromRgb(rgb.r, rgb.g, rgb.b)                         // Brighter
        );

        // Update UI element backgrounds
        nowPlayingPanel_->Background = gradientBrush;
        visualizationCanvas_->Background = gradientBrush;
    }
};
```

## D3D11 Shader-Based Effects

### Spectrum Visualization Shader

```hlsl
// Vertex Shader
struct VS_INPUT {
    float2 position : POSITION;
    float2 texCoord : TEXCOORD0;
};

struct VS_OUTPUT {
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

cbuffer SpectrumData : register(b0) {
    float4 frequencyBands[16];  // 64 frequency bins as float4
    float spectralCentroid;
    float rmsAmplitude;
    float harmonicRatio;
    float time;
};

VS_OUTPUT VertexMain(VS_INPUT input) {
    VS_OUTPUT output;
    output.position = float4(input.position, 0.0f, 1.0f);
    output.texCoord = input.texCoord;
    return output;
}

// Pixel Shader
float4 PixelMain(VS_OUTPUT input) : SV_TARGET {
    float2 uv = input.texCoord;

    // Sample frequency data based on X coordinate
    int freqIndex = int(uv.x * 64.0f);
    float amplitude = frequencyBands[freqIndex / 4][freqIndex % 4];

    // Create spectrum bar visualization
    float barHeight = amplitude * 0.8f + 0.1f;
    float intensity = step(1.0f - uv.y, barHeight);

    // Color mapping based on frequency and amplitude
    float3 baseColor = HSVtoRGB(float3(
        uv.x * 0.8f + 0.1f,  // Hue varies by frequency
        0.8f,                 // High saturation
        intensity             // Brightness based on presence
    ));

    // Add temporal animation using audio energy
    float pulse = sin(time * 10.0f * rmsAmplitude) * 0.1f + 0.9f;
    baseColor *= pulse;

    return float4(baseColor, 1.0f);
}
```

### Waveform Visualization

```hlsl
cbuffer WaveformData : register(b1) {
    float waveformSamples[512];  // Time-domain audio samples
    float waveformScale;
};

float4 WaveformPixelShader(VS_OUTPUT input) : SV_TARGET {
    float2 uv = input.texCoord;

    // Sample waveform based on X coordinate
    int sampleIndex = int(uv.x * 511.0f);
    float amplitude = waveformSamples[sampleIndex] * waveformScale;

    // Create waveform line
    float centerY = 0.5f;
    float waveY = centerY + amplitude * 0.4f;
    float distance = abs(uv.y - waveY);

    // Anti-aliased line rendering
    float lineWidth = 0.002f;
    float alpha = 1.0f - smoothstep(0.0f, lineWidth, distance);

    // Color based on amplitude
    float3 color = lerp(
        float3(0.2f, 0.4f, 0.8f),  // Blue for low amplitude
        float3(1.0f, 0.6f, 0.2f),  // Orange for high amplitude
        abs(amplitude)
    );

    return float4(color, alpha);
}
```

## Performance Optimization Strategies

### GPU Memory Management

```cpp
class VisualizationBufferManager {
private:
    ID3D11Buffer* spectrumConstantBuffer_;
    ID3D11Buffer* waveformConstantBuffer_;

    // Double-buffered GPU resources
    ID3D11Texture2D* renderTargets_[2];
    int currentBuffer_ = 0;

public:
    void UpdateGPUData(const SpectrumData& audioData) {
        // Map constant buffer for GPU update
        D3D11_MAPPED_SUBRESOURCE mappedResource;
        HRESULT hr = d3dContext_->Map(spectrumConstantBuffer_, 0,
                                    D3D11_MAP_WRITE_DISCARD, 0,
                                    &mappedResource);

        if (SUCCEEDED(hr)) {
            // Copy audio data to GPU-accessible memory
            SpectrumConstants* constants =
                static_cast<SpectrumConstants*>(mappedResource.pData);

            memcpy(constants->frequencyBands, audioData.frequencyBands,
                   sizeof(constants->frequencyBands));
            constants->spectralCentroid = audioData.spectralCentroid;
            constants->rmsAmplitude = audioData.rmsAmplitude;
            constants->time = GetCurrentTime();

            d3dContext_->Unmap(spectrumConstantBuffer_, 0);
        }
    }

    void SwapRenderTargets() {
        currentBuffer_ = 1 - currentBuffer_;  // Toggle between 0 and 1
    }
};
```

### Adaptive Quality Scaling

> **Superseded by E4-S7** — see "Adaptive quality (as built)" above. The three-tier shape and the render-scale
> fractions survived; the threshold pair below did not. A drop above 20 ms and a raise below 14 ms is a rule
> about the tier that is already drawing, and it alternates for ever on any preset whose cost between tiers
> spans that gap. On this repository's own trap case it changes tier 65 times in two minutes.

When frame rate drops below 60fps, the visualization engine implements automatic quality reduction:

```cpp
class QualityController {
private:
    float averageFrameTime_ = 16.7f;  // Target 60fps
    int currentQualityLevel_ = 2;     // 0=low, 1=medium, 2=high

public:
    void UpdateQuality(float frameTime) {
        // Exponential moving average of frame time
        averageFrameTime_ = 0.1f * frameTime + 0.9f * averageFrameTime_;

        if (averageFrameTime_ > 20.0f && currentQualityLevel_ > 0) {
            // Frame rate too low, reduce quality
            ReduceQuality();
        } else if (averageFrameTime_ < 14.0f && currentQualityLevel_ < 2) {
            // Frame rate stable, try increasing quality
            IncreaseQuality();
        }
    }

private:
    void ReduceQuality() {
        switch (--currentQualityLevel_) {
            case 1:  // Medium quality
                SetRenderResolution(0.75f);    // 75% resolution
                SetEffectComplexity(0.8f);     // Simpler shaders
                break;
            case 0:  // Low quality
                SetRenderResolution(0.5f);     // 50% resolution
                SetEffectComplexity(0.5f);     // Basic shaders only
                break;
        }
    }
};
```

This visualization engine architecture delivers professional-quality real-time audio visualization while maintaining optimal performance across diverse hardware configurations.