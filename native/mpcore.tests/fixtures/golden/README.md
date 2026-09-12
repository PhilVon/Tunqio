# Golden images (E4-S4, AC-121)

One PNG per shipped preset, each 320x180 BGRA, rendered on WARP from the fixed `mp_analysis_frame`
`fixed_frame()` in `../../src/test_preset_golden.cpp` builds. They are compared byte for byte within a stated
tolerance by the `[golden]` tests; nothing else reads them.

**These are the reviewable artefact of a preset change.** To re-record after an intended change, set
`MPCORE_GOLDEN_UPDATE=1` and run `mpcore.tests [golden]`, then look at the diff before committing it. A golden
image rewritten without being looked at is a test that agrees with whatever the code now does.
