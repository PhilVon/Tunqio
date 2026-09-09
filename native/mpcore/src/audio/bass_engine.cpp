// BASS + BASSmix + BASSWASAPI engine. The only translation unit that includes BASS headers.
//
// Shape (ADR-003, ADR-010): BASS runs on the "no sound" device purely as a decoder; a BASSmix decode
// mixer is the single source of PCM; BASSWASAPI owns the output thread and pulls from the mixer through
// output_proc. Nothing on that path allocates or blocks: it is one BASS_ChannelGetData plus a gain loop.
#include "audio/bass_engine.h"

#include "abi/last_error.h"
#include "common/log.h"

#include <algorithm>
#include <bass.h>
#include <bassmix.h>
#include <basswasapi.h>
#include <chrono>
#include <cstdio>
#include <cstring>

#include <windows.h>

namespace mp::audio {
namespace {

std::atomic<bool> g_instance_exists{false};

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

} // namespace

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
    return MP_OK;
}

engine::~engine() {
    {
        std::lock_guard lock{control_};
        playing_.store(false, std::memory_order_release);
        free_output(); // joins the WASAPI thread: no callback after this point
        current_ = nullptr;
        for (auto& t : tracks_) {
            if (t->stream != 0) {
                BASS_StreamFree(t->stream);
            }
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
    if (output_open_) {
        BASS_WASAPI_Stop(TRUE);
        BASS_WASAPI_Free();
        output_open_ = false;
        output_started_ = false;
    }
}

// ---- output -------------------------------------------------------------------------------------

mp_result engine::set_output(const mp_output_config& config) {
    std::lock_guard lock{control_};
    free_output();

    DWORD flags = 0;
    if (config.mode == MP_OUTPUT_EXCLUSIVE) {
        // AUTOFORMAT lets BASSWASAPI pick the nearest format the device accepts in exclusive mode.
        flags |= BASS_WASAPI_EXCLUSIVE | BASS_WASAPI_AUTOFORMAT;
    }
    if (config.event_driven != 0) {
        flags |= BASS_WASAPI_EVENT;
    }
    const float buffer_s = config.buffer_ms != 0 ? static_cast<float>(config.buffer_ms) / 1000.0f : 0.0f;

    // Shared mode ignores freq/chans (the device mix format is used); exclusive mode requests the mixer's.
    if (!BASS_WASAPI_Init(config.device_index, mixer_rate_, mixer_channels_, flags, buffer_s, 0.0f, &output_proc,
                          this)) {
        return bass_fail("BASS_WASAPI_Init");
    }
    output_open_ = true;

    BASS_WASAPI_INFO info{};
    if (!BASS_WASAPI_GetInfo(&info)) {
        const mp_result r = bass_fail("BASS_WASAPI_GetInfo");
        free_output();
        return r;
    }
    exclusive_ = (info.initflags & BASS_WASAPI_EXCLUSIVE) != 0;
    output_rate_ = info.freq;
    output_channels_ = info.chans;
    output_buffer_bytes_ = info.buflen;
    output_format_ = info.format;

    // The mixer must produce exactly what the device consumes; recreate it at the output format and
    // re-attach the current source at its position (surprise recorded in the spike doc).
    if (output_rate_ != mixer_rate_ || output_channels_ != mixer_channels_) {
        QWORD resume_pos = 0;
        if (current_ != nullptr) {
            resume_pos = BASS_Mixer_ChannelGetPosition(current_->stream, BASS_POS_BYTE);
            BASS_Mixer_ChannelRemove(current_->stream);
        }
        const mp_result r = create_mixer(output_rate_, output_channels_);
        if (r != MP_OK) {
            free_output();
            return r;
        }
        if (current_ != nullptr) {
            BASS_ChannelSetPosition(current_->stream, resume_pos, BASS_POS_BYTE);
            if (!BASS_Mixer_StreamAddChannel(mixer_, current_->stream, BASS_MIXER_CHAN_NORAMPIN)) {
                return bass_fail("BASS_Mixer_StreamAddChannel (re-attach)");
            }
            BASS_Mixer_ChannelSetSync(current_->stream, BASS_SYNC_END | BASS_SYNC_MIXTIME, 0, &end_sync, this);
        }
    }

    if (!BASS_WASAPI_Start()) {
        const mp_result r = bass_fail("BASS_WASAPI_Start");
        free_output();
        return r;
    }
    output_started_ = true;
    log(MP_LOG_INFO, "output: %s, %u Hz / %u ch, format %u, buffer %u bytes (%u ms)",
        exclusive_ ? "exclusive" : "shared", output_rate_, output_channels_, output_format_, output_buffer_bytes_,
        output_rate_ != 0 ? output_buffer_bytes_ * 1000u / (output_rate_ * output_channels_ * 4u) : 0u);
    return MP_OK;
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
    if (current_ == t) {
        BASS_Mixer_ChannelRemove(t->stream);
        current_ = nullptr;
        playing_.store(false, std::memory_order_release);
    }
    BASS_StreamFree(t->stream);
    tracks_.erase(it);
    return MP_OK;
}

// ---- transport ----------------------------------------------------------------------------------

mp_result engine::play(track* t, int64_t start_ms) {
    std::lock_guard lock{control_};
    if (!output_open_) {
        return state_fail("mp_engine_play: no output set (mp_engine_set_output first)");
    }
    if (current_ != nullptr) {
        BASS_Mixer_ChannelRemove(current_->stream);
        current_ = nullptr;
    }
    const QWORD start = BASS_ChannelSeconds2Bytes(t->stream, static_cast<double>(start_ms) / 1000.0);
    if (!BASS_ChannelSetPosition(t->stream, start, BASS_POS_BYTE)) {
        return bass_fail("BASS_ChannelSetPosition");
    }
    // NORAMPIN: no fade-in on the join (gapless); the mixer resamples if the source rate differs.
    if (!BASS_Mixer_StreamAddChannel(mixer_, t->stream, BASS_MIXER_CHAN_NORAMPIN)) {
        return bass_fail("BASS_Mixer_StreamAddChannel");
    }
    // MIXTIME: fires when the end is mixed, not when it is heard; the managed side compensates with the clock.
    BASS_Mixer_ChannelSetSync(t->stream, BASS_SYNC_END | BASS_SYNC_MIXTIME, 0, &end_sync, this);
    current_ = t;
    playing_.store(true, std::memory_order_release);
    if (!output_started_) {
        if (!BASS_WASAPI_Start()) {
            return bass_fail("BASS_WASAPI_Start");
        }
        output_started_ = true;
    }
    emit(MP_EVENT_TRACK_STARTED, reinterpret_cast<int64_t>(t), start_ms, nullptr);
    return MP_OK;
}

mp_result engine::pause() {
    std::lock_guard lock{control_};
    if (current_ == nullptr) {
        return state_fail("mp_engine_pause: nothing is playing");
    }
    BASS_Mixer_ChannelFlags(current_->stream, BASS_MIXER_CHAN_PAUSE, BASS_MIXER_CHAN_PAUSE);
    playing_.store(false, std::memory_order_release);
    return MP_OK;
}

mp_result engine::resume() {
    std::lock_guard lock{control_};
    if (current_ == nullptr) {
        return state_fail("mp_engine_resume: nothing is loaded");
    }
    BASS_Mixer_ChannelFlags(current_->stream, 0, BASS_MIXER_CHAN_PAUSE);
    playing_.store(true, std::memory_order_release);
    return MP_OK;
}

mp_result engine::stop() {
    std::lock_guard lock{control_};
    playing_.store(false, std::memory_order_release);
    if (current_ != nullptr) {
        BASS_Mixer_ChannelRemove(current_->stream);
        current_ = nullptr;
    }
    return MP_OK;
}

mp_result engine::seek(int64_t position_ms) {
    std::lock_guard lock{control_};
    if (current_ == nullptr) {
        return state_fail("mp_engine_seek: nothing is loaded");
    }
    const QWORD pos = BASS_ChannelSeconds2Bytes(current_->stream, static_cast<double>(position_ms) / 1000.0);
    // MIXER_RESET drops what the mixer already buffered from the old position.
    if (!BASS_Mixer_ChannelSetPosition(current_->stream, pos, BASS_POS_BYTE | BASS_POS_MIXER_RESET)) {
        return bass_fail("BASS_Mixer_ChannelSetPosition");
    }
    return MP_OK;
}

void engine::set_volume(float linear) {
    gain_.store(std::clamp(linear, 0.0f, 4.0f), std::memory_order_relaxed);
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
    DWORD buffered = output_open_ ? BASS_WASAPI_GetData(nullptr, BASS_DATA_AVAILABLE) : 0;
    if (buffered == static_cast<DWORD>(-1)) {
        buffered = 0;
    }
    out.output_buffered_bytes = buffered;
    const track* t = current_;
    if (t == nullptr) {
        out.position_ms = 0;
        return MP_OK;
    }
    const QWORD pos = BASS_Mixer_ChannelGetPositionEx(t->stream, BASS_POS_BYTE, buffered);
    out.position_ms =
        pos == static_cast<QWORD>(-1) ? 0 : static_cast<int64_t>(BASS_ChannelBytes2Seconds(t->stream, pos) * 1000.0);
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
    copy_utf8(out.output_format, sizeof out.output_format, output_open_ ? format : "none");
    return MP_OK;
}

// ---- audio thread -------------------------------------------------------------------------------

// BASSWASAPI calls this on its own thread; the data it wants is always 32-bit float regardless of the
// device format. Real-time rules: no locks, no allocation, no logging.
unsigned long __stdcall engine::output_proc(void* buffer, unsigned long length, void* user) {
    auto* self = static_cast<engine*>(user);
    const int64_t t0 = qpc_now();

    DWORD got = BASS_ChannelGetData(self->mixer_, buffer, length);
    if (got == static_cast<DWORD>(-1)) {
        got = 0;
    }
    if (got < length) {
        std::memset(static_cast<char*>(buffer) + got, 0, length - got);
        if (self->playing_.load(std::memory_order_acquire)) {
            self->underruns_.fetch_add(1, std::memory_order_relaxed);
        }
    }

    const float gain = self->gain_.load(std::memory_order_relaxed);
    if (gain != 1.0f) {
        auto* samples = static_cast<float*>(buffer);
        const size_t n = got / sizeof(float);
        for (size_t i = 0; i < n; ++i) {
            samples[i] *= gain;
        }
    }

    self->callbacks_.fetch_add(1, std::memory_order_relaxed);
    const auto us = static_cast<uint32_t>((qpc_now() - t0) * 1'000'000 / qpc_frequency());
    uint32_t prev = self->callback_max_us_.load(std::memory_order_relaxed);
    while (us > prev && !self->callback_max_us_.compare_exchange_weak(prev, us, std::memory_order_relaxed)) {
    }
    return length; // always a full buffer: silence pads a short read
}

void __stdcall engine::end_sync(unsigned long /*handle*/, unsigned long channel, unsigned long /*data*/, void* user) {
    auto* self = static_cast<engine*>(user);
    // Mix-time sync on the WASAPI thread. The listener only enqueues (Interop trampoline rule).
    self->playing_.store(false, std::memory_order_release);
    self->emit(MP_EVENT_TRACK_ENDED, static_cast<int64_t>(channel), 0, nullptr);
}

} // namespace mp::audio
