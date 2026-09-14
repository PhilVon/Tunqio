using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;
using Tunqio.Core;

namespace Tunqio.App.Crash;

/// <summary>
/// Opt-in crash capture (E8-S5): with <c>diagnostics.crashReporting</c> on, a crash that ends the process writes a report into
/// the data root's crash folder before it ends: a minidump through <c>MiniDumpWriteDump</c>, the last 200 log lines, and what
/// the exception was. Off (the default), nothing is captured and Windows does what it always does.
/// </summary>
/// <remarks>
/// <para>
/// Two ways in. A managed crash reaches <see cref="CaptureUnhandled"/> from App's XAML and AppDomain handlers (T-188). A native
/// crash (an access violation on one of mpcore's own threads, say) reaches the top-level unhandled-exception filter this
/// class registers with <c>SetUnhandledExceptionFilter</c>. A crash inside an mpcore export on a managed caller's thread
/// never gets that far: the export's SEH guard has already turned it into <c>MP_E_INTERNAL</c>.
/// </para>
/// <para>
/// The filter sees the CLR's own exception code too (0xE0434352) for a managed exception nothing caught. That one is left to
/// the AppDomain handler, which has the exception object, and every filter hands on to the one registered before it, so the
/// runtime and Windows still do what they would have done. One report per process: whichever path gets there first.
/// </para>
/// <para>
/// Writing from inside a crashing process is best effort. The handler does as little as it can (the log lines are already
/// formatted, the P/Invokes are bound at registration), and nothing it does may throw.
/// </para>
/// </remarks>
public sealed class CrashReporter
{
    /// <summary>The code Windows sees for a managed exception: the AppDomain handler's, not the native filter's.</summary>
    public const uint ClrExceptionCode = 0xE0434352;

    private const string NativeSource = "Native";

    private static CrashReporter? s_installed;
    private static TopLevelFilter? s_filter;
    private static TopLevelFilter? s_previous;
    private static int s_filterRegistered;

    private readonly CrashReportStore _store;
    private readonly CrashLogBuffer _log;
    private readonly ISettingsStore _settings;
    private readonly string _appVersion;
    private readonly Guid _sessionId;
    private readonly TimeProvider _time;
    private volatile bool _enabled;
    private int _captured;

    /// <param name="store">Where reports go.</param>
    /// <param name="log">The session's last log lines.</param>
    /// <param name="settings">Where <c>diagnostics.crashReporting</c> is read, and followed while the app runs.</param>
    /// <param name="appVersion">The build, as a report records it.</param>
    /// <param name="sessionId">The session, as the log names it.</param>
    /// <param name="clock">The report folder's time; the real clock by default.</param>
    public CrashReporter(CrashReportStore store, CrashLogBuffer log, ISettingsStore settings, string appVersion, Guid sessionId, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        _store = store;
        _log = log;
        _settings = settings;
        _appVersion = appVersion ?? string.Empty;
        _sessionId = sessionId;
        _time = clock ?? TimeProvider.System;
        _enabled = ReadEnabled(settings);
        settings.Changed += OnSettingChanged;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int TopLevelFilter(nint exceptionPointers);

    /// <summary>Whether a crash now would be captured: the setting as it stands.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>The opt-in rule in one place: <c>diagnostics.crashReporting</c>, off unless the user turned it on.</summary>
    public static bool ReadEnabled(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.GetValue(SettingsKeys.DiagnosticsCrashReporting, SettingsKeys.Defaults.DiagnosticsCrashReporting);
    }

    /// <summary>
    /// Makes this the process's reporter: App's handlers reach it through <see cref="CaptureUnhandled"/>, and the native filter
    /// is registered now if the setting is on, or when it is turned on later.
    /// </summary>
    public void Install()
    {
        s_installed = this;
        if (_enabled)
        {
            RegisterNativeFilter();
        }

        Log.Information(
            "Crash reporting: {State}; reports go to {Folder}",
            _enabled ? "on, a crash writes a minidump and the last 200 log lines" : "off, nothing is captured", _store.Root);
    }

    /// <summary>App's unhandled-exception handlers: a report from the installed reporter, or null when there is none or it is off.</summary>
    public static CrashReport? CaptureUnhandled(string source, Exception? exception) => s_installed?.CaptureManaged(source, exception);

    /// <summary>Writes a report for a managed exception that is ending the process; null when off, already captured, or it failed.</summary>
    public CrashReport? CaptureManaged(string source, Exception? exception) =>
        Capture(
            source,
            exception?.GetType().FullName ?? "(no exception object)",
            exception?.Message ?? string.Empty,
            exception?.ToString(),
            null,
            0);

    private CrashReport? CaptureNative(nint exceptionPointers, uint code) =>
        Capture(NativeSource, NativeExceptionText.Type(code), NativeExceptionText.Where(exceptionPointers, code), null, code, exceptionPointers);

#pragma warning disable CA1031 // A crash handler reports what went wrong and never throws.
    private CrashReport? Capture(string source, string type, string message, string? stack, uint? code, nint exceptionPointers)
    {
        if (!_enabled || Interlocked.Exchange(ref _captured, 1) != 0)
        {
            return null;
        }

        try
        {
            string folder = _store.CreateReportFolder(_time.GetUtcNow(), Environment.ProcessId);
            string dumpPath = Path.Combine(folder, CrashReportStore.DumpFileName);
            long bytes = MiniDump.Write(dumpPath, exceptionPointers, out string? problem);
            if (bytes > 0)
            {
                Log.Fatal("Crash report: minidump of {DumpBytes} bytes ({DumpSize}) written to {DumpPath}", bytes, FormatSize(bytes), dumpPath);
            }
            else
            {
                Log.Fatal("Crash report: no minidump could be written to {DumpPath} ({Problem})", dumpPath, problem);
            }

            // After the dump line, so the report's own log says how big its dump is.
            CrashReportStore.WriteLog(folder, _log.Snapshot());
            CrashReportStore.WriteInfo(folder, new CrashReportInfo(
                source, type, message, stack, code, _time.GetUtcNow(), _sessionId.ToString(), _appVersion, bytes, problem));
            Log.Fatal("Crash report written to {Folder}: {Source} {Type}", folder, source, type);
            return CrashReportStore.Load(folder);
        }
        catch (Exception e)
        {
            try
            {
                Log.Error(e, "Crash report could not be written");
            }
            catch
            {
                // Nothing left to tell.
            }

            return null;
        }
    }

    private static int OnNativeException(nint exceptionPointers)
    {
        try
        {
            uint code = exceptionPointers == 0 ? 0 : unchecked((uint)Marshal.ReadInt32(Marshal.ReadIntPtr(exceptionPointers)));
            if (code != ClrExceptionCode && s_installed is { } reporter)
            {
                reporter.CaptureNative(exceptionPointers, code);
            }
        }
        catch
        {
            // Whatever happened, the previous filter still gets its turn.
        }

        return s_previous is { } previous ? previous(exceptionPointers) : 0; // EXCEPTION_CONTINUE_SEARCH
    }
#pragma warning restore CA1031

    private static void RegisterNativeFilter()
    {
        if (Interlocked.Exchange(ref s_filterRegistered, 1) != 0)
        {
            return;
        }

        MiniDump.Prepare();
        s_filter = OnNativeException;
        nint previous = SetUnhandledExceptionFilter(s_filter);
        s_previous = previous == 0 ? null : Marshal.GetDelegateForFunctionPointer<TopLevelFilter>(previous);
        Log.Information("Crash reporting: native crash filter registered (a previous filter to hand on to: {HasPrevious})", previous != 0);
    }

    private void OnSettingChanged(object? sender, string key)
    {
        if (key != SettingsKeys.DiagnosticsCrashReporting)
        {
            return;
        }

        bool enabled = ReadEnabled(_settings);
        if (enabled == _enabled)
        {
            return;
        }

        _enabled = enabled;
        Log.Information("Crash reporting turned {State}", enabled ? "on" : "off");
        if (enabled && ReferenceEquals(s_installed, this))
        {
            RegisterNativeFilter();
        }
    }

    /// <summary>"812 KB", "3.4 MB": a dump's size as the log and the dialog say it.</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} bytes"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:F0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):F1} MB"),
    };

