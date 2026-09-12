# Spike E4-S8: what "audio-to-picture latency" is, what it measures at, and where its floor is

Card T-59, criterion AC-131's second clause. Run on 2026-09-12 on the development machine (Windows 10 Pro
19045, i7-9700K, 8 logical cores, NVIDIA GeForce RTX 4080 SUPER, default output 44 100 Hz / 2 ch / float,
shared mode, requested buffer 40 ms). The harness is `tools/LatencyRunner`; the mechanism it measures is
`mp_renderer_set_av_sync` and `mp_renderer_drain_latency`, new at ABI 0.17.

This document exists because AC-131 offers two ways to be satisfied - "p95 within one refresh interval on the
reference machine, **or** a spike doc explaining the floor" - and only the second is honestly available. This
is the second. The first is not refused; it is deferred with its reasons, and [what it would take](#what-would-settle-the-first-clause)
is written down at the bottom.

## 1. There are four edges and only three of them are visible

"Latency" names four different things, and adding guesses about them together is how a number stops meaning
anything:

| # | Edge | Measured here? |
|---|------|----------------|
| 1 | Audio reaching the mixer, and the analysis publishing a frame from it | yes, as `analysis_qpc` to `first_seen_qpc` |
| 2 | That frame reaching a drawn picture | yes, as `first_seen_qpc` to `present_qpc` |
| 3 | Where the LISTENER is while both of the above happen | yes, and it is the largest term |
| 4 | The picture reaching the screen | **no. Not from inside this process, and not ever** |

So the number is defined as a **position error** rather than as a sum of intervals. `mp_analysis_frame` carries
`mixer_byte_pos` - the mixer byte its hop begins at - and `mp_clock` gives the listener's own position on the
same axis (`mixer_byte_pos - output_buffered_bytes`, the same arithmetic that times a gapless join). One axis,
two readings, one subtraction:

```
av_error_ms = (audible mixer position at the instant the frame was presented)
            - (mixer position the picture was drawn from, plus half a hop)
```

Positive is a **late** picture: it is showing audio the listener already heard. Negative is an **early** one.
The half hop is not a rounding: a frame is documented as describing the 11.6 ms *starting* at the byte it
names, so the instant the picture stands for is the middle of that hop, and leaving it out is a 5.8 ms bias
against a 16.7 ms budget.

## 2. The sign is the surprise, and the output buffer is the budget

The analysis runs at MIX time. The mixer is **ahead** of the loudspeaker by whatever WASAPI has buffered. So a
renderer that draws the newest analysis frame - which is what every build up to ABI 0.16 did - is showing the
future, and `av_error_ms` is negative and roughly minus the output buffer.

Compensation is therefore a deliberate **delay**, and the output buffer is the budget it is paid out of. That
is the whole mechanism of E4-S8, and it is not what the phrase "latency compensation" first suggests.

It also means compensation is free where there is nothing to compensate against. On an offline engine or an
`MP_DEVICE_NONE` one, `output_buffered_bytes` is zero, the listener is level with the mixer, there is no older
frame to prefer, and `MP_AV_SYNC_AUDIBLE` picks exactly what `MP_AV_SYNC_NEWEST` picks. Every golden image and
every headless test in the suite runs on that path, which is why turning compensation on by default changed no
picture in the repository.

## 3. What it measures

Three runs, each two phases of the same audio on the same device minutes apart: the uncompensated phase first,
then the compensated one. 1280x720, quality pinned High, a 440 Hz tone the harness writes to its own scratch
directory. Every figure is milliseconds.

| Run | Phase | n | av error p50 | p95 | p99 | \|error\| p50 | **\|error\| p95** | p99 |
|-----|-------|---|--------------|-----|-----|---------------|-------------------|-----|
| RTX 4080, spectrum-bars, 30 s | newest | 1756 | **−61.25** | −53.29 | −50.95 | 61.25 | 68.73 | 70.79 |
| | audible | 1742 | **+3.11** | +11.34 | +15.65 | 5.42 | **11.45** | 15.90 |
| WARP, spectrum-bars, 20 s | newest | 1047 | −59.16 | −50.93 | −48.82 | 59.16 | 66.71 | 68.98 |
| | audible | 1027 | **+0.04** | +11.25 | +16.33 | 5.49 | **11.86** | 17.37 |
| WARP, ambient-glow, 20 s | newest | 1054 | −61.86 | −54.06 | −51.91 | 61.84 | 69.70 | 71.81 |
| | audible | 1187 | **+3.81** | +11.34 | +20.07 | 5.26 | **11.47** | 20.07 |

Read the first row as: **the picture was 61 ms early**, showing audio that had been mixed but had not left the
sound card. The second as: after compensation it was 3 ms late, and 95% of pictures were within 11.5 ms of the
sound in either direction.

Three things in that table matter more than the headline.

**The rasteriser makes no difference.** An RTX 4080 SUPER, the WARP software rasteriser, and WARP running
ambient-glow - which T-148 measured at ten to seventeen times the per-frame cost of the other three presets -
produce a compensated `|error|` p95 of 11.45, 11.86 and 11.47 ms. A 0.4 ms spread across a range of GPU cost
that wide says the floor is **not** the GPU. This is the single most useful result here, because it is what
lets a number measured on this machine say anything at all about a machine with a weaker one.

**The output buffer is not the number `mp_engine_stats` reports.** Stats says 40 ms - the buffer that was
requested. `BASS_WASAPI_GetData(BASS_DATA_AVAILABLE)`, which is what the compensation actually reads, says
73-76 ms is in flight. Nearly double, consistently, across every run. The compensation is right either way
because it uses the live reading, but a caller reasoning from `output_buffer_ms` about how much budget it has
would be wrong by 35 ms. Filed as its own card.

**The offset lever reaches the number.** A fourth run at `-offset 16.7` (one refresh interval, the cheapest
guess at edge 4) moved the compensated distribution from a median of +3.1 ms to −18.0 ms. Whatever a caller
learns about its own display, it has a knob that spends it.

## 4. The floor, and what it is made of

The compensated `|error|` p95 sits at 11.4-11.9 ms and does not move. Three things put it there, and only one
of them is a cost anybody could buy their way out of.

**(a) The renderer can only change what it draws once per frame - and this is the whole floor.** The target
(where the listener is) moves continuously; the picture is chosen at the top of a render frame and then held
for the whole of it. So even with a perfect choice every time, the error sweeps across roughly one render tick
and is about uniform over it. At 60 Hz that is ±8.3 ms, whose 95th percentile is 7.9 ms. Nothing about the
audio path or the GPU changes this; it is what "sixty pictures a second" means. It scales with the refresh
rate: on a 120 Hz display the same code has a floor of about 4 ms.

**(b) The renderer sees only about two thirds of the hops.** `mp_analysis_try_get_latest` is a triple buffer
that offers the NEWEST frame and nothing else, so a poller sees a hop only if it polls between that hop and
the next. The analysis publishes 512-frame hops at 86.1 Hz (44.1 kHz) and the renderer polls at 59; in the
30 s run it saw 1680 distinct frames out of about 2583 published, 65%. The frames it can choose among are
therefore ~17.9 ms apart rather than 11.6, and the nearest one to the target is up to 8.9 ms away. Fixing this
would mean the analysis offering a queue rather than a latest, and it would buy at most a couple of
milliseconds against (a), which is the reason it has not been done.

**(c) Jitter in the two readings.** (a) and (b) together predict a p95 near 8.5 ms; the measurement is 11.4.
The remaining ~3 ms is the WASAPI buffer depth changing between the pick and the present, plus scheduling.

## 5. What is NOT in any number above

Every one of these is strictly **additive** to a picture that is already slightly late, so 11.4 ms measured
here is a floor for the room and not a bound on it.

- **Present to photon.** The harness is headless: the renderer draws into an offscreen texture, so there is no
  swap chain, no `Present` and no compositor. On a real `SwapChainPanel` this is at least one refresh interval
  and usually two, plus the panel's own response. **This alone is as large as the entire measured error**, and
  it is the reason a p95 measured here cannot be compared with a criterion written about a screen.
- **The device's analogue and driver delay** past what WASAPI reports as buffered.
- **The spectrum's own 21-23 ms.** The picture is aligned on the hop `mixer_byte_pos` names, which is where the
  `waveform` field comes from. The `spectrum` in the *same frame* is a 2048-point Hann transform whose energy
  centroid sits 1024 samples - 23.2 ms at 44.1 kHz - earlier than the newest sample. One frame, two content
  instants, 23 ms apart: a preset drawing bars is that much further behind than a preset drawing a waveform,
  and no choice of frame makes both right at once. A caller who cares more about the spectrum sets
  `offset_ms` to about −23.
- **The reference machine.** See below.

## 6. Why the harness is headless, and what that costs

Headless was chosen so the run needs no shell and no desktop, which means it can happen while somebody is at
the keyboard (T-166 - the UIA harnesses cannot). It costs exactly edge 4, which is the edge above.

The alternative - driving the real app and reading its `SwapChainPanel` - would not have recovered it either.
A WinUI window captures black on this machine, `Present` returns when the frame is queued rather than when it
is lit, and `DXGI_FRAME_STATISTICS` counts refreshes rather than photons. **Edge 4 is a camera measurement.**
Nothing inside the process can see it, so the honest choice was the cheap harness that says so.

## 7. Why this is a report and not a gate

`tools/LatencyRunner` exits 0 when it produced a usable measurement, and deliberately **not** when the p95 was
inside the budget. Three reasons, in the order they matter:

1. The photon edge is missing, so a pass would be a claim the harness cannot support.
2. This machine is not the reference machine.
3. This project has retired four wall-clock bounds in one day (T-143, T-134, T-130, T-119) for the same
   defect: the machine's spare capacity was never part of the claim. A gate that only holds on an idle box is
   not a gate, and the honest form is a number with its distribution beside it.

What IS gated, in `native/mpcore.tests/src/test_latency.cpp`, is the arithmetic: with an offset of −50 ms the
renderer draws a frame 50 ms of *audio* further back, to within a hop. That claim is about the mixer's byte
axis and cannot be broken by a busy machine.

## What would settle the first clause

AC-131's first clause needs three things this run does not have, and none of them is code:

1. The reference machine - a low-power integrated GPU rather than an i7-9700K with an RTX 4080 SUPER. This
   project has already retired its reference-machine criteria to T-90 and assumes them passing; §3 above
   argues the rasteriser is not what this number depends on, which is the strongest thing that can be said
   without the machine.
2. A **camera**. Edge 4 is a photograph of a screen next to a microphone, and everything else is a proxy.
3. A decision about what "within one refresh interval" is a bound on - the signed error, or its magnitude.
   They differ by a factor of two here, and this document reports both for that reason.
