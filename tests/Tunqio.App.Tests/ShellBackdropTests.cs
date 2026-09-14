using System.Text.RegularExpressions;
using FluentAssertions;
using Tunqio.App.Shell;

namespace Tunqio.App.Tests;

/// <summary>
/// E7-S6 (T-79): the Windows 10 half of the Windows 11 enhancements. AC-521 is the fallback decision for each backdrop
/// probe, with a probe that throws; AC-522 is high contrast switching the backdrop off and back on without a restart.
/// </summary>
/// <remarks>
/// Nothing here changes a Windows setting. High contrast is flipped on a fake <see cref="IAccessibilitySignals"/>, the
/// same seam reactive theming is tested through, and the Mica and acrylic probes are answered by a fake
/// <see cref="IBackdropSupport"/>; what is left untested is the two lines that ask Windows.
/// </remarks>
public class ShellBackdropTests
{
    private sealed class FakeSupport(Func<bool> mica, Func<bool> acrylic) : IBackdropSupport
    {
        public int MicaAsked { get; private set; }

        public int AcrylicAsked { get; private set; }

        public bool IsMicaSupported()
        {
            MicaAsked++;
            return mica();
        }

        public bool IsDesktopAcrylicSupported()
        {
            AcrylicAsked++;
            return acrylic();
        }
    }

    private sealed class FakeSignals : IAccessibilitySignals
    {
        public event EventHandler? Changed;

        public bool AnimationsEnabled => true;

        public bool HighContrast { get; set; }

        public Exception? ReadFailure { get; set; }

        bool IAccessibilitySignals.HighContrast => ReadFailure is null ? HighContrast : throw ReadFailure;

        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;

        /// <summary>What Windows does when high contrast is thrown: change the value and say so.</summary>
        public void Set(bool highContrast)
        {
            HighContrast = highContrast;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class RecordingSurface : IShellSurface
    {
        public List<ShellBackdrop.Kind> Shown { get; } = [];

        public Exception? Failure { get; set; }

        public void Show(ShellBackdrop.Kind which)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Shown.Add(which);
        }
    }

    private sealed class Harness
    {
        public FakeSignals Signals { get; } = new();

        public RecordingSurface Surface { get; } = new();

        public List<string> Info { get; } = [];

        public List<string> Warnings { get; } = [];

        /// <summary>Actions posted to "the UI thread", run only when the test says so.</summary>
        public Queue<Action> Posted { get; } = new();

        public ShellBackdropController Build(ShellBackdrop.Kind supported) =>
            new(Signals, supported, Surface, Posted.Enqueue, Info.Add, Warnings.Add);

        public void RunPosted()
        {
            while (Posted.TryDequeue(out Action? action))
            {
                action();
            }
        }
    }

    // ---- AC-521: each probe's fallback decision ------------------------------------------------------------------------

    [Fact]
    public void Windows_11_gets_Mica_and_acrylic_is_not_asked()
    {
        var support = new FakeSupport(() => true, () => true);
        var warnings = new List<string>();

        ShellBackdrop.Probe(support, warnings.Add).Should().Be(ShellBackdrop.Kind.Mica);

        support.AcrylicAsked.Should().Be(0);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Windows_10_without_Mica_falls_back_to_acrylic_quietly()
    {
        // This machine, build 19045: MicaController.IsSupported() is false, DesktopAcrylicController.IsSupported() true.
        var warnings = new List<string>();

        ShellBackdrop.Probe(new FakeSupport(() => false, () => true), warnings.Add).Should().Be(ShellBackdrop.Kind.Acrylic);

        warnings.Should().BeEmpty("an unsupported material is the expected answer on Windows 10, not a fault");
    }

    [Fact]
    public void A_machine_with_neither_material_gets_the_solid_surface()
    {
        var warnings = new List<string>();

        ShellBackdrop.Probe(new FakeSupport(() => false, () => false), warnings.Add).Should().Be(ShellBackdrop.Kind.None);

        warnings.Should().BeEmpty();
    }

    [Fact]
    public void A_Mica_probe_that_throws_is_one_warning_and_falls_back_to_acrylic()
    {
        var warnings = new List<string>();
        var support = new FakeSupport(() => throw new TypeLoadException("MicaController is not on this build"), () => true);

        ShellBackdrop.Kind kind = ShellBackdrop.Probe(support, warnings.Add);

        kind.Should().Be(ShellBackdrop.Kind.Acrylic);
        warnings.Should().ContainSingle().Which.Should().Contain("Mica").And.Contain("TypeLoadException");
    }

    [Fact]
    public void Both_probes_throwing_is_two_warnings_and_the_solid_surface_never_a_throw()
    {
        var warnings = new List<string>();
        var support = new FakeSupport(
            () => throw new InvalidOperationException("class not registered"),
            () => throw new EntryPointNotFoundException("no composition"));

        Func<ShellBackdrop.Kind> probe = () => ShellBackdrop.Probe(support, warnings.Add);

        probe.Should().NotThrow().Which.Should().Be(ShellBackdrop.Kind.None);
        warnings.Should().HaveCount(2);
        warnings[0].Should().Contain("Mica");
        warnings[1].Should().Contain("desktop acrylic");
    }

    [Theory]
    [InlineData(ShellBackdrop.Kind.Mica, false, ShellBackdrop.Kind.Mica)]
    [InlineData(ShellBackdrop.Kind.Acrylic, false, ShellBackdrop.Kind.Acrylic)]
    [InlineData(ShellBackdrop.Kind.None, false, ShellBackdrop.Kind.None)]
    [InlineData(ShellBackdrop.Kind.Mica, true, ShellBackdrop.Kind.None)]
    [InlineData(ShellBackdrop.Kind.Acrylic, true, ShellBackdrop.Kind.None)]
    [InlineData(ShellBackdrop.Kind.None, true, ShellBackdrop.Kind.None)]
    public void High_contrast_always_means_the_solid_surface(ShellBackdrop.Kind supported, bool highContrast, ShellBackdrop.Kind shown) =>
        ShellBackdrop.ForSurface(supported, highContrast).Should().Be(shown);

    // ---- AC-522: high contrast switches the backdrop live -------------------------------------------------------------

    [Fact]
    public void At_startup_the_supported_material_is_shown_and_left_to_the_app_to_log()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);

