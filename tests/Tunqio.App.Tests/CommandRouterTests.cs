using Microsoft.Extensions.Logging;
using Tunqio.App.Activation;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S1, AC-473 and AC-153 (the command-line half): what a launch's arguments, Explorer's files and a <c>tunqio://</c> URI
/// turn into. The files are real ones in a temp folder, because "does this path exist and is it audio" is the rule under
/// test; nothing is played - the target records what it was asked.
/// </summary>
public sealed class CommandRouterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tunqio-router-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RecordingTarget _target = new();
    private readonly ListLogger<CommandRouter> _log = new();

    public CommandRouterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder that will not go is not a test failure.
        }
    }

    private CommandRouter Router() => new(_target, _log);

    private string File_(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "not audio; nothing decodes it");
        return path;
    }

    private static string Uri_(string command, string path) => $"tunqio://{command}?path={Uri.EscapeDataString(path)}";

    private Task<RouteResult> RedirectedAsync(params string[] tokens) => Router().RouteAsync(tokens, workingDirectory: null, emptyMeansShow: true);

    // ---- paths: flow 2 and the fifty-file selection ------------------------------------------------------------------------

    [Fact]
    public async Task One_audio_file_plays_at_the_current_position_and_brings_the_window_forward_Async()
    {
        string song = File_("Song.flac");

        await RedirectedAsync(song);

        _target.Calls.Should().Equal(["foreground", "file:" + song]);
    }

    [Fact]
    public async Task Fifty_files_are_one_queue_replacement_not_fifty_Async()
    {
        string[] files = [.. Enumerable.Range(1, 50).Select(i => File_($"{i:00}.flac"))];

        RouteResult result = await RedirectedAsync(files);

        result.Commands.Should().ContainSingle().Which.Kind.Should().Be(RoutedCommandKind.PlayPaths);
        result.Commands[0].Paths.Should().Equal(files, "Explorer's order is the user's order");
        _target.Calls.Should().Equal(["foreground", "play:50"]);
    }

    [Fact]
    public async Task A_folder_replaces_the_queue_Async()
    {
        File_(Path.Combine("Album", "01.flac"));
        string folder = Path.Combine(_root, "Album");

        await RedirectedAsync(folder);

        _target.Calls.Should().Equal(["foreground", "play:1"]);
        _target.Paths.Should().Equal([folder]);
    }

    [Fact]
    public async Task The_launch_s_own_relative_path_resolves_against_its_working_directory_Async()
    {
        string song = File_("Relative.mp3");

        await Router().RouteAsync(["Relative.mp3"], _root, emptyMeansShow: false);

        _target.Calls.Should().Equal(["foreground", "file:" + song]);
    }

    [Fact]
    public async Task A_redirected_relative_path_is_refused_because_there_is_no_directory_to_resolve_it_in_Async()
    {
        File_("Relative.mp3");

        RouteResult result = await RedirectedAsync("Relative.mp3");

        _target.Calls.Should().BeEmpty();
        result.Refusals.Should().ContainSingle().Which.Should().Contain("not a full path");
        _log.Warnings.Should().ContainSingle();
    }

    [Fact]
    public async Task A_missing_file_and_a_file_that_is_not_audio_are_each_refused_with_one_line_Async()
    {
        string notes = File_("notes.txt");
        string gone = Path.Combine(_root, "gone.flac");
        string song = File_("Song.flac");

        RouteResult result = await RedirectedAsync(gone, notes, song);

        result.Refusals.Should().HaveCount(2);
        result.Refusals[0].Should().Contain("does not exist");
        result.Refusals[1].Should().Contain("not an audio format");
        _log.Warnings.Should().HaveCount(2, "one line per refusal");
        _target.Calls.Should().Equal(["foreground", "file:" + song], "what was playable still plays");
    }

    [Fact]
    public async Task The_app_s_own_switches_are_skipped_with_their_values_and_an_unknown_one_is_refused_Async()
    {
        string song = File_("Song.flac");

        RouteResult result = await Router().RouteAsync(
            ["--data-root", @"C:\scratch\root", "--redact-paths", "--bogus", song], _root, emptyMeansShow: false);

        result.Refusals.Should().ContainSingle().Which.Should().Contain("--bogus");
        _target.Calls.Should().Equal(["foreground", "file:" + song], "the data root's value is not a path to play");
    }

    [Fact]
    public async Task A_path_that_cannot_be_a_path_is_refused_not_thrown_Async()
    {
        RouteResult result = await RedirectedAsync("C:\\bad\0name.flac");

        result.Refusals.Should().ContainSingle();
        _target.Calls.Should().BeEmpty();
    }

    // ---- tunqio:// ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Play_decodes_the_path_and_replaces_the_queue_Async()
    {
        string song = File_(Path.Combine("With spaces & 100% + more", "Café.flac"));

        RouteResult result = await RedirectedAsync(Uri_("play", song));

        result.Refusals.Should().BeEmpty();
        _target.Calls.Should().Equal(["foreground", "play:1"]);
        _target.Paths.Should().Equal([song], "percent-escapes decode, and a literal + stays a +");
    }

    [Fact]
    public async Task Queue_appends_without_bringing_the_window_forward_Async()
    {
        string song = File_("Later.flac");

        await RedirectedAsync(Uri_("queue", song));

        _target.Calls.Should().Equal(["queue:1"]);
    }

    [Fact]
    public async Task Play_takes_every_path_it_is_given_Async()
    {
        string one = File_("1.flac");
        string two = File_("2.flac");

        await RedirectedAsync($"tunqio://play?path={Uri.EscapeDataString(one)}&volume=11&path={Uri.EscapeDataString(two)}");

        _target.Paths.Should().Equal([one, two], "an unknown parameter is ignored, not fatal");
    }

    [Theory]
    [InlineData("tunqio://toggle", "toggle")]
    [InlineData("tunqio://next", "next")]
    [InlineData("tunqio://previous", "previous")]
    [InlineData("TUNQIO://Toggle/", "toggle")]
    [InlineData("tunqio:next", "next")]
    public async Task Transport_commands_reach_the_session_and_leave_the_window_alone_Async(string uri, string call)
    {
        await RedirectedAsync(uri);

        _target.Calls.Should().Equal([call]);
    }

    [Fact]
    public async Task Show_only_brings_the_window_forward_Async()
    {
        await RedirectedAsync("tunqio://show");

        _target.Calls.Should().Equal(["foreground"]);
    }

    [Fact]
    public async Task A_browser_s_trailing_slash_is_not_part_of_the_command_Async()
    {
        string song = File_("Slash.flac");

        await RedirectedAsync($"tunqio://play/?path={Uri.EscapeDataString(song)}");

        _target.Calls.Should().Equal(["foreground", "play:1"]);
    }

    [Theory]
    [InlineData("tunqio://rewind", "unknown")]
    [InlineData("tunqio://", "unknown")]
    [InlineData("tunqio://play/extra?path=C%3A%5Cx.flac", "unknown")]
    [InlineData("tunqio://play", "names no path")]
    [InlineData("tunqio://queue?volume=3", "names no path")]
    [InlineData("tunqio://play?path=", "empty path")]
    [InlineData("tunqio://play?path=relative.flac", "not a full path")]
    [InlineData("tunqio://play?path=C%3A%5Cnowhere%5Cat-all.flac", "does not exist")]
    [InlineData("tunqio://play?path=%ZZ%E0", "not a full path")]
    public async Task Malformed_and_unknown_uris_are_refused_with_one_line_and_run_nothing_Async(string uri, string reason)
    {
        RouteResult result = await RedirectedAsync(uri);

        result.Commands.Should().BeEmpty();
        result.Refusals.Should().ContainSingle().Which.Should().Contain(reason);
        _log.Warnings.Should().ContainSingle();
        _target.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Paths_run_before_the_uris_that_came_with_them_Async()
    {
        string song = File_("First.flac");

        await RedirectedAsync("tunqio://toggle", song);

        _target.Calls.Should().Equal(["foreground", "file:" + song, "toggle"]);
    }

    // ---- jump list items (E7-S5): tunqio://track?id= and tunqio://playlist?id= ------------------------------------------

    [Fact]
    public async Task A_jump_list_track_item_plays_that_track_and_brings_the_window_forward_Async()
    {
        RouteResult result = await RedirectedAsync(CommandRouter.TrackUri(42));

        result.Commands.Should().ContainSingle().Which.Should().Be(RoutedCommand.ForId(RoutedCommandKind.PlayTrack, 42));
        _target.Calls.Should().Equal(["foreground", "track:42"]);
    }

    [Fact]
    public async Task A_jump_list_playlist_item_plays_that_playlist_and_brings_the_window_forward_Async()
    {
        await RedirectedAsync(CommandRouter.PlaylistUri(7));

        _target.Calls.Should().Equal(["foreground", "playlist:7"]);
    }

    [Fact]
    public void The_item_arguments_are_the_uri_grammar_the_router_reads()
    {
        CommandRouter.TrackUri(42).Should().Be("tunqio://track?id=42");
        CommandRouter.PlaylistUri(7).Should().Be("tunqio://playlist?id=7");
    }

    [Fact]
    public async Task A_cold_start_with_an_item_s_arguments_plays_it_beside_the_app_s_own_switches_Async()
    {
        RouteResult result = await Router().RouteAsync(
            ["--data-root", @"C:\scratch\root", CommandRouter.PlaylistUri(3)], _root, emptyMeansShow: false);

        result.Refusals.Should().BeEmpty();
        _target.Calls.Should().Equal(["foreground", "playlist:3"]);
    }

    [Theory]
    [InlineData("TUNQIO://Track/?ID=5&volume=1", "track:5")]
    [InlineData("tunqio:playlist?id=12", "playlist:12")]
    public async Task Case_a_trailing_slash_and_other_parameters_do_not_change_the_item_Async(string uri, string call)
    {
        await RedirectedAsync(uri);

        _target.Calls.Should().Equal(["foreground", call]);
    }

    [Theory]
    [InlineData("tunqio://track", "names no id")]
    [InlineData("tunqio://playlist?name=Mix", "names no id")]
    [InlineData("tunqio://track?id=", "not a positive whole number")]
    [InlineData("tunqio://track?id=abc", "not a positive whole number")]
    [InlineData("tunqio://playlist?id=-3", "not a positive whole number")]
    [InlineData("tunqio://playlist?id=0", "not a positive whole number")]
    [InlineData("tunqio://track?id=99999999999999999999", "not a positive whole number")]
    public async Task A_malformed_item_id_is_refused_with_one_line_and_runs_nothing_Async(string uri, string reason)
    {
        RouteResult result = await RedirectedAsync(uri);

        result.Commands.Should().BeEmpty();
        result.Refusals.Should().ContainSingle().Which.Should().Contain(reason);
        _log.Warnings.Should().ContainSingle();
        _target.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_track_that_no_longer_exists_is_refused_by_the_target_with_one_line_and_never_throws_Async()
    {
        _target.FailOn = "track:404";

        Func<Task> route = () => RedirectedAsync(CommandRouter.TrackUri(404), "tunqio://next");

        await route.Should().NotThrowAsync();
        _log.Warnings.Should().ContainSingle().Which.Should().Contain("PlayTrack (id 404) refused");
        _target.Calls.Should().Equal(["foreground", "track:404", "next"], "the next command still runs");
    }

    // ---- empty input, and never throwing --------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_launch_with_nothing_to_say_shows_the_window_Async()
    {
        await RedirectedAsync(["--data-root", @"C:\scratch"]);

        _target.Calls.Should().Equal(["foreground"]);
    }

    [Fact]
    public async Task The_first_launch_with_no_arguments_does_nothing_Async()
    {
        RouteResult result = await Router().RouteAsync([], _root, emptyMeansShow: false);

        result.Commands.Should().BeEmpty();
        _target.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_command_the_session_cannot_carry_out_is_logged_and_the_next_one_still_runs_Async()
    {
        _target.FailOn = "toggle";

        Func<Task> route = () => RedirectedAsync("tunqio://toggle", "tunqio://next");

        await route.Should().NotThrowAsync();
        _target.Calls.Should().Equal(["toggle", "next"]);
        _log.Warnings.Should().ContainSingle().Which.Should().Contain("no session to play on");
    }

    [Fact]
    public async Task Null_input_is_nothing_rather_than_an_exception_Async()
    {
        RouteResult result = await Router().RouteAsync(null, null, emptyMeansShow: false);

        result.Commands.Should().BeEmpty();
    }

    private sealed class RecordingTarget : ICommandTarget
    {
        public List<string> Calls { get; } = [];

        public List<string> Paths { get; } = [];

        public string? FailOn { get; set; }

        public Task PlayFileNowAsync(string file, CancellationToken ct) => RecordAsync("file:" + file);

        public Task PlayPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            Paths.AddRange(paths);
            return RecordAsync("play:" + paths.Count);
        }

        public Task QueuePathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
        {
            Paths.AddRange(paths);
            return RecordAsync("queue:" + paths.Count);
        }

        public Task TogglePlayPauseAsync(CancellationToken ct) => RecordAsync("toggle");

        public Task NextAsync(CancellationToken ct) => RecordAsync("next");

        public Task PreviousAsync(CancellationToken ct) => RecordAsync("previous");

        public Task PlayTrackAsync(long trackId, CancellationToken ct) => RecordAsync("track:" + trackId);

        public Task PlayPlaylistAsync(long playlistId, CancellationToken ct) => RecordAsync("playlist:" + playlistId);

        public void BringToForeground() => Calls.Add("foreground");

        private Task RecordAsync(string call)
        {
            Calls.Add(call);
            return call == FailOn
                ? Task.FromException(new InvalidOperationException("audio did not start, so there is no session to play on"))
                : Task.CompletedTask;
        }
    }
}

/// <summary>Keeps every formatted line, for asserting how many a behaviour writes.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _lines = [];

    public IReadOnlyList<string> Warnings
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines.Where(l => l.Level == LogLevel.Warning).Select(l => l.Message)];
            }
        }
    }

    public IReadOnlyList<string> Infos
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines.Where(l => l.Level == LogLevel.Information).Select(l => l.Message)];
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_lines)
        {
            _lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
