# Gapless-join fixtures (E1-S2)

One linear chirp, 200 Hz to 2000 Hz over 4 s at 0.25 full scale, cut at 2 s: `a` is the first half, `b` the second, each encoded on its own. A sample-continuous join of `a` then `b` reproduces the chirp; `native/mpcore.tests/src/test_gapless.cpp` measures the lag and the residual at the seam against the same chirp regenerated in C++.

Do not edit the files by hand. Regenerate with ffmpeg on PATH:

```bash
dotnet run -c Release -p:Platform=x64 --project tools/FixtureGen -- gapless -out tests/fixtures/gapless
```

| Pair | Codec arguments |
|------|-----------------|
| wav (48000 Hz) | (written by FixtureGen) |
| aiff (48000 Hz) | (written by FixtureGen) |
| flac (48000 Hz) | `-c:a flac -compression_level 5` |
| flac-44k (44100 Hz) | `-c:a flac -compression_level 5` |
| mp3 (48000 Hz) | `-c:a libmp3lame -b:a 192k -id3v2_version 3` |
| mp3-44k (44100 Hz) | `-c:a libmp3lame -b:a 192k -id3v2_version 3` |
| m4a (48000 Hz) | `-c:a aac -b:a 160k` |
| alac (48000 Hz) | `-c:a alac` |
| ogg (48000 Hz) | `-c:a libvorbis -q:a 4` |
| opus (48000 Hz) | `-c:a libopus -b:a 96k` |
| wma (48000 Hz) | `-c:a wmav2 -b:a 128k` |
| wv (48000 Hz) | `-c:a wavpack` |
