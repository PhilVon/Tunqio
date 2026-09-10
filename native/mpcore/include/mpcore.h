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
 *  - Structs are POD with uint32_t struct_size first; the callee rejects a size it does not know with
 *    MP_E_INVALID_ARG, so fields can be appended in an ABI-minor bump.
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
 * in that struct are all its work, and half a frame would be worse than none).
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
#define MP_ABI_MINOR 7u

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
/* Output devices. Call with out = NULL to get the count; otherwise *count is in/out (capacity/written). */
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

/* ---- analysis (ABI 0.2 draft; the tap that feeds it is E1-S8, the frame itself E4-S1) ---------- */

#define MP_ANALYSIS_SPECTRUM_BINS 1024u
#define MP_ANALYSIS_WAVEFORM_SAMPLES 512u
#define MP_ANALYSIS_OCTAVE_BANDS 10u

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
    uint8_t reserved[3];
} mp_analysis_frame;

/* Copies the newest complete frame. MP_E_STATE when none is available yet. */
MP_API mp_result MP_CALL mp_analysis_try_get_latest(mp_engine* engine,
                                                    mp_analysis_frame* out_frame); /* not implemented until E4-S1 */

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
} mp_render_stats;

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

typedef enum mp_quality_policy {
    MP_QUALITY_AUTO = 0,
    MP_QUALITY_LOW = 1,
    MP_QUALITY_MEDIUM = 2,
    MP_QUALITY_HIGH = 3
} mp_quality_policy;

/* Creates the device (hardware, WARP fallback), the flip-model composition swap chain and the render thread, and
 * hands the swap chain to the SwapChainPanel. Call on the UI thread; swap_chain_panel_native is the panel's
 * IUnknown (the core queries ISwapChainPanelNative). engine may be NULL until the analysis stream lands (E4-S1). */
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
MP_API mp_result MP_CALL mp_renderer_enum_presets(mp_renderer* renderer, mp_preset_info* out,
                                                  uint32_t* count); /* not implemented until E4-S3 */
MP_API mp_result MP_CALL mp_renderer_set_preset(mp_renderer* renderer,
                                                const char* utf8_id); /* not implemented until E4-S3 */
MP_API mp_result MP_CALL mp_renderer_set_param(mp_renderer* renderer, const char* utf8_name,
                                               float value); /* not implemented until E4-S3 */
MP_API mp_result MP_CALL mp_renderer_set_theme(mp_renderer* renderer,
                                               const mp_theme_colors* colors); /* not implemented until E4-S6 */
MP_API mp_result MP_CALL mp_renderer_set_quality(mp_renderer* renderer,
                                                 mp_quality_policy policy); /* not implemented until E4-S7 */

#ifdef __cplusplus
} /* extern "C" */
#endif
