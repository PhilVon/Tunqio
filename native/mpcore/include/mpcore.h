/*
 * mpcore.h - the C ABI of mpcore.dll (docs/solution-structure.md, "ABI rules").
 *
 * This is the only header the managed side (Tunqio.Interop) reads. Every export is extern "C",
 * __cdecl, returns mp_result (or a plain scalar for pure queries), never throws, and never
 * invokes a callback after the owning handle is destroyed.
 *
 * Rules:
 *  - Handles are opaque pointers. A handle is invalid after its destroy/close call returns, and every
 *    track handle is invalid after its engine is destroyed.
 *  - Structs are POD with uint32_t struct_size first, and that size says which header the caller was built
 *    against. A size this build knows is served as such. A SMALLER size is a caller built against an older
 *    header: it is served the prefix that size covers - only those fields are read from an in struct, only
 *    those fields are written to an out struct, and the bytes beyond it are never touched - and the fields its
 *    header did not have take their documented zero default. A LARGER size, or one too small to hold the
 *    struct's first meaningful field, is MP_E_INVALID_ARG: the first asks for fields this build has never
 *    heard of, and the second names no header that ever existed. So fields can be appended in an ABI-minor
 *    bump, and a binary built against the shorter struct keeps working (ABI 0.12; before it the check was an
 *    exact `struct_size == sizeof` and appending broke every existing caller).
 *  - An array of out structs (mp_engine_enum_devices, mp_renderer_enum_presets) takes out[0].struct_size as
 *    the size of every element, because that size is also the stride the core writes at.
 *  - Strings are UTF-8, NUL-terminated. Out-strings are fixed-size fields inside structs.
 *  - Callbacks run on native threads (WASAPI mix thread, event threads). Do nothing but enqueue.
 *  - Appending exports or struct fields is ABI-minor; changing or removing anything is ABI-major.
 *
 * History: 0.1 version and error surface (E0-S1). 0.2 engine, track, clock, stats, events, log (E0-S4
 * draft; exports marked "not implemented" return MP_E_STATE until the story that implements them lands).
 * 0.3 renderer: create/destroy/resize/visible/stats implemented by the render spike (E0-S5); presets, theme and
 * quality are declared and stubbed for E4. 0.4 engine skeleton (E1-S1): MP_DEVICE_NONE output and
 * mp_engine_render for headless use; guard fades on pause/resume/stop/seek; audio-taper volume. 0.5 gapless
 * join (E1-S2 spike): mp_engine_preload_next queues the successor and the mix-time END sync starts it where the
 * current track ends; MP_EVENT_TRACK_STARTED/ENDED carry the mixer byte position of the join in b. E1-S3: the join
 * events name the tracks by handle, as the natural end does. The join is heard when mp_clock.mixer_byte_pos minus
 * output_buffered_bytes passes b. 0.6 ReplayGain (E1-S5): mp_track_set_replaygain replaces the never-implemented
 * engine-level stub (a stub that only ever returned MP_E_STATE, so no consumer changes behaviour): the gain belongs
 * to the track, is applied by the mixer per source, and so switches on the exact frame of a gapless join. 0.7
 * crossfade (E1-S4): mp_engine_set_crossfade is implemented and mp_engine_preload_next_ex takes the join mode per
 * boundary, since only the caller knows whether two tracks share an album. E1-S6 (no version change; no export or
 * struct moved): mp_engine_set_output asks the device for its own mix format rather than whatever rate the mixer was
 * left at (in either mode: BASSWASAPI honours a differing rate in shared mode too, and Windows then resamples every
 * frame), and an exclusive mode the driver refuses falls back to shared with MP_EVENT_ERROR saying why. E1-S7 (no
 * version change): device changes are watched and raised as MP_EVENT_DEVICE_LOST / MP_EVENT_DEVICE_CHANGED, whose a,
 * b and message are documented at mp_event_type; the engine parks playback when the open device goes and migrates
 * itself only when the caller asked for MP_DEVICE_DEFAULT. E1-S8 (no ABI change): a DSP on the mixer feeds the
 * analysis tap - fixed 512-frame hops of mixed PCM, each carrying the mixer byte position of its first frame,
 * through a lock-free ring. That is the audio side of the analysis stream; mp_analysis_try_get_latest stays
 * unimplemented until E4-S1, which is what turns a hop into an mp_analysis_frame (the spectrum, bands and onset
 * in that struct are all its work, and half a frame would be worse than none). 0.8 analysis frames (E4-S1): a
 * thread drains the tap and publishes an mp_analysis_frame per hop, so mp_analysis_try_get_latest returns one
 * instead of MP_E_STATE. No struct or signature moved - a caller built against 0.7 sees the same surface start
 * answering - but an export that was never implemented beginning to work is new function, and that is a minor.
 * bands, spectral_centroid_hz, harmonic_ratio and onset stay zero until E4-S2 extracts them. 0.9 presets
 * (E4-S3): mp_renderer_enum_presets, mp_renderer_set_preset and mp_renderer_set_param are implemented over the
 * preset loader, and mp_renderer_create's engine argument is finally read - the renderer polls
 * mp_analysis_try_get_latest per frame and feeds the preset's constant buffer from it. Same reasoning as 0.8,
 * and for three exports rather than one: they were declared at 0.3 and stubbed, nothing about their signatures
 * moved, and a caller that has been checking for MP_E_STATE sees them start working. That is new function, and
 * new function is a minor. set_theme (E4-S6) and set_quality (E4-S7) are still stubs. 0.10 feature extraction
 * (E4-S2): bands, spectral_centroid_hz, harmonic_ratio and onset carry the values documented at
 * mp_analysis_frame instead of the zeros 0.8 promised until now. No field moved and the struct is the same size
 * - but a field that was documented as zero and is now a measurement is new function by the same reading as 0.8
 * and 0.9, and a caller that special-cased the zeros wants to know. The band count stays ten, which is what the
 * header and the preset constant buffer have said since 0.8 and 0.9 (docs/roadmap-and-backlog.md said six; ten
 * is what shipped and ten is what twenty hertz to twenty kilohertz actually is). 0.11 analysis continuity
 * (T-135): the analysis restarts its sliding window when the tap has lost hops instead of sliding it across the
 * gap, publishes no frame until the window is contiguous again, and reports the restarts in the first of the
 * three bytes mp_analysis_frame.reserved held. A minor and not a major, on the same reading as 0.8 to 0.10 and
 * for the same reason the rule at the top gives: nothing moved. The struct is the same size, every field is at
 * the offset it was at, sizeof is the number every caller already passes, and a binary built against 0.10 reads
 * exactly the bytes it read before - what changed is that a byte documented as reserved (so, zero) now carries
 * a measurement, which is new function, and new function is a minor. Note that appending a field would NOT have
 * been: the header's rule says a size the callee does not know is refused, but the check is `struct_size ==
 * sizeof(T)`, so a longer struct would refuse every existing caller rather than serve them the old fields. The
 * reserved bytes are there so that a new field costs nothing, and this is what they are for. 0.12 the
 * struct_size rule becomes the one the rule at the top always claimed (T-140): a caller whose struct_size
 * is smaller than this build's is served the prefix that size covers instead of being refused, so appending a
 * field is at last the minor this header said it was. Nothing moved and no signature changed - what changed is
 * that calls which returned MP_E_INVALID_ARG now do their work, which is new function by the same reading as
 * 0.8 to 0.11, and for a rule rather than an export. A larger struct_size is still refused (the caller is
 * asking for fields this build cannot fill), and so is one too small to hold the struct's first meaningful
 * field. One protocol was tightened to pay for it: mp_engine_enum_devices now requires out[0].struct_size, the
 * way mp_renderer_enum_presets always has, because with a variable element size that number is the stride.
 * 0.13 the theme (E4-S6): mp_renderer_set_theme is implemented over a new field at the end of the preset
 * constant buffer, so the four colours the shell paints its own background gradient from reach every preset
 * too. Same reading as 0.8 to 0.12 - an export that was declared and stubbed since 0.3 beginning to work is new
 * function, and new function is a minor. mp_theme_colors did not move and no signature changed. The *preset*
 * contract is versioned separately and it did move: b0's schema is 2, because a field was appended to it. That
 * costs a schema 1 preset nothing - nothing before `theme` moved, so its shader reads exactly what it read
 * before out of a buffer that is merely longer than the block it declares - and this build still loads one, as
 * mpcore.tests' schema 1 fixtures prove. What a schema number buys is the other direction: a preset written
 * against a contract this build has never heard of is refused rather than guessed at. 0.14 adaptive quality
 * (E4-S7): mp_renderer_set_quality is implemented over a controller on the render thread, and mp_render_stats
 * grows a tail saying what that controller decided and what it decided it on. Same reading as 0.8 to 0.13 for
 * the export - declared and stubbed since 0.3, beginning to work, which is new function and so a minor - and
 * 0.12's reading for the struct: the seven fields are APPENDED, nothing before them moved, so a caller built
 * against 0.13 passes the struct_size it always passed and is served exactly the prefix it understands. That
 * is the thing T-140 bought and this is the first story to spend it. One new enum, mp_render_cost_source,
 * names which of two measurements the controller is acting on; a new type is not a break because no existing
 * signature mentions it.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(MP_STATIC)
#define MP_API
#elif defined(MP_BUILDING_DLL)
#define MP_API __declspec(dllexport)
#else
#define MP_API __declspec(dllimport)
#endif

#define MP_CALL __cdecl

/* ABI version. Interop refuses to load on a MAJOR mismatch (mpcore_abi_version() >> 16). */
#define MP_ABI_MAJOR 0u
#define MP_ABI_MINOR 14u

