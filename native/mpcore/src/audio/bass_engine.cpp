// BASS + BASSmix + BASSWASAPI engine. The only translation unit that includes BASS headers.
//
// Shape (ADR-003, ADR-010): BASS runs on the "no sound" device purely as a decoder; a BASSmix decode
// mixer is the single source of PCM; BASSWASAPI owns the output thread and pulls from the mixer through
// output_proc. Nothing on that path allocates, locks or logs: it is one BASS_ChannelGetData plus the
// envelope-and-volume loop in pull(). With MP_DEVICE_NONE there is no output thread and render() is the
// same pull stage driven by the caller, which is how the tests hear the engine.
//
// Fades (E1-S1): the audio thread owns an envelope that ramps linearly between 0 and 1 over
// k_guard_fade_ms. Pause asks for 0 and, once the ramp lands, holds: the output keeps running with
// silence and the mixer is left alone, so the position freezes and resume is immediate. Stop, seek and a
// play over a running source fade out first and, on a live device, wait for the ramp before touching the
// mixer. Volume is a separate gain on an audio taper, interpolated across each buffer.
//
// Gapless join (E1-S2 spike, the first cut E1-S3 builds on): preload_next parks a second source; the
// MIXTIME END sync of the playing source runs on the audio thread at the exact mix position where that
// source ends, and BASSmix applies a BASS_Mixer_StreamAddChannelEx made inside such a sync at that same
// position, so the successor's first frame follows the predecessor's last with nothing between them. BASS
// itself removes encoder delay and padding for MP3 (LAME/Xing/VBRI/iTunes headers) unless
// BASS_MP3_IGNOREDELAY is passed; what each other format gives is measured in docs/spikes/e1-s2-gapless-join.md.
//
// Crossfade (E1-S4): the engine's envelope above is one gain on the mixed output, so it cannot fade two sources
// against each other, and a BASS_ChannelSlideAttribute on a source inside a mixer never advances (measured in
// E1-S5). Each source therefore carries its own equal-power envelope in a DSP (fade_proc) that the mixer runs as
// it decodes that source, keyed to the source's own frame position; BASSmix then applies the source's
// BASS_ATTRIB_VOL (ReplayGain) on top. A mix-time POS sync on the playing source (fade_sync) adds the queued one
// at the fade point, the way end_sync adds it at the end for a gapless join. A source added mid-buffer starts at
// that buffer's beginning (spike finding 1), which for a user crossfade of seconds is an alignment error of one
// output buffer and accepted; the gapless join keeps its LIMIT exactness.
#include "audio/bass_engine.h"

#include "abi/last_error.h"
#include "audio/mp4_gapless.h"
#include "common/log.h"
#include "common/rt_guard.h"

#include <algorithm>
#include <bass.h>
#include <bassmix.h>
#include <basswasapi.h>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <thread>

#include <windows.h>

namespace mp::audio {
namespace {

std::atomic<bool> g_instance_exists{false};
// The one engine, for the BASSWASAPI notification callback's test seam. Set after create() succeeds and cleared
// before the destructor touches anything, so a simulated notification can never reach a half-built engine.
std::atomic<engine*> g_instance{nullptr};

std::wstring utf8_to_wide(const char* utf8) {
    if (utf8 == nullptr || *utf8 == '\0') {
        return {};
    }
    const int n = MultiByteToWideChar(CP_UTF8, 0, utf8, -1, nullptr, 0);
    if (n <= 0) {
        return {};
    }
    std::wstring w(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, utf8, -1, w.data(), n);
    return w;
}

void copy_utf8(char* dst, size_t cap, const char* src) {
    if (src == nullptr) {
        dst[0] = '\0';
        return;
    }
    const size_t n = std::min(std::strlen(src), cap - 1);
    std::memcpy(dst, src, n);
    dst[n] = '\0';
}

const char* bass_error_name(int code) {
    switch (code) {
    case BASS_OK:
        return "OK";
    case BASS_ERROR_MEM:
        return "MEM";
    case BASS_ERROR_FILEOPEN:
        return "FILEOPEN";
    case BASS_ERROR_DRIVER:
        return "DRIVER";
    case BASS_ERROR_HANDLE:
        return "HANDLE";
    case BASS_ERROR_FORMAT:
        return "FORMAT";
    case BASS_ERROR_INIT:
        return "INIT";
    case BASS_ERROR_ALREADY:
        return "ALREADY";
    case BASS_ERROR_ILLPARAM:
        return "ILLPARAM";
    case BASS_ERROR_DEVICE:
        return "DEVICE";
    case BASS_ERROR_NOTFILE:
        return "NOTFILE";
    case BASS_ERROR_NOTAVAIL:
        return "NOTAVAIL";
    case BASS_ERROR_FILEFORM:
        return "FILEFORM";
    case BASS_ERROR_CODEC:
        return "CODEC";
    case BASS_ERROR_ENDED:
        return "ENDED";
    case BASS_ERROR_BUSY:
        return "BUSY";
    default:
        return "UNKNOWN";
    }
}

// Records "what failed: BASS error N (NAME)" and maps the BASS code onto mp_result.
mp_result bass_fail(const char* what) {
    const int code = BASS_ErrorGetCode();
    char text[256];
    std::snprintf(text, sizeof text, "%s: BASS error %d (%s)", what, code, bass_error_name(code));
    mp::abi::set_last_error(text);
    log(MP_LOG_ERROR, "%s", text);
    switch (code) {
    case BASS_ERROR_DEVICE:
    case BASS_ERROR_DRIVER:
    case BASS_ERROR_BUSY:
    case BASS_ERROR_NOTAVAIL:
        return MP_E_DEVICE;
    case BASS_ERROR_INIT:
    case BASS_ERROR_ALREADY:
        return MP_E_STATE;
    default:
        return MP_E_BASS;
    }
}

// The mix format of the device BASS_WASAPI_Init(device_index) will open. MP_DEVICE_DEFAULT is resolved to the
// output device Windows marks default, since BASS_WASAPI_GetDeviceInfo has no such index.
bool wasapi_device_info(int32_t device_index, BASS_WASAPI_DEVICEINFO& out) {
    if (device_index >= 0) {
        return BASS_WASAPI_GetDeviceInfo(static_cast<DWORD>(device_index), &out) != FALSE;
    }
    if (device_index != MP_DEVICE_DEFAULT) {
        return false;
    }
    BASS_WASAPI_DEVICEINFO di{};
    for (DWORD i = 0; BASS_WASAPI_GetDeviceInfo(i, &di); ++i) {
        if ((di.flags & (BASS_DEVICE_INPUT | BASS_DEVICE_LOOPBACK)) != 0) {
            continue;
        }
        if ((di.flags & BASS_DEVICE_DEFAULT) != 0) {
            out = di;
            return true;
        }
    }
    return false;
}

#if defined(MP_STATIC)
std::atomic<bool> g_force_exclusive_failure{false};
bool exclusive_init_forced_to_fail() noexcept {
    return g_force_exclusive_failure.load(std::memory_order_relaxed);
}
#else
constexpr bool exclusive_init_forced_to_fail() noexcept {
    return false;
}
#endif

mp_result state_fail(const char* what) {
    mp::abi::set_last_error(what);
    return MP_E_STATE;
}

const char* codec_name(DWORD ctype) {
    if ((ctype & BASS_CTYPE_STREAM_WAV) == BASS_CTYPE_STREAM_WAV) {
        return "wav";
    }
    switch (ctype) {
    case BASS_CTYPE_STREAM_MP3:
        return "mp3";
    case BASS_CTYPE_STREAM_OGG:
        return "ogg";
    case BASS_CTYPE_STREAM_AIFF:
        return "aiff";
    case BASS_CTYPE_STREAM_MF:
        return "mf";
    case 0x10900:
        return "flac"; // BASS_CTYPE_STREAM_FLAC (bassflac.h)
    case 0x10901:
        return "flac"; // BASS_CTYPE_STREAM_FLAC_OGG
    case 0x11200:
        return "opus"; // BASS_CTYPE_STREAM_OPUS (bassopus.h)
    case 0x10500:
        return "wv"; // BASS_CTYPE_STREAM_WV (basswv.h)
    case 0x10700:
        return "ape"; // BASS_CTYPE_STREAM_APE (bass_ape.h)
    default:
        return "unknown";
    }
}

std::wstring module_directory() {
    HMODULE module = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCWSTR>(&module_directory), &module);
    wchar_t path[MAX_PATH];
    const DWORD n = GetModuleFileNameW(module, path, MAX_PATH);
    if (n == 0 || n >= MAX_PATH) {
        return {};
    }
    std::wstring dir{path, n};
    const size_t slash = dir.find_last_of(L"\\/");
    return slash == std::wstring::npos ? std::wstring{} : dir.substr(0, slash + 1);
}

int64_t qpc_now() {
    LARGE_INTEGER t;
    QueryPerformanceCounter(&t);
    return t.QuadPart;
}

int64_t qpc_frequency() {
    static const int64_t f = [] {
        LARGE_INTEGER fr;
        QueryPerformanceFrequency(&fr);
        return fr.QuadPart;
    }();
    return f;
}

constexpr uint32_t k_default_rate = 48000;
constexpr uint32_t k_default_channels = 2;
constexpr uint32_t k_guard_wait_ms = 150; // longest a control call waits for a fade on a live device

// NORAMPIN: BASSmix must not add its own ramp; the envelope in pull() is the fade-in. LIMIT: the mixer stops a
// BASS_ChannelGetData at the exact frame the source ends instead of finishing the buffer, so the END sync (and
// a successor added in it) lands on that frame rather than on the buffer boundary; pull() loops to fill the rest.
constexpr DWORD k_source_flags = BASS_MIXER_CHAN_NORAMPIN | BASS_MIXER_CHAN_LIMIT;

} // namespace