        controller.Evaluate().Should().Be(ShellBackdrop.Kind.Acrylic);

        h.Surface.Shown.Should().Equal(ShellBackdrop.Kind.Acrylic);
        h.Info.Should().BeEmpty("App logs 'Shell backdrop: Acrylic' for the first choice");
        h.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Turning_high_contrast_on_shows_the_solid_surface_and_turning_it_off_restores_the_material()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);
        controller.Evaluate();

        h.Signals.Set(highContrast: true);
        h.Surface.Shown.Should().Equal([ShellBackdrop.Kind.Acrylic], "the change event is not on the UI thread, so nothing happens until it is posted there");
        h.RunPosted();
        controller.Current.Should().Be(ShellBackdrop.Kind.None);

        h.Signals.Set(highContrast: false);
        h.RunPosted();

        h.Surface.Shown.Should().Equal(ShellBackdrop.Kind.Acrylic, ShellBackdrop.Kind.None, ShellBackdrop.Kind.Acrylic);
        controller.Current.Should().Be(ShellBackdrop.Kind.Acrylic);
        h.Info.Should().HaveCount(2);
        h.Info[0].Should().Contain("solid surface while high contrast is on");
        h.Info[1].Should().Contain("Acrylic restored");
    }

    [Fact]
    public void On_Windows_11_the_same_switch_takes_Mica_off_and_puts_it_back()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Mica);
        controller.Evaluate();

        h.Signals.Set(true);
        h.RunPosted();
        h.Signals.Set(false);
        h.RunPosted();

        h.Surface.Shown.Should().Equal(ShellBackdrop.Kind.Mica, ShellBackdrop.Kind.None, ShellBackdrop.Kind.Mica);
    }

    [Fact]
    public void High_contrast_already_on_at_startup_starts_solid_and_says_why()
    {
        var h = new Harness();
        h.Signals.HighContrast = true;
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);

        controller.Evaluate().Should().Be(ShellBackdrop.Kind.None);

        h.Info.Should().ContainSingle().Which.Should().Contain("high contrast is on").And.Contain("Acrylic supported");
    }

    [Fact]
    public void A_lost_notification_is_caught_by_the_next_poll()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);
        controller.Evaluate();

        h.Signals.HighContrast = true; // changed with no event
        h.Posted.Should().BeEmpty();
        controller.Evaluate(); // the window's one-second poll

        controller.Current.Should().Be(ShellBackdrop.Kind.None);
    }

    [Fact]
    public void A_poll_with_nothing_changed_touches_nothing()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);

        for (int i = 0; i < 10; i++)
        {
            controller.Evaluate();
        }

        h.Signals.Set(false); // a theme or accent change raises the same event with high contrast unchanged
        h.RunPosted();

        h.Surface.Shown.Should().ContainSingle();
        h.Info.Should().BeEmpty();
    }

    [Fact]
    public void A_surface_that_throws_is_one_warning_per_change_and_never_a_throw()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);
        controller.Evaluate();
        h.Surface.Failure = new InvalidOperationException("backdrop target gone");

        h.Signals.Set(true);
        Action posted = h.RunPosted;
        posted.Should().NotThrow();
        for (int i = 0; i < 5; i++)
        {
            controller.Evaluate(); // polls after the failure
        }

        h.Warnings.Should().ContainSingle().Which.Should().Contain("showing None failed");
        controller.Current.Should().Be(ShellBackdrop.Kind.Acrylic, "what is on screen is what the last successful change put there");
        controller.Problem.Should().NotBeNull();

        h.Surface.Failure = null;
        h.Signals.Set(false);
        h.RunPosted();
        controller.Current.Should().Be(ShellBackdrop.Kind.Acrylic);
    }

    [Fact]
    public void A_high_contrast_read_that_throws_leaves_the_backdrop_alone()
    {
        var h = new Harness();
        using ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);
        controller.Evaluate();
        h.Signals.ReadFailure = new System.ComponentModel.Win32Exception(5);

        Func<ShellBackdrop.Kind?> evaluate = controller.Evaluate;
        evaluate.Should().NotThrow().Which.Should().Be(ShellBackdrop.Kind.Acrylic);
        controller.Evaluate();

        h.Warnings.Should().ContainSingle().Which.Should().Contain("high contrast could not be read");
    }

    [Fact]
    public void Disposing_stops_following_the_signals()
    {
        var h = new Harness();
        ShellBackdropController controller = h.Build(ShellBackdrop.Kind.Acrylic);
        controller.Evaluate();
        h.Signals.Subscribers.Should().Be(1);

        controller.Dispose();
        h.Signals.Set(true);
        h.RunPosted();
        controller.Evaluate();

        h.Signals.Subscribers.Should().Be(0);
        h.Surface.Shown.Should().ContainSingle();
    }
}