typedef enum mp_result {
    MP_OK = 0,
    MP_E_INVALID_ARG = 1,
    MP_E_BASS = 2,
    MP_E_DEVICE = 3,
    MP_E_D3D = 4,
    MP_E_STATE = 5,
    MP_E_INTERNAL = 6
} mp_result;

/* ---- version and errors (ABI 0.1) -------------------------------------------------------------- */

/* (MP_ABI_MAJOR << 16) | MP_ABI_MINOR. */
MP_API uint32_t MP_CALL mpcore_abi_version(void);

/* Product version string ("major.minor.patch"), the same value the MSIX and the managed assemblies carry.
 * Static storage; never freed by the caller. */
MP_API const char* MP_CALL mp_version(void);

/* Copies the calling thread's last error message (UTF-8, NUL-terminated, possibly empty) into buf.
 * Returns MP_E_INVALID_ARG when buf is NULL or len is 0; the message is truncated to fit. */
MP_API mp_result MP_CALL mp_last_error(char* buf, size_t len);

/* ---- logging (ABI 0.2) ------------------------------------------------------------------------- */

typedef enum mp_log_level {
    MP_LOG_TRACE = 0,
    MP_LOG_DEBUG = 1,
    MP_LOG_INFO = 2,
    MP_LOG_WARN = 3,
    MP_LOG_ERROR = 4
} mp_log_level;

