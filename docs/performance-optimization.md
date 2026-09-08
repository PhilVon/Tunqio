# Performance Optimization Guide

> **Status: foundation document, partially superseded.** Intel IPP is not linked in 1.0 (ADR-003). CPU affinity pinning is a diagnostic toggle, not a default (ADR-010). The C++ idioms here apply as written inside the native core (ADR-004); budgets and the adaptive-quality design remain the targets, verified by the harnesses in build-test-release.md. See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

This document outlines critical performance optimization strategies for maintaining real-time audio playback with sub-10ms visualization latency while achieving 60fps UI responsiveness across diverse hardware configurations.

## Memory Management and Cache Optimization

### Cache Line Alignment for Lock-Free Structures

Critical atomic variables require 64-byte alignment to prevent false sharing between CPU cores during high-frequency operations:

```cpp
// Properly aligned lock-free ring buffer
template<typename T, size_t SIZE>
class alignas(64) LockFreeRingBuffer {
private:
    // Each atomic on separate cache lines to prevent false sharing
    alignas(64) std::atomic<uint32_t> writePos_{0};
    alignas(64) std::atomic<uint32_t> readPos_{0};

    // Audio data aligned for SIMD operations
    alignas(32) T buffer_[SIZE];

public:
    bool Write(const T& item) noexcept {
        // Memory ordering ensures visibility across threads
        uint32_t currentWrite = writePos_.load(std::memory_order_relaxed);
        uint32_t nextWrite = (currentWrite + 1) % SIZE;

        if (nextWrite == readPos_.load(std::memory_order_acquire)) {
            return false;  // Buffer full
        }

        buffer_[currentWrite] = item;
        writePos_.store(nextWrite, std::memory_order_release);
        return true;
    }

    bool Read(T& item) noexcept {
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

### Memory Pool Management

Pre-allocate memory pools to eliminate allocations in real-time audio paths:

```cpp
class AudioMemoryPool {
private:
    static constexpr size_t BLOCK_SIZE = 4096;  // 4KB blocks
    static constexpr size_t POOL_SIZE = 256;    // 1MB total

    struct alignas(64) MemoryBlock {
        uint8_t data[BLOCK_SIZE];
        std::atomic<bool> inUse{false};
    };

    std::array<MemoryBlock, POOL_SIZE> memoryBlocks_;
    std::atomic<uint32_t> nextBlock_{0};

public:
    void* Allocate() noexcept {
        // Lock-free allocation using atomic operations
        uint32_t start = nextBlock_.load(std::memory_order_relaxed);

        for (uint32_t i = 0; i < POOL_SIZE; ++i) {
            uint32_t index = (start + i) % POOL_SIZE;
            bool expected = false;

            if (memoryBlocks_[index].inUse.compare_exchange_weak(
                    expected, true, std::memory_order_acquire)) {
                nextBlock_.store((index + 1) % POOL_SIZE, std::memory_order_relaxed);
                return memoryBlocks_[index].data;
            }
        }

        return nullptr;  // Pool exhausted
    }

    void Deallocate(void* ptr) noexcept {
        if (!ptr) return;

        // Calculate block index from pointer
        auto blockPtr = static_cast<uint8_t*>(ptr);
        auto basePtr = reinterpret_cast<uint8_t*>(memoryBlocks_.data());
        ptrdiff_t offset = blockPtr - basePtr;

        if (offset >= 0 && offset < sizeof(memoryBlocks_)) {
            size_t index = offset / sizeof(MemoryBlock);
            memoryBlocks_[index].inUse.store(false, std::memory_order_release);
        }
    }
};
```

### SIMD Optimization for Audio Processing

Leverage SIMD instructions for bulk audio operations:

```cpp
class SIMDAudioProcessor {
public:
    // Process 8 float samples simultaneously using AVX
    static void ApplyGain(float* samples, size_t count, float gain) {
        const __m256 gainVector = _mm256_set1_ps(gain);
        size_t simdCount = count & ~7;  // Round down to multiple of 8

        for (size_t i = 0; i < simdCount; i += 8) {
            __m256 audioVector = _mm256_load_ps(&samples[i]);
            audioVector = _mm256_mul_ps(audioVector, gainVector);
            _mm256_store_ps(&samples[i], audioVector);
        }

        // Handle remaining samples
        for (size_t i = simdCount; i < count; ++i) {
            samples[i] *= gain;
        }
    }

