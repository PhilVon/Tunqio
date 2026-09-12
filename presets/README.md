# Built-in visualization presets

This is the preset root Tunqio ships. Everything in this directory is copied to `presets/` beside `mpcore.dll`
in the app's output and into the MSIX payload (`src/Tunqio.App/Tunqio.App.csproj`, `TunqioPresetContent`), because
that is where the core looks: `MPCORE_PRESET_ROOT` when it is set, otherwise `presets/` next to the module
(`native/mpcore/src/render/preset.cpp`, `default_preset_root`).

A preset is a **directory** holding `preset.json` and the HLSL file it names. The format, the constant-buffer
contract and the rules `preset.json` is validated against are in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("Preset format and constant-buffer contract").
Anything here that is not a directory containing a `preset.json` — this file, for one — is ignored by the scan.

Two of the four built-ins of ADR-009 are here, written by E4-S4: `spectrum-bars` and `waveform`. E4-S5 adds
`radial-spectrum` and `ambient-glow` beside them, with no build change needed to pick them up. The core also
carries `builtin-bars` compiled into itself (E4-S3), which is why a missing preset root costs a user choices
rather than a picture; it is not one of the four.

What the two here are, what their parameters mean, and why `smoothing` is spatial rather than temporal are in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("The presets that ship").

**Editing a shader here changes a checked-in golden image.** `native/mpcore.tests/fixtures/golden/<id>.png` is
what `mpcore.tests [golden]` compares a fresh render against; re-record with `MPCORE_GOLDEN_UPDATE=1` and look
at the diff before committing it. The same suite holds the accessibility contract's luminance-flash limit, so a
preset made much brighter has to be measured, not argued about.

The test fixtures under `native/mpcore.tests/fixtures/presets` are not these: two of them are deliberately
broken, and they are found through `MPCORE_SOURCE_ROOT` from the source tree, never from a build output.

`tools/check-presets.ps1` fails when this directory has not reached a built app.
