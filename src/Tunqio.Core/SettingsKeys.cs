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

    /// <summary>
    /// E6-S6: the first-run welcome has been settled for this profile and is never shown again. True when it ran; false
    /// when the profile was judged to predate it (a previous launch or a library folder) and it was never shown.
    /// </summary>
    public const string UiWelcomeShown = "ui.welcomeShown";
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

    /// <summary>
    /// T-184: the renderer's attack and decay envelope on the analysis frame, for every preset. <c>viz.temporalSmoothing</c>
    /// switches it; <c>viz.temporalAttackMs</c> and <c>viz.temporalDecayMs</c> are the two time constants, kept while it
    /// is off so turning it back on restores them.
    /// </summary>
    public const string VizTemporalSmoothing = "viz.temporalSmoothing";
    public const string VizTemporalAttackMs = "viz.temporalAttackMs";
    public const string VizTemporalDecayMs = "viz.temporalDecayMs";

    /// <summary>
    /// T-157: <c>viz.params.&lt;preset&gt;.&lt;name&gt;</c>, one float for each parameter a person has moved on Settings &gt;
    /// Visualization. An absent key means the manifest's default, which stays in the preset. A parameter the manifest
    /// marks hidden (Ambient Glow's <c>art_*</c>, set by code from the album art) is never stored.
    /// </summary>
    public const string VizParamsPrefix = "viz.params.";

    /// <summary>The prefix every stored parameter of one preset shares, such as <c>viz.params.spectrum-bars.</c>.</summary>
    public static string VizParams(string presetId) => VizParamsPrefix + presetId + ".";

    /// <summary>The key for one preset parameter, such as <c>viz.params.spectrum-bars.bars</c>.</summary>
    public static string VizParam(string presetId, string name) => VizParams(presetId) + name;

    public const string DiagnosticsCrashReporting = "diagnostics.crashReporting";

    /// <summary>
    /// E6-S4: <c>shortcuts.&lt;action&gt;</c>, one per row of the shell's shortcut table, holding the chord as text
    /// ("Ctrl+Alt+P"), or "" for an action left without a key. An absent key means the default, which stays in code.
    /// </summary>
    public const string ShortcutsPrefix = "shortcuts.";

    /// <summary>The key for one shortcut action, such as <c>shortcuts.playPause</c>.</summary>
    public static string Shortcut(string actionId) => ShortcutsPrefix + actionId;

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

        /// <summary>
        /// Off on a first run (T-184): an envelope delays how soon a transient is drawn, which works against ADR-012's
        /// latency target, so it is a trade a person chooses rather than one made for them.
        /// </summary>
        public const bool VizTemporalSmoothing = false;

        /// <summary>A 20 ms rise reaches half height within one 60 Hz refresh, so the default costs at most a frame.</summary>
        public const float VizTemporalAttackMs = 20f;

        /// <summary>A 300 ms fall: bars that drop back over a beat rather than vanishing between two.</summary>
        public const float VizTemporalDecayMs = 300f;
        public const bool DiagnosticsCrashReporting = false;
    }
}