    // Mix two audio streams with SIMD
    static void MixStreams(const float* input1, const float* input2,
                          float* output, size_t count) {
        size_t simdCount = count & ~7;

        for (size_t i = 0; i < simdCount; i += 8) {
            __m256 stream1 = _mm256_load_ps(&input1[i]);
            __m256 stream2 = _mm256_load_ps(&input2[i]);
            __m256 mixed = _mm256_add_ps(stream1, stream2);
            _mm256_store_ps(&output[i], mixed);
        }

        for (size_t i = simdCount; i < count; ++i) {
            output[i] = input1[i] + input2[i];
        }
    }
};
```

## Thread Priority and Scheduling

### Real-Time Thread Configuration

```cpp
class ThreadManager {
public:
    enum class ThreadType {
        AudioCallback,      // Highest priority
        AudioAnalysis,      // High priority
        FormatConversion,   // Normal priority
        UI                  // Normal priority
    };

    static bool SetThreadPriority(std::thread& thread, ThreadType type) {
        HANDLE handle = thread.native_handle();

        int priority = [type]() {
            switch (type) {
                case ThreadType::AudioCallback:
                    return THREAD_PRIORITY_TIME_CRITICAL;
                case ThreadType::AudioAnalysis:
                    return THREAD_PRIORITY_ABOVE_NORMAL;
                case ThreadType::FormatConversion:
                case ThreadType::UI:
                default:
                    return THREAD_PRIORITY_NORMAL;
            }
        }();

        return SetThreadPriority(handle, priority) != 0;
    }

    // Set thread affinity to specific CPU cores
    static bool SetThreadAffinity(std::thread& thread, uint32_t coreMask) {
        HANDLE handle = thread.native_handle();
        DWORD_PTR result = SetThreadAffinityMask(handle, coreMask);
        return result != 0;
    }

    // Dedicated core assignment for audio callback thread
    static void AssignAudioCallbackCore() {
        SYSTEM_INFO sysInfo;
        GetSystemInfo(&sysInfo);

        if (sysInfo.dwNumberOfProcessors >= 4) {
            // Use last CPU core exclusively for audio callback
            uint32_t audioCoreMask = 1 << (sysInfo.dwNumberOfProcessors - 1);
            SetThreadAffinity(audioCallbackThread_, audioCoreMask);
        }
    }
};
```

### Thread Synchronization Without Blocking

```cpp
class NonBlockingThreadSync {
private:
    // Spin-wait with exponential backoff
    static void SmartSpin(std::atomic<bool>& flag, uint32_t maxSpins = 1000) {
        uint32_t spinCount = 0;

        while (flag.load(std::memory_order_acquire) && spinCount < maxSpins) {
            if (spinCount < 100) {
                _mm_pause();  // CPU hint for spin-wait loop
            } else if (spinCount < 500) {
                std::this_thread::yield();  // Yield to other threads
            } else {
                std::this_thread::sleep_for(std::chrono::microseconds(1));
            }
            ++spinCount;
        }
    }

public:
    // Lock-free event signaling
    class EventSignal {
        std::atomic<bool> signaled_{false};

    public:
        void Signal() {
            signaled_.store(true, std::memory_order_release);
        }

        bool WaitForSignal(uint32_t maxSpins = 1000) {
            SmartSpin(signaled_, maxSpins);
            bool wasSignaled = signaled_.load(std::memory_order_acquire);
            if (wasSignaled) {
                signaled_.store(false, std::memory_order_relaxed);
            }
            return wasSignaled;
        }
    };
};
```

## Audio Processing Optimization

### Efficient FFT Implementation

```cpp
class OptimizedFFTProcessor {
private:
    // Intel IPP context for hardware-accelerated FFT
    IppsFFTSpec_R_32f* fftSpec_;
    IppsFFTSpec_C_32fc* ifftSpec_;

    // Pre-allocated work buffers (no real-time allocation)
    alignas(32) float* workBuffer_;
    alignas(32) float* windowFunction_;