typedef void(MP_CALL* mp_log_cb)(mp_log_level level, const char* utf8_message, void* user);

/* Installs the process-wide log sink (NULL removes it). Messages at or above min_level are delivered.
 * Never called from the audio callback. */
MP_API mp_result MP_CALL mp_log_set_sink(mp_log_cb sink, void* user, mp_log_level min_level);

/* ---- engine (ABI 0.2) -------------------------------------------------------------------------- */

typedef struct mp_engine mp_engine; /* opaque; one per process */
typedef struct mp_track mp_track;   /* opaque; owned by the engine that opened it */

typedef enum mp_output_mode { MP_OUTPUT_SHARED = 0, MP_OUTPUT_EXCLUSIVE = 1 } mp_output_mode;
typedef enum mp_fade_mode { MP_FADE_NONE = 0, MP_FADE_GUARD = 1 } mp_fade_mode;
/* How a queued track follows the playing one (mp_engine_preload_next_ex). */
typedef enum mp_join_mode {
    MP_JOIN_GAPLESS = 0,  /* sample-continuous: the successor's first frame follows the predecessor's last */
    MP_JOIN_CROSSFADE = 1 /* the user crossfade (mp_engine_set_crossfade) when one is set; else gapless */
} mp_join_mode;

typedef struct mp_engine_config {
    uint32_t struct_size;
    uint32_t sample_rate;   /* mixer rate before an output is set; 0 = 48000. The output's mix format wins. */
    uint32_t channels;      /* 0 = 2 */
    const char* plugin_dir; /* UTF-8 directory holding the BASS add-on DLLs; NULL = the directory of mpcore.dll */
} mp_engine_config;

/* device_index values besides an index from mp_engine_enum_devices. */
#define MP_DEVICE_DEFAULT (-1)
#define MP_DEVICE_NONE (-2) /* no device: audio is produced only when the caller pulls it with mp_engine_render */

typedef struct mp_output_config {
    uint32_t struct_size;
    int32_t device_index; /* index from mp_engine_enum_devices, MP_DEVICE_DEFAULT or MP_DEVICE_NONE */
    mp_output_mode mode;  /* MP_OUTPUT_EXCLUSIVE falls back to shared when the device refuses it */
    uint32_t buffer_ms;   /* 0 = device default */
    uint8_t event_driven; /* 1 = WASAPI event-driven buffering (lower latency; buffer becomes one period) */
    uint8_t reserved[3];
} mp_output_config;

typedef struct mp_device_info {
    uint32_t struct_size;
    int32_t index;
    char name[256];
    char id[256];
    uint32_t mix_sample_rate;
    uint32_t mix_channels;
    uint32_t min_period_us;
    uint32_t default_period_us;
    uint8_t is_default;
    uint8_t is_enabled;
    uint8_t reserved[2];
} mp_device_info;

typedef struct mp_track_info {
    uint32_t struct_size;
    int64_t duration_ms;
    uint32_t sample_rate;
    uint32_t channels;
    uint32_t bits_per_sample; /* 0 when unknown (e.g. lossy formats) */
    char codec[32];           /* "wav", "flac", "mp3", "opus", "ogg", "wv", "ape", "aiff", "mf" (Media Foundation) */
    int64_t total_frames;     /* PCM frames, from the prescan */
} mp_track_info;

