# Spike E0-S4: BASS + BASSmix + BASSWASAPI hello world in mpcore

Card T-14. Run on 2026-09-09 on the development machine (Windows 10 Pro 19045, default output "Speakers
(Focusrite USB Audio)", mix format 44 100 Hz / 2 ch, WASAPI period min 3.00 ms, default 10.00 ms). The code
is the first cut of the real engine, not a throwaway: `native/mpcore/src/audio/bass_engine.cpp` behind the
ABI in `native/mpcore/include/mpcore.h`, driven by the console spike `native/spikes/bass_hello`.

```
bass_hello [--seconds N] [--exclusive-seconds N] [--device INDEX] [--buffer-ms N] [--quiet]
```

It generates a 440 Hz sine WAV (48 kHz, stereo, 16-bit, -20 dBFS) in `%TEMP%`, opens it through
`mp_track_open`, and plays it in four passes, sampling `mp_engine_get_clock` once a second against the wall
clock: shared, shared event-driven, exclusive, exclusive event-driven. The verdict requires zero underruns
and a clock drift slope under 2 ms/s on the two shared passes; the exclusive passes are informational.

## Results

Requested buffer 10 ms in every pass.

| Pass | Duration | Output format | WASAPI buffer | Underruns | Clock drift (max offset, slope) | Callback max |
|------|----------|---------------|---------------|-----------|--------------------------------|--------------|
| Shared | 60 s | 48 000 Hz / 2 ch / float (device mix format) | 8 992 bytes = **23 ms** | **0** (6 034 callbacks) | 1.14 ms, +0.013 ms/s | 348 µs |
| Shared, event-driven | 6 s | 48 000 Hz / 2 ch / float | 8 992 bytes = 23 ms | 0 | 0.49 ms, +0.10 ms/s | 348 µs |
| Exclusive | 6 s | 44 100 Hz / 2 ch / **24-bit** (AUTOFORMAT) | 28 224 bytes = **80 ms** | 0 | 25.7 ms (converging), -1.4 ms/s | 348 µs |
| Exclusive, event-driven | 6 s | 44 100 Hz / 2 ch / 24-bit | 3 528 bytes = **10 ms** | 0 | 1.19 ms, +0.36 ms/s | 348 µs |

Audible check: the tone played continuously through the default device for the whole 60 s pass; the
underrun counter (a short read from the mixer while a track is playing) stayed at zero, and the clock
advanced at wall-clock rate to within 1 ms. Callback cost stayed under 0.35 ms at 10 to 20 ms periods.

## Exact BASS usage

Init (once per process, "no sound" device, BASS is a decoder only):

```
BASS_SetConfig(BASS_CONFIG_UPDATEPERIOD, 0);   // no BASS update thread: BASSWASAPI pulls
BASS_SetConfig(BASS_CONFIG_UPDATETHREADS, 0);
BASS_SetConfig(BASS_CONFIG_FLOATDSP, 1);
BASS_SetConfig(BASS_CONFIG_DEV_DEFAULT, 1);
BASS_Init(0, 48000, 0, NULL, NULL);            // device 0 = no sound
BASS_PluginLoad(L"bassflac.dll" | L"bassopus.dll" | L"basswv.dll" | L"bass_ape.dll", BASS_UNICODE)
```

Streams and mixer:

```
BASS_StreamCreateFile(FALSE, wpath, 0, 0, BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT | BASS_STREAM_PRESCAN | BASS_UNICODE)
BASS_Mixer_StreamCreate(rate, chans, BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIXER_NONSTOP | BASS_MIXER_POSEX)
BASS_Mixer_StreamAddChannel(mixer, stream, BASS_MIXER_CHAN_NORAMPIN)
BASS_Mixer_ChannelSetSync(stream, BASS_SYNC_END | BASS_SYNC_MIXTIME, 0, end_sync, self)
BASS_Mixer_ChannelSetPosition(stream, bytes, BASS_POS_BYTE | BASS_POS_MIXER_RESET)     // seek
BASS_Mixer_ChannelFlags(stream, BASS_MIXER_CHAN_PAUSE, BASS_MIXER_CHAN_PAUSE)           // pause
```

Output:

```
flags = 0                                                     // shared
flags = BASS_WASAPI_EXCLUSIVE | BASS_WASAPI_AUTOFORMAT        // exclusive
flags |= BASS_WASAPI_EVENT                                    // event-driven variants
BASS_WASAPI_Init(device, mixer_rate, mixer_chans, flags, 0.010f, 0.0f, output_proc, self)
BASS_WASAPI_GetInfo(&info)   // info.freq / info.chans decide the mixer format; info.buflen is in float bytes
BASS_WASAPI_Start()
output_proc: got = BASS_ChannelGetData(mixer, buffer, length); zero-fill the rest; apply gain; return length
```

Clock:

```
buffered = BASS_WASAPI_GetData(NULL, BASS_DATA_AVAILABLE)                      // float bytes not yet played
pos      = BASS_Mixer_ChannelGetPositionEx(stream, BASS_POS_BYTE, buffered)    // needs BASS_MIXER_POSEX
position_ms = BASS_ChannelBytes2Seconds(stream, pos) * 1000
```

## Surprises worth carrying into E1

1. **The mixer must be recreated at the output's mix format.** Shared mode ignores the requested rate and
   uses the device mix format (48 kHz here); exclusive AUTOFORMAT chose 44.1 kHz / 24-bit for the same
   device. `set_output` therefore reads `BASS_WASAPI_INFO` and rebuilds the mixer when rate or channels
   differ, re-adding the current source at its byte position. BASSmix resamples the 48 kHz source to
   44.1 kHz transparently; the source position stays in source units, so the clock maths is unchanged.
2. **A 10 ms request gives 23 ms in shared mode** on this device, with or without `BASS_WASAPI_EVENT`.
   BASSWASAPI appears to add the device period to the requested buffer. 23 ms is the practical shared-mode
   floor here; the "10 ms buffer" in the E0-S4 criterion is what was requested, not what WASAPI grants.
3. **Exclusive mode without EVENT gives an 80 ms buffer; with EVENT exactly 10 ms.** Event-driven exclusive
   is the low-latency path and should be the default for exclusive output in E1-S6.
4. **Non-event exclusive shows a converging clock offset**: the compensated position lags wall time by a
   growing amount (-14, -22, -25, -26 ms) that flattens out after about five seconds and does not grow
   afterwards. It is not a rate error (the 60 s shared pass drifts 0.013 ms/s). The most plausible cause is
   the 80 ms buffer filling progressively after start; the event-driven variant does not show it. Recorded
   for E4-S8 (latency harness) to measure against the real acoustic output; do not trust `mp_clock` in
   non-event exclusive mode during the first five seconds.
5. **`BASS_DATA_AVAILABLE` reports more than our buffer.** Shared mode reports about 55 to 65 ms buffered
   although `buflen` is 23 ms; it seems to include WASAPI's own engine buffer. Using it as the POSEX delay
   nevertheless yields a clock that tracks wall time within 1 ms, so it is the right quantity for
   compensation. Whether the absolute offset matches what the ear hears is E4-S8's question.
6. `BASS_SYNC_END | BASS_SYNC_MIXTIME` fires when the end is *mixed*, one output buffer before it is heard.
   The managed side must delay the "track ended" UI transition by `output_latency_ms` (or use the clock).
7. The E1 design in audio-engine.md wanted `BASS_MIXER_END`; `BASS_MIXER_NONSTOP` is the right flag for a
   player: the mixer keeps producing silence between tracks so the WASAPI thread never stalls and the
   device stays open.
8. Two engine-lifetime rules fell out: BASS is process-global, so `mp_engine_create` refuses a second
   engine, and tracks belong to the engine that opened them (freed on destroy).
9. `/sdl` is on for the native projects: it rewrites a pointer operand of `delete` to `0x8123` afterwards.
   Irrelevant to BASS, but it cost an hour in the ASan proof and is worth knowing (see build-test-release.md).

## What the ABI draft now contains

`mpcore.h` 0.2: engine create/destroy, set_output (with `event_driven`), enum_devices, event callback,
track open/close/info, play/pause/resume/stop/seek/volume, clock (with `output_buffered_bytes`), stats,
log sink, and the analysis frame struct. Preload, ReplayGain, crossfade, preview and analysis exports are
declared and return `MP_E_STATE` naming the story that implements them. Renderer exports are left to the
E0-S5 render spike.

## Headless coverage

`mpcore.tests` gained ten `[engine]` tests that run without an output device: struct_size rejection,
single-instance rule, 50 create/destroy cycles, WAV info from a generated fixture, missing-file error,
play-without-output state error, idle clock and stats, device enumeration count protocol, "not implemented"
messages, and engine-owned track lifetime. They pass in Debug, Release and ASan.