    static constexpr size_t FFT_SIZE = 1024;
    static constexpr size_t OVERLAP = FFT_SIZE / 2;

public:
    OptimizedFFTProcessor() {
        // Initialize Intel IPP FFT context
        int specSize, specBufferSize, bufferSize;
        ippsFFTGetSize_R_32f(static_cast<int>(log2(FFT_SIZE)), IPP_FFT_DIV_INV_BY_N,
                            ippAlgHintFast, &specSize, &specBufferSize, &bufferSize);

        fftSpec_ = reinterpret_cast<IppsFFTSpec_R_32f*>(_aligned_malloc(specSize, 64));
        workBuffer_ = static_cast<float*>(_aligned_malloc(bufferSize * sizeof(float), 32));

        ippsFFTInit_R_32f(&fftSpec_, static_cast<int>(log2(FFT_SIZE)),
                         IPP_FFT_DIV_INV_BY_N, ippAlgHintFast, 0, 0);

        // Pre-compute Hann window coefficients
        windowFunction_ = static_cast<float*>(_aligned_malloc(FFT_SIZE * sizeof(float), 32));
        ippsWinHann_32f(windowFunction_, FFT_SIZE);
    }

    // Optimized FFT processing with minimal overhead
    void ProcessSpectrum(const float* timeData, float* freqData) {
        alignas(32) float windowedData[FFT_SIZE];

        // Apply window function with SIMD
        ippsMul_32f(timeData, windowFunction_, windowedData, FFT_SIZE);

        // Perform FFT using Intel IPP (hardware-optimized)
        ippsFFTFwd_RToCCS_32f(windowedData, freqData, fftSpec_, workBuffer_);
    }

    ~OptimizedFFTProcessor() {
        _aligned_free(fftSpec_);
        _aligned_free(workBuffer_);
        _aligned_free(windowFunction_);
    }
};
```

### Audio Buffer Size Optimization

```cpp
class DynamicBufferManager {
private:
    struct BufferConfig {
        uint32_t primarySize;
        uint32_t analysisSize;
        uint32_t crossfadeSize;
        double targetLatency;
    };

    static constexpr BufferConfig PERFORMANCE_CONFIGS[] = {
        {2048,  512,  240,  5.0},   // Low latency (audiophile)
        {4096,  1024, 480,  10.0},  // Balanced (default)
        {8192,  2048, 960,  20.0},  // High compatibility
        {16384, 4096, 1920, 40.0}   // Maximum stability
    };

public:
    static BufferConfig GetOptimalConfig(const SystemInfo& sysInfo) {
        // Select configuration based on system capabilities
        int configIndex = 1;  // Default to balanced

        if (sysInfo.cpuCores >= 8 && sysInfo.memoryGB >= 16) {
            configIndex = 0;  // Low latency
        } else if (sysInfo.cpuCores <= 2 || sysInfo.memoryGB <= 4) {
            configIndex = 3;  // Maximum stability
        } else if (sysInfo.hasRealtimeAudioIssues) {
            configIndex = 2;  // High compatibility
        }

        return PERFORMANCE_CONFIGS[configIndex];
    }

    // Dynamic buffer size adjustment based on performance metrics
    static void AdjustBufferSizes(AudioEngine& engine, const PerformanceMetrics& metrics) {
        if (metrics.audioDropouts > 0) {
            // Increase buffer sizes for stability
            engine.SetBufferSizeMultiplier(1.5f);
        } else if (metrics.averageLatency < metrics.targetLatency * 0.8f) {
            // Decrease buffer sizes for lower latency
            engine.SetBufferSizeMultiplier(0.8f);
        }
    }
};
```

## GPU and Rendering Optimization

### Efficient D3D11 Resource Management

```cpp
class D3D11ResourcePool {
private:
    struct RenderTarget {
        ID3D11Texture2D* texture;
        ID3D11RenderTargetView* rtv;
        ID3D11ShaderResourceView* srv;
        bool inUse;
    };

    std::vector<RenderTarget> renderTargets_;
    uint32_t currentTarget_ = 0;

public:
    void InitializePool(ID3D11Device* device, uint32_t width, uint32_t height, uint32_t count) {
        renderTargets_.resize(count);

        D3D11_TEXTURE2D_DESC textureDesc = {
            .Width = width,
            .Height = height,
            .MipLevels = 1,
            .ArraySize = 1,
            .Format = DXGI_FORMAT_B8G8R8A8_UNORM,
            .SampleDesc = {1, 0},
            .Usage = D3D11_USAGE_DEFAULT,
            .BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            .CPUAccessFlags = 0,
            .MiscFlags = D3D11_RESOURCE_MISC_SHARED
        };

        for (auto& rt : renderTargets_) {
            HRESULT hr = device->CreateTexture2D(&textureDesc, nullptr, &rt.texture);

            if (SUCCEEDED(hr)) {
                device->CreateRenderTargetView(rt.texture, nullptr, &rt.rtv);
                device->CreateShaderResourceView(rt.texture, nullptr, &rt.srv);
                rt.inUse = false;
            }
        }
    }