typedef struct mp_clock {
    uint32_t struct_size;
    int64_t position_ms;           /* audible position of the current track, output latency compensated */
    int64_t mixer_byte_pos;        /* bytes the mixer has produced since it was created (float frames * channels * 4) */
    uint32_t output_latency_ms;    /* WASAPI buffer length */
    int64_t qpc_ticks;             /* QueryPerformanceCounter at the time of the reading */
    int64_t output_buffered_bytes; /* float bytes mixed but not yet played (what position_ms was compensated by) */
} mp_clock;

typedef struct mp_engine_stats {
    uint32_t struct_size;
    uint64_t callbacks; /* output callbacks served */
    uint64_t underruns; /* callbacks the mixer could not fill while a track was playing */
    uint32_t callback_max_us;
    uint32_t output_sample_rate;
    uint32_t output_channels;
    uint32_t output_buffer_ms;
    uint8_t exclusive;
    uint8_t output_started;
    uint8_t reserved[2];
    char output_format[16]; /* "float", "16bit", "24bit", "32bit", "8bit" */
} mp_engine_stats;

typedef enum mp_event_type {
    MP_EVENT_TRACK_STARTED = 1,  /* a = track handle; b = start_ms for mp_engine_play, the mixer byte position at a
                                    gapless join (mp_clock.mixer_byte_pos units) */
    MP_EVENT_TRACK_ENDED = 2,    /* natural end; a = track handle (0 only if the source ended before mp_engine_play
                                    had recorded it); b = the mixer byte position when a preloaded successor took
                                    over, else 0 */
    MP_EVENT_DEVICE_LOST = 3,    /* the open device went (unplugged, disabled, failed): playback is parked where it
                                    stood and the output is closed. a = the device index that went, b = 0, message =
                                    its endpoint id. Reopen with mp_engine_set_output and resume; nothing is lost. */
    MP_EVENT_DEVICE_CHANGED = 4, /* a = device index, message = its endpoint id. b = 1 when playback has already
                                    moved there (the caller asked for MP_DEVICE_DEFAULT and the default moved), b = 0
                                    when the device is only being offered - the one that was lost has come back, and
                                    it is the caller's choice whether to switch to it. */
    MP_EVENT_UNDERRUN = 5,
    MP_EVENT_ERROR = 6 /* message set */
} mp_event_type;

typedef struct mp_event {
    uint32_t struct_size;
    mp_event_type type;
    int64_t a;
    int64_t b;
    const char* message; /* UTF-8, valid only for the duration of the callback; may be NULL */
} mp_event;

typedef void(MP_CALL* mp_event_cb)(const mp_event* ev, void* user);

/* Creates the engine: BASS "no sound" device, plugins loaded, mixer created. One engine per process. */
MP_API mp_result MP_CALL mp_engine_create(const mp_engine_config* config, mp_engine** out_engine);
/* Stops the output, joins the audio thread, frees every track and BASS. No callback fires after return. */
MP_API mp_result MP_CALL mp_engine_destroy(mp_engine* engine);

/* (Re)opens the output. The device is opened at its own mix format (mp_device_info.mix_sample_rate / mix_channels) in
 * both modes and the mixer adopts what was granted, so a device sitting at 44 100 Hz is driven at 44 100 Hz and a
 * 44 100 Hz source reaches it without being resampled by anyone; a playing track continues across the change.
 * MP_OUTPUT_EXCLUSIVE additionally lets BASSWASAPI pick the nearest format the device accepts in exclusive mode when
 * it will not take that one. Exclusive mode is an opt-in a driver
 * may refuse (another application holds the device, or no offered format is accepted): rather than leave the caller
 * without output, the device is then opened in shared mode, MP_EVENT_ERROR carries a message naming the device and
 * the reason, and mp_engine_stats.exclusive reads 0. A shared-mode failure is the caller's to handle and raises no
 * event. */
MP_API mp_result MP_CALL mp_engine_set_output(mp_engine* engine, const mp_output_config* config);
/* Output devices. Call with out = NULL to get the count; otherwise *count is in/out (capacity/written) and
 * out[0].struct_size is the size of every element (ABI 0.12). */
MP_API mp_result MP_CALL mp_engine_enum_devices(mp_engine* engine, mp_device_info* out, uint32_t* count);
MP_API mp_result MP_CALL mp_engine_set_event_callback(mp_engine* engine, mp_event_cb callback, void* user);

/* Opens a file as a decode stream and prescans it. */
MP_API mp_result MP_CALL mp_track_open(mp_engine* engine, const char* utf8_path, mp_track** out_track);
MP_API mp_result MP_CALL mp_track_close(mp_track* track);
MP_API mp_result MP_CALL mp_track_get_info(mp_track* track, mp_track_info* out_info);

