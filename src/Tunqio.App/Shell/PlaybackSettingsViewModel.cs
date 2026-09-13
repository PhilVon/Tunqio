using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Tunqio.App.Controls;
using Tunqio.Core;
using Tunqio.Core.Playback;

namespace Tunqio.App.Shell;

/// <summary>
/// Settings › Playback (E6-S3, docs/ui-screens-and-flows.md): gapless, crossfade, ReplayGain mode and preamp, and resume
/// on launch. Each is written to <c>settings.json</c> as it changes, and each says when it takes effect, because the
/// session reads them at different moments: the join settings when it queues the next track, ReplayGain when it opens
/// one, resume at launch. A change that says "now" and lands a track later reads as broken.
/// </summary>
/// <remarks>
/// The previous-track threshold the screen inventory lists has no setting key; <c>PlayQueue.Back</c> fixes it at 3 s,
/// and it is not added here.
/// </remarks>
public sealed partial class PlaybackSettingsViewModel : ObservableObject
{
    /// <summary>The ReplayGain modes in the order the page lists them.</summary>
    public static readonly IReadOnlyList<ReplayGainMode> Modes = [ReplayGainMode.Off, ReplayGainMode.Track, ReplayGainMode.Album];

    /// <summary>The preamp range offered, in dB.</summary>
    public const double PreampMinDb = -12;

    public const double PreampMaxDb = 12;

    private readonly ISettingsStore _settings;
    private bool _seeding;

    [ObservableProperty]
    public partial bool Gapless { get; set; }

    /// <summary>Crossfade in seconds, 0 to 12 in half-second steps; 0 is off.</summary>
    [ObservableProperty]
    public partial double CrossfadeSeconds { get; set; }

    [ObservableProperty]
    public partial int ReplayGainIndex { get; set; }

    [ObservableProperty]
    public partial double PreampDb { get; set; }

    [ObservableProperty]
    public partial bool ResumeOnLaunch { get; set; }

    public PlaybackSettingsViewModel(ISettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _seeding = true;
        try
        {
            Gapless = settings.GetValue(SettingsKeys.PlaybackGapless, SettingsKeys.Defaults.PlaybackGapless);
            CrossfadeSeconds = CrossfadePolicy.Duration(settings.GetValue(SettingsKeys.PlaybackCrossfadeMs, SettingsKeys.Defaults.PlaybackCrossfadeMs)).TotalSeconds;
            ReplayGainMode mode = ReplayGainPolicy.ParseMode(settings.GetValue<string?>(SettingsKeys.PlaybackReplayGain, null));
            ReplayGainIndex = Math.Max(0, Modes.ToList().IndexOf(mode));
            PreampDb = Math.Clamp(settings.GetValue(SettingsKeys.PlaybackReplayGainPreampDb, SettingsKeys.Defaults.PlaybackReplayGainPreampDb), PreampMinDb, PreampMaxDb);
            ResumeOnLaunch = settings.GetValue(SettingsKeys.PlaybackResumeOnLaunch, SettingsKeys.Defaults.PlaybackResumeOnLaunch);
        }
        finally
        {
            _seeding = false;
        }
    }

    public static double CrossfadeMaxSeconds => CrossfadePolicy.MaxCrossfade.TotalSeconds;

    /// <summary>What a ReplayGain mode is called on the page.</summary>
    public static string ModeName(ReplayGainMode mode) => mode switch
    {
        ReplayGainMode.Off => "Off",
        ReplayGainMode.Track => "Track gain",
        _ => "Album gain",
    };

    /// <summary>"Off" or "3.5 s", beside the slider.</summary>
    public string CrossfadeLabel => CrossfadeSeconds <= 0
        ? "Off"
        : string.Create(CultureInfo.CurrentCulture, $"{CrossfadeSeconds:0.#} s");

    /// <summary>"+2.0 dB" beside the preamp slider.</summary>
    public string PreampLabel => string.Create(CultureInfo.CurrentCulture, $"{PreampDb:+0.0;-0.0;0.0} dB");

    /// <summary>When the join settings apply: the session queues the next track's join when the current one starts.</summary>
    public const string JoinTakesEffect = "Takes effect from the next track change.";

    /// <summary>When ReplayGain applies: the gain is set as a track is opened.</summary>
    public const string GainTakesEffect = "Takes effect from the next track that starts.";

    /// <summary>When resume applies.</summary>
    public const string ResumeTakesEffect = "Takes effect the next time Tunqio starts.";

    partial void OnGaplessChanged(bool value) => Store(SettingsKeys.PlaybackGapless, value);

    partial void OnCrossfadeSecondsChanged(double value)
    {
        OnPropertyChanged(nameof(CrossfadeLabel));
        Store(SettingsKeys.PlaybackCrossfadeMs, (int)CrossfadePolicy.Duration((int)Math.Round(value * 1000)).TotalMilliseconds);
    }

    partial void OnReplayGainIndexChanged(int value)
    {
        if (value >= 0 && value < Modes.Count)
        {
            Store(SettingsKeys.PlaybackReplayGain, Modes[value].ToString().ToLowerInvariant());
        }
    }

    partial void OnPreampDbChanged(double value)
    {
        OnPropertyChanged(nameof(PreampLabel));
        Store(SettingsKeys.PlaybackReplayGainPreampDb, (float)Math.Clamp(value, PreampMinDb, PreampMaxDb));
    }

    partial void OnResumeOnLaunchChanged(bool value) => Store(SettingsKeys.PlaybackResumeOnLaunch, value);

    private void Store<T>(string key, T value)
    {
        if (_seeding)
        {
            return;
        }

        _settings.SetValue(key, value);
        _settings.FlushAsync().Forget("Save " + key);
    }
}
