using System.Security.Cryptography;
using System.Text;
using Tunqio.App.Shell;
using Tunqio.Library;

namespace Tunqio.App.Activation;

/// <summary>
/// The single-instance key (E7-S1, AC-475): one running Tunqio per data root, not one per machine. The default profile
/// and every <c>--data-root</c> scratch profile are separate instances, so a harness launched against a scratch root
/// can never redirect its activation into the app somebody is using on the default one.
/// </summary>
/// <remarks>
/// The root is normalised before it is hashed (full path, no trailing separator, case folded, because NTFS paths are
/// case-insensitive), so two spellings of one folder are one instance. It is hashed rather than embedded because the
/// key names a kernel object and a data root can be any length.
/// </remarks>
public static class InstanceKey
{
    /// <summary>Every key starts with this, so a key read in a log names the product.</summary>
    public const string Prefix = "Tunqio-";

    /// <summary>The key for a data root, or for the default profile when <paramref name="dataRoot"/> is null.</summary>
    public static string For(string? dataRoot) => ForDataRoot(dataRoot ?? new AppPaths().DataRoot);

    /// <summary>The key for an explicit data root.</summary>
    public static string ForDataRoot(string dataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        string normal = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normal));
        return Prefix + Convert.ToHexString(hash, 0, 16);
    }

    /// <summary>
    /// False for the measurement modes: a spike opens its own window to measure it and must neither register as the
    /// running instance nor be sent into one. <c>--library-spike</c> in particular runs on the default profile.
    /// </summary>
    public static bool Applies(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return !(LibrarySpikeRunner.IsRequested(args)
            || RenderSpikeRunner.IsRequested(args)
            || NowPlayingSpikeRunner.IsRequested(args)
            || ShellSpikeRunner.IsRequested(args));
    }
}