#if defined(MP_STATIC)
void testing::force_exclusive_failure(bool on) noexcept {
    g_force_exclusive_failure.store(on, std::memory_order_relaxed);
}

void testing::simulate_device_notification(uint32_t notify, uint32_t device) noexcept {
    engine* const e = g_instance.load(std::memory_order_acquire);
    if (e != nullptr) {
        e->simulate_device_notification_for_test(notify, device);
    }
}
#endif

// ---- lifetime -----------------------------------------------------------------------------------

mp_result engine::create(const mp_engine_config& config, std::unique_ptr<engine>& out) {
    if (g_instance_exists.exchange(true)) {
        return state_fail("mp_engine_create: an engine already exists in this process");
    }
    std::unique_ptr<engine> e{new engine{}};
    const mp_result r = e->init(config);
    if (r != MP_OK) {
        e.reset(); // ~engine clears the instance flag
        return r;
    }
    e->start_device_watch();
    g_instance.store(e.get(), std::memory_order_release);
    out = std::move(e);
    return MP_OK;
}

mp_result engine::init(const mp_engine_config& config) {
    if (HIWORD(BASS_GetVersion()) != BASSVERSION) {
        return state_fail("mp_engine_create: bass.dll is not version 2.4");
    }
    // BASS only decodes here; BASSWASAPI drives timing, so no update thread and no device buffer.
    BASS_SetConfig(BASS_CONFIG_UPDATEPERIOD, 0);
    BASS_SetConfig(BASS_CONFIG_UPDATETHREADS, 0);
    BASS_SetConfig(BASS_CONFIG_FLOATDSP, 1);
    BASS_SetConfig(BASS_CONFIG_DEV_DEFAULT, 1);

    const uint32_t rate = config.sample_rate != 0 ? config.sample_rate : k_default_rate;
    const uint32_t channels = config.channels != 0 ? config.channels : k_default_channels;

    if (!BASS_Init(0 /* no sound */, rate, 0, nullptr, nullptr)) {
        return bass_fail("BASS_Init(no sound)");
    }

    const std::wstring plugin_dir = config.plugin_dir != nullptr ? utf8_to_wide(config.plugin_dir) : module_directory();
    load_plugins(plugin_dir);

    const mp_result r = create_mixer(rate, channels);
    if (r != MP_OK) {
        return r;
    }
    log(MP_LOG_INFO, "mpcore engine created: BASS %08lX, mixer %u Hz / %u ch, %zu plugins",
        static_cast<unsigned long>(BASS_GetVersion()), rate, channels, plugins_.size());
    return MP_OK;
}

void engine::load_plugins(const std::wstring& dir) {
    static const wchar_t* const k_plugins[] = {L"bassflac.dll", L"bassopus.dll", L"basswv.dll", L"bass_ape.dll"};
    for (const wchar_t* name : k_plugins) {
        const std::wstring path = dir + name;
        const HPLUGIN plugin = BASS_PluginLoad(path.c_str(), 0); // wide overload adds BASS_UNICODE
        if (plugin != 0) {
            plugins_.push_back(plugin);
        } else {
            char narrow[MAX_PATH];
            WideCharToMultiByte(CP_UTF8, 0, path.c_str(), -1, narrow, sizeof narrow, nullptr, nullptr);
            log(MP_LOG_WARN, "plugin not loaded: %s (BASS error %d)", narrow, BASS_ErrorGetCode());
        }
    }
}

mp_result engine::create_mixer(uint32_t rate, uint32_t channels) {
    // Decode mixer: pulled by the output thread. NONSTOP keeps it producing silence with no source, so the
    // output never stalls; POSEX enables latency-compensated source positions.
    const HSTREAM mixer = BASS_Mixer_StreamCreate(
        rate, channels, BASS_SAMPLE_FLOAT | BASS_STREAM_DECODE | BASS_MIXER_NONSTOP | BASS_MIXER_POSEX);
    if (mixer == 0) {
        return bass_fail("BASS_Mixer_StreamCreate");
    }
    if (mixer_ != 0) {
        BASS_StreamFree(mixer_);
    }
    mixer_ = mixer;
    mixer_rate_ = rate;
    mixer_channels_ = channels;
    fade_frames_.store(std::max(1u, rate * k_guard_fade_ms / 1000u), std::memory_order_relaxed);
    // The tap belongs to this mixer: its byte counter is the new mixer's position, and whatever the old one
    // published is about audio that is no longer being made. The DSP went with the old mixer's handle.
    tap_.reset(channels);
    if (BASS_ChannelSetDSP(mixer_, &tap_proc, this, 0) == 0) {
        return bass_fail("BASS_ChannelSetDSP (analysis tap)");
    }
    return MP_OK;
}

void engine::tap_proc(unsigned long, unsigned long, void* buffer, unsigned long length, void* user) {
    auto* self = static_cast<engine*>(user);
    self->tap_.write_bytes(static_cast<const float*>(buffer), static_cast<uint32_t>(length), qpc_now());
}

engine::~engine() {
    // Before control_ is taken: the watch thread takes it as well, so joining from inside would deadlock.
    g_instance.store(nullptr, std::memory_order_release);
    stop_device_watch();
    {
        std::lock_guard lock{control_};
        playing_.store(false, std::memory_order_release);
        free_output(); // joins the WASAPI thread: no callback after this point
        current_.store(nullptr, std::memory_order_relaxed);
        next_.store(nullptr, std::memory_order_relaxed);
        for (auto& t : tracks_) {
            free_track_streams(*t);
        }
        tracks_.clear();
        if (mixer_ != 0) {
            BASS_StreamFree(mixer_);
            mixer_ = 0;
        }
        for (HPLUGIN p : plugins_) {
            BASS_PluginFree(p);
        }
        plugins_.clear();
        BASS_Free();
    }
    {
        std::lock_guard lock{event_mutex_};
        event_cb_ = nullptr;
    }
    g_instance_exists.store(false);
}

void engine::free_output() noexcept {
    if (output_open_ && !offline_) {
        BASS_WASAPI_Stop(TRUE);
        BASS_WASAPI_Free();
    }
    output_open_ = false;
    output_started_ = false;
    offline_ = false;
}

// ---- output -------------------------------------------------------------------------------------

mp_result engine::adopt_output_format() {
    // The mixer must produce exactly what the device consumes; recreate it at the output format and
    // re-attach the current source at its position (surprise recorded in the spike doc). In exclusive mode
    // that rate is the device's own, so a source at the same rate is never resampled (ADR-003 item 2).
    if (output_rate_ == mixer_rate_ && output_channels_ == mixer_channels_) {
        return MP_OK;
    }
    uint64_t resume_pos = 0;
    track* const cur = current_.load(std::memory_order_acquire);
    remove_outgoing(); // the old mixer goes; a fading predecessor is not carried over
    if (cur != nullptr) {
        resume_pos = source_position(*cur, 0);
        BASS_Mixer_ChannelRemove(cur->stream);
        cur->fade_sync = 0;
    }
    if (const mp_result r = create_mixer(output_rate_, output_channels_); r != MP_OK) {
        return r;
    }
    if (cur != nullptr) {
        set_source_position(*cur, resume_pos);
        if (const mp_result a = attach_source(*cur); a != MP_OK) {
            return a;
        }
        arm_crossfade();
    }
    return MP_OK;
}

