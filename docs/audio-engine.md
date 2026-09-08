# Audio Engine Architecture

> **Status: foundation document, partially superseded.** PortAudio, libsamplerate, Intel IPP/FFTW and the hand-written WASAPI clients are removed by ADR-003; bassmix, basswasapi and a vendored pffft FFT inside the native core cover those roles, and the gapless join uses a mix-time END sync rather than a custom crossfade buffer. Code samples are directional; the implementation is a native C++ core calling the BASS C API directly, exposed to the C# shell through a C ABI (ADR-004). Thread priorities and budgets are restated in ADR-010. See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

The audio engine forms the critical foundation of the music player, implementing a four-threaded architecture built on BASS.dll with lock-free ring buffers and sample-accurate timing guarantees.

## Engine Core Architecture

### Foundation Layer - BASS.dll Integration

The engine leverages BASS.dll's mature codec ecosystem while adding custom buffering and analysis capabilities:

```cpp
class AudioEngine {
private:
    ICodecManager* codecMgr_;           // Format detection & decoder selection
    PlaybackStateMachine* stateMachine_; // FSM for play/pause/seek/stop
    CircularBufferPool* bufferPool_;    // Lock-free buffer management
    RealtimeProcessor* processor_;      // FFT/analysis for visualization
    OutputDeviceManager* outputMgr_;    // WASAPI/ASIO device handling

public:
    void Initialize(const AudioConfig& config);
    void LoadTrack(const std::string& filepath);
    void Play();
    void Pause();
    void Seek(double position);
    VisualizationData GetRealtimeData();
};
```

### Codec Management Strategy

**Lazy Plugin Loading**: Detect audio format through file header analysis, then load appropriate BASS plugin DLL only when required. This reduces memory footprint and startup time.

```cpp
class CodecManager {
private:
    std::unordered_map<AudioFormat, HPLUGIN> loadedPlugins_;

public:
    HSTREAM CreateStream(const std::string& filepath) {
        AudioFormat format = DetectFormat(filepath);
        EnsurePluginLoaded(format);
        return BASS_StreamCreateFile(FALSE, filepath.c_str(), 0, 0,
                                   BASS_STREAM_DECODE | BASS_UNICODE);
    }
};
```

**Format Negotiation**: All input audio converts to a consistent 32-bit float format at native sample rate, then resamples to the visualization engine's target rate (48kHz) using BASS_ATTRIB_FREQ.

## Four-Thread Processing Model

### Thread 1: Audio Callback (THREAD_PRIORITY_TIME_CRITICAL)

**Purpose**: Manages hardware buffer delivery with absolute timing precision.

```cpp
void CALLBACK AudioCallbackProc(HSTREAM handle, void* buffer,
                                DWORD length, void* user) {
    AudioEngine* engine = static_cast<AudioEngine*>(user);

    // Zero-allocation path - critical for real-time performance
    float* outputBuffer = static_cast<float*>(buffer);
    DWORD samples = length / sizeof(float);

    // Read from primary buffer via lock-free atomic operations
    engine->GetPrimaryBuffer().ReadSamples(outputBuffer, samples);
}
```

**Timing Characteristics**:
- **Priority**: THREAD_PRIORITY_TIME_CRITICAL
- **Interval**: Hardware-driven (typically 10-20ms at 48kHz)
- **Constraints**: Zero allocations, <1ms processing time
- **Failure Mode**: Audio dropouts if timing violated

### Thread 2: Analysis Processing (THREAD_PRIORITY_ABOVE_NORMAL)

**Purpose**: Real-time spectral analysis for visualization data extraction.

```cpp
class AnalysisProcessor {
private:
    static constexpr size_t FFT_SIZE = 1024;
    static constexpr size_t OVERLAP = FFT_SIZE / 2;

    // Intel IPP or FFTW3 context
    IppsFFTSpec_R_32f* fftSpec_;
    float* workBuffer_;
    float* window_;  // Hann window coefficients

public:
    void ProcessBlock() {
        float samples[FFT_SIZE];

        // Lock-free read from primary buffer
        if (primaryBuffer_.ReadSamples(samples, FFT_SIZE)) {
            // Apply windowing function
            ippsWinHann_32f_I(samples, FFT_SIZE);

            // Perform FFT analysis
            float spectrum[FFT_SIZE/2 + 1];
            ippsFFTFwd_RToCCS_32f(samples, spectrum, fftSpec_, workBuffer_);

            // Extract visualization features
            VisualizationData vizData = ExtractFeatures(spectrum);

            // Write to visualization buffer
            visualizationBuffer_.WriteSample(vizData);
        }
    }
};
```

