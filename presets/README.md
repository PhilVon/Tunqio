# Built-in visualization presets

This is the preset root Tunqio ships. Everything in this directory is copied to `presets/` beside `mpcore.dll`
in the app's output and into the MSIX payload (`src/Tunqio.App/Tunqio.App.csproj`, `TunqioPresetContent`), because
that is where the core looks: `MPCORE_PRESET_ROOT` when it is set, otherwise `presets/` next to the module
(`native/mpcore/src/render/preset.cpp`, `default_preset_root`).

A preset is a **directory** holding `preset.json` and the HLSL file it names. The format, the constant-buffer
contract and the rules `preset.json` is validated against are in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("Preset format and constant-buffer contract").
Anything here that is not a directory containing a `preset.json` — this file, for one — is ignored by the scan.

**There are no presets here yet.** E4-S3 built the loader and carries one preset compiled into the core
(`builtin-bars`), which is why a missing preset root costs a user choices rather than a picture. The four
built-ins of ADR-009 are files, and E4-S4 (Spectrum Bars, Waveform) and E4-S5 (Radial Spectrum, Ambient Glow)
write them — into this directory, with no build change needed to pick them up.

The test fixtures under `native/mpcore.tests/fixtures/presets` are not these: two of them are deliberately
broken, and they are found through `MPCORE_SOURCE_ROOT` from the source tree, never from a build output.

`tools/check-presets.ps1` fails when this directory has not reached a built app.
