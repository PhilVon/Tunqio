// ISwapChainPanelNative as declared by the Windows App SDK (microsoft.ui.xaml.media.dxinterop.h in the
// Microsoft.WindowsAppSDK.WinUI package). Declared here so mpcore does not depend on a NuGet include path; the
// IID is the WinUI 3 one (63aad0b8-...), distinct from the UWP interface in the Windows SDK.
#pragma once

#include <dxgi1_2.h>
#include <unknwn.h>

struct __declspec(uuid("63aad0b8-7c24-40ff-85a8-640d944cc325")) __declspec(novtable) ISwapChainPanelNativeWinUI3
    : IUnknown {
    virtual HRESULT STDMETHODCALLTYPE SetSwapChain(IDXGISwapChain* swapChain) = 0;
};
