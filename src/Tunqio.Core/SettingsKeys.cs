namespace Tunqio.Core;

/// <summary>Setting keys and defaults from docs/solution-structure.md ("Settings keys"). Keep the two in step.</summary>
public static class SettingsKeys
{
    public const string OutputDeviceId = "output.deviceId";
    public const string OutputMode = "output.mode"; // "shared" | "exclusive"
    public const string OutputBufferMs = "output.bufferMs";

    public const string PlaybackGapless = "playback.gapless";
    public const string PlaybackCrossfadeMs = "playback.crossfadeMs";
    public const string PlaybackReplayGain = "playback.replayGain"; // "off" | "track" | "album"
    public const string PlaybackReplayGainPreampDb = "playback.replayGainPreampDb";
    public const string PlaybackResumeOnLaunch = "playback.resumeOnLaunch";

    public const string LibrarySplitArtists = "library.splitArtists";
    public const string LibraryWriteRatingsToFiles = "library.writeRatingsToFiles";

    public const string UiTheme = "ui.theme"; // "system" | "light" | "dark"
    public const string UiMode = "ui.mode";   // "discovery" | "focus" | "curation"
    public const string UiHoverPreview = "ui.hoverPreview";

    /// <summary>E5-S5 (Q-74): the first-hover offer to turn previews on has been made, or the user chose on the settings page; it is never made again.</summary>
    public const string UiHoverPreviewOffered = "ui.hoverPreviewOffered";
    public const string UiReactiveTheming = "ui.reactiveTheming";
    public const string UiReactiveSmoothing = "ui.reactiveSmoothing";
    public const string UiCloseToTray = "ui.closeToTray";
    public const string UiMinimizeToTray = "ui.minimizeToTray";
    public const string UiToastOnTrackChange = "ui.toastOnTrackChange";

    /// <summary>Library views (E3-S8): the Albums grid's sort ("title" | "artist" | "year" | "added" | "played") and the Tracks table's hidden columns (comma-separated <c>TrackColumn</c> names).</summary>
    public const string UiAlbumsSort = "ui.albumsSort";
    public const string UiTracksHiddenColumns = "ui.tracksHiddenColumns";

    public const string VizPreset = "viz.preset";
    public const string VizQuality = "viz.quality"; // "auto" | "low" | "medium" | "high"

    public const string DiagnosticsCrashReporting = "diagnostics.crashReporting";

    /// <summary>Bookkeeping written by the host on every launch (E0-S6 proves persistence with it).</summary>
    public const string AppLaunchCount = "app.launchCount";
    public const string AppLastLaunchUtc = "app.lastLaunchUtc";
    public const string AppLastSessionId = "app.lastSessionId";

    public static class Defaults
    {
        public const string OutputMode = "shared";
        public const int OutputBufferMsShared = 40;
        public const int OutputBufferMsExclusive = 10;
        public const bool PlaybackGapless = true;
        public const int PlaybackCrossfadeMs = 0;
        public const string PlaybackReplayGain = "album";
        public const float PlaybackReplayGainPreampDb = 0f;
        public const bool PlaybackResumeOnLaunch = true;
        public const bool LibrarySplitArtists = true;
        public const bool LibraryWriteRatingsToFiles = false;
        public const string UiTheme = "system";
        public const string UiMode = "discovery";
        public const bool UiHoverPreview = false;
        public const bool UiHoverPreviewOffered = false;
        public const bool UiReactiveTheming = true;
        public const float UiReactiveSmoothing = 0.15f;
        public const bool UiCloseToTray = false;
        public const bool UiMinimizeToTray = false;
        public const bool UiToastOnTrackChange = false;
        public const string UiAlbumsSort = "title";
        public const string UiTracksHiddenColumns = "";
        /// <summary>Ambient Glow on a first run (E5-S3, Phil in Q-72: one preset for every mode, Ambient Glow as the default).</summary>
        public const string VizPreset = "ambient-glow";
        public const string VizQuality = "auto";
        public const bool DiagnosticsCrashReporting = false;
    }
}
