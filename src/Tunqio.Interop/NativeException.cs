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
public sealed class NativeAbiMismatchException
    : InvalidOperationException
{
    public NativeAbiMismatchException(int expectedMajor, int expectedMinor, int actualMajor, int actualMinor, string path)
        : base(Describe(expectedMajor, expectedMinor, actualMajor, actualMinor, path))
    {
        ExpectedMajor = expectedMajor;
        ExpectedMinor = expectedMinor;
        ActualMajor = actualMajor;
        ActualMinor = actualMinor;
        Path = path;
    }

    public int ExpectedMajor { get; }

    /// <summary>The ABI minor this build of Tunqio.Interop was compiled against (T-161, Q-36).</summary>
    public int ExpectedMinor { get; }

    public int ActualMajor { get; }

    public int ActualMinor { get; }

    public string Path { get; }

    /// <summary>True when the core is merely too OLD rather than incompatible: the usual cause is a stale build.</summary>
    public bool IsStaleCore => ActualMajor == ExpectedMajor && ActualMinor < ExpectedMinor;

    private static string Describe(int expectedMajor, int expectedMinor, int actualMajor, int actualMinor, string path)
    {
        string found = $"{path} implements mpcore ABI {actualMajor}.{actualMinor}";
        if (actualMajor == expectedMajor && actualMinor < expectedMinor)
        {
            // The T-161 case, and the message is written for the developer who is actually going to hit it: a
            // project-scoped build leaves an old mpcore.dll beside a new shell, and every symptom of that shows
            // up as a missing feature rather than as a missing DLL.
            return found
                + $"; this build of Tunqio.Interop was compiled against {expectedMajor}.{expectedMinor} and calls exports that core does not have. "
                + "The core beside the app is older than the app. In a development tree, build the SOLUTION so the native project is built too: "
                + "msbuild Tunqio.sln -p:Configuration=Debug -p:Platform=x64. Otherwise reinstall so the native core and the shell match.";
        }

        return found
            + $"; this build of Tunqio.Interop requires ABI major {expectedMajor} (built against {expectedMajor}.{expectedMinor}). "
            + "Reinstall so the native core and the shell match.";
    }
}