/* Replaces the current source with track at start_ms and starts the output if needed. A source that was
 * playing is guard-faded out first (50 ms, waited for on a live device); the new one fades in over 50 ms. */
MP_API mp_result MP_CALL mp_engine_play(mp_engine* engine, mp_track* track, int64_t start_ms);
/* Queues next (rewound to 0) to start at mix time exactly where the current track ends: no gap, no overlap, no
 * fade. NULL clears the queue; so does mp_engine_stop, closing the track, or playing it by hand. Fires
 * MP_EVENT_TRACK_ENDED then MP_EVENT_TRACK_STARTED (b = mixer byte position of the join) from the audio thread.
 * Encoder delay and padding are removed by the decoder where the format carries them (see
 * docs/spikes/e1-s2-gapless-join.md for the per-format result). */
MP_API mp_result MP_CALL mp_engine_preload_next(mp_engine* engine, mp_track* next);
/* mp_engine_preload_next with the join chosen per boundary (ABI 0.7). MP_JOIN_GAPLESS is mp_engine_preload_next.
 * MP_JOIN_CROSSFADE: when a crossfade is set and the playing track's length is known, next starts crossfade_ms
 * before the playing track's end and the two overlap on an equal-power curve (the outgoing follows cos, the
 * incoming sin, so their powers sum to one); each track's ReplayGain still applies under the fade. The crossfade
 * cannot start exactly on a frame: the incoming starts at the beginning of the output buffer in which the fade
 * point is mixed, up to one buffer early. MP_EVENT_TRACK_STARTED(next, b = that mixer byte position) fires when the
 * overlap starts, and MP_EVENT_TRACK_ENDED(previous, b = 0) when the outgoing track's last frame is mixed. The
 * clock follows next from the start of the overlap. With no crossfade set, an unknown length, or a fade point
 * already passed (also by a seek into the fade window), the join is the gapless one. The caller decides the mode:
 * the engine knows nothing of albums. */
MP_API mp_result MP_CALL mp_engine_preload_next_ex(mp_engine* engine, mp_track* next, mp_join_mode mode);
/* Fades out over 50 ms on the audio thread, then holds: the output keeps running with silence and the source
 * position freezes, so resume is immediate. Returns at once. */
MP_API mp_result MP_CALL mp_engine_pause(mp_engine* engine);
MP_API mp_result MP_CALL mp_engine_resume(mp_engine* engine);
/* MP_FADE_GUARD fades out over 50 ms before the source is removed (waited for on a live device). */
MP_API mp_result MP_CALL mp_engine_stop(mp_engine* engine, mp_fade_mode fade);
/* Guard-faded: out, reposition, in. While paused the position moves and the hold stays. */
MP_API mp_result MP_CALL mp_engine_seek(mp_engine* engine, int64_t position_ms);
/* Slider position 0..1 on an audio taper: gain = 10^(2 (v - 1)) (-20 dB at 0.5, -40 dB just above 0, silence
 * at 0). Applied at the mixer output, interpolated across one output buffer so there is no zipper noise;
 * 0 therefore mutes within one buffer without a click. */
MP_API mp_result MP_CALL mp_engine_set_volume(mp_engine* engine, float linear);
/* ReplayGain for one track: gain_db is the whole gain to apply (tag gain plus the user's preamp, decided by the
 * caller), peak the tagged linear peak of the source (1.0 = full scale; <= 0 means unknown). The gain is applied
 * per source by the mixer, so a preloaded track's gain takes effect on the exact frame of its gapless join, and
 * it survives seeks and output changes. Clipping prevention: when peak is known and peak * 10^(gain_db/20) would
 * exceed full scale, the gain is reduced to 1/peak. A change to the track being heard is ramped by the mixer, not
 * stepped. Non-finite arguments are MP_E_INVALID_ARG. */
MP_API mp_result MP_CALL mp_track_set_replaygain(mp_track* track, float gain_db, float peak);
/* User crossfade length for MP_JOIN_CROSSFADE joins: 0 = off (the join is gapless), clamped to 12 000 ms. Takes
 * effect on the queued join too, unless its fade point has passed. Manual skips, stops and seeks keep the 50 ms
 * guard fade; the crossfade is only ever between a track and the one queued after it. */
MP_API mp_result MP_CALL mp_engine_set_crossfade(mp_engine* engine, uint32_t ms);
/* Lock-free with respect to the audio thread. */
MP_API mp_result MP_CALL mp_engine_get_clock(mp_engine* engine, mp_clock* out_clock);
MP_API mp_result MP_CALL mp_engine_get_stats(mp_engine* engine, mp_engine_stats* out_stats);
/* Pulls the next frames of mixed float PCM (interleaved, the mixer's channel count) exactly as the output
 * thread would, volume and fades applied. Only with MP_DEVICE_NONE; MP_E_STATE when a device is open. */