mp_result engine::open_output(const mp_output_config& config, bool exclusive, char* fail_text, size_t fail_cap) {
    const auto fail = [&](mp_result r) {
        copy_utf8(fail_text, fail_cap, std::string{mp::abi::last_error()}.c_str());
        free_output();
        return r;
    };

    DWORD flags = 0;
    // Ask for the device's own mix format, not whatever rate the mixer happens to be running at. In exclusive mode
    // that is what makes a device left at 44 100 Hz open at 44 100 Hz, so a 44 100 Hz source reaches it unresampled
    // (E1-S6 AC-51). In shared mode it matters just as much for a different reason: BASSWASAPI honours a rate that
    // differs from the device's, and Windows then resamples every frame on the way out - measured on the dev machine,
    // where the mixer's default 48 kHz was driving a 44 100 Hz device.
    uint32_t request_rate = mixer_rate_;
    uint32_t request_channels = mixer_channels_;
    BASS_WASAPI_DEVICEINFO di{};
    if (wasapi_device_info(config.device_index, di) && di.mixfreq != 0 && di.mixchans != 0) {
        request_rate = di.mixfreq;
        request_channels = di.mixchans;
    }
    if (exclusive) {
        // AUTOFORMAT lets BASSWASAPI pick the nearest format the device accepts when it will not take that one.
        flags |= BASS_WASAPI_EXCLUSIVE | BASS_WASAPI_AUTOFORMAT;
    }
    if (config.event_driven != 0) {
        flags |= BASS_WASAPI_EVENT;
    }
    const float buffer_s = config.buffer_ms != 0 ? static_cast<float>(config.buffer_ms) / 1000.0f : 0.0f;

    if (exclusive && exclusive_init_forced_to_fail()) {
        mp::abi::set_last_error("BASS_WASAPI_Init(exclusive): forced failure (test seam)");
        return fail(MP_E_DEVICE);
    }
    // Both modes are opened at the device's mix format; BASS_WASAPI_GetInfo below says what was actually granted.
    if (!BASS_WASAPI_Init(config.device_index, request_rate, request_channels, flags, buffer_s, 0.0f, &output_proc,
                          this)) {
        return fail(bass_fail(exclusive ? "BASS_WASAPI_Init(exclusive)" : "BASS_WASAPI_Init"));
    }
    output_open_ = true;

    BASS_WASAPI_INFO info{};
    if (!BASS_WASAPI_GetInfo(&info)) {
        return fail(bass_fail("BASS_WASAPI_GetInfo"));
    }
    exclusive_ = (info.initflags & BASS_WASAPI_EXCLUSIVE) != 0;
    output_rate_ = info.freq;
    output_channels_ = info.chans;
    output_buffer_bytes_ = info.buflen;
    output_format_ = info.format;

    if (const mp_result r = adopt_output_format(); r != MP_OK) {
        return fail(r);
    }
    if (!BASS_WASAPI_Start()) {
        return fail(bass_fail("BASS_WASAPI_Start"));
    }
    output_started_ = true;
    fail_text[0] = '\0';
    return MP_OK;
}

mp_result engine::set_output(const mp_output_config& config) {
    std::lock_guard lock{control_};
    return set_output_locked(config);
}

mp_result engine::set_output_locked(const mp_output_config& config) {
    wanted_ = config;
    have_wanted_ = true;
    free_output();
    open_device_ = MP_DEVICE_NONE;
    open_device_id_.clear();

    if (config.device_index == MP_DEVICE_NONE) {
        // Headless: the mixer keeps its rate; render() is the output thread.
        output_open_ = true;
        output_started_ = true;
        offline_ = true;
        exclusive_ = false;
        output_rate_ = mixer_rate_;
        output_channels_ = mixer_channels_;
        output_buffer_bytes_ = 0;
        output_format_ = 0;
        log(MP_LOG_INFO, "output: none (render), %u Hz / %u ch", output_rate_, output_channels_);
        return MP_OK;
    }

    const bool wants_exclusive = config.mode == MP_OUTPUT_EXCLUSIVE;
    char why[512] = {};
    mp_result r = open_output(config, wants_exclusive, why, sizeof why);
    if (r != MP_OK && wants_exclusive) {
        // Exclusive mode is an opt-in and a driver may simply refuse it (another app holds the device, the
        // hardware takes no format we can offer). Losing the music over that would be the wrong trade: say why
        // and carry on in shared mode, which is what the user had before they opted in (R-3, E1-S6 AC-50).
        char message[640];
        char device[64];
        if (config.device_index == MP_DEVICE_DEFAULT) {
            copy_utf8(device, sizeof device, "the default device");
        } else {
            std::snprintf(device, sizeof device, "device %d", config.device_index);
        }
        std::snprintf(message, sizeof message, "exclusive mode is not available on %s (%s); playing in shared mode",
                      device, why[0] != '\0' ? why : "no reason given");
        log(MP_LOG_WARN, "%s", message);
        emit(MP_EVENT_ERROR, 0, 0, message);
        r = open_output(config, false, why, sizeof why);
        if (r == MP_OK) {
            // The exclusive attempt left its message in the thread's last error; the call succeeded.
            mp::abi::clear_last_error();
        }
    }
    if (r != MP_OK) {
        return r;
    }

    remember_open_device();
    log(MP_LOG_INFO, "output: %s (%s), %u Hz / %u ch, format %u, buffer %u bytes (%u ms)",
        open_device_id_.empty() ? "device" : open_device_id_.c_str(), exclusive_ ? "exclusive" : "shared", output_rate_,
        output_channels_, output_format_, output_buffer_bytes_,
        output_rate_ != 0 ? output_buffer_bytes_ * 1000u / (output_rate_ * output_channels_ * 4u) : 0u);
    return MP_OK;
}

// ---- device changes (E1-S7) ---------------------------------------------------------------------

void engine::remember_open_device() {
    open_device_ = MP_DEVICE_NONE;
    open_device_id_.clear();
    const DWORD device = BASS_WASAPI_GetDevice();
    if (device == static_cast<DWORD>(-1)) {
        return;
    }
    open_device_ = static_cast<int32_t>(device);
    BASS_WASAPI_DEVICEINFO di{};
    if (BASS_WASAPI_GetDeviceInfo(device, &di) && di.id != nullptr) {
        open_device_id_ = di.id;
    }
    // A device that comes back is the one that went: the wait for it ends when it is open again.
    if (!open_device_id_.empty() && open_device_id_ == lost_device_id_) {
        lost_device_id_.clear();
    }
}

void engine::park_for_lost_device() noexcept {
    // The audio thread is gone with the device, so the pause ramp cannot run: land the envelope by hand. The source
    // keeps its position, which is what makes "reconnect and carry on" possible at all.
    playing_.store(false, std::memory_order_release);
    pause_pending_.store(false, std::memory_order_release);
    env_target_.store(0.0f, std::memory_order_release);
    env_level_.store(0.0f, std::memory_order_release);
    hold_.store(true, std::memory_order_release);
}

void engine::notify_proc(unsigned long notify, unsigned long device, void* user) {
    static_cast<engine*>(user)->post_device_note(static_cast<uint32_t>(notify), static_cast<uint32_t>(device));
}

void engine::post_device_note(uint32_t notify, uint32_t device) noexcept {
    {
        std::lock_guard lock{note_mutex_};
        if (watch_stop_) {
            return;
        }
        notes_.push_back({notify, device});
    }
    note_cv_.notify_one();
}

void engine::start_device_watch() {
    device_watch_ = std::thread{[this]() { watch_loop(); }};
    BASS_WASAPI_SetNotify(&notify_proc, this);
}

void engine::stop_device_watch() noexcept {
    BASS_WASAPI_SetNotify(nullptr, nullptr); // no notification can arrive after this returns
    {
        std::lock_guard lock{note_mutex_};
        watch_stop_ = true;
        notes_.clear();
    }
    note_cv_.notify_all();
    if (device_watch_.joinable()) {
        device_watch_.join();
    }
}

#if defined(MP_STATIC)
void engine::simulate_device_notification_for_test(uint32_t notify, uint32_t device) noexcept {
    uint64_t before = 0;
    {
        std::lock_guard lock{note_mutex_};
        before = notes_handled_;
    }
    post_device_note(notify, device);
    std::unique_lock lock{note_mutex_};
    note_cv_.wait(lock, [&]() { return watch_stop_ || notes_handled_ > before; });
}
#endif

void engine::watch_loop() noexcept {
    for (;;) {
        device_note note{};
        {
            std::unique_lock lock{note_mutex_};
            note_cv_.wait(lock, [this]() { return watch_stop_ || !notes_.empty(); });
            if (watch_stop_) {
                return;
            }
            note = notes_.front();
            notes_.pop_front();
        }
        handle_device_note(note);
        {
            std::lock_guard lock{note_mutex_};
            ++notes_handled_;
        }
        note_cv_.notify_all();
    }
}

