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
not `mp_renderer_set_theme`; see the head of `ambient-glow/ambient-glow.hlsl` for why the two are different
jobs, and for the order they resolve in (album art, then the theme, then the preset's built-in ramp).

## The theme

`b0` ends with `float4 theme[4]` — `mp_theme_colors`' primary, secondary, accent and background, in that order,
RGBA. It is renderer-wide, it survives a preset switch (unlike a parameter, which returns to its default), and
the shell drives it at 30 Hz from the music. That is the schema-2 field; a schema 1 preset simply declares a
shorter block and reads exactly what it read before.

**Alpha 0 means "the shell has not told me a theme."** Until `mp_renderer_set_theme` is called every channel is
zero, so a preset must treat `theme[i].a <= 0` as "use my own colours". That branch is what lets a themed preset
keep a byte-identical golden image on an unthemed renderer, and every shipped preset relies on it.

All four shipped presets read it, and all four do the same thing with it (T-162): the theme replaces the three
stops of the preset's own colour ramp, leaving the `colour` parameter to go on choosing *where* on that ramp to
sample. The two are orthogonal — the theme says which colours, `colour` says what moves along them — and a
`theme_mix` parameter (default 1) scales from the preset's own palette to the theme's, so a person who wants the
shipped colours back can have them.

If you write a preset that draws with the theme, note the one rule the shipped three follow: a theme colour is
scaled so it is **no more luminous than the stop it replaces**, so a theme can change a preset's hue but cannot
make it brighter. That is what keeps the flash-safety measurements below valid under a palette the preset author
never saw. `mpcore.tests [theme]` measures it over the seven corners of the sRGB cube.

## Adaptive quality reaches only a preset whose cost is in its pixels

The renderer's one quality lever is **render scale** (E4-S7): a lower tier draws fewer pixels and stretches them.
That saves time only where time is spent per pixel. Of the four here, that is `ambient-glow` alone — one quad whose
whole picture is pixel-shader arithmetic. The other three draw primitives, and at 1080p on WARP they cost well under
a millisecond a frame (T-148), so the controller never has cause to lower them, and pinning them to Low costs
them resolution without buying back any time.

Nothing in `preset.json` says which kind a preset is, and nothing needs to: the controller learns it at runtime from
the cost ratio it measures across a tier boundary (about 1.0 per-primitive, up to 2.25 per-pixel). What it means for
you as an author is that **a preset which does its work per pixel is the one adaptive quality will act on** — test
it at Low as well as High, because a user on a slow GPU will see that tier. The controller itself is described in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("Adaptive quality (as built, E4-S7)").

What the four here are, what their parameters mean, and why `smoothing` is spatial rather than temporal are in
[docs/visualization-engine.md](../docs/visualization-engine.md) ("The presets that ship").

**Editing a shader here changes a checked-in golden image.** `native/mpcore.tests/fixtures/golden/<id>.png` is
what `mpcore.tests [golden]` compares a fresh render against; re-record with `MPCORE_GOLDEN_UPDATE=1` and look
at the diff before committing it. There are six: one per preset on a renderer nobody has themed, plus
`spectrum-bars-themed.png` and `ambient-glow-themed.png`, which are recorded under a theme pinned in the test
file. A preset that draws with the theme needs the theme pinned in its fixture, or its golden becomes a
function of whatever the theming last set. The same suite holds the accessibility contract's luminance-flash limit, so a
preset made much brighter has to be measured, not argued about.

The test fixtures under `native/mpcore.tests/fixtures/presets` are not these: two of them are deliberately
broken, and they are found through `MPCORE_SOURCE_ROOT` from the source tree, never from a build output.

`tools/check-presets.ps1` fails when this directory has not reached a built app.
