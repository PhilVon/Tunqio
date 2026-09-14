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
    public static Logger Create(AppPaths paths, Guid sessionId) => new LoggerConfiguration()
        .MinimumLevel.Debug()
        .Enrich.WithProperty("SessionId", sessionId)
        .WriteTo.Debug(formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(
            paths.LogFileTemplate,
            formatProvider: CultureInfo.InvariantCulture,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{SessionId}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
}
