# Visualization Engine Architecture

> **Status: foundation document, partially superseded.** The D3D11 → D3D9Ex → WPF D3DImage bridge is replaced by a WinUI 3 SwapChainPanel with a composition swap chain and a dedicated render thread (ADR-002). The sub-10 ms latency figure is restated as a measurable one-refresh target with look-ahead compensation (ADR-012). Analysis, feature extraction, shader and quality-scaling sections remain valid; presets are data-driven per ADR-009. The next section is **as built** and supersedes the HLSL sketches further down, which describe constant buffers the engine does not have. See [decisions.md](decisions.md) for the record and [README.md](README.md) for the current reading order.

## Preset format and constant-buffer contract (as built, E4-S3)

A preset is a directory: `preset.json` plus the HLSL it names. The core scans one preset root at renderer creation — `MPCORE_PRESET_ROOT` when set, otherwise `presets/` beside `mpcore.dll` — and compiles a preset's shader only when it is selected. Nothing is compiled ahead of time and nothing is cached on disk; `D3DCompile` on a preset of this size costs single-digit milliseconds.

The core also carries **one preset compiled into it**, `builtin-bars`. It is always first in the catalogue and is what a renderer starts on, so a missing or empty preset root costs the user a choice rather than a picture — and `mp_renderer_set_preset` always has a previous preset to fall back to. It is not one of the four built-ins of ADR-009; those are files, and E4-S4/E4-S5 write them.

```json
{
  "schema": 1,
  "id": "spectrum-bars",
  "name": "Spectrum Bars",
  "shader": "spectrum-bars.hlsl",
  "vertex_entry": "VSMain",
  "pixel_entry": "PSMain",
  "topology": "trianglelist",
  "vertex_count": 6,
  "instance_count": 64,
  "clear": [0.04, 0.04, 0.06, 1.0],
  "parameters": [
    { "name": "gain", "default": 1.0, "min": 0.0, "max": 4.0 }
  ]
}
```

`id` and `name` are required; everything else has a default. `id` is 1–63 bytes of `[A-Za-z0-9._-]` and `name` at most 127, because both have to fit `mp_preset_info`. `shader` must name a file beside `preset.json` — absolute paths and anything containing `..` are refused rather than resolved, because a preset is data and will one day be data a user downloaded. At most 16 parameters; a `default` outside `min`..`max` is clamped to it. A manifest that does not parse, or whose shader is not there, is skipped with a warning in the log: one bad preset must not cost the user the others.

Every preset sees the same resources and declares none of its own:

```hlsl
cbuffer Frame : register(b0) {
    float4 viewport;   // x = width px, y = height px, z = 1/width, w = 1/height
    float4 timing;     // x = seconds since the renderer started, y = seconds since the last frame,
                       // z = frame index, w = mp_analysis_frame.sequence (0 when nothing has played)
    float4 level;      // x = rms, y = peak, z = spectral centroid Hz, w = harmonic ratio
    float4 counts;     // x = onset (0 or 1), y = band count, z = spectrum bins, w = waveform samples
    float4 bands[3];   // the 10 octave bands in .x .y .z .w order; the last two floats are unused
    float4 params[4];  // the preset's own parameters, in the order preset.json declares them
};
Buffer<float> Spectrum : register(t0);   // 1024 magnitudes, full-scale sine = 1.0
Buffer<float> Waveform : register(t1);   // 512 mono samples, the newest hop
```

Both SRVs are bound to the vertex and the pixel stage, so a preset may drive geometry or colour from either. The spectrum and waveform are buffers rather than `cbuffer` arrays because HLSL packs a float array one value per `float4` register: 1024 bins would cost 16 KB of constant buffer to carry 4 KB of data. The renderer polls `mp_analysis_try_get_latest` once per frame and only re-uploads the two buffers when the sequence has moved; `b0` is written every frame because time has.

The contract is the versioned part: `schema` is 1, and a preset that names a schema this build does not know is refused rather than guessed at. Adding a field to the end of `b0` is a schema bump, not a silent change.

**Error reporting.** `mp_renderer_set_preset` compiles on the *calling* thread, and hands the render thread either a finished preset or nothing at all. A shader that does not compile therefore returns `MP_E_D3D` with the compiler's own first diagnostic in `mp_last_error` — file, line, error code and message — while the preset that was already drawing keeps drawing. On the managed side that is `PresetCompilationException.CompilerMessage`, which is what the preset switcher shows.

The visualization engine provides real-time audio-reactive graphics through D3D11/WPF integration, achieving sub-10ms latency from audio sample to visual update while maintaining 60fps performance.

## D3D11/WPF Integration Strategy

### DXGI Surface Sharing Implementation

The core challenge of integrating hardware-accelerated D3D11 rendering with WPF involves sharing GPU textures between rendering contexts without performance penalties.

```cpp
class D3D11WPFBridge {
private:
    ID3D11Device* d3dDevice_;
    ID3D11DeviceContext* d3dContext_;
    IDirect3DDevice9Ex* d3d9Device_;
    ID3D11Texture2D* sharedTexture_;
    IDXGISurface* dxgiSurface_;

public:
    HRESULT Initialize(HWND hwnd, int width, int height) {
        // Create D3D11 device and context
        CreateD3D11Device();

        // Create shared texture with specific bind flags
        D3D11_TEXTURE2D_DESC textureDesc = {
            .Width = width,
            .Height = height,
            .MipLevels = 1,
            .ArraySize = 1,
            .Format = DXGI_FORMAT_B8G8R8A8_UNORM,  // WPF-compatible format
            .SampleDesc = {1, 0},
            .Usage = D3D11_USAGE_DEFAULT,
            .BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE,
            .CPUAccessFlags = 0,
            .MiscFlags = D3D11_RESOURCE_MISC_SHARED
        };

        HRESULT hr = d3dDevice_->CreateTexture2D(&textureDesc, nullptr,
                                               &sharedTexture_);

        // Get shared handle for WPF consumption
        IDXGIResource* dxgiResource;
        hr = sharedTexture_->QueryInterface(&dxgiResource);
        HANDLE sharedHandle;
        hr = dxgiResource->GetSharedHandle(&sharedHandle);

        // Create D3D9Ex wrapper for WPF D3DImage
        CreateD3D9ExSurface(sharedHandle);

        return hr;
    }

    void Present() {
        // Render to D3D11 shared texture
        RenderVisualization();

        // Flush D3D11 context to ensure GPU completion
        d3dContext_->Flush();

        // WPF will read from shared surface via D3DImage
        NotifyWPFUpdate();
    }
};
```

### WPF Integration via D3DImage

```csharp
public class D3DVisualizationControl : D3DImage, INotifyPropertyChanged
{
    private IntPtr sharedSurfaceHandle;
    private D3D9ExSurface d3d9Surface;

    public void UpdateSharedSurface(IntPtr handle)
    {
        sharedSurfaceHandle = handle;
        CreateD3D9Surface();

        // Set WPF back buffer to shared D3D9 surface
        SetBackBuffer(D3DResourceType.IDirect3DSurface9, d3d9Surface.NativeSurface);
    }

    private void CreateD3D9Surface()
    {
        // Create D3D9Ex device wrapper
        var d3d9Ex = new Direct3DEx();
        var device = new DeviceEx(d3d9Ex, 0, DeviceType.Hardware, IntPtr.Zero,
                                CreateFlags.HardwareVertexProcessing |
                                CreateFlags.Multithreaded |
                                CreateFlags.FpuPreserve,
                                presentParams);

        // Open shared surface using handle from D3D11
        d3d9Surface = new D3D9ExSurface(device, sharedSurfaceHandle);
    }
}
```

## Real-Time Audio Analysis Pipeline

### FFT Processing Architecture

The visualization engine processes audio through a dedicated analysis pipeline optimized for real-time performance:

```cpp
class AudioAnalyzer {
private:
    static constexpr size_t FFT_SIZE = 1024;
    static constexpr size_t OVERLAP_SIZE = FFT_SIZE / 2;
    static constexpr float SAMPLE_RATE = 48000.0f;
    static constexpr float UPDATE_RATE = 86.0f;  // ~11.6ms intervals

    // FFT processing context (Intel IPP or FFTW3)
    IppsFFTSpec_R_32f* fftSpec_;
    float* workBuffer_;
    float* windowFunction_;  // Pre-computed Hann window

    // Ring buffer for overlapped processing
    CircularBuffer<float> inputBuffer_;
    CircularBuffer<SpectrumData> outputBuffer_;

public:
    void ProcessAudioBlock(const float* samples, size_t sampleCount) {
        // Add samples to input buffer
        inputBuffer_.Write(samples, sampleCount);

        // Process overlapped FFT windows
        while (inputBuffer_.AvailableRead() >= FFT_SIZE) {
            ProcessFFTWindow();
        }
    }

private:
    void ProcessFFTWindow() {
        float timeData[FFT_SIZE];
        float freqData[FFT_SIZE / 2 + 1];

        // Read windowed audio samples
        inputBuffer_.Read(timeData, FFT_SIZE);

        // Apply Hann window to reduce spectral leakage
        ippsWinHann_32f_I(timeData, FFT_SIZE);

        // Perform forward FFT
        ippsFFTFwd_RToCCS_32f(timeData, freqData, fftSpec_, workBuffer_);

        // Extract visualization features
        SpectrumData spectrum = ExtractFeatures(freqData);

        // Write to output buffer for UI consumption
        outputBuffer_.Write(spectrum);

        // Advance input buffer by overlap amount
        inputBuffer_.Advance(OVERLAP_SIZE);
    }
};
```

### Feature Extraction for Visualization

The analyzer extracts multiple audio characteristics for different visualization purposes:

```cpp
struct SpectrumData {
    float spectralCentroid;     // Brightness: weighted mean frequency
    float rmsAmplitude;         // Overall energy level
    float harmonicRatio;        // Harmonic content vs. noise
    float frequencyBands[6];    // Octave band energy distribution
    float peakFrequency;        // Dominant frequency component
    uint64_t timestamp;         // Sample-accurate timing
};

SpectrumData AudioAnalyzer::ExtractFeatures(const float* spectrum) {
    SpectrumData data = {};
    data.timestamp = GetAudioSampleTime();

    // Calculate spectral centroid (brightness measure)
    float weightedSum = 0.0f, magnitudeSum = 0.0f;
    for (size_t i = 1; i < FFT_SIZE / 2; ++i) {
        float magnitude = std::abs(spectrum[i]);
        float frequency = (i * SAMPLE_RATE) / FFT_SIZE;

        weightedSum += magnitude * frequency;
        magnitudeSum += magnitude;
    }
    data.spectralCentroid = weightedSum / (magnitudeSum + 1e-10f);

    // Calculate RMS amplitude from time domain
    float sumSquares = 0.0f;
    for (size_t i = 0; i < FFT_SIZE; ++i) {
        sumSquares += timeData[i] * timeData[i];
    }
    data.rmsAmplitude = std::sqrt(sumSquares / FFT_SIZE);

    // Extract octave band energy distribution
    ExtractOctaveBands(spectrum, data.frequencyBands);

    // Calculate harmonic-to-noise ratio
    data.harmonicRatio = CalculateHarmonicRatio(spectrum);

    return data;
}
```

### Octave Band Analysis

```cpp
void AudioAnalyzer::ExtractOctaveBands(const float* spectrum,
                                     float* bands) {
    // Define octave band boundaries (Hz)
    const float bandBoundaries[] = {
        20.0f,    // Sub-bass
        60.0f,    // Bass
        250.0f,   // Low midrange
        2000.0f,  // Midrange
        6000.0f,  // High midrange
        20000.0f  // Presence
    };

    for (int band = 0; band < 6; ++band) {
        float lowFreq = (band == 0) ? 0 : bandBoundaries[band - 1];
        float highFreq = bandBoundaries[band];

        // Convert frequency to FFT bin indices
        int lowBin = static_cast<int>((lowFreq * FFT_SIZE) / SAMPLE_RATE);
        int highBin = static_cast<int>((highFreq * FFT_SIZE) / SAMPLE_RATE);

        // Sum energy in frequency range
        float energy = 0.0f;
        for (int bin = lowBin; bin <= highBin && bin < FFT_SIZE / 2; ++bin) {
            energy += spectrum[bin] * spectrum[bin];
        }

        bands[band] = std::sqrt(energy / (highBin - lowBin + 1));
    }
}
```

## Audio-Reactive UI System

### HSL Color Mapping

The UI system maps audio features to visual properties through HSL color space manipulation:

```cpp
class AudioColorMapper {
private:
    // Exponential moving average for temporal smoothing
    struct EMA {
        float alpha = 0.15f;  // Smoothing factor
        float value = 0.0f;

        void Update(float newValue) {
            value = alpha * newValue + (1.0f - alpha) * value;
        }
    };

    EMA spectralCentroidEMA_;
    EMA rmsAmplitudeEMA_;
    EMA harmonicRatioEMA_;

public:
    HSLColor MapAudioToColor(const SpectrumData& data) {
        // Update exponential moving averages
        spectralCentroidEMA_.Update(data.spectralCentroid);
        rmsAmplitudeEMA_.Update(data.rmsAmplitude);
        harmonicRatioEMA_.Update(data.harmonicRatio);

        // Map spectral centroid to hue (200Hz-8kHz → 240°-60°)
        float normalizedCentroid = std::clamp(
            (spectralCentroidEMA_.value - 200.0f) / 7800.0f, 0.0f, 1.0f);
        float hue = 240.0f - (normalizedCentroid * 180.0f);  // Blue to yellow

        // Map RMS amplitude to saturation (0-1 with 0.2 floor)
        float saturation = std::clamp(
            rmsAmplitudeEMA_.value * 0.8f + 0.2f, 0.2f, 1.0f);

        // Map harmonic ratio to lightness (more harmonics = warmer)
        float lightness = std::clamp(
            0.5f + (harmonicRatioEMA_.value - 0.5f) * 0.3f, 0.3f, 0.8f);

        return HSLColor{hue, saturation, lightness};
    }
};
```

### Performance-Optimized UI Updates

To maintain 60fps performance while processing audio-reactive updates:

```cpp
class VisualizationRenderer {
private:
    static constexpr float UPDATE_THRESHOLD = 5.0f;  // HSL units
    HSLColor lastColor_;
    std::atomic<bool> updatePending_{false};

public:
    void UpdateVisualization(const SpectrumData& audioData) {
        HSLColor newColor = colorMapper_.MapAudioToColor(audioData);

        // Only update if significant change occurred
        if (HSLDistance(newColor, lastColor_) > UPDATE_THRESHOLD) {
            QueueUIUpdate(newColor);
            lastColor_ = newColor;
        }
    }

private:
    void QueueUIUpdate(const HSLColor& color) {
        if (!updatePending_.exchange(true, std::memory_order_acquire)) {
            // Dispatch to UI thread via WPF Dispatcher
            wpfDispatcher_->BeginInvoke([=]() {
                ApplyColorToUI(color);
                updatePending_.store(false, std::memory_order_release);
            });
        }
    }

    void ApplyColorToUI(const HSLColor& color) {
        // Convert HSL to RGB for WPF brush creation
        RGBColor rgb = HSLToRGB(color);

        // Create hardware-accelerated gradient brush
        auto gradientBrush = std::make_shared<LinearGradientBrush>(
            Color::FromRgb(rgb.r * 0.8f, rgb.g * 0.8f, rgb.b * 0.8f),  // Darker
            Color::FromRgb(rgb.r, rgb.g, rgb.b)                         // Brighter
        );

        // Update UI element backgrounds
        nowPlayingPanel_->Background = gradientBrush;
        visualizationCanvas_->Background = gradientBrush;
    }
};
```

## D3D11 Shader-Based Effects

### Spectrum Visualization Shader

```hlsl
// Vertex Shader
struct VS_INPUT {
    float2 position : POSITION;
    float2 texCoord : TEXCOORD0;
};

struct VS_OUTPUT {
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD0;
};

cbuffer SpectrumData : register(b0) {
    float4 frequencyBands[16];  // 64 frequency bins as float4
    float spectralCentroid;
    float rmsAmplitude;
    float harmonicRatio;
    float time;
};

VS_OUTPUT VertexMain(VS_INPUT input) {
    VS_OUTPUT output;
    output.position = float4(input.position, 0.0f, 1.0f);
    output.texCoord = input.texCoord;
    return output;
}

// Pixel Shader
float4 PixelMain(VS_OUTPUT input) : SV_TARGET {
    float2 uv = input.texCoord;

    // Sample frequency data based on X coordinate
    int freqIndex = int(uv.x * 64.0f);
    float amplitude = frequencyBands[freqIndex / 4][freqIndex % 4];

    // Create spectrum bar visualization
    float barHeight = amplitude * 0.8f + 0.1f;
    float intensity = step(1.0f - uv.y, barHeight);

    // Color mapping based on frequency and amplitude
    float3 baseColor = HSVtoRGB(float3(
        uv.x * 0.8f + 0.1f,  // Hue varies by frequency
        0.8f,                 // High saturation
        intensity             // Brightness based on presence
    ));

    // Add temporal animation using audio energy
    float pulse = sin(time * 10.0f * rmsAmplitude) * 0.1f + 0.9f;
    baseColor *= pulse;

    return float4(baseColor, 1.0f);
}
```

### Waveform Visualization

```hlsl
cbuffer WaveformData : register(b1) {
    float waveformSamples[512];  // Time-domain audio samples
    float waveformScale;
};

float4 WaveformPixelShader(VS_OUTPUT input) : SV_TARGET {
    float2 uv = input.texCoord;

    // Sample waveform based on X coordinate
    int sampleIndex = int(uv.x * 511.0f);
    float amplitude = waveformSamples[sampleIndex] * waveformScale;

    // Create waveform line
    float centerY = 0.5f;
    float waveY = centerY + amplitude * 0.4f;
    float distance = abs(uv.y - waveY);

    // Anti-aliased line rendering
    float lineWidth = 0.002f;
    float alpha = 1.0f - smoothstep(0.0f, lineWidth, distance);

    // Color based on amplitude
    float3 color = lerp(
        float3(0.2f, 0.4f, 0.8f),  // Blue for low amplitude
        float3(1.0f, 0.6f, 0.2f),  // Orange for high amplitude
        abs(amplitude)
    );

    return float4(color, alpha);
}
```

## Performance Optimization Strategies

### GPU Memory Management

```cpp
class VisualizationBufferManager {
private:
    ID3D11Buffer* spectrumConstantBuffer_;
    ID3D11Buffer* waveformConstantBuffer_;

    // Double-buffered GPU resources
    ID3D11Texture2D* renderTargets_[2];
    int currentBuffer_ = 0;

public:
    void UpdateGPUData(const SpectrumData& audioData) {
        // Map constant buffer for GPU update
        D3D11_MAPPED_SUBRESOURCE mappedResource;
        HRESULT hr = d3dContext_->Map(spectrumConstantBuffer_, 0,
                                    D3D11_MAP_WRITE_DISCARD, 0,
                                    &mappedResource);

        if (SUCCEEDED(hr)) {
            // Copy audio data to GPU-accessible memory
            SpectrumConstants* constants =
                static_cast<SpectrumConstants*>(mappedResource.pData);

            memcpy(constants->frequencyBands, audioData.frequencyBands,
                   sizeof(constants->frequencyBands));
            constants->spectralCentroid = audioData.spectralCentroid;
            constants->rmsAmplitude = audioData.rmsAmplitude;
            constants->time = GetCurrentTime();

            d3dContext_->Unmap(spectrumConstantBuffer_, 0);
        }
    }

    void SwapRenderTargets() {
        currentBuffer_ = 1 - currentBuffer_;  // Toggle between 0 and 1
    }
};
```

### Adaptive Quality Scaling

When frame rate drops below 60fps, the visualization engine implements automatic quality reduction:

```cpp
class QualityController {
private:
    float averageFrameTime_ = 16.7f;  // Target 60fps
    int currentQualityLevel_ = 2;     // 0=low, 1=medium, 2=high

public:
    void UpdateQuality(float frameTime) {
        // Exponential moving average of frame time
        averageFrameTime_ = 0.1f * frameTime + 0.9f * averageFrameTime_;

        if (averageFrameTime_ > 20.0f && currentQualityLevel_ > 0) {
            // Frame rate too low, reduce quality
            ReduceQuality();
        } else if (averageFrameTime_ < 14.0f && currentQualityLevel_ < 2) {
            // Frame rate stable, try increasing quality
            IncreaseQuality();
        }
    }

private:
    void ReduceQuality() {
        switch (--currentQualityLevel_) {
            case 1:  // Medium quality
                SetRenderResolution(0.75f);    // 75% resolution
                SetEffectComplexity(0.8f);     // Simpler shaders
                break;
            case 0:  // Low quality
                SetRenderResolution(0.5f);     // 50% resolution
                SetEffectComplexity(0.5f);     // Basic shaders only
                break;
        }
    }
};
```

This visualization engine architecture delivers professional-quality real-time audio visualization while maintaining optimal performance across diverse hardware configurations.