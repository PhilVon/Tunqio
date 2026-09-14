using System.Globalization;
using Serilog;
using Serilog.Core;
using Tunqio.Library;

namespace Tunqio.App;

/// <summary>
/// The Serilog file logger for a data root: the app's own, and the one a second instance writes its redirect line to
/// before it exits (E7-S1), so both processes' lines land in the same daily file.
/// </summary>
internal static class AppLogging
{
    /// <summary>The log file's line format; the crash report's log lines (E8-S5) use the same one.</summary>
    public const string OutputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SessionId}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    /// <param name="paths">The data root whose logs folder the file goes in.</param>
    /// <param name="sessionId">Stamped on every line.</param>
    /// <param name="extra">A further sink: the app's crash log buffer (E8-S5); none for the short-lived redirect processes.</param>
    public static Logger Create(AppPaths paths, Guid sessionId, ILogEventSink? extra = null)
    {
        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithProperty("SessionId", sessionId)
            .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
            .WriteTo.File(
                paths.LogFileTemplate,
                formatProvider: CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true,
                outputTemplate: OutputTemplate);
        if (extra is not null)
        {
            configuration = configuration.WriteTo.Sink(extra);
        }

        return configuration.CreateLogger();
    }
}
