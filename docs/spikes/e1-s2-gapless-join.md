# Spike E1-S2: gapless join per format

Card T-21. Run on 2026-09-09 on the development machine, headless (`MP_DEVICE_NONE`, `mp_engine_render`), so the
seam is a buffer the test inspects rather than something heard. As with E0-S4 the code is the first cut of the
real thing, not a throwaway: `mp_engine_preload_next` now queues a successor in `bass_engine.cpp` and the
measurement is the `[gapless]` suite in `native/mpcore.tests/src/test_gapless.cpp`, run on every CI build.

## Fixtures

`tools/FixtureGen gapless` writes `tests/fixtures/gapless/<pair>/a.<ext>` and `b.<ext>`: one linear chirp,
200 Hz to 2000 Hz over 4 s at 0.25 full scale, cut at exactly 2.0 s and each half encoded on its own, in every
format an encoder exists for (APE and MPC have none, card T-88). A chirp has no period, so a cross-correlation
against the chirp regenerated in the test has a single peak and a lossy codec cannot hide a seam in it. Two
pairs are at 44.1 kHz to run the mixer's resampler through the join; the rest are at the 48 kHz mixer rate.

The engine plays `a` at 0 with `b` preloaded and is rendered for 4.5 s in **479-frame** pulls (96 000 is not a
multiple, so the seam falls inside a buffer: a join that is only buffer-accurate cannot pass by luck). Per pair:

- **lag** in output frames at which the audio after the seam best matches the reference (0 is
  sample-continuous, positive means frames were inserted, negative that frames were lost or overlapped);
- **residual**: RMS error against the reference at that lag, relative to the signal RMS, over 2.1 to 3.0 s;
- **seam residual**: the same at lag 0 across 1.98 to 2.02 s, the number a gap or an overlap shows up in;
- **step ratio**: the largest sample-to-sample step across 1.95 to 2.05 s over the chirp's own largest step
  there, the number a click shows up in;
- **join at**: the mixer frame the `MP_EVENT_TRACK_STARTED` for `b` carries, against 96 000.

## Results

| Pair | Decoder | Rate | a frames | b frames | Join at | Lag before | Lag after | Residual after | Seam residual | Step ratio | Verdict |
|------|---------|------|----------|----------|---------|------------|-----------|----------------|---------------|------------|---------|
| wav | BASS | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0001 | 0.0001 | 1.00 | continuous |
| aiff | BASS | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0001 | 0.0001 | 1.00 | continuous |
| flac | bassflac | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0001 | 0.0001 | 1.00 | continuous |
| flac-44k | bassflac | 44 100 | 88 200 | 88 200 | 96 000 | 0 | 0 | 0.0001 | 0.0006 | 1.08 | continuous (resampled) |
| alac | Media Foundation | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0001 | 0.0001 | 1.00 | continuous |
| wv | basswv | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0001 | 0.0001 | 1.00 | continuous |
| mp3 | BASS | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0300 | 0.0301 | 1.08 | continuous (LAME delay and padding removed by BASS) |
| mp3-44k | BASS | 44 100 | 88 200 | 88 200 | 96 000 | 0 | 0 | 0.0300 | 0.0300 | 1.10 | continuous (resampled) |
| ogg | BASS | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0147 | 0.0220 | 1.40 | continuous |
| opus | bassopus | 48 000 | 96 000 | 96 000 | 96 000 | 0 | 0 | 0.0100 | 0.0110 | 1.14 | continuous (pre-skip applied) |
| m4a (AAC) | Media Foundation | 48 000 | 96 000 | 96 000 | 97 280 (+1 280) | **+1 024** | **+2 304** | 0.0145 | 1.73 | 1.04 | **best-effort**: priming not removed |
| wma | Media Foundation | 48 000 | 96 000 | 96 000 | 94 208 (-1 792) | 0 | **-1 792** | 0.0034 | 1.61 | 6.27 | **best-effort**: decoder drops the tail |

The residuals of the continuous lossy rows (0.01 to 0.03) are the codecs' own coding error on the chirp, the
same before and after the seam; the seam residual equals the residual after, so the join added nothing. The
`[gapless]` suite asserts lag 0 before and after, the exact join frame, the residual bound per pair and a step
ratio under 1.6 for the ten continuous pairs, and only that the join happened for the two best-effort ones.

## What it took: three findings about BASSmix