void engine::handle_device_note(const device_note& note) {
    std::lock_guard lock{control_};
    const bool live = output_open_ && !offline_ && open_device_ >= 0;
    const bool is_open_device = live && note.device == static_cast<uint32_t>(open_device_);

    switch (note.notify) {
    case BASS_WASAPI_NOTIFY_FAIL:
    case BASS_WASAPI_NOTIFY_DISABLED: {
        if (!is_open_device) {
            return; // some other device came and went; nothing here is playing through it
        }
        // The device the music is going through has gone. Park where it stands and say so: the shell decides
        // whether to move to the default device, because silently continuing somewhere else is exactly what
        // ui-screens-and-flows.md forbids ("no silent continuation on the wrong device without notice").
        const int32_t lost = open_device_;
        lost_device_id_ = open_device_id_;
        park_for_lost_device();
        free_output();
        open_device_ = MP_DEVICE_NONE;
        open_device_id_.clear();
        log(MP_LOG_WARN, "output device %d lost (%s); playback parked", lost, lost_device_id_.c_str());
        emit(MP_EVENT_DEVICE_LOST, lost, 0, lost_device_id_.c_str());
        return;
    }
    case BASS_WASAPI_NOTIFY_ENABLED: {
        // Only the device that was lost is worth reporting: the shell offers "switch back" and the user chooses.
        // Every other device appearing is the Settings list's business, not a running playback's.
        if (lost_device_id_.empty()) {
            return;
        }
        BASS_WASAPI_DEVICEINFO di{};
        if (!BASS_WASAPI_GetDeviceInfo(note.device, &di) || di.id == nullptr || lost_device_id_ != di.id) {
            return;
        }
        log(MP_LOG_INFO, "output device %u is back (%s)", note.device, di.id);
        emit(MP_EVENT_DEVICE_CHANGED, static_cast<int64_t>(note.device), 0, di.id);
        return;
    }
    case BASS_WASAPI_NOTIFY_DEFOUTPUT: {
        // Asking for MP_DEVICE_DEFAULT means following the default, so this one migrates itself. A caller that
        // named a device is left alone: its choice did not change because Windows changed its mind.
        if (!have_wanted_ || wanted_.device_index != MP_DEVICE_DEFAULT || !live) {
            return;
        }
        if (note.device == static_cast<uint32_t>(open_device_)) {
            return; // already there
        }
        const mp_result r = set_output_locked(wanted_);
        if (r != MP_OK) {
            char message[320];
            std::snprintf(message, sizeof message, "the default output device changed and could not be opened: %s",
                          std::string{mp::abi::last_error()}.c_str());
            log(MP_LOG_ERROR, "%s", message);
            park_for_lost_device();
            emit(MP_EVENT_ERROR, 0, 0, message);
            return;
        }
        log(MP_LOG_INFO, "default output device changed; playback moved to device %d (%s)", open_device_,
            open_device_id_.c_str());
        emit(MP_EVENT_DEVICE_CHANGED, open_device_, 1, open_device_id_.c_str());
        return;
    }
    default:
        return;
    }
}

mp_result engine::enum_devices(mp_device_info* out, uint32_t* count) {
    std::lock_guard lock{control_};
    uint32_t written = 0;
    uint32_t total = 0;
    BASS_WASAPI_DEVICEINFO di{};
    for (DWORD i = 0; BASS_WASAPI_GetDeviceInfo(i, &di); ++i) {
        if ((di.flags & (BASS_DEVICE_INPUT | BASS_DEVICE_LOOPBACK)) != 0) {
            continue;
        }
        if ((di.flags & BASS_DEVICE_ENABLED) == 0) {
            continue;
        }
        if (out != nullptr && written < *count) {
            mp_device_info& d = out[written];
            std::memset(&d, 0, sizeof d);
            d.struct_size = sizeof d;
            d.index = static_cast<int32_t>(i);
            copy_utf8(d.name, sizeof d.name, di.name);
            copy_utf8(d.id, sizeof d.id, di.id);
            d.mix_sample_rate = di.mixfreq;
            d.mix_channels = di.mixchans;
            d.min_period_us = static_cast<uint32_t>(di.minperiod * 1'000'000.0f);
            d.default_period_us = static_cast<uint32_t>(di.defperiod * 1'000'000.0f);
            d.is_default = (di.flags & BASS_DEVICE_DEFAULT) != 0 ? 1 : 0;
            d.is_enabled = 1;
            ++written;
        }
        ++total;
    }
    *count = out != nullptr ? written : total;
    return MP_OK;
}

void engine::set_event_callback(mp_event_cb callback, void* user) {
    std::lock_guard lock{event_mutex_};
    event_cb_ = callback;
    event_user_ = user;
}

void engine::emit(mp_event_type type, int64_t a, int64_t b, const char* message) noexcept {
    std::lock_guard lock{event_mutex_};
    if (event_cb_ == nullptr) {
        return;
    }
    mp_event ev{};
    ev.struct_size = sizeof ev;
    ev.type = type;
    ev.a = a;
    ev.b = b;
    ev.message = message;
    event_cb_(&ev, event_user_);
}

// ---- tracks -------------------------------------------------------------------------------------

mp_result engine::open_track(const char* utf8_path, track*& out) {
    std::lock_guard lock{control_};
    const std::wstring path = utf8_to_wide(utf8_path);
    if (path.empty()) {
        mp::abi::set_last_error("mp_track_open: empty or invalid UTF-8 path");
        return MP_E_INVALID_ARG;
    }
    // Decode + float + prescan (exact length and seeking); the wide overload adds BASS_UNICODE.
    const HSTREAM stream =
        BASS_StreamCreateFile(FALSE, path.c_str(), 0, 0, BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT | BASS_STREAM_PRESCAN);
    if (stream == 0) {
        return bass_fail("BASS_StreamCreateFile");
    }
    BASS_CHANNELINFO ci{};
    BASS_ChannelGetInfo(stream, &ci);
    const QWORD length_bytes = BASS_ChannelGetLength(stream, BASS_POS_BYTE);

    auto t = std::make_unique<track>();
    t->stream = stream;
    t->path = path;
    t->owner = this;
    t->frame_bytes = static_cast<uint32_t>(sizeof(float)) * ci.chans;
    t->info.struct_size = sizeof(mp_track_info);
    t->info.sample_rate = ci.freq;
    t->info.channels = ci.chans;
    t->info.bits_per_sample = ci.origres & 0xFFFF;
    copy_utf8(t->info.codec, sizeof t->info.codec, codec_name(ci.ctype));
    if (length_bytes != static_cast<QWORD>(-1)) {
        t->info.duration_ms = static_cast<int64_t>(BASS_ChannelBytes2Seconds(stream, length_bytes) * 1000.0);
        t->info.total_frames = ci.chans != 0 ? static_cast<int64_t>(length_bytes / (sizeof(float) * ci.chans)) : 0;
    } else {
        t->info.duration_ms = -1;
        t->info.total_frames = -1;
    }

    // Media Foundation delivers an MP4's priming and padding as audio (E1-S2 spike); wrap such a stream in one
    // that trims them. Other decoders (bassflac, bassopus, BASS's MP3) already do this themselves.
    if (ci.ctype == BASS_CTYPE_STREAM_MF && ci.chans != 0 && ci.freq != 0) {
        if (const auto g = read_mp4_gapless(path); g && g->timescale == ci.freq) {
            const HSTREAM wrapper =
                BASS_StreamCreate(ci.freq, ci.chans, BASS_STREAM_DECODE | BASS_SAMPLE_FLOAT, &trim_proc, t.get());
            if (wrapper == 0) {
                BASS_StreamFree(stream);
                return bass_fail("BASS_StreamCreate (trim)");
            }
            t->inner = stream;
            t->stream = wrapper;
            t->trim_priming = g->priming_frames;
            t->trim_valid = g->valid_frames;
            t->trim_skip = g->priming_frames;
            t->trim_delivered = 0;
            t->info.total_frames = static_cast<int64_t>(g->valid_frames);
            t->info.duration_ms = static_cast<int64_t>(g->valid_frames * 1000 / ci.freq);
            log(MP_LOG_DEBUG, "mp4 gapless: priming %llu, valid %llu frames (%s)",
                static_cast<unsigned long long>(g->priming_frames), static_cast<unsigned long long>(g->valid_frames),
                g->from == mp4_gapless::source::itunsmpb ? "iTunSMPB" : "edit list");
        }
    }
    // The crossfade envelope rides on whichever stream the mixer pulls; it is a no-op until a fade is armed.
    if (BASS_ChannelSetDSP(t->stream, &fade_proc, t.get(), 0) == 0) {
        const mp_result r = bass_fail("BASS_ChannelSetDSP (fade)");
        free_track_streams(*t);
        return r;
    }
    out = t.get();
    tracks_.push_back(std::move(t));
    return MP_OK;
}

bool engine::owns(const track* t) const {
    std::lock_guard lock{control_};
    return std::any_of(tracks_.begin(), tracks_.end(), [t](const auto& p) { return p.get() == t; });
}

