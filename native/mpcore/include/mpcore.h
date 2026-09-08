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
 * The renderer exports (mp_renderer_*) are drafted by the render spike (E0-S5).
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
#define MP_ABI_MINOR 2u

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

typedef struct mp_engine_config {
    uint32_t struct_size;
    uint32_t sample_rate;   /* mixer rate before an output is set; 0 = 48000. The output's mix format wins. */
    uint32_t channels;      /* 0 = 2 */
    const char* plugin_dir; /* UTF-8 directory holding the BASS add-on DLLs; NULL = the directory of mpcore.dll */
} mp_engine_config;

typedef struct mp_output_config {
    uint32_t struct_size;
    int32_t device_index; /* index from mp_engine_enum_devices; -1 = default device */
    mp_output_mode mode;
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
    MP_EVENT_TRACK_STARTED = 1,
    MP_EVENT_TRACK_ENDED = 2, /* natural end; a = track handle as integer */
    MP_EVENT_DEVICE_LOST = 3,
    MP_EVENT_DEVICE_CHANGED = 4,
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

/* (Re)opens the output. The mixer adopts the device's mix format; a playing track continues. */
MP_API mp_result MP_CALL mp_engine_set_output(mp_engine* engine, const mp_output_config* config);
/* Output devices. Call with out = NULL to get the count; otherwise *count is in/out (capacity/written). */
MP_API mp_result MP_CALL mp_engine_enum_devices(mp_engine* engine, mp_device_info* out, uint32_t* count);
MP_API mp_result MP_CALL mp_engine_set_event_callback(mp_engine* engine, mp_event_cb callback, void* user);

/* Opens a file as a decode stream and prescans it. */
MP_API mp_result MP_CALL mp_track_open(mp_engine* engine, const char* utf8_path, mp_track** out_track);
MP_API mp_result MP_CALL mp_track_close(mp_track* track);
MP_API mp_result MP_CALL mp_track_get_info(mp_track* track, mp_track_info* out_info);

/* Replaces the current source with track at start_ms and starts the output if needed. */
MP_API mp_result MP_CALL mp_engine_play(mp_engine* engine, mp_track* track, int64_t start_ms);
MP_API mp_result MP_CALL mp_engine_preload_next(mp_engine* engine, mp_track* next); /* not implemented until E1-S3 */
MP_API mp_result MP_CALL mp_engine_pause(mp_engine* engine);
MP_API mp_result MP_CALL mp_engine_resume(mp_engine* engine);
MP_API mp_result MP_CALL mp_engine_stop(mp_engine* engine, mp_fade_mode fade);
MP_API mp_result MP_CALL mp_engine_seek(mp_engine* engine, int64_t position_ms);
MP_API mp_result MP_CALL mp_engine_set_volume(mp_engine* engine, float linear);
MP_API mp_result MP_CALL mp_engine_set_replaygain(mp_engine* engine, float gain_db,
                                                  float peak);                    /* not implemented until E1-S5 */
MP_API mp_result MP_CALL mp_engine_set_crossfade(mp_engine* engine, uint32_t ms); /* not implemented until E1-S4 */
/* Lock-free with respect to the audio thread. */
MP_API mp_result MP_CALL mp_engine_get_clock(mp_engine* engine, mp_clock* out_clock);
MP_API mp_result MP_CALL mp_engine_get_stats(mp_engine* engine, mp_engine_stats* out_stats);

MP_API mp_result MP_CALL mp_preview_start(mp_engine* engine, mp_track* track,
                                          float gain_db);    /* not implemented until E5-S5 */
MP_API mp_result MP_CALL mp_preview_stop(mp_engine* engine); /* not implemented until E5-S5 */

/* ---- analysis (ABI 0.2 draft; implemented by E1-S8 / E4-S1) ------------------------------------ */

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
                                                    mp_analysis_frame* out_frame); /* not implemented until E1-S8 */

#ifdef __cplusplus
} /* extern "C" */
#endif
