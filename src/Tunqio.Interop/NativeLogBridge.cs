using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Tunqio.Interop;

/// <summary>
/// Forwards <c>mp_log_set_sink</c> messages into <see cref="ILogger"/> under the "native" category. The core
/// never logs from the audio callback, so formatting here is safe. Process-wide; the last installed logger wins.
/// </summary>
public static unsafe class NativeLogBridge
{
    private static ILogger? _logger;

    /// <summary>Installs <paramref name="logger"/> as the native sink for messages at or above <paramref name="minLevel"/>.</summary>
    public static void Install(ILogger logger, MpLogLevel minLevel = MpLogLevel.Debug)
    {
        _logger = logger;
        NativeException.ThrowIfFailed(NativeMethods.LogSetSink(&OnNativeLog, null, minLevel), "mp_log_set_sink");
    }

    /// <summary>Removes the sink.</summary>
    public static void Uninstall()
    {
        NativeException.ThrowIfFailed(NativeMethods.LogSetSink(null, null, MpLogLevel.Error), "mp_log_set_sink");
        _logger = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnNativeLog(MpLogLevel level, byte* message, void* user)
    {
        ILogger? logger = _logger;
        if (logger is null || message == null)
        {
            return;
        }

        string text = Marshal.PtrToStringUTF8((nint)message) ?? string.Empty;
        LogLevel mapped = level switch
        {
            MpLogLevel.Trace => LogLevel.Trace,
            MpLogLevel.Debug => LogLevel.Debug,
            MpLogLevel.Info => LogLevel.Information,
            MpLogLevel.Warn => LogLevel.Warning,
            _ => LogLevel.Error,
        };
#pragma warning disable CA2254 // the native message is the whole message
        logger.Log(mapped, "[native] {Message}", text);
#pragma warning restore CA2254
    }
}
