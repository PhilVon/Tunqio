using System.Reflection;
using System.Runtime.InteropServices;

namespace Tunqio.Interop;

/// <summary>
/// Resolves <c>mpcore.dll</c> for the bindings and refuses to use one whose ABI major differs from
/// <see cref="ExpectedAbiMajor"/>. The check runs once, on the first P/Invoke, through a DllImport resolver;
/// a mismatch surfaces as <see cref="NativeAbiMismatchException"/> from that first call instead of a crash later.
/// </summary>
public static unsafe class NativeLibraryLoader
{
    /// <summary>The ABI major this assembly was written against (<c>MP_ABI_MAJOR</c>).</summary>
    public const int ExpectedAbiMajor = 0;

    /// <summary>
    /// The ABI minor this assembly was written against (<c>MP_ABI_MINOR</c>). A core reporting a LOWER minor is
    /// refused: it does not have the exports these bindings call, and T-161 is the record of what happens when
    /// it is merely tolerated. A core reporting a higher minor is accepted, because that is what a minor is for.
    /// </summary>
    /// <remarks>
    /// Q-36, answered by Phil 2026-09-12: refuse always, not only in a development tree. The trade accepted is
    /// that a partially updated install fails at startup rather than silently losing a feature - which is the
    /// better failure, because the silent one cost two false review rejections in a single day: an app whose
    /// core predated <c>mp_renderer_enum_presets</c> opened Settings › Visualization with an empty preset list
    /// and no Refresh button, and twelve harness cases reported missing CONTROLS. Nothing anywhere said the
    /// core was old.
    /// </remarks>
    public const int ExpectedAbiMinor = 16;

    private static readonly object Gate = new();
    private static nint _handle;
    private static string? _loadedPath;

    /// <summary>The full path of the library in use, once loaded.</summary>
    public static string? LoadedPath => _loadedPath;

    /// <summary>Installs the resolver; called from <see cref="NativeMethods"/>'s static constructor before any P/Invoke.</summary>
    internal static void Install() => NativeLibrary.SetDllImportResolver(typeof(NativeMethods).Assembly, Resolve);

    /// <summary>
    /// Loads <paramref name="path"/>, reads its ABI and product version without touching anything else, and
    /// unloads it again. Used by the resolver and by tests with deliberately incompatible stubs.
    /// </summary>
    public static (int Major, int Minor, string Version) Probe(string path)
    {
        nint handle = NativeLibrary.Load(path);
        try
        {
            return ReadVersions(handle);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
    }

    /// <summary>
    /// Probes <paramref name="path"/> and throws <see cref="NativeAbiMismatchException"/> when its major differs
    /// or its minor is below <see cref="ExpectedAbiMinor"/>.
    /// </summary>
    public static void EnsureCompatible(string path)
    {
        (int major, int minor, _) = Probe(path);
        ThrowIfIncompatible(major, minor, path);
    }

    private static void ThrowIfIncompatible(int major, int minor, string path)
    {
        if (major != ExpectedAbiMajor || minor < ExpectedAbiMinor)
        {
            throw new NativeAbiMismatchException(ExpectedAbiMajor, ExpectedAbiMinor, major, minor, path);
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, NativeMethods.LibraryName, StringComparison.Ordinal))
        {
            return nint.Zero; // default probing for anything else
        }

        lock (Gate)
        {
            if (_handle != nint.Zero)
            {
                return _handle;
            }

            string path = Path.Combine(AppContext.BaseDirectory, NativeMethods.LibraryName + ".dll");
            nint handle = NativeLibrary.Load(path);
            (int major, int minor, _) = ReadVersions(handle);
            if (major != ExpectedAbiMajor || minor < ExpectedAbiMinor)
            {
                NativeLibrary.Free(handle);
                throw new NativeAbiMismatchException(ExpectedAbiMajor, ExpectedAbiMinor, major, minor, path);
            }

            _handle = handle;
            _loadedPath = path;
            return handle;
        }
    }

    private static (int Major, int Minor, string Version) ReadVersions(nint handle)
    {
        var abi = (delegate* unmanaged[Cdecl]<uint>)NativeLibrary.GetExport(handle, "mpcore_abi_version");
        var version = (delegate* unmanaged[Cdecl]<byte*>)NativeLibrary.GetExport(handle, "mp_version");
        uint packed = abi();
        byte* text = version();
        return ((int)(packed >> 16), (int)(packed & 0xFFFF), text == null ? string.Empty : Marshal.PtrToStringUTF8((nint)text) ?? string.Empty);
    }
}