MP_API mp_result MP_CALL mp_engine_render(mp_engine* engine, float* out_interleaved, uint32_t frames);

MP_API mp_result MP_CALL mp_preview_start(mp_engine* engine, mp_track* track,
                                          float gain_db);    /* not implemented until E5-S5 */
MP_API mp_result MP_CALL mp_preview_stop(mp_engine* engine); /* not implemented until E5-S5 */

/* ---- analysis (ABI 0.2 draft; the tap that feeds it is E1-S8, the frame itself ABI 0.8 / E4-S1) -- */

#define MP_ANALYSIS_SPECTRUM_BINS 1024u
#define MP_ANALYSIS_WAVEFORM_SAMPLES 512u
#define MP_ANALYSIS_OCTAVE_BANDS 10u

/* One 512-frame hop turned into features (E4-S1). `sequence` counts frames since the engine was created and is
 * how a poller knows what it holds is new; `mixer_byte_pos` is the hop's first frame in the units of
 * mp_clock.mixer_byte_pos, so a frame lines up with what is being heard exactly as a gapless join does
 * (mp_clock.mixer_byte_pos minus output_buffered_bytes).
 *
 * `spectrum` is a 2048-point Hann-windowed real FFT advanced one hop at a time, bins 0..1023 (rate/2048 apart:
 * 23.44 Hz at 48 kHz; Nyquist is dropped). Magnitudes are scaled so a full-scale sine reads 1.0 in its own bin.
 * `waveform` is the newest hop mixed to mono. `rms` and `peak` are of the hop as mixed, all channels.
 *
 * The rest are E4-S2's extraction over that spectrum. `bands` are ten octaves, centred on the ISO 31.5 Hz to
 * 16 kHz series and partitioning bins 1..1023 between them (bin 0 is DC and is in none of them; the top band is
 * closed at the last bin). A band is the quadrature sum of its magnitudes over the Hann window's 1.5-bin noise
 * bandwidth, which makes it an amplitude on the same scale as `spectrum`: a full-scale sine anywhere inside a
 * band reads 1.0 there, and a tone on a band edge splits between two bands in quadrature rather than appearing
 * in both. `spectral_centroid_hz` is the magnitude-weighted mean frequency over bins 1..1023, and 0 in silence.
 * `harmonic_ratio` is one minus the spectral flatness (the geometric mean of the bin magnitudes over their
 * arithmetic mean): 0..1, how much of the spectrum is tone rather than noise - ~1.0 for a sine, ~0.15 for white
 * noise, 0 in silence. It is not a count of harmonics. `onset` is 1 on the hop a transient was detected in
 * (half-wave-rectified spectral flux against a threshold that is part of the frame's own spectral sum and part
 * the median of the last 43 hops' flux), and stays 0 for three hops afterwards so one event is one flag. Its
 * resolution is the hop: the flag means "during the 10.67 ms starting at `mixer_byte_pos`".
 *
 * `discontinuities` counts, modulo 256, the times the analysis has had to restart: the tap's ring overran and
 * hops were lost, so the window could not slide on. A consumer that carries anything from one frame to the next
 * - a smoothed level, a beat history, anything integrated over time - compares this with the value on the frame
 * it held before, and starts again if it moved. It is a count and not a flag because the frame it would flag is
 * one frame in ninety-four a second, and a 30 Hz poll would miss two out of three of them; a number that
 * differs is still different however slowly it is sampled. Every frame published is the transform of 2048
 * contiguous samples whatever this says - after a gap the analysis withholds frames until that is true again,
 * rather than publishing the spectrum of a splice - so this is about what a consumer computed across frames,
 * never about the frame in hand. In real-time playback it never moves: the ring holds 170 ms and the producer
 * is the WASAPI mix thread. It moves where audio is made faster than it is played, as a headless render does. */
typedef struct mp_analysis_frame {
    uint32_t struct_size;
    uint32_t sequence;
    int64_t mixer_byte_pos;
    int64_t qpc_ticks;
    float spectrum[MP_ANALYSIS_SPECTRUM_BINS];
    float waveform[MP_ANALYSIS_WAVEFORM_SAMPLES];
    float bands[MP_ANALYSIS_OCTAVE_BANDS];
    float rms;
    float peak;
    float spectral_centroid_hz;
    float harmonic_ratio;
    uint8_t onset;
    uint8_t discontinuities;
    uint8_t reserved[2];
} mp_analysis_frame;

/* Copies the newest complete frame. MP_E_STATE when none is available yet (nothing has played since the engine,
 * or the mixer, was created). Lock-free with respect to the analysis thread, and safe from any thread. */
MP_API mp_result MP_CALL mp_analysis_try_get_latest(mp_engine* engine, mp_analysis_frame* out_frame);

/* ---- renderer (ABI 0.3; E0-S5 spike, E4-S3 completes the preset surface) ---------------------- */