    RenderTarget* AcquireRenderTarget() {
        // Find available render target
        for (auto& rt : renderTargets_) {
            if (!rt.inUse) {
                rt.inUse = true;
                return &rt;
            }
        }
        return nullptr;  // Pool exhausted
    }

    void ReleaseRenderTarget(RenderTarget* rt) {
        if (rt) {
            rt->inUse = false;
        }
    }
};
```

### Shader Optimization for Real-Time Visualization

```hlsl
// Optimized spectrum visualization shader
cbuffer PerFrameData : register(b0)
{
    float4x4 viewProjection;
    float4 frequencyData[16];  // 64 frequency bins packed as float4
    float time;
    float amplitude;
    float2 resolution;
};

// Vertex shader for instanced spectrum bars
struct VS_INPUT
{
    uint vertexId : SV_VertexID;
    uint instanceId : SV_InstanceID;
};

struct VS_OUTPUT
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
    float amplitude : TEXCOORD1;
};

VS_OUTPUT VertexMain(VS_INPUT input)
{
    VS_OUTPUT output;

    // Generate quad vertices procedurally (no vertex buffer needed)
    float2 quadPos = float2(input.vertexId & 1, (input.vertexId >> 1) & 1);

    // Calculate bar position and size
    float barWidth = 2.0f / 64.0f;  // 64 frequency bars across screen
    float barX = -1.0f + input.instanceId * barWidth;

    // Sample frequency amplitude for this bar
    float freqAmplitude = frequencyData[input.instanceId / 4][input.instanceId % 4];
    float barHeight = freqAmplitude * 1.8f;  // Scale to screen height

    // Position vertex
    output.position = float4(
        barX + quadPos.x * barWidth,
        -1.0f + quadPos.y * barHeight,
        0.0f,
        1.0f
    );

    output.texCoord = quadPos;
    output.amplitude = freqAmplitude;

    return output;
}

// Optimized pixel shader with minimal branching
float4 PixelMain(VS_OUTPUT input) : SV_TARGET
{
    // Color mapping based on frequency and amplitude
    float3 baseColor = lerp(
        float3(0.1f, 0.3f, 0.8f),  // Blue for low frequencies
        float3(1.0f, 0.5f, 0.1f),  // Orange for high frequencies
        input.texCoord.x
    );

    // Intensity falloff from bottom to top
    float intensity = 1.0f - input.texCoord.y;
    intensity = pow(intensity, 2.0f);  // Exponential falloff

    // Amplitude-based brightness
    float brightness = input.amplitude * 0.8f + 0.2f;

    return float4(baseColor * intensity * brightness, 1.0f);
}
```

## Adaptive Quality Management

### Performance Monitoring System

```cpp
class PerformanceMonitor {
private:
    struct PerformanceMetrics {
        std::atomic<double> averageFrameTime{16.67};  // Target 60fps
        std::atomic<uint64_t> audioDropouts{0};
        std::atomic<uint64_t> skippedFrames{0};
        std::atomic<double> cpuUsage{0.0};
        std::atomic<double> gpuUsage{0.0};
    };

    PerformanceMetrics metrics_;
    std::chrono::high_resolution_clock::time_point lastUpdate_;

public:
    void RecordFrameTime(double frameTimeMs) {
        // Exponential moving average for frame time
        double current = metrics_.averageFrameTime.load(std::memory_order_relaxed);
        double updated = 0.1 * frameTimeMs + 0.9 * current;
        metrics_.averageFrameTime.store(updated, std::memory_order_relaxed);
    }

    void RecordAudioDropout() {
        metrics_.audioDropouts.fetch_add(1, std::memory_order_relaxed);
    }

