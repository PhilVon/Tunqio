using System.Runtime.InteropServices;
using System.Text;

namespace Tunqio.Interop;

/// <summary>Thrown when the loaded <c>mpcore.dll</c> speaks a different ABI major version than this assembly.</summary>
public sealed class NativeAbiMismatchException(int expectedMajor, int actualMajor, int actualMinor)
    : InvalidOperationException(
        $"mpcore.dll ABI {actualMajor}.{actualMinor} is incompatible with the expected ABI major {expectedMajor}.")
{
    public int ExpectedMajor { get; } = expectedMajor;

    public int ActualMajor { get; } = actualMajor;

    public int ActualMinor { get; } = actualMinor;
}

/// <summary>Version and error surface of the native core (the E0-S1 slice of the ABI).</summary>
public static class NativeEngineInfo
{
    /// <summary>The ABI major this assembly was written against (<c>MP_ABI_MAJOR</c>).</summary>
    public const int ExpectedAbiMajor = 0;

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

    /// <summary>Refuses to continue when the library's ABI major differs from <see cref="ExpectedAbiMajor"/>.</summary>
    public static void EnsureAbiCompatible()
    {
        int major = AbiMajor;
        if (major != ExpectedAbiMajor)
        {
            throw new NativeAbiMismatchException(ExpectedAbiMajor, major, AbiMinor);
        }
    }

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
