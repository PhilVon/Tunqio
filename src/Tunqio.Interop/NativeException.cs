namespace Tunqio.Interop;

/// <summary>A native call returned something other than <see cref="MpResult.Ok"/>; carries the thread's last error text.</summary>
public sealed class NativeException(MpResult result, string operation, string nativeMessage)
    : InvalidOperationException($"{operation} failed with {result}: {nativeMessage}")
{
    public MpResult Result { get; } = result;

    public string Operation { get; } = operation;

    public string NativeMessage { get; } = nativeMessage;

    /// <summary>Throws when <paramref name="result"/> is not <see cref="MpResult.Ok"/>, reading <c>mp_last_error</c> for the message.</summary>
    internal static void ThrowIfFailed(MpResult result, string operation)
    {
        if (result != MpResult.Ok)
        {
            throw new NativeException(result, operation, NativeEngineInfo.LastError());
        }
    }
}

/// <summary>Thrown when the loaded <c>mpcore.dll</c> speaks a different ABI major version than this assembly.</summary>
public sealed class NativeAbiMismatchException(int expectedMajor, int actualMajor, int actualMinor, string path)
    : InvalidOperationException(
        $"{path} implements mpcore ABI {actualMajor}.{actualMinor}; this build of Tunqio.Interop requires ABI major {expectedMajor}. Reinstall so the native core and the shell match.")
{
    public int ExpectedMajor { get; } = expectedMajor;

    public int ActualMajor { get; } = actualMajor;

    public int ActualMinor { get; } = actualMinor;

    public string Path { get; } = path;
}