mp_result engine::close_track(track* t) {
    std::lock_guard lock{control_};
    const auto it = std::find_if(tracks_.begin(), tracks_.end(), [t](const auto& p) { return p.get() == t; });
    if (it == tracks_.end()) {
        mp::abi::set_last_error("mp_track_close: unknown track handle");
        return MP_E_INVALID_ARG;
    }
    if (next_.load(std::memory_order_acquire) == t) {
        next_.store(nullptr, std::memory_order_release);
        arm_crossfade(); // nothing to fade into any more
    }
    if (outgoing_.load(std::memory_order_acquire) == t) {
        remove_outgoing(); // its tail is cut; the incoming is what is heard
    }
    if (current_.load(std::memory_order_acquire) == t) {
        guard_out();
        remove_outgoing();
        BASS_Mixer_ChannelRemove(t->stream);
        current_.store(nullptr, std::memory_order_release);
        playing_.store(false, std::memory_order_release);
    }
    free_track_streams(*t);
    tracks_.erase(it);
    return MP_OK;
}

// ---- transport ----------------------------------------------------------------------------------

// Fade the envelope to silence. On a live device this waits (bounded) for the audio thread to get there,
// so the caller can change the mixer without a step in the output. Headless, or when the output is not
// running, nothing is being heard, so the level is dropped straight away. Held (paused) output is silent already.
void engine::guard_out() {
    pause_pending_.store(false, std::memory_order_relaxed);
    env_target_.store(0.0f, std::memory_order_release);
    if (hold_.load(std::memory_order_acquire) || offline_ || !output_started_ ||
        current_.load(std::memory_order_acquire) == nullptr) {
        env_level_.store(0.0f, std::memory_order_release);
        return;
    }
    const auto deadline = std::chrono::steady_clock::now() + std::chrono::milliseconds(k_guard_wait_ms);
    while (env_level_.load(std::memory_order_acquire) > 0.0f && std::chrono::steady_clock::now() < deadline) {
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    env_level_.store(0.0f, std::memory_order_release); // a stalled output must not leave the fade half done
}

void engine::begin_fade_in() noexcept {
    pause_pending_.store(false, std::memory_order_relaxed);
    hold_.store(false, std::memory_order_release);
    env_target_.store(1.0f, std::memory_order_release);
}

mp_result engine::play(track* t, int64_t start_ms) {
    std::lock_guard lock{control_};
    if (!output_open_) {
        return state_fail("mp_engine_play: no output set (mp_engine_set_output first)");
    }
    if (track* const cur = current_.load(std::memory_order_acquire); cur != nullptr) {
        guard_out();
        remove_outgoing();
        BASS_Mixer_ChannelRemove(cur->stream);
        cur->fade_sync = 0; // went with the channel
        cur->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        current_.store(nullptr, std::memory_order_release);
    }
    if (next_.load(std::memory_order_acquire) == t) {
        next_.store(nullptr, std::memory_order_release); // playing the preloaded track by hand consumes it
    }
    // A manual play is the guard envelope's fade-in, not a crossfade's: no per-source fades on this one.
    t->fade_in_frames.store(0, std::memory_order_release);
    t->fade_out_start.store(track::k_no_fade, std::memory_order_release);
    const QWORD start = BASS_ChannelSeconds2Bytes(t->stream, static_cast<double>(start_ms) / 1000.0);
    if (!set_source_position(*t, start)) {
        return bass_fail("BASS_ChannelSetPosition");
    }
    if (const mp_result r = attach_source(*t); r != MP_OK) {
        return r;
    }
    current_.store(t, std::memory_order_release);
    env_level_.store(0.0f, std::memory_order_release);
    begin_fade_in();
    playing_.store(true, std::memory_order_release);
    arm_crossfade(); // a track queued before this play still follows it
    if (!output_started_) {
        if (!BASS_WASAPI_Start()) {
            return bass_fail("BASS_WASAPI_Start");
        }
        output_started_ = true;
    }
    emit(MP_EVENT_TRACK_STARTED, reinterpret_cast<int64_t>(t), start_ms, nullptr);
    return MP_OK;
}

mp_result engine::preload_next(track* next, mp_join_mode mode) {
    std::lock_guard lock{control_};
    if (next == nullptr) {
        next_.store(nullptr, std::memory_order_release);
        next_crossfade_.store(false, std::memory_order_release);
        arm_crossfade();
        return MP_OK;
    }
    if (next == current_.load(std::memory_order_acquire)) {
        mp::abi::set_last_error("mp_engine_preload_next: the track is already playing");
        return MP_E_INVALID_ARG;
    }
    if (next == outgoing_.load(std::memory_order_acquire)) {
        mp::abi::set_last_error("mp_engine_preload_next: the track is still fading out");
        return MP_E_INVALID_ARG;
    }
    // Rewind here, on the control thread; the audio thread only adds it to the mixer.
    if (!set_source_position(*next, 0)) {
        return bass_fail("BASS_ChannelSetPosition (preload)");
    }
    next->fade_out_start.store(track::k_no_fade, std::memory_order_release); // armed once it is the current
    next_crossfade_.store(mode == MP_JOIN_CROSSFADE, std::memory_order_release);
    next_.store(next, std::memory_order_release);
    arm_crossfade();
    return MP_OK;
}

mp_result engine::set_crossfade(uint32_t ms) {
    std::lock_guard lock{control_};
    const uint32_t clamped = std::min(ms, k_max_crossfade_ms);
    crossfade_ms_.store(clamped, std::memory_order_release);
    log(MP_LOG_DEBUG, "crossfade: %u ms%s", clamped, clamped != ms ? " (clamped)" : "");
    arm_crossfade();
    return MP_OK;
}

// Makes the current source's fade-out and fade-point sync match what is queued. Position based throughout: the
// fade-out is armed as "from frame S over N frames" and the DSP takes the gain from where the source is, so it
// needs no state machine; the sync at S adds the queued source. Nothing is armed when the crossfade is off, the
// join is gapless, the current length is unknown, or the source is already past S (then the END sync makes the
// gapless join, which is the fallback everywhere).
void engine::arm_crossfade() {
    track* const cur = current_.load(std::memory_order_acquire);
    if (cur == nullptr) {
        return;
    }
    if (cur->fade_sync != 0) {
        BASS_Mixer_ChannelRemoveSync(cur->stream, cur->fade_sync);
        cur->fade_sync = 0;
    }
    cur->fade_out_start.store(track::k_no_fade, std::memory_order_release);

    const uint32_t ms = crossfade_ms_.load(std::memory_order_acquire);
    if (next_.load(std::memory_order_acquire) == nullptr || !next_crossfade_.load(std::memory_order_acquire) ||
        ms == 0 || cur->info.total_frames <= 0 || cur->info.sample_rate == 0 || cur->frame_bytes == 0) {
        return;
    }
    const auto total = static_cast<uint64_t>(cur->info.total_frames);
    const uint64_t len = std::min<uint64_t>(total, static_cast<uint64_t>(ms) * cur->info.sample_rate / 1000u);
    const uint64_t start = total - len;
    // dsp_pos is what the DSP has already seen; behind it the fade could only be joined late.
    const uint64_t seen = cur->dsp_pos.load(std::memory_order_acquire);
    if (seen >= start || start < cur->trim_origin) {
        return;
    }
    cur->fade_out_frames.store(static_cast<uint32_t>(len), std::memory_order_relaxed);
    cur->fade_out_start.store(start, std::memory_order_release);
    // The sync position is in the stream's own counter, which for a wrapper starts at trim_origin.
    const QWORD sync_pos = (start - cur->trim_origin) * cur->frame_bytes;
    cur->fade_sync = BASS_Mixer_ChannelSetSync(cur->stream, BASS_SYNC_POS | BASS_SYNC_MIXTIME | BASS_SYNC_ONETIME,
                                               sync_pos, &fade_sync, this);
    if (cur->fade_sync == 0) {
        cur->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        log(MP_LOG_WARN, "crossfade: BASS_Mixer_ChannelSetSync(POS) failed (BASS error %d); the join will be gapless",
            BASS_ErrorGetCode());
    }
}

void engine::remove_outgoing() noexcept {
    if (track* const out = outgoing_.exchange(nullptr, std::memory_order_acq_rel); out != nullptr) {
        BASS_Mixer_ChannelRemove(out->stream);
        out->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        emit(MP_EVENT_TRACK_ENDED, reinterpret_cast<int64_t>(out), 0, nullptr);
    }
}

mp_result engine::pause() {
    std::lock_guard lock{control_};
    if (current_.load(std::memory_order_acquire) == nullptr) {
        return state_fail("mp_engine_pause: nothing is playing");
    }
    if (hold_.load(std::memory_order_acquire)) {
        return MP_OK;
    }
    playing_.store(false, std::memory_order_release);
    // The audio thread ramps the envelope down and then engages the hold itself (see pull()).
    pause_pending_.store(true, std::memory_order_release);
    env_target_.store(0.0f, std::memory_order_release);
    return MP_OK;
}

mp_result engine::resume() {
    std::lock_guard lock{control_};
    if (current_.load(std::memory_order_acquire) == nullptr) {
        return state_fail("mp_engine_resume: nothing is loaded");
    }
    begin_fade_in();
    playing_.store(true, std::memory_order_release);
    return MP_OK;
}

mp_result engine::stop(mp_fade_mode fade) {
    std::lock_guard lock{control_};
    playing_.store(false, std::memory_order_release);
    next_.store(nullptr, std::memory_order_release); // nothing to join to any more
    next_crossfade_.store(false, std::memory_order_release);
    if (track* const cur = current_.load(std::memory_order_acquire); cur != nullptr) {
        if (fade == MP_FADE_GUARD) {
            guard_out();
        } else {
            pause_pending_.store(false, std::memory_order_relaxed);
            env_target_.store(0.0f, std::memory_order_release);
            env_level_.store(0.0f, std::memory_order_release);
        }
        remove_outgoing();
        BASS_Mixer_ChannelRemove(cur->stream);
        cur->fade_sync = 0;
        cur->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        current_.store(nullptr, std::memory_order_release);
    }
    hold_.store(false, std::memory_order_release);
    return MP_OK;
}

mp_result engine::seek(int64_t position_ms) {
    std::lock_guard lock{control_};
    track* const cur = current_.load(std::memory_order_acquire);
    if (cur == nullptr) {
        return state_fail("mp_engine_seek: nothing is loaded");
    }
    const bool was_held = hold_.load(std::memory_order_acquire);
    const bool was_playing = playing_.load(std::memory_order_acquire);
    guard_out();
    remove_outgoing(); // a seek is a hard cut of what was fading under this track
    const QWORD pos = BASS_ChannelSeconds2Bytes(cur->stream, static_cast<double>(position_ms) / 1000.0);
    bool ok;
    if (cur->inner == 0) {
        // MIXER_RESET drops what the mixer already buffered from the old position.
        ok = BASS_Mixer_ChannelSetPosition(cur->stream, pos, BASS_POS_BYTE | BASS_POS_MIXER_RESET);
        if (ok && cur->frame_bytes != 0) {
            cur->dsp_pos.store(pos / cur->frame_bytes, std::memory_order_release);
        }
    } else {
        // A user stream only resets: take the wrapper out (its syncs go with it), reposition, put it back.
        BASS_Mixer_ChannelRemove(cur->stream);
        cur->fade_sync = 0;
        ok = set_source_position(*cur, pos) && attach_source(*cur) == MP_OK;
    }
    if (!ok) {
        const mp_result r = bass_fail("BASS_Mixer_ChannelSetPosition");
        if (was_playing) {
            begin_fade_in();
        }
        return r;
    }
    // Re-arm from the new position: a seek into or past the fade window makes the join gapless (the sync would
    // not fire and the fade-out would start part-way down), a seek back before it restores the crossfade.
    arm_crossfade();
    if (was_held || !was_playing) {
        // Paused: stay silent and held at the new position; resume fades in from there.
        hold_.store(true, std::memory_order_release);
        pause_pending_.store(false, std::memory_order_relaxed);
    } else {
        begin_fade_in();
    }
    return MP_OK;
}

float engine::volume_taper(float slider) noexcept {
    if (!(slider > 0.0f)) { // also catches NaN
        return 0.0f;
    }
    if (slider >= 1.0f) {
        return 1.0f;
    }
    return std::pow(10.0f, 2.0f * (slider - 1.0f));
}

void engine::set_volume(float slider) {
    volume_target_.store(volume_taper(slider), std::memory_order_release);
}

float engine::replaygain_linear(float gain_db, float peak) noexcept {
    float gain = std::pow(10.0f, gain_db / 20.0f);
    if (peak > 0.0f && gain * peak > 1.0f) {
        gain = 1.0f / peak; // the loudest sample lands exactly on full scale
    }
    return gain;
}

// The gain is the source channel's own volume, which BASSmix applies as it mixes that source: a preloaded track
// carries its gain into the join, and the value stays with the stream through seeks and re-attachment. A change
// to the source being heard is ramped by the mixer itself (BASS ramps volume changes on channels it plays unless
// BASS_ATTRIB_NORAMP is set; only the start ramp is turned off by BASS_MIXER_CHAN_NORAMPIN), so it is heard as a
// level change, not a step. BASS_ChannelSlideAttribute is not used: a mixer does not advance a decoding source's
// slides (measured: the value never moved).
mp_result engine::set_replaygain(track* t, float gain_db, float peak) {
    std::lock_guard lock{control_};
    const float gain = replaygain_linear(gain_db, peak);
    t->gain_db = gain_db;
    t->peak = peak;
    t->gain = gain;
    if (!BASS_ChannelSetAttribute(t->stream, BASS_ATTRIB_VOL, gain)) {
        return bass_fail("BASS_ChannelSetAttribute(BASS_ATTRIB_VOL)");
    }
    log(MP_LOG_DEBUG, "replaygain: %.2f dB, peak %.3f -> x%.4f%s", static_cast<double>(gain_db),
        static_cast<double>(peak), static_cast<double>(gain),
        gain_db != 0.0f && gain * peak > 0.999f && peak > 0.0f ? " (limited)" : "");
    return MP_OK;
}

// ---- clock and stats ----------------------------------------------------------------------------

mp_result engine::get_clock(mp_clock& out) const {
    // Reads only BASS position queries and atomics; the control mutex is not taken so the UI can poll freely.
    out.struct_size = sizeof out;
    out.qpc_ticks = qpc_now();
    out.mixer_byte_pos = mixer_ != 0 ? static_cast<int64_t>(BASS_ChannelGetPosition(mixer_, BASS_POS_BYTE)) : 0;
    out.output_latency_ms =
        output_rate_ != 0 ? output_buffer_bytes_ * 1000u / (output_rate_ * output_channels_ * 4u) : 0u;
    // Bytes still sitting in the WASAPI buffer have been mixed but not heard; POSEX subtracts them.
    DWORD buffered = output_open_ && !offline_ ? BASS_WASAPI_GetData(nullptr, BASS_DATA_AVAILABLE) : 0;
    if (buffered == static_cast<DWORD>(-1)) {
        buffered = 0;
    }
    out.output_buffered_bytes = buffered;
    const track* t = current_.load(std::memory_order_acquire);
    if (t == nullptr) {
        out.position_ms = 0;
        return MP_OK;
    }
    const uint64_t pos = source_position(*t, buffered);
    out.position_ms = static_cast<int64_t>(BASS_ChannelBytes2Seconds(t->stream, pos) * 1000.0);
    return MP_OK;
}

mp_result engine::get_stats(mp_engine_stats& out) const {
    std::memset(&out, 0, sizeof out);
    out.struct_size = sizeof out;
    out.callbacks = callbacks_.load(std::memory_order_relaxed);
    out.underruns = underruns_.load(std::memory_order_relaxed);
    out.callback_max_us = callback_max_us_.load(std::memory_order_relaxed);
    out.output_sample_rate = output_rate_;
    out.output_channels = output_channels_;
    out.output_buffer_ms =
        output_rate_ != 0 ? output_buffer_bytes_ * 1000u / (output_rate_ * output_channels_ * 4u) : 0u;
    out.exclusive = exclusive_ ? 1 : 0;
    out.output_started = output_started_ ? 1 : 0;
    const char* format = "unknown";
    switch (output_format_) {
    case BASS_WASAPI_FORMAT_FLOAT:
        format = "float";
        break;
    case BASS_WASAPI_FORMAT_8BIT:
        format = "8bit";
        break;
    case BASS_WASAPI_FORMAT_16BIT:
        format = "16bit";
        break;
    case BASS_WASAPI_FORMAT_24BIT:
        format = "24bit";
        break;
    case BASS_WASAPI_FORMAT_32BIT:
        format = "32bit";
        break;
    default:
        break;
    }
    if (offline_) {
        format = "render";
    }
    copy_utf8(out.output_format, sizeof out.output_format, output_open_ ? format : "none");
    return MP_OK;
}

// ---- audio thread -------------------------------------------------------------------------------

// The pull stage. Real-time rules: no locks, no allocation, no logging (rt::scope counts allocations in
// Debug builds). Runs on the BASSWASAPI thread through output_proc, or on the caller's thread through
// render() when there is no device.
void engine::pull(void* buffer, uint32_t bytes) noexcept {
    [[maybe_unused]] mp::rt::scope rt_guard; // counts allocations in Debug; nothing in Release
    const int64_t t0 = qpc_now();
    auto* samples = static_cast<float*>(buffer);
    const uint32_t channels = std::max(1u, mixer_channels_);
    const uint32_t frames = bytes / (sizeof(float) * channels);

    if (hold_.load(std::memory_order_acquire)) {
        std::memset(buffer, 0, bytes); // paused: the mixer is not advanced, so the position stays put
    } else {
        // A source with LIMIT ends the read at its last frame; the next read continues with whatever follows it
        // (a successor added by end_sync, or NONSTOP silence), so a short read here is a real underrun -- with one
        // exception. When that last frame is the first frame of this pull, the read returns nothing at all: BASSmix
        // runs end_sync inside it, which is what puts the successor under the mixer, and only the read after that
        // can reach it. Breaking on that zero would drop a whole buffer of the successor and count an underrun for
        // it, which is what a join landing on a period boundary used to do (T-108, found by the E1-S11 soak). One
        // more read is enough, and only one: at the true end of the queue nothing joins, so nothing retries.
        uint32_t got = 0;
        while (got < bytes) {
            const DWORD n = BASS_ChannelGetData(mixer_, static_cast<char*>(buffer) + got, bytes - got);
            bool joined_here = false;
            if (track* const joined = join_pending_.exchange(nullptr, std::memory_order_acq_rel); joined != nullptr) {
                joined_here = true;
                // A gapless join: end_sync swapped the sources inside that read; the read stopped on the last frame
                // of the old source (LIMIT), so the mixer position now is exactly where the new one starts. A
                // crossfade: fade_sync added the new source under the old one and noted where.
                track* const ended = join_ended_.load(std::memory_order_relaxed);
                const int64_t at = ended != nullptr
                                       ? static_cast<int64_t>(BASS_ChannelGetPosition(mixer_, BASS_POS_BYTE))
                                       : join_at_.load(std::memory_order_relaxed);
                if (ended != nullptr) {
                    emit(MP_EVENT_TRACK_ENDED, reinterpret_cast<int64_t>(ended), at, nullptr);
                }
                emit(MP_EVENT_TRACK_STARTED, reinterpret_cast<int64_t>(joined), at, nullptr);
            }
            if (n == static_cast<DWORD>(-1)) {
                break;
            }
            if (n == 0) {
                if (!joined_here) {
                    break;
                }
                continue; // the successor arrived during that read; it is the next read that reaches it
            }
            got += n;
        }
        if (got < bytes) {
            std::memset(static_cast<char*>(buffer) + got, 0, bytes - got);
            if (playing_.load(std::memory_order_acquire)) {
                underruns_.fetch_add(1, std::memory_order_relaxed);
            }
        }

        // Envelope: linear ramp over fade_frames_ toward the target. Volume: linear interpolation from the
        // last buffer's gain to the new target across this buffer (no zipper, and a mute lands within it).
        float env = env_level_.load(std::memory_order_acquire);
        const float target = env_target_.load(std::memory_order_acquire);
        const float step = 1.0f / static_cast<float>(std::max(1u, fade_frames_.load(std::memory_order_relaxed)));
        const float vol0 = volume_current_;
        const float vol1 = volume_target_.load(std::memory_order_acquire);
        const float vol_step = frames != 0 ? (vol1 - vol0) / static_cast<float>(frames) : 0.0f;
        const bool flat = env == target && vol0 == vol1;
        if (!(flat && env == 1.0f && vol1 == 1.0f)) {
            float vol = vol0;
            for (uint32_t f = 0; f < frames; ++f) {
                if (env < target) {
                    env = std::min(target, env + step);
                } else if (env > target) {
                    env = std::max(target, env - step);
                }
                vol += vol_step;
                const float g = env * vol;
                float* frame = samples + static_cast<size_t>(f) * channels;
                for (uint32_t c = 0; c < channels; ++c) {
                    frame[c] *= g;
                }
            }
        }
        volume_current_ = vol1;
        env_level_.store(env, std::memory_order_release);
        if (env <= 0.0f && pause_pending_.load(std::memory_order_acquire)) {
            pause_pending_.store(false, std::memory_order_relaxed);
            hold_.store(true, std::memory_order_release);
        }
    }

    callbacks_.fetch_add(1, std::memory_order_relaxed);
    const auto us = static_cast<uint32_t>((qpc_now() - t0) * 1'000'000 / qpc_frequency());
    uint32_t prev = callback_max_us_.load(std::memory_order_relaxed);
    while (us > prev && !callback_max_us_.compare_exchange_weak(prev, us, std::memory_order_relaxed)) {
    }
}

void engine::free_track_streams(track& t) noexcept {
    if (t.stream != 0) {
        BASS_StreamFree(t.stream);
        t.stream = 0;
    }
    if (t.inner != 0) {
        BASS_StreamFree(t.inner);
        t.inner = 0;
    }
}

// ---- MP4 trimming wrapper (T-102) ---------------------------------------------------------------

// STREAMPROC of the wrapper: pulled by the mixer on the audio thread. Drops the priming frames still owed
// (using the caller's buffer as scratch), then hands out inner frames until the valid count is reached. No
// allocation, no logging.
unsigned long __stdcall engine::trim_proc(unsigned long /*handle*/, void* buffer, unsigned long length, void* user) {
    auto* t = static_cast<track*>(user);
    const uint32_t frame_bytes = t->frame_bytes;
    if (frame_bytes == 0) {
        return BASS_STREAMPROC_END;
    }
    while (t->trim_skip > 0) {
        const auto want =
            static_cast<DWORD>(std::min<uint64_t>(t->trim_skip * frame_bytes, length) / frame_bytes * frame_bytes);
        if (want == 0) {
            return BASS_STREAMPROC_END;
        }
        const DWORD got = BASS_ChannelGetData(t->inner, buffer, want);
        if (got == 0 || got == static_cast<DWORD>(-1)) {
            return BASS_STREAMPROC_END; // shorter than its own priming: nothing to play
        }
        t->trim_skip -= got / frame_bytes;
    }
    const uint64_t remaining = t->trim_valid > t->trim_delivered ? t->trim_valid - t->trim_delivered : 0;
    const auto want =
        static_cast<DWORD>(std::min<uint64_t>(remaining * frame_bytes, length) / frame_bytes * frame_bytes);
    if (want == 0) {
        return BASS_STREAMPROC_END;
    }
    DWORD got = BASS_ChannelGetData(t->inner, buffer, want);
    if (got == static_cast<DWORD>(-1)) {
        return BASS_STREAMPROC_END;
    }
    got = got / frame_bytes * frame_bytes;
    t->trim_delivered += got / frame_bytes;
    const bool ended = got < want || t->trim_delivered >= t->trim_valid;
    return got | (ended ? BASS_STREAMPROC_END : 0);
}

// Control thread, source not in the mixer. For a wrapper: reset its counter (the one thing a user stream can
// do), move the inner stream to the same audio frame past the priming, and remember what frame the counter
// now starts at. Position 0 decodes from the file's start and drops the priming exactly; anything else is a
// Media Foundation seek, which is not sample-accurate (spike doc), as it was before the wrapper.
bool engine::set_source_position(track& t, uint64_t bytes) noexcept {
    if (t.inner == 0) {
        if (!BASS_ChannelSetPosition(t.stream, bytes, BASS_POS_BYTE)) {
            return false;
        }
        t.dsp_pos.store(t.frame_bytes != 0 ? bytes / t.frame_bytes : 0, std::memory_order_release);
        return true;
    }
    if (t.frame_bytes == 0 || !BASS_ChannelSetPosition(t.stream, 0, BASS_POS_BYTE)) {
        return false;
    }
    const uint64_t frames = bytes / t.frame_bytes;
    if (frames == 0) {
        if (!BASS_ChannelSetPosition(t.inner, 0, BASS_POS_BYTE)) {
            return false;
        }
        t.trim_skip = t.trim_priming;
    } else {
        if (!BASS_ChannelSetPosition(t.inner, (frames + t.trim_priming) * t.frame_bytes, BASS_POS_BYTE)) {
            return false;
        }
        t.trim_skip = 0;
    }
    t.trim_delivered = frames;
    t.trim_origin = frames;
    t.dsp_pos.store(frames, std::memory_order_release);
    return true;
}

uint64_t engine::source_position(const track& t, uint32_t delay) noexcept {
    const QWORD pos = BASS_Mixer_ChannelGetPositionEx(t.stream, BASS_POS_BYTE, delay);
    const uint64_t counted = pos == static_cast<QWORD>(-1) ? 0 : pos;
    return counted + t.trim_origin * t.frame_bytes;
}

mp_result engine::attach_source(track& t) {
    // The mixer resamples if the source rate differs.
    if (!BASS_Mixer_StreamAddChannel(mixer_, t.stream, k_source_flags)) {
        return bass_fail("BASS_Mixer_StreamAddChannel");
    }
    // MIXTIME: fires when the end is mixed, not when it is heard; the managed side compensates with the clock.
    BASS_Mixer_ChannelSetSync(t.stream, BASS_SYNC_END | BASS_SYNC_MIXTIME, 0, &end_sync, this);
    return MP_OK;
}

// BASSWASAPI calls this on its own thread; the data it wants is always 32-bit float regardless of the
// device format.
unsigned long __stdcall engine::output_proc(void* buffer, unsigned long length, void* user) {
    auto* self = static_cast<engine*>(user);
    self->pull(buffer, static_cast<uint32_t>(length));
    return length; // always a full buffer: silence pads a short read
}

mp_result engine::render(float* out_interleaved, uint32_t frames) {
    if (!output_open_ || !offline_) {
        return state_fail("mp_engine_render: the output is not MP_DEVICE_NONE");
    }
    if (frames == 0) {
        return MP_OK;
    }
    pull(out_interleaved, frames * static_cast<uint32_t>(sizeof(float)) * mixer_channels_);
    return MP_OK;
}

// Mix-time END sync, called by BASSmix from inside the BASS_ChannelGetData in pull() when `channel` runs out.
// Without LIMIT a source added here would start at the beginning of the buffer being mixed (measured: a 200-frame
// overlap with 479-frame pulls); with it the read stops on the source's last frame and the successor's first
// frame is the next one read. The events are raised by pull() after the read, when the mixer position is that
// frame. BASS drops the ended source from the mixer itself. No allocation and no logging (Interop trampoline rule).
// The events name the track (mp_track*, as the header promises), not the BASS channel: the source that ran out
// is current_, which the control plane stores after attaching it, so the one case the two disagree (a source
// shorter than the buffer it was attached in, ending before play() stored it) reports 0 rather than a wrong
// handle. The lookup cannot walk tracks_ here (that is the control plane's, under its mutex).
void __stdcall engine::end_sync(unsigned long /*handle*/, unsigned long channel, unsigned long /*data*/, void* user) {
    auto* self = static_cast<engine*>(user);
    // The tail of a crossfade: the predecessor has faded to its last frame under the current source. BASS drops
    // it from the mixer; nothing follows it, since its successor is already playing.
    if (track* const out = self->outgoing_.load(std::memory_order_acquire); out != nullptr && out->stream == channel) {
        self->outgoing_.store(nullptr, std::memory_order_release);
        out->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        self->emit(MP_EVENT_TRACK_ENDED, reinterpret_cast<int64_t>(out), 0, nullptr);
        return;
    }
    track* const cur = self->current_.load(std::memory_order_acquire);
    track* const ended = cur != nullptr && cur->stream == channel ? cur : nullptr;
    track* const next = self->next_.exchange(nullptr, std::memory_order_acq_rel);
    if (next != nullptr && BASS_Mixer_StreamAddChannelEx(self->mixer_, next->stream, k_source_flags, 0, 0)) {
        BASS_Mixer_ChannelSetSync(next->stream, BASS_SYNC_END | BASS_SYNC_MIXTIME, 0, &end_sync, self);
        self->current_.store(next, std::memory_order_release);
        self->join_ended_.store(ended, std::memory_order_relaxed);
        self->join_pending_.store(next, std::memory_order_release);
        return;
    }
    self->playing_.store(false, std::memory_order_release);
    self->emit(MP_EVENT_TRACK_ENDED, reinterpret_cast<int64_t>(ended), 0, nullptr);
}

// ---- crossfade (E1-S4) --------------------------------------------------------------------------

// Mix-time POS sync at the fade point of the current source, from inside the BASS_ChannelGetData in pull(). Adds
// the queued source with its fade-in armed and makes it the current one; the old current keeps playing as
// outgoing_ with the fade-out arm_crossfade gave it, until its END sync. The queued source starts at the
// beginning of the buffer being mixed (spike finding 1): join_at_ records that position for the event. No
// allocation and no logging. A third source is never kept: a crossfade that starts while the previous one is
// still fading cuts the older tail.
void __stdcall engine::fade_sync(unsigned long /*handle*/, unsigned long channel, unsigned long /*data*/, void* user) {
    auto* self = static_cast<engine*>(user);
    track* const cur = self->current_.load(std::memory_order_acquire);
    if (cur == nullptr || cur->stream != channel) {
        return;
    }
    if (!self->next_crossfade_.load(std::memory_order_acquire)) {
        cur->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        return;
    }
    track* const next = self->next_.exchange(nullptr, std::memory_order_acq_rel);
    if (next == nullptr) {
        cur->fade_out_start.store(track::k_no_fade, std::memory_order_release); // nothing to fade into: play out
        return;
    }
    const uint32_t ms = self->crossfade_ms_.load(std::memory_order_acquire);
    uint64_t len_in = static_cast<uint64_t>(ms) * next->info.sample_rate / 1000u;
    if (next->info.total_frames > 0) {
        len_in = std::min<uint64_t>(len_in, static_cast<uint64_t>(next->info.total_frames));
    }
    next->fade_in_frames.store(static_cast<uint32_t>(len_in), std::memory_order_release);
    if (!BASS_Mixer_StreamAddChannelEx(self->mixer_, next->stream, k_source_flags, 0, 0)) {
        next->fade_in_frames.store(0, std::memory_order_release);
        cur->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        self->next_.store(next, std::memory_order_release); // the END sync will still join it
        return;
    }
    BASS_Mixer_ChannelSetSync(next->stream, BASS_SYNC_END | BASS_SYNC_MIXTIME, 0, &end_sync, self);
    if (track* const older = self->outgoing_.exchange(cur, std::memory_order_acq_rel); older != nullptr) {
        BASS_Mixer_ChannelRemove(older->stream);
        older->fade_out_start.store(track::k_no_fade, std::memory_order_release);
        self->emit(MP_EVENT_TRACK_ENDED, reinterpret_cast<int64_t>(older), 0, nullptr);
    }
    self->join_at_.store(static_cast<int64_t>(BASS_ChannelGetPosition(self->mixer_, BASS_POS_BYTE)),
                         std::memory_order_relaxed);
    self->current_.store(next, std::memory_order_release);
    self->join_ended_.store(nullptr, std::memory_order_relaxed);
    self->join_pending_.store(next, std::memory_order_release);
}

// The per-source envelope, run by the mixer on the audio thread as it decodes the source (BASS applies a decoding
// channel's DSPs inside BASS_ChannelGetData). Equal power: the incoming follows sin, the outgoing cos, over the
// same fraction of their fades, so the sum of their powers is constant across the overlap. The gain is a function
// of the source frame, which the DSP counts itself. Nothing here when no fade is armed. No allocation, no logging.
void __stdcall engine::fade_proc(unsigned long /*handle*/, unsigned long /*channel*/, void* buffer,
                                 unsigned long length, void* user) {
    auto* t = static_cast<track*>(user);
    const uint32_t frame_bytes = t->frame_bytes;
    if (frame_bytes == 0) {
        return;
    }
    const uint32_t frames = static_cast<uint32_t>(length / frame_bytes);
    const uint64_t pos = t->dsp_pos.load(std::memory_order_relaxed);
    t->dsp_pos.store(pos + frames, std::memory_order_relaxed);

    const uint32_t in_frames = t->fade_in_frames.load(std::memory_order_acquire);
    const uint64_t out_start = t->fade_out_start.load(std::memory_order_acquire);
    const uint32_t out_frames = t->fade_out_frames.load(std::memory_order_relaxed);
    const bool fading_in = in_frames != 0 && pos < in_frames;
    const bool fading_out = out_start != track::k_no_fade && pos + frames > out_start;
    if (!fading_in && !fading_out) {
        return;
    }
    constexpr float k_half_pi = 1.5707963f;
    const float in_step = fading_in ? k_half_pi / static_cast<float>(in_frames) : 0.0f;
    const float out_step = fading_out && out_frames != 0 ? k_half_pi / static_cast<float>(out_frames) : 0.0f;
    auto* samples = static_cast<float*>(buffer);
    const uint32_t channels = frame_bytes / static_cast<uint32_t>(sizeof(float));
    for (uint32_t f = 0; f < frames; ++f) {
        const uint64_t p = pos + f;
        float g = 1.0f;
        if (fading_in && p < in_frames) {
            g *= std::sin(in_step * static_cast<float>(p));
        }
        if (fading_out && p >= out_start) {
            const uint64_t into = p - out_start;
            g *= into >= out_frames ? 0.0f : std::cos(out_step * static_cast<float>(into));
        }
        float* frame = samples + static_cast<size_t>(f) * channels;
        for (uint32_t c = 0; c < channels; ++c) {
            frame[c] *= g;
        }
    }
}

} // namespace mp::audio
