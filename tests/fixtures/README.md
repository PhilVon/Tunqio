# Test fixtures

`library/` is the generated mini-library described in [docs/library-and-data.md](../../docs/library-and-data.md)
("Fixtures for testing"): 60 one-second files across 8 albums in ten formats, with the edge cases the scanner
must handle (compilation without album artist, multi-disc album, three-artist track, corrupt tag header,
folder-art-only album, unicode paths). `library/manifest.json` records every file's SHA-256 and expected tags.

Do not edit the files by hand. Regenerate with ffmpeg on PATH:

```bash
dotnet run -c Release -p:Platform=x64 --project tools/FixtureGen -- files -out tests/fixtures/library
```

Two runs produce identical bytes (`Tunqio.Library.Tests.FixtureLibraryTests.Generation_is_deterministic`).

`gapless/` is the E1-S2 set: one 4 s chirp cut at 2.0 s, each half encoded on its own in every format an encoder
exists for (two pairs at 44.1 kHz); `native/mpcore.tests` joins each pair and matches the seam against the chirp
([docs/spikes/e1-s2-gapless-join.md](../../docs/spikes/e1-s2-gapless-join.md)). Regenerate with:

```bash
dotnet run -c Release -p:Platform=x64 --project tools/FixtureGen -- gapless -out tests/fixtures/gapless
```

`library-100k.db` (not committed, gitignored) is the synthetic 100 000-track database for repository and search
benchmarks:

```bash
dotnet run -c Release -p:Platform=x64 --project tools/FixtureGen -- db -out tests/fixtures/library-100k.db
```
