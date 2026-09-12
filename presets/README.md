# Built-in visualization presets

This is the preset root Tunqio ships. Everything in this directory is copied to `presets/` beside `mpcore.dll`
in the app's output and into the MSIX payload (`src/Tunqio.App/Tunqio.App.csproj`, `TunqioPresetContent`), because
that is where the core looks: `MPCORE_PRESET_ROOT` when it is set, otherwise `presets/` next to the module
(`native/mpcore/src/render/preset.cpp`, `default_preset_root`).

A preset is a **directory** holding `preset.json` and the HLSL file it names. The format, the constant-buffer
contract and the rules `preset.json` is validated against are in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("Preset format and constant-buffer contract").
Anything here that is not a directory containing a `preset.json` — this file, for one — is ignored by the scan.

**Your own presets go somewhere else**: `%LocalAppData%\Tunqio\presets`, which the shell hands the core as a
second root (E4-S9) and which `Settings › Visualization` names, opens and refreshes. This directory is the app's
and is replaced by an update; that one is yours. An id already taken by a preset here, or by the compiled-in
`builtin-bars`, is refused there and the skip is logged, so a file cannot shadow a shipped preset.

**Every parameter here declares what a settings page needs to show it** (T-142): a `label`, a `step` where the
number is a whole one, a `unit` where it measures something, `choices` where it is a mode rather than a quantity,
and `hidden` on the three `art_*` parameters, which are set by code and carry packed sRGB integers. None of that
is visible to a shader, so none of it moved the `schema`, and a preset that declares none of it still loads.

All four built-ins of ADR-009 are here: `spectrum-bars` and `waveform` from E4-S4, `radial-spectrum` and
`ambient-glow` from E4-S5 — and the second pair needed no build change to be picked up, which was the point of
the first pair's layout. The core also carries `builtin-bars` compiled into itself (E4-S3), which is why a
missing preset root costs a user choices rather than a picture; it is not one of the four.

`ambient-glow` is the one with a colour source outside the analysis stream: its `art_primary`, `art_secondary`
and `art_accent` parameters carry the album art palette (E3-S7), one sRGB colour packed into each float, with
`-1` — the default — meaning "no art". `AmbientGlowPalette` in `Tunqio.Core` is the managed side of that. It is
not `mp_renderer_set_theme`, which is E4-S6's and still a stub; see the head of `ambient-glow/ambient-glow.hlsl`
for why the two are different jobs.

What the four here are, what their parameters mean, and why `smoothing` is spatial rather than temporal are in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("The presets that ship").

**Editing a shader here changes a checked-in golden image.** `native/mpcore.tests/fixtures/golden/<id>.png` is
what `mpcore.tests [golden]` compares a fresh render against; re-record with `MPCORE_GOLDEN_UPDATE=1` and look
at the diff before committing it. The same suite holds the accessibility contract's luminance-flash limit, so a
preset made much brighter has to be measured, not argued about.

The test fixtures under `native/mpcore.tests/fixtures/presets` are not these: two of them are deliberately
broken, and they are found through `MPCORE_SOURCE_ROOT` from the source tree, never from a build output.

`tools/check-presets.ps1` fails when this directory has not reached a built app.
