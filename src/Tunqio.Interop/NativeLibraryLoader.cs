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

    /// <summary>Probes <paramref name="path"/> and throws <see cref="NativeAbiMismatchException"/> when its major differs.</summary>
    public static void EnsureCompatible(string path)
    {
        (int major, int minor, _) = Probe(path);
        if (major != ExpectedAbiMajor)
        {
            throw new NativeAbiMismatchException(ExpectedAbiMajor, major, minor, path);
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
            if (major != ExpectedAbiMajor)
            {
                NativeLibrary.Free(handle);
                throw new NativeAbiMismatchException(ExpectedAbiMajor, major, minor, path);
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