typedef struct mp_renderer mp_renderer; /* opaque; one per SwapChainPanel */

typedef struct mp_renderer_config {
    uint32_t struct_size;
    uint32_t width; /* initial back-buffer size in physical pixels; 0 = 1 until mp_renderer_resize */
    uint32_t height;
    float scale_x; /* SwapChainPanel CompositionScaleX/Y; 0 = 1.0 */
    float scale_y;
    uint8_t force_warp; /* 1 = software rasteriser (also the automatic fallback when no hardware device) */
    uint8_t vsync;      /* 1 = Present(1); 0 = present as fast as the compositor allows */
    uint8_t headless;   /* 1 = render into an offscreen texture; swap_chain_panel_native may be NULL (tests) */
    uint8_t reserved;
} mp_renderer_config;

#define MP_RENDER_HISTOGRAM_BUCKETS 6u

/* Which measurement the quality controller is deciding on (ABI 0.14). The frame-to-frame interval is not a
 * measurement of how much work a frame is: with vsync it is pinned to the refresh whether the GPU is idle or
 * drowning, so a controller reading it would be blind on exactly the path the product ships. A D3D11 timestamp
 * pair around the draw is the work itself. The interval is the fallback for a device that will not make the
 * queries or keeps reporting them disjoint, and it is honest where the renderer is headless and unpaced -
 * which is where the tests measure. */
typedef enum mp_render_cost_source {
    MP_RENDER_COST_GPU_TIMESTAMP = 0,
    MP_RENDER_COST_FRAME_INTERVAL = 1
} mp_render_cost_source;

typedef struct mp_render_stats {
    uint32_t struct_size;
    uint64_t frames;  /* frames rendered since creation */
    uint64_t resizes; /* ResizeBuffers performed on the render thread */
    double fps;       /* frames in the most recent whole second */
    float frame_ms_last;
    float frame_ms_max;
    float frame_ms_avg;
    uint32_t
        frame_ms_histogram[MP_RENDER_HISTOGRAM_BUCKETS]; /* frame-to-frame: <8.4, <16.7, <20, <33.4, <50, >=50 ms */
    uint64_t dxgi_present_count;                         /* DXGI_FRAME_STATISTICS.PresentCount */
    uint64_t dxgi_missed_refreshes;                      /* refresh intervals skipped between consecutive presents */
    uint32_t width;
    uint32_t height;
    uint8_t warp;
    uint8_t headless;
    uint8_t device_lost;
    uint8_t visible;
    char adapter[128];
    /* ---- adaptive quality (ABI 0.14, appended) ----
     * A caller built against 0.13 passes the shorter struct_size and never sees these; a caller that does see
     * them and has never called mp_renderer_set_quality sees the defaults, which are what a renderer starts at.
     */
    uint32_t quality_policy;  /* mp_quality_policy as last requested; MP_QUALITY_AUTO until one is set */
    uint32_t quality_tier;    /* the tier in force: MP_QUALITY_LOW / MEDIUM / HIGH, never MP_QUALITY_AUTO,
                               * because auto is a policy and something is always drawing at one of the three */
    uint32_t quality_changes; /* tier changes the CONTROLLER decided, since the renderer was created; a tier
                               * the caller pinned is not one. The oscillation counter: a controller that is
                               * thrashing says so here rather than only on screen */
    uint32_t render_width;    /* the rectangle of the back buffer the picture is drawn into, which is the
                               * surface size times the tier's render scale. Equals width/height at High */
    uint32_t render_height;
    float render_scale;   /* 1.0 High, 0.75 Medium, 0.5 Low */
    float frame_cost_ms;  /* the smoothed per-frame cost the controller is deciding on */
    uint32_t cost_source; /* mp_render_cost_source: where frame_cost_ms came from */
} mp_render_stats;

/* One entry of the preset catalogue. A preset is data (ADR-009): a directory holding preset.json and the HLSL it
 * names, compiled by the core at load. The core carries one built-in preset that is always present and always
 * first, so a missing or empty preset directory costs the user a choice, not a picture. */
typedef struct mp_preset_info {
    uint32_t struct_size;
    char id[64];
    char name[128];
} mp_preset_info;

typedef struct mp_theme_colors {
    uint32_t struct_size;
    float primary[4];
    float secondary[4];
    float accent[4];
    float background[4];
} mp_theme_colors;

/* AUTO is the renderer's own judgement and the other three pin it. AUTO is a policy and never a tier: what
 * mp_render_stats.quality_tier reports is always one of LOW, MEDIUM or HIGH. */
typedef enum mp_quality_policy {
    MP_QUALITY_AUTO = 0,
    MP_QUALITY_LOW = 1,
    MP_QUALITY_MEDIUM = 2,
    MP_QUALITY_HIGH = 3
} mp_quality_policy;

