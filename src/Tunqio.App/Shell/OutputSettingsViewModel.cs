using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.App.Playback;
using Tunqio.Core;
using Tunqio.Core.Audio;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>One entry in the Output page's device list. <see cref="Id"/> is null for "the system default".</summary>
public sealed record OutputDeviceRow(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Settings › Output (E6-S3, docs/ui-screens-and-flows.md): the device list with the default marked, shared or
/// exclusive, the buffer, a readout of what the output actually opened at, the test tone, and "use the system
/// default" for a device that has gone. Every change is stored and applied at once through
/// <see cref="PlaybackSession.ApplyOutputAsync"/>, so what the page says is what is playing.
/// </summary>
public sealed partial class OutputSettingsViewModel : ObservableObject
{
    /// <summary>The buffers offered, in milliseconds. 0 is "the mode's default".</summary>
    public static readonly IReadOnlyList<int> BufferChoices = [0, 10, 20, 40, 80, 120, 200];

    private readonly IPlaybackSessionSource _source;
    private readonly ISettingsStore _settings;
    private readonly string _toneDirectory;
    private bool _seeding;

    [ObservableProperty]
    public partial IReadOnlyList<OutputDeviceRow> Devices { get; set; } = [];

    [ObservableProperty]
    public partial OutputDeviceRow? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial bool Exclusive { get; set; }

    /// <summary>The chosen buffer in milliseconds; 0 is the default for the mode.</summary>
    [ObservableProperty]
    public partial int BufferMs { get; set; }

    /// <summary>"48000 Hz · 2 ch · float · shared", from what the engine opened, not from what was asked for.</summary>
    [ObservableProperty]
    public partial string Opened { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasAudio { get; set; }

    [ObservableProperty]
    public partial string Notice { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasNotice { get; set; }

    /// <param name="source">The session, which may arrive after the page is built.</param>
    /// <param name="settings">Where <c>output.*</c> is read from; the session writes it back.</param>
    /// <param name="toneDirectory">Where the test tone's WAV is written for the engine to open (the data root).</param>
    public OutputSettingsViewModel(IPlaybackSessionSource source, ISettingsStore settings, string toneDirectory)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(toneDirectory);
        _source = source;
        _settings = settings;
        _toneDirectory = toneDirectory;
    }

    /// <summary>"Default" in the list: the device Windows plays through when nothing is chosen.</summary>
    public const string DefaultDeviceName = "System default";

    /// <summary>The label for a buffer choice: "Default (40 ms)" for 0, else "20 ms".</summary>
    public static string BufferLabel(int bufferMs, bool exclusive) => bufferMs <= 0
        ? string.Create(CultureInfo.CurrentCulture, $"Default ({OutputPolicy.DefaultBufferMs(exclusive ? OutputMode.Exclusive : OutputMode.Shared)} ms)")
        : string.Create(CultureInfo.CurrentCulture, $"{bufferMs} ms");

    /// <summary>
    /// The rows for <paramref name="devices"/>: the system default first, naming the device it currently is, then every
    /// device by name, the default marked. A stored device that is not connected stays in the list, so the page shows
    /// what the user chose rather than silently choosing something else.
    /// </summary>
    public static IReadOnlyList<OutputDeviceRow> Rows(IReadOnlyList<OutputDevice> devices, string? storedId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        OutputDevice? systemDefault = devices.FirstOrDefault(d => d.IsDefault);
        var rows = new List<OutputDeviceRow>
        {
            new(null, systemDefault is null ? DefaultDeviceName : DefaultDeviceName + " (" + systemDefault.Name + ")"),
        };
        rows.AddRange(devices.Select(d => new OutputDeviceRow(d.Id, d.IsDefault ? d.Name + " · default" : d.Name)));
        if (storedId is not null && !devices.Any(d => string.Equals(d.Id, storedId, StringComparison.OrdinalIgnoreCase)))
        {
            rows.Add(new OutputDeviceRow(storedId, "Not connected (" + storedId + ")"));
        }

        return rows;
    }

    /// <summary>Reads the devices and the stored preference, and what the output opened at.</summary>
    public void Load()
    {
        PlaybackSession? session = _source.Session;
        HasAudio = session is not null;
        OutputPreference preference = OutputPolicy.Read(_settings);
        _seeding = true;
        try
        {
            Devices = Rows(session?.OutputDevices() ?? [], preference.DeviceId);
            SelectedDevice = Devices.FirstOrDefault(r => string.Equals(r.Id, preference.DeviceId, StringComparison.OrdinalIgnoreCase)) ?? Devices[0];
            Exclusive = preference.Mode == OutputMode.Exclusive;
            BufferMs = preference.BufferMs ?? 0;
        }
        finally
        {
            _seeding = false;
        }

        ReadOpened();
    }

    /// <summary>Test tone: writes the WAV once, then plays it through the open output.</summary>
    public async Task PlayTestToneAsync(CancellationToken ct = default)
    {
        if (_source.Session is not { } session)
        {
            SetNotice("Audio is not available, so there is no output to test.");
            return;
        }

        string path = Path.Combine(_toneDirectory, "test-tone.wav");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(_toneDirectory);
            await File.WriteAllBytesAsync(path, TestTone.Wav(), ct);
        }

        bool started = await session.PlayTestToneAsync(path, ct);
        SetNotice(started
            ? "Playing a test tone through " + (SelectedDevice?.Name ?? DefaultDeviceName) + "."
            : "The test tone did not start. If a preview is still playing, try again in a moment.");
    }

    /// <summary>"Use the system default": for a device that has gone, without forgetting which one was chosen.</summary>
    public async Task UseSystemDefaultAsync(CancellationToken ct = default)
    {
        if (_source.Session is not { } session)
        {
            return;
        }

        bool opened = await session.UseSystemDefaultOutputAsync(ct);
        SetNotice(opened ? "Playing through the system default." : "The system default output could not be opened.");
        ReadOpened();
    }

    partial void OnSelectedDeviceChanged(OutputDeviceRow? value) => ApplyAsync().Forget("Apply output device");

    partial void OnExclusiveChanged(bool value) => ApplyAsync().Forget("Apply output mode");

    partial void OnBufferMsChanged(int value) => ApplyAsync().Forget("Apply output buffer");

    private async Task ApplyAsync()
    {
        if (_seeding || _source.Session is not { } session)
        {
            return;
        }

        var preference = new OutputPreference(SelectedDevice?.Id, Exclusive ? OutputMode.Exclusive : OutputMode.Shared, BufferMs > 0 ? BufferMs : null);
        bool opened = await session.ApplyOutputAsync(preference);
        ReadOpened();
        if (!opened)
        {
            SetNotice("That output could not be opened. The previous one is still in use.");
        }
        else if (Exclusive && session.EngineStats is { Exclusive: false })
        {
            SetNotice("The device would not open in exclusive mode, so it is playing in shared mode.");
        }
        else
        {
            ClearNotice();
        }
    }

    private void ReadOpened()
    {
        try
        {
            if (_source.Session is not { } session)
            {
                Opened = "No output is open.";
                return;
            }

            EngineStats stats = session.EngineStats;
            TimeSpan buffered = stats.BufferedDuration(session.EngineClock);
            Opened = string.Create(
                CultureInfo.CurrentCulture,
                $"{stats.OutputSampleRate} Hz · {stats.OutputChannels} ch · {stats.OutputFormat} · {(stats.Exclusive ? "exclusive" : "shared")} · {buffered.TotalMilliseconds:F0} ms in flight");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Opened = "The output could not be read.";
            Serilog.Log.Debug(e, "Output settings could not read the engine");
        }
    }

    private void SetNotice(string text)
    {
        HasNotice = false;
        Notice = text;
        HasNotice = text.Length > 0;
    }

    private void ClearNotice()
    {
        Notice = string.Empty;
        HasNotice = false;
    }
}