    QualityLevel GetRecommendedQuality() const {
        double frameTime = metrics_.averageFrameTime.load();
        uint64_t dropouts = metrics_.audioDropouts.load();

        if (frameTime > 20.0 || dropouts > 0) {
            return QualityLevel::Low;
        } else if (frameTime > 18.0) {
            return QualityLevel::Medium;
        } else {
            return QualityLevel::High;
        }
    }
};
```

### Automatic Quality Adjustment

```cpp
class QualityManager {
private:
    QualityLevel currentLevel_ = QualityLevel::High;
    std::chrono::steady_clock::time_point lastAdjustment_;

public:
    void UpdateQuality(const PerformanceMetrics& metrics) {
        auto now = std::chrono::steady_clock::now();
        auto timeSinceLastAdjustment = now - lastAdjustment_;

        // Only adjust quality every 2 seconds to prevent oscillation
        if (timeSinceLastAdjustment < std::chrono::seconds(2)) {
            return;
        }

        QualityLevel recommended = metrics.GetRecommendedQuality();

        if (recommended != currentLevel_) {
            ApplyQualityLevel(recommended);
            currentLevel_ = recommended;
            lastAdjustment_ = now;
        }
    }

private:
    void ApplyQualityLevel(QualityLevel level) {
        switch (level) {
            case QualityLevel::Low:
                SetRenderResolution(0.5f);
                SetFFTResolution(512);
                SetUpdateRate(30);  // 30fps
                break;

            case QualityLevel::Medium:
                SetRenderResolution(0.75f);
                SetFFTResolution(1024);
                SetUpdateRate(45);  // 45fps
                break;

            case QualityLevel::High:
                SetRenderResolution(1.0f);
                SetFFTResolution(2048);
                SetUpdateRate(60);  // 60fps
                break;
        }
    }
};
```

## System Resource Management

### Memory Usage Optimization

```cpp
class MemoryManager {
private:
    static constexpr size_t MAX_MEMORY_USAGE = 512 * 1024 * 1024;  // 512MB limit

    std::atomic<size_t> currentUsage_{0};
    std::unordered_map<void*, size_t> allocations_;
    std::mutex allocationsMutex_;

public:
    void* Allocate(size_t size) {
        if (currentUsage_.load() + size > MAX_MEMORY_USAGE) {
            TriggerGarbageCollection();

            if (currentUsage_.load() + size > MAX_MEMORY_USAGE) {
                return nullptr;  // Out of memory budget
            }
        }

        void* ptr = _aligned_malloc(size, 32);
        if (ptr) {
            currentUsage_.fetch_add(size, std::memory_order_relaxed);

            std::lock_guard<std::mutex> lock(allocationsMutex_);
            allocations_[ptr] = size;
        }

        return ptr;
    }

    void Deallocate(void* ptr) {
        if (!ptr) return;

        std::lock_guard<std::mutex> lock(allocationsMutex_);
        auto it = allocations_.find(ptr);
        if (it != allocations_.end()) {
            currentUsage_.fetch_sub(it->second, std::memory_order_relaxed);
            allocations_.erase(it);
            _aligned_free(ptr);
        }
    }

private:
    void TriggerGarbageCollection() {
        // Force cleanup of unused visualization textures
        visualizationRenderer_->CleanupUnusedResources();

        // Clear audio analysis cache
        audioAnalyzer_->ClearAnalysisCache();

        // Compact memory pools
        audioMemoryPool_->Compact();
    }
};
```

### CPU Affinity Optimization

```cpp
class CPUAffinityManager {
private:
    struct CoreAssignment {
        uint32_t audioCallbackCore;
        uint32_t audioAnalysisCore;
        uint32_t uiThreadCore;
        uint32_t backgroundCore;
    };

public:
    static CoreAssignment GetOptimalCoreAssignment() {
        SYSTEM_INFO sysInfo;
        GetSystemInfo(&sysInfo);

        uint32_t coreCount = sysInfo.dwNumberOfProcessors;
        CoreAssignment assignment = {};

        if (coreCount >= 4) {
            // Dedicate last core to audio callback
            assignment.audioCallbackCore = coreCount - 1;
            assignment.audioAnalysisCore = coreCount - 2;
            assignment.uiThreadCore = 0;  // First core for UI
            assignment.backgroundCore = 1;  // Second core for background tasks
        } else {
            // Share cores on lower-end systems
            assignment.audioCallbackCore = 0;
            assignment.audioAnalysisCore = 1;
            assignment.uiThreadCore = 0;
            assignment.backgroundCore = 1;
        }

        return assignment;
    }

    static void ApplyCoreAssignments(const CoreAssignment& assignment) {
        SetThreadAffinity(audioCallbackThread_, 1 << assignment.audioCallbackCore);
        SetThreadAffinity(audioAnalysisThread_, 1 << assignment.audioAnalysisCore);
        SetThreadAffinity(uiThread_, 1 << assignment.uiThreadCore);
        SetThreadAffinity(backgroundThread_, 1 << assignment.backgroundCore);
    }
};
```

These performance optimization strategies ensure the music player maintains professional-grade real-time performance across diverse hardware configurations while gracefully degrading quality when system resources are constrained.