/* Creates the device (hardware, WARP fallback), the flip-model composition swap chain and the render thread, and
 * hands the swap chain to the SwapChainPanel. Call on the UI thread; swap_chain_panel_native is the panel's
 * IUnknown (the core queries ISwapChainPanelNative). engine may be NULL, and then presets see silence: with one,
 * the render thread reads mp_analysis_try_get_latest per frame and the preset's constant buffer carries it. */
MP_API mp_result MP_CALL mp_renderer_create(mp_engine* engine, void* swap_chain_panel_native,
                                            const mp_renderer_config* config, mp_renderer** out_renderer);
/* Stops and joins the render thread, releases the swap chain and device. */
MP_API mp_result MP_CALL mp_renderer_destroy(mp_renderer* renderer);
/* Physical pixel size and composition scale; applied on the render thread before the next frame. */
MP_API mp_result MP_CALL mp_renderer_resize(mp_renderer* renderer, uint32_t width, uint32_t height, float scale_x,
                                            float scale_y);
/* 0 pauses the render loop (hidden panel, minimised window); 1 resumes it. */
MP_API mp_result MP_CALL mp_renderer_set_visible(mp_renderer* renderer, uint8_t visible);
MP_API mp_result MP_CALL mp_renderer_get_stats(mp_renderer* renderer, mp_render_stats* out_stats);
/* Two calls, as mp_engine_enum_devices: out == NULL puts the total in *count; otherwise at most *count entries
 * are written (out[0].struct_size set by the caller names the size of every element) and *count becomes how
 * many were. The catalogue is scanned when the renderer is created and does not change under the caller. */
MP_API mp_result MP_CALL mp_renderer_enum_presets(mp_renderer* renderer, mp_preset_info* out, uint32_t* count);
/* Switches preset. The HLSL is compiled on the calling thread and only swapped in if the device accepted it, so
 * MP_E_D3D means nothing changed - the preset that was drawing is still drawing - and mp_last_error carries the
 * shader compiler's own diagnostic (file, line, error code, text) for the caller to show. MP_E_INVALID_ARG when
 * no preset has that id. */
MP_API mp_result MP_CALL mp_renderer_set_preset(mp_renderer* renderer, const char* utf8_id);
/* Sets a parameter the active preset declares in its manifest; a value outside the declared range is clamped to
 * it. MP_E_INVALID_ARG names the parameter, and what the preset does declare, when it does not declare this one.
 * Parameters return to their defaults on a preset switch. */
MP_API mp_result MP_CALL mp_renderer_set_param(mp_renderer* renderer, const char* utf8_name, float value);
/* Sets the renderer-wide theme (E4-S6): the four colours reach every preset as b0's `theme`, in this struct's
 * order, and survive a preset switch - unlike a parameter, which belongs to one preset and returns to its
 * default. Channels outside 0..1 are clamped to it; a channel that is not a finite number is MP_E_INVALID_ARG
 * and nothing changes. Until a theme is set every channel is zero, so alpha 0 is how a preset reads "the shell
 * has not told me one". Cheap and safe to call at the rate the shell's own theming runs (30 Hz). */
MP_API mp_result MP_CALL mp_renderer_set_theme(mp_renderer* renderer, const mp_theme_colors* colors);
/* How the renderer is allowed to trade detail for frame rate (E4-S7). MP_QUALITY_AUTO - the default - hands
 * the tier to a controller on the render thread; the other three pin it and stop the controller deciding.
 *
 * What a tier changes is the RENDER SCALE: the back buffer stays the size of the panel and the picture is
 * drawn into 100%, 75% or 50% of it, which the compositor stretches back out. That is the only lever, and
 * T-148 is why: of the four shipped presets only ambient-glow can exhaust a rasteriser (150 fps on WARP
 * against 1331-2596 for the other three), and it is the only one whose cost is per-pixel rather than
 * per-primitive - so a lever measured in pixels is the only one that helps the preset that needs help.
 *
 * The controller decides on a smoothed per-frame cost (mp_render_stats.frame_cost_ms) against one refresh at
 * 60 Hz. It drops a tier after three quarters of a second over budget, and raises one only when the cost it
 * PREDICTS at the tier above - the smoothed cost times the ratio it last measured across that boundary - is
 * inside 80% of the budget. Predicting rather than thresholding is what stops it alternating on a preset
 * whose cost changes a lot between tiers; a raise it has to undo doubles the wait before the next one, which
 * bounds the alternation even when the prediction is wrong.
 *
 * Everything the controller decided, and what it decided it on, is in mp_render_stats' 0.14 tail. Cheap to
 * call; MP_E_INVALID_ARG on a policy that is not one of the four. */
MP_API mp_result MP_CALL mp_renderer_set_quality(mp_renderer* renderer, mp_quality_policy policy);

#ifdef __cplusplus
} /* extern "C" */
#endif
