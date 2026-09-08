using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.ApplicationDirectory | DllImportSearchPath.SafeDirectories)]

namespace Tunqio.Interop;

/// <summary>Result codes of the mpcore C ABI (<c>mp_result</c> in <c>mpcore.h</c>).</summary>
public enum MpResult
{
    Ok = 0,
    InvalidArgument = 1,
    Bass = 2,
    Device = 3,
    D3D = 4,
    State = 5,
    Internal = 6,
}

/// <summary>
/// Source-generated bindings for <c>mpcore.h</c>. One entry per export; every export is <c>__cdecl</c>.
/// Nothing outside this class names the native library.
/// </summary>
internal static unsafe partial class NativeMethods
{
    internal const string LibraryName = "mpcore";

    [LibraryImport(LibraryName, EntryPoint = "mpcore_abi_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial uint AbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "mp_version")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial byte* Version();

    [LibraryImport(LibraryName, EntryPoint = "mp_last_error")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial MpResult LastError(byte* buffer, nuint length);
}