**Feature Extraction Pipeline**:
- **Spectral Centroid**: Brightness measure via weighted frequency mean
- **RMS Amplitude**: Energy level from time-domain samples
- **Harmonic-to-Noise Ratio**: Compare harmonic peaks to spectral floor
- **Frequency Band Energy**: Six octave bands from sub-bass to presence

**Timing Characteristics**:
- **Priority**: THREAD_PRIORITY_ABOVE_NORMAL
- **Update Rate**: 86Hz (11.6ms intervals)
- **Processing Budget**: <8ms per FFT block
- **Buffer Size**: 1024 samples with 50% overlap

### Thread 3: Format Conversion (THREAD_PRIORITY_NORMAL)

**Purpose**: Sample rate conversion and gapless playback preparation.

```cpp
class FormatConverter {
private:
    SRC_STATE* srcState_;  // libsamplerate context
    CrossfadeBuffer crossfadeBuffer_;

public:
    void PrepareNextTrack(const std::string& nextTrack) {
        // Pre-decode first 2-3 seconds for gapless transition
        HSTREAM nextStream = BASS_StreamCreateFile(FALSE, nextTrack.c_str(),
                                                 0, 0, BASS_STREAM_DECODE);

        // Resample to match current playback rate
        float buffer[CROSSFADE_SAMPLES];
        BASS_ChannelGetData(nextStream, buffer, sizeof(buffer));

        // Prepare crossfade buffer with 50ms linear fade
        crossfadeBuffer_.Prepare(buffer, CROSSFADE_SAMPLES);
    }
};
```

**Sample Rate Conversion**: Uses libsamplerate (SRC) for high-quality resampling when source and output sample rates differ. Maintains separate SRC contexts for each audio stream to prevent state contamination.

**Crossfade Implementation**:
```cpp
void CrossfadeBuffer::ApplyFade(float* currentBuffer, float* nextBuffer,
                              size_t samples) {
    constexpr float FADE_TIME_MS = 50.0f;
    const float fadeStep = 1.0f / (FADE_TIME_MS * SAMPLE_RATE / 1000.0f);

    for (size_t i = 0; i < samples; ++i) {
        float fadePos = i * fadeStep;
        currentBuffer[i] *= (1.0f - fadePos);  // Fade out
        nextBuffer[i] *= fadePos;              // Fade in
    }
}
```

### Thread 4: Main UI Thread (THREAD_PRIORITY_NORMAL)

**Purpose**: Consume visualization data and coordinate UI updates.

The UI thread operates on a 60Hz cycle, reading visualization data via lock-free operations and updating D3D11 shader uniforms for real-time visual effects.

## Lock-Free Buffer Implementation

### Circular Buffer Design

```cpp
template<typename T, size_t SIZE>
class LockFreeRingBuffer {
private:
    alignas(64) std::atomic<uint32_t> writePos_{0};  // Separate cache line
    alignas(64) std::atomic<uint32_t> readPos_{0};   // Separate cache line
    T buffer_[SIZE];

public:
    bool Write(const T& item) {
        uint32_t currentWrite = writePos_.load(std::memory_order_relaxed);
        uint32_t nextWrite = (currentWrite + 1) % SIZE;

        if (nextWrite == readPos_.load(std::memory_order_acquire)) {
            return false;  // Buffer full
        }

        buffer_[currentWrite] = item;
        writePos_.store(nextWrite, std::memory_order_release);
        return true;
    }

    bool Read(T& item) {
        uint32_t currentRead = readPos_.load(std::memory_order_relaxed);

        if (currentRead == writePos_.load(std::memory_order_acquire)) {
            return false;  // Buffer empty
        }

        item = buffer_[currentRead];
        readPos_.store((currentRead + 1) % SIZE, std::memory_order_release);
        return true;
    }
};
```

### Buffer Sizing Strategy

| Buffer Type | Size | Duration | Purpose |
|-------------|------|----------|---------|
| Primary Audio | 4MB | 85ms @ 48kHz stereo | Main audio stream buffering |
| Analysis Buffer | 1MB | 21ms @ 1024 samples | Visualization data queue |
| Crossfade Buffer | 9.6KB | 50ms @ 48kHz stereo | Gapless transition audio |
| Hardware Buffer | 1.9-3.8KB | 10-20ms | System-managed audio output |

**Cache Line Alignment**: All atomic variables use 64-byte alignment to prevent false sharing between CPU cores during high-frequency atomic operations.

## Gapless Playback Implementation

### Sample-Accurate Timing