/// <summary>
/// AC-521's "nothing that only exists on Windows 11 is called unguarded", as a guard on the source rather than a promise:
/// every backdrop material and controller is named in <c>ShellBackdrop.cs</c> alone, where each is probed before use,
/// and no DWM window attribute (the corner preference, the Mica and backdrop-type attributes) or the undocumented
/// composition attribute is called anywhere, managed or native.
/// </summary>
/// <remarks>
/// WinRT calls newer than build 19041 are guarded by the compiler instead: the projections carry
/// <c>SupportedOSPlatform</c> and the build's <c>SupportedOSPlatformVersion</c> is 10.0.19041.0, so CA1416 under
/// <c>-warnaserror</c> refuses one that is not checked. The Windows App SDK's own types carry no such attribute, which
/// is why this test exists for the ones that depend on Windows 11.
/// </remarks>
public class WindowsVersionGuardTests
{
    private static readonly Regex Material = new(@"\b(MicaController|DesktopAcrylicController|MicaBackdrop|DesktopAcrylicBackdrop)\b", RegexOptions.Compiled);

    private static readonly Regex Dwm = new(
        @"\b(DwmSetWindowAttribute|DWMWA_\w+|DWMSBT_\w+|DWMWCP_\w+|SetWindowCompositionAttribute)\b", RegexOptions.Compiled);

    private static IEnumerable<string> SourceFiles()
    {
        string[] roots = [RepoPaths.File("src"), RepoPaths.File("native")];
        string[] extensions = [".cs", ".xaml", ".cpp", ".h", ".hpp"];
        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin" or "bass"));
    }

    private static string Relative(string file) => Path.GetRelativePath(RepoPaths.Root, file);

    [Fact]
    public void Backdrop_materials_are_named_only_where_they_are_probed()
    {
        var files = SourceFiles().ToList();
        var naming = files.Where(f => Material.IsMatch(File.ReadAllText(f))).Select(Relative).ToList();

        naming.Should().Equal(
            [Path.Combine("src", "Tunqio.App", "Shell", "ShellBackdrop.cs")],
            "a Mica or acrylic material used outside ShellBackdrop skips its IsSupported probe and throws or fails silently on Windows 10");
    }

    [Fact]
    public void No_DWM_window_attribute_is_set_anywhere()
    {
        var calling = SourceFiles().Where(f => Dwm.IsMatch(File.ReadAllText(f))).Select(Relative).ToList();

        calling.Should().BeEmpty(
            "DWMWA_WINDOW_CORNER_PREFERENCE and DWMWA_SYSTEMBACKDROP_TYPE exist only on Windows 11; rounded corners and snap " +
            "layouts come from the system frame both windows keep, so the app has no reason to set them");
    }

    [Fact]
    public void The_scan_sees_the_source_tree_it_is_meant_to_guard()
    {
        // A guard that found no files would pass on anything.
        var files = SourceFiles().Select(Relative).ToList();

        files.Should().Contain(Path.Combine("src", "Tunqio.App", "MainWindow.xaml.cs"))
            .And.Contain(Path.Combine("src", "Tunqio.App", "Shell", "MiniPlayerWindow.xaml.cs"));
        files.Should().Contain(f => f.StartsWith("native", StringComparison.Ordinal) && f.EndsWith(".cpp", StringComparison.Ordinal));
    }
}