1. **A source added inside a mix-time END sync does not start at the sync position.** It starts at the
   beginning of the buffer being mixed: with 479-frame pulls every pair joined 200 frames early (96 000 =
   200 × 479 + 200) with `b` mixed over the tail of `a` (seam residual 1.37, an overlap), and with 480-frame
   pulls the same code looked sample-accurate only because 96 000 is a multiple of 480. The
   `StreamAddChannelEx(start)` parameter is a delay relative to that same buffer start and would need the exact
   in-buffer offset, which the callback is not told.
2. **`BASS_MIXER_CHAN_LIMIT` is the fix.** With it the mixer's `BASS_ChannelGetData` stops at the frame the
   source runs out instead of finishing the buffer, the END sync fires there, the successor added in it is the
   next frame read, and the mixer position after that read is the join frame exactly. The pull stage therefore
   loops: a short read followed by another read for the rest of the buffer (NONSTOP silence when nothing
   follows), still one buffer, no allocation. A short read that stays short is what an underrun is now.
3. **This also holds for a resampled source.** The 44.1 kHz pairs join at frame 96 000 with a seam residual of
   0.0006; the resampler's state carries across because the two sources are separate channels of one mixer
   running at the output rate (the E0-S4 rule that the mixer is rebuilt at the device's mix format stays).

The events: `end_sync` only swaps the sources and parks a flag; `pull()` raises `MP_EVENT_TRACK_ENDED` then
`MP_EVENT_TRACK_STARTED` after the read that stopped, both with `b` = the mixer byte position of the join, so
the managed side can time the now-playing change against `mp_clock.mixer_byte_pos` (E1-S3's 100 ms criterion)
without guessing.

## AAC through Media Foundation

The ffmpeg-encoded `a.m4a` carries the standard MP4 gapless description: an edit list (`elst` media time
1 024 = the AAC encoder delay, duration 2 000 ms) and a media header of 97 024 frames (1 024 priming + 96 000
audio). No `iTunSMPB`; iTunes, qaac and Nero write that tag instead (typically priming 2 112) and ffmpeg
writes the edit list. BASS reports the track as 96 000 frames (it trusts the movie duration) but the Media
Foundation source hands it the raw decoded frames: the first 1 024 are priming, the audio arrives 1 024 frames
late, and the stream ends after 97 280 frames (95 AAC frames of 1 024), so the join lands 1 280 frames late and
`b` then contributes its own 1 024 of priming: a 2 304-frame lag with 1 024 frames of near-silence in it.

Trimming by seeking is not an option either. Seeking the MF stream is not sample-accurate (measured: a play
from 21 ms landed at frame 0, from 42 ms at frame 992, from 500 ms at frame 6 949), so a "skip the priming"
seek cannot be relied on, and BASS's `BASS_MP3_IGNOREDELAY` / LAME logic does not apply to MF streams.

What would give gapless AAC: read the priming and padding ourselves (the `elst` media time and the
`iTunSMPB` freeform atom, a small MP4 atom walk) and wrap the MF decode stream in a user decode stream
(`BASS_StreamCreate` with a `STREAMPROC`) that decodes from 0, discards the priming frames and ends after the
valid count; the wrapper is then an ordinary source for the mixer, LIMIT and all. Seeks would go to the MF
stream underneath and stay inexact, which they are today. The BASS_AAC add-on, which does this itself, is GPL
and excluded by the licence policy (THIRD-PARTY-NOTICES.md). This is a story of its own, not part of the
spike; until it lands AAC is best-effort.

## WMA through Media Foundation

The WMA decoder delivers 94 208 frames for a track BASS reports as 96 000: the last 1 792 frames (the
codec's own delay) never come out, so the join happens 1 792 frames early and the end of every WMA track is
cut by 37 ms at 48 kHz. Nothing in the file describes this (WMA has no gapless metadata), and no player treats
WMA as gapless; best-effort, listed as such in product-scope.md.

## Carried into E1-S3

- `mp_engine_preload_next(next)` queues the successor (rewound to 0); NULL, `mp_engine_stop`, closing the
  queued track, or playing it by hand clear the queue. The join needs no fade: the envelope stays at 1.
- The exact join frame is in the events' `b`; `mp_engine_get_clock` returns `b`'s position from the join on.
- Sources are added with `NORAMPIN | LIMIT` everywhere (play, re-attach after a device change, the join).
- ABI 0.5: nothing changed in signatures; `mp_engine_preload_next` is implemented and the events' `b` is
  documented.