    [DllImport("kernel32.dll")]
    private static extern nint SetUnhandledExceptionFilter(TopLevelFilter filter);

    /// <summary>What a native exception was and where, read from its EXCEPTION_RECORD (x64 layout).</summary>
    public static class NativeExceptionText
    {
        private const int AddressOffset = 16;
        private const int ParameterCountOffset = 24;
        private const int FirstParameterOffset = 32;

        public static string Type(uint code) => string.Create(CultureInfo.InvariantCulture, $"Native exception 0x{code:X8} ({Name(code)})");

        public static string Name(uint code) => code switch
        {
            0xC0000005 => "access violation",
            0xC00000FD => "stack overflow",
            0xC0000094 => "integer divide by zero",
            0xC000001D => "illegal instruction",
            0xC0000374 => "heap corruption",
            0x80000003 => "breakpoint",
            _ => "structured exception",
        };

        public static string Where(nint exceptionPointers, uint code)
        {
            if (exceptionPointers == 0)
            {
                return "no exception record";
            }

            nint record = Marshal.ReadIntPtr(exceptionPointers);
            nint address = Marshal.ReadIntPtr(record, AddressOffset);
            string module = ModuleOf(address) ?? "an unknown module";
            if (code == 0xC0000005 && Marshal.ReadInt32(record, ParameterCountOffset) >= 2)
            {
                long operation = Marshal.ReadInt64(record, FirstParameterOffset);
                long target = Marshal.ReadInt64(record, FirstParameterOffset + 8);
                string verb = operation switch { 0 => "reading", 1 => "writing", 8 => "executing", _ => "accessing" };
                return string.Create(CultureInfo.InvariantCulture, $"{verb} address 0x{target:X} at 0x{address:X} in {module}");
            }

            return string.Create(CultureInfo.InvariantCulture, $"at 0x{address:X} in {module}");
        }

        private static string? ModuleOf(nint address)
        {
            const uint FromAddress = 0x4;
            const uint UnchangedRefCount = 0x2;
            if (!GetModuleHandleExW(FromAddress | UnchangedRefCount, address, out nint module))
            {
                return null;
            }

            char[] buffer = new char[520];
            uint length = GetModuleFileNameW(module, buffer, (uint)buffer.Length);
            return length == 0 ? null : Path.GetFileName(new string(buffer, 0, (int)length));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetModuleHandleExW(uint flags, nint address, out nint module);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetModuleFileNameW(nint module, [Out] char[] buffer, uint size);
    }
}
