using System.Runtime.InteropServices;
using System.Text;

namespace Tunqio.Interop;

/// <summary>Version and error surface of the native core.</summary>
public static class NativeEngineInfo
{
    /// <summary>The ABI major this assembly was written against (<c>MP_ABI_MAJOR</c>).</summary>
    public const int ExpectedAbiMajor = NativeLibraryLoader.ExpectedAbiMajor;

    /// <summary>The ABI minor this assembly was written against (<c>MP_ABI_MINOR</c>); a core below it is refused.</summary>
    public const int ExpectedAbiMinor = NativeLibraryLoader.ExpectedAbiMinor;

    /// <summary>ABI major of the loaded library.</summary>
    public static int AbiMajor => (int)(NativeMethods.AbiVersion() >> 16);

    /// <summary>ABI minor of the loaded library.</summary>
    public static int AbiMinor => (int)(NativeMethods.AbiVersion() & 0xFFFF);

    /// <summary>Product version reported by the library (<c>mp_version</c>), e.g. <c>0.1.0</c>.</summary>
    public static unsafe string Version
    {
        get
        {
            byte* p = NativeMethods.Version();
            return p == null ? string.Empty : Marshal.PtrToStringUTF8((nint)p) ?? string.Empty;
        }
    }

    /// <summary>
    /// Forces the library to load and its ABI to be checked. The DllImport resolver already refuses an
    /// incompatible library on first use; this just makes that first use explicit.
    /// </summary>
    public static void EnsureAbiCompatible() => _ = NativeMethods.AbiVersion();

    /// <summary>The calling thread's last native error message; empty when the last call succeeded.</summary>
    public static unsafe string LastError()
    {
        Span<byte> buffer = stackalloc byte[512];
        fixed (byte* p = buffer)
        {
            MpResult result = NativeMethods.LastError(p, (nuint)buffer.Length);
            if (result != MpResult.Ok)
            {
                return string.Empty;
            }
        }

        int length = buffer.IndexOf((byte)0);
        return Encoding.UTF8.GetString(length < 0 ? buffer : buffer[..length]);
    }
}