```cpp
class GaplessPlayback {
private:
    HSYNC endSync_;
    CrossfadeBuffer nextTrackBuffer_;

public:
    void SetupGaplessTransition(HSTREAM currentStream, HSTREAM nextStream) {
        // Calculate exact sample boundary where current track ends
        QWORD endPosition = BASS_ChannelGetLength(currentStream, BASS_POS_BYTE);

        // Set sync callback at precise end point
        endSync_ = BASS_ChannelSetSync(currentStream, BASS_SYNC_END,
                                     0, &OnTrackEnd, this);

        // Pre-load next track buffer
        PrepareNextTrackBuffer(nextStream);
    }

private:
    static void CALLBACK OnTrackEnd(HSYNC handle, DWORD channel,
                                  DWORD data, void* user) {
        GaplessPlayback* gapless = static_cast<GaplessPlayback*>(user);
        gapless->ExecuteGaplessTransition();
    }
};
```

### BASS Mixer Integration

Uses BASS_Mixer streams for seamless transitions:

```cpp
void AudioEngine::CreateMixerStream() {
    mixerStream_ = BASS_Mixer_StreamCreate(SAMPLE_RATE, 2,
                                         BASS_STREAM_DECODE |
                                         BASS_MIXER_NONSTOP);

    // Add current track to mixer
    BASS_Mixer_StreamAddChannel(mixerStream_, currentStream_,
                               BASS_MIXER_CHAN_NORAMPIN);
}
```

## Audio Output Management

### Hybrid Output Strategy

The engine implements three output modes to balance performance and compatibility:

#### Mode 1: Exclusive WASAPI + Loopback Capture

```cpp
class ExclusiveWASAPIOutput : public IAudioOutput {
private:
    IMMDevice* outputDevice_;
    IAudioClient* exclusiveClient_;
    IAudioClient* loopbackClient_;  // For system audio mixing

public:
    HRESULT Initialize() override {
        // Initialize exclusive mode client
        HRESULT hr = outputDevice_->Activate(__uuidof(IAudioClient),
                                           CLSCTX_ALL, nullptr,
                                           (void**)&exclusiveClient_);

        // Configure for lowest possible latency
        WAVEFORMATEX* mixFormat;
        hr = exclusiveClient_->GetMixFormat(&mixFormat);
        hr = exclusiveClient_->Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE,
                                        AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                                        0, 0, mixFormat, nullptr);

        // Setup loopback capture for system audio mixing
        SetupLoopbackCapture();

        return hr;
    }
};
```

**Benefits**: Sub-5ms latency, exclusive hardware access, maintained system audio mixing

#### Mode 2: Shared WASAPI with Buffer Optimization

```cpp
class SharedWASAPIOutput : public IAudioOutput {
private:
    static constexpr REFERENCE_TIME BUFFER_DURATION = 100000; // 10ms

public:
    HRESULT Initialize() override {
        return audioClient_->Initialize(AUDCLNT_SHAREMODE_SHARED,
                                      AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                                      BUFFER_DURATION, 0,
                                      mixFormat_, nullptr);
    }
};
```

**Benefits**: Better system compatibility, 15-20ms total latency, automatic format conversion

#### Mode 3: ASIO Passthrough

```cpp
class ASIOOutput : public IAudioOutput {
    // PortAudio wrapper for ASIO driver access
    PaStream* asioStream_;

public:
    HRESULT Initialize() override {
        PaStreamParameters outputParams = {
            .device = Pa_GetDefaultOutputDevice(),
            .channelCount = 2,
            .sampleFormat = paFloat32,
            .suggestedLatency = 0.005  // 5ms target
        };

        return Pa_OpenStream(&asioStream_, nullptr, &outputParams,
                           SAMPLE_RATE, 128, paClipOff,
                           &ASIOCallback, this);
    }
};
```

**Benefits**: <5ms total latency, bypass Windows audio stack, professional audio interface support

## Performance Monitoring and Optimization

### Real-time Metrics Collection

```cpp
class PerformanceMonitor {
private:
    std::atomic<uint64_t> audioCallbackOverruns_{0};
    std::atomic<uint64_t> analysisSkippedFrames_{0};
    std::atomic<double> averageAnalysisTime_{0.0};

public:
    void RecordAudioCallbackTiming(double processingTime) {
        if (processingTime > TARGET_CALLBACK_TIME_MS) {
            audioCallbackOverruns_.fetch_add(1, std::memory_order_relaxed);
        }
    }

    AudioEngineMetrics GetMetrics() const {
        return {
            .callbackOverruns = audioCallbackOverruns_.load(),
            .skippedAnalysisFrames = analysisSkippedFrames_.load(),
            .avgAnalysisTime = averageAnalysisTime_.load()
        };
    }
};
```

### Adaptive Quality Scaling

When system performance degrades, the engine implements fallback strategies:

- **Analysis Resolution**: Reduce FFT size from 1024 to 512 samples
- **Update Rate**: Lower analysis thread frequency from 86Hz to 43Hz
- **Precision**: Switch from double to single precision for non-critical calculations
- **Buffer Size**: Increase buffer sizes to provide more processing headroom

This audio engine architecture provides professional-grade performance while maintaining compatibility across diverse Windows audio configurations.