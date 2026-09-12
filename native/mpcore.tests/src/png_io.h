// PNG read and write for the golden-image tests, over WIC - the imaging stack Windows already ships, so no
// third-party decoder joins the build for two files a test reads.
//
// Pixels are BGRA8, tightly packed and row-major, which is what renderer::capture_frame hands back and what a
// B8G8R8A8_UNORM render target holds. The PNG on disk is 32-bit BGRA, so a round trip is lossless: a golden
// image compared against a fresh capture differs only where the rendering differs.
#pragma once

#include <cstdint>
#include <filesystem>
#include <string>
#include <vector>
#include <wincodec.h>
#include <wrl/client.h>

#include <windows.h>

namespace mp::tests {

// CoInitializeEx for the scope, tolerating a thread that is already apartment-initialised. Catch2 runs every
// test on the same thread, so this is entered and left many times in a run; that is what the S_FALSE case is.
class com_scope {
public:
    com_scope() {
        const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        owned_ = SUCCEEDED(hr); // RPC_E_CHANGED_MODE: someone else initialised it differently; leave it alone
    }
    ~com_scope() {
        if (owned_) {
            CoUninitialize();
        }
    }
    com_scope(const com_scope&) = delete;
    com_scope& operator=(const com_scope&) = delete;

private:
    bool owned_ = false;
};

namespace detail {

inline Microsoft::WRL::ComPtr<IWICImagingFactory> wic_factory(std::string& error) {
    Microsoft::WRL::ComPtr<IWICImagingFactory> factory;
    const HRESULT hr =
        CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.GetAddressOf()));
    if (FAILED(hr)) {
        error = "CoCreateInstance(WICImagingFactory) failed: HRESULT 0x" + std::to_string(hr);
    }
    return factory;
}

} // namespace detail

inline bool write_png(const std::filesystem::path& path, const std::vector<uint8_t>& bgra, uint32_t width,
                      uint32_t height, std::string& error) {
    if (bgra.size() != static_cast<size_t>(width) * height * 4u) {
        error = "write_png: " + std::to_string(bgra.size()) + " bytes is not " + std::to_string(width) + "x" +
                std::to_string(height) + " BGRA";
        return false;
    }
    const com_scope com;
    auto factory = detail::wic_factory(error);
    if (!factory) {
        return false;
    }

    std::error_code ec;
    std::filesystem::create_directories(path.parent_path(), ec);

    Microsoft::WRL::ComPtr<IWICStream> stream;
    if (FAILED(factory->CreateStream(&stream)) || FAILED(stream->InitializeFromFilename(path.c_str(), GENERIC_WRITE))) {
        error = "write_png: cannot open " + path.string() + " for writing";
        return false;
    }
    Microsoft::WRL::ComPtr<IWICBitmapEncoder> encoder;
    Microsoft::WRL::ComPtr<IWICBitmapFrameEncode> frame;
    if (FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)) ||
        FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)) ||
        FAILED(encoder->CreateNewFrame(&frame, nullptr)) || FAILED(frame->Initialize(nullptr))) {
        error = "write_png: the PNG encoder would not start on " + path.string();
        return false;
    }
    WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
    if (FAILED(frame->SetSize(width, height)) || FAILED(frame->SetPixelFormat(&format))) {
        error = "write_png: the encoder refused the size or pixel format";
        return false;
    }
    if (format != GUID_WICPixelFormat32bppBGRA) {
        error = "write_png: the encoder would not take 32bppBGRA";
        return false;
    }
    const UINT stride = width * 4u;
    if (FAILED(frame->WritePixels(height, stride, stride * height, const_cast<BYTE*>(bgra.data()))) ||
        FAILED(frame->Commit()) || FAILED(encoder->Commit())) {
        error = "write_png: writing " + path.string() + " failed";
        return false;
    }
    return true;
}

inline bool read_png(const std::filesystem::path& path, std::vector<uint8_t>& bgra, uint32_t& width, uint32_t& height,
                     std::string& error) {
    const com_scope com;
    auto factory = detail::wic_factory(error);
    if (!factory) {
        return false;
    }

    Microsoft::WRL::ComPtr<IWICBitmapDecoder> decoder;
    if (FAILED(factory->CreateDecoderFromFilename(path.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand,
                                                  &decoder))) {
        error = "read_png: cannot open " + path.string();
        return false;
    }
    Microsoft::WRL::ComPtr<IWICBitmapFrameDecode> frame;
    Microsoft::WRL::ComPtr<IWICFormatConverter> converter;
    if (FAILED(decoder->GetFrame(0, &frame)) || FAILED(factory->CreateFormatConverter(&converter)) ||
        FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone, nullptr, 0.0,
                                     WICBitmapPaletteTypeCustom))) {
        error = "read_png: " + path.string() + " would not convert to 32bppBGRA";
        return false;
    }
    UINT w = 0;
    UINT h = 0;
    if (FAILED(converter->GetSize(&w, &h)) || w == 0 || h == 0) {
        error = "read_png: " + path.string() + " has no size";
        return false;
    }
    bgra.resize(static_cast<size_t>(w) * h * 4u);
    const UINT stride = w * 4u;
    if (FAILED(converter->CopyPixels(nullptr, stride, stride * h, bgra.data()))) {
        error = "read_png: reading the pixels of " + path.string() + " failed";
        return false;
    }
    width = w;
    height = h;
    return true;
}

} // namespace mp::tests
