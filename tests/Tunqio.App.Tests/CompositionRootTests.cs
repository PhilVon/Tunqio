using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core;
using Tunqio.Core.Library;
using Tunqio.Core.Visualization;
using Tunqio.Library;
using Tunqio.Library.Database;

namespace Tunqio.App.Tests;

/// <summary>
/// T-180: the assembled application, asserted. Three features shipped disabled in one day by the same defect —
/// an optional reference parameter on a wiring path that a production caller omitted.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why no ordinary test can catch this shape.</b> T-147/T-156 (<c>ReactiveThemeController</c>'s
/// <c>IVisualizationHost? renderer = null</c>) and T-179 (<c>NativeRenderer.Create</c>'s
/// <c>NativeEngine? engine = null</c>) were both found by Phil looking at the product, never by a test, and the
/// tests were not weak. They test the object, and the object was fine. A test constructs its subject
/// <em>explicitly</em> and therefore always passes the argument, so the one call site that assembles the app — the
/// only one with nothing asserting it — is precisely the one place the omission can hide. agent-static counted it
/// on T-179: 35 sites construct a renderer or attach a host, of which exactly 2 are production, and the defect had
/// a population of one caller.
/// </para>
/// <para>
/// <b>So this asserts the seam rather than the call.</b> An omitted optional argument and an explicit <c>null</c>
/// compile to the same IL, so no test can tell a caller that forgot from a caller that meant it. What a test
/// <em>can</em> do is refuse the declaration that makes forgetting possible: an optional parameter whose type is a
/// Tunqio collaborator, on anything but a data record, must either be made required — so the compiler asks every
/// caller the question — or be written down here with the reason its default is legitimate. Both defects had such
/// a declaration, so both go red against this test at their pre-fix commits.
/// </para>
/// </remarks>
public sealed class CompositionRootTests
{
    /// <summary>The shipped assemblies. A collaborator is a type one of these declares.</summary>
    private static readonly Assembly[] Shipped =
    [
        typeof(App).Assembly,                    // Tunqio (the WinUI app)
        typeof(IVisualizationHost).Assembly,     // Tunqio.Core
        typeof(Interop.NativeEngine).Assembly,   // Tunqio.Interop
        typeof(AppPaths).Assembly,               // Tunqio.Library
    ];

    /// <summary>
    /// Every optional Tunqio-typed parameter on a wiring path, with the reason its default is legitimate.
    /// A default that is not written down here is a defect waiting for the caller who omits it: make the
    /// parameter required (nullable is fine — required and nullable says "answer the question, and null is a
    /// valid answer"), or add the entry and say why.
    /// </summary>
    /// <remarks>
    /// The test applied to every one of the twenty-two seams the scan found at T-180: <b>does a real production
    /// caller rely on this default, and does omitting it substitute something equivalent rather than switching a
    /// feature off?</b> Eighteen failed it — no caller anywhere relied on the default, so it existed only to be
    /// omitted by mistake — and are now required and positional. These four passed it.
    /// </remarks>
    private static readonly ApprovedDefault[] Approved =
    [
        new(
            "Tunqio.App.Shell.NowPlayingViewModel.ctor", "navigator",
            "NowPlayingSpikeRunner (E2-S3 measurement mode, --nowplaying-spike) constructs the panel over a "
            + "NoSessionSource with no library behind it, and measures art through the panel's own binding. It is "
            + "a real shipped caller that wants the default, and what it loses - artist and album lines that do "
            + "not navigate - is not a feature the spike is measuring."),
        new(
            "Tunqio.Library.Repositories.LibraryService.ctor", "tagReader",
            "The only default in the whole scan that substitutes rather than subtracts: null builds a "
            + "TagLibTagReader with default options, so a caller that omits it gets a working reader and nothing "
            + "is switched off. LibrarySpikeRunner (E3-S3) relies on it."),
        new(
            "Tunqio.Library.Repositories.LibraryService.ctor", "artCache",
            "LibrarySpikeRunner (E3-S3 measurement mode, --library-spike) opens a chosen database with no app "
            + "data root beside it and never shows a window; extracting album art into a cache it does not have "
            + "is not something it can do. The DI factory passes the container's cache, and "
            + "The_composition_root_registers_an_art_cache_for_the_scanner_to_extract_into asserts that path."),
        new(
            "Tunqio.Library.Repositories.LibraryService.ctor", "durationProbe",
            "Nothing implements IDurationProbe in any shipped assembly - the only implementation in the "
            + "repository is a test fake - so every caller, DI factory included, passes null today. "
            + "docs/library-and-data.md says so: the probe is 'the engine's decode stream, bound by the host; "
            + "absent until then'. This is a seam waiting for its story, not a feature that was wired and lost, "
            + "and The_duration_probe_is_a_seam_with_no_implementation_not_a_feature_left_unwired pins it."),
    ];

    /// <summary>Declaring type, member and parameter, plus why a caller may leave it out.</summary>
    private sealed record ApprovedDefault(string Member, string Parameter, string Why);

    [Fact]
    public void No_wiring_path_has_an_unapproved_optional_collaborator()
    {
        Seam[] found = [.. Seams()];

        // The guard against the guard, and it has already earned its place: reflection over a WinUI assembly can
        // quietly drop a type it cannot load, and a scan that inspected nothing would report nothing and pass for
        // ever. So assert what was LOOKED AT rather than what was found - the three types the three defects were
        // in - because after the fix none of them yields a seam any more, which is the point.
        string[] inspected = [.. Scanned().Select(t => t.Name)];
        inspected.Should().Contain(
            [nameof(MainWindow), nameof(ReactiveThemeController), nameof(Interop.NativeRenderer), nameof(App)],
            "these are where T-156 and T-179 lived and where the app is assembled; a scan that cannot load them "
            + "is asserting nothing at all");
        inspected.Length.Should().BeGreaterThan(
            200, "the four shipped assemblies have hundreds of types between them, and a scan that suddenly sees "
            + "a handful has lost an assembly rather than found a tidier codebase");

        string[] unapproved =
        [
            .. found
                .Where(s => !Approved.Any(a => a.Member == s.Member && a.Parameter == s.Parameter))
                .Select(s => s.ToString()),
        ];

        unapproved.Should().BeEmpty(
            "an optional Tunqio-typed parameter on a wiring path is how T-156 and T-179 both shipped a feature "
            + "disabled: the caller omits it, the compiler is content, every unit test passes because tests pass "
            + "the argument explicitly, and the feature is absent only in the assembled app. Make it required, or "
            + "add it to CompositionRootTests.Approved with the reason. Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, unapproved));
    }

    [Fact]
    public void Every_approved_default_still_exists()
    {
        Seam[] found = [.. Seams()];

        string[] stale =
        [
            .. Approved
                .Where(a => !found.Any(s => s.Member == a.Member && s.Parameter == a.Parameter))
                .Select(a => a.Member + "(" + a.Parameter + ")"),
        ];

        stale.Should().BeEmpty(
            "an approval that no longer matches a parameter is a reason nobody can check any more, and a list "
            + "with dead entries in it is a list people stop reading");
    }

    /// <summary>
    /// The other half of the same defect, wearing a container instead of a parameter list:
    /// <c>provider.GetService&lt;T&gt;()</c> returns null for an unregistered service exactly as quietly as an
    /// omitted argument does. Every type <see cref="App.OnLaunched"/> resolves has to be registered by
    /// <see cref="AppServices.AddTunqio"/>, or start-up throws after the window is already up (T-120) or,
    /// worse, silently drops a feature.
    /// </summary>
    [Theory]
    [InlineData(typeof(ISettingsStore))]
    [InlineData(typeof(IPlaybackSessionSource))]
    [InlineData(typeof(ILibraryNavigator))]
    [InlineData(typeof(OpenCoordinator))]
    [InlineData(typeof(ITrackRepository))]
    [InlineData(typeof(LibraryScanCoordinator))]
    [InlineData(typeof(ShellNotices))]
    [InlineData(typeof(IVisualizationHost))]
    [InlineData(typeof(IArtCache))]
    [InlineData(typeof(AudioStartup))]
    [InlineData(typeof(ILibraryWatcher))]
    [InlineData(typeof(LibraryDatabase))]
    public void The_composition_root_registers_everything_start_up_resolves(Type service)
    {
        Root().Should().Contain(
            d => d.ServiceType == service,
            "App.OnLaunched resolves {0} while the window is being built, and registration is the only thing "
            + "standing between that and a feature the assembled app does not have",
            service.Name);
    }

    /// <summary>
    /// Named separately because it is the one <see cref="Approved"/> entry that leans on it:
    /// <c>LibraryService</c> may be built without an art cache, so the path that must have one is asserted here.
    /// </summary>
    [Fact]
    public void The_composition_root_registers_an_art_cache_for_the_scanner_to_extract_into()
    {
        Root().Should().Contain(
            d => d.ServiceType == typeof(IArtCache) && d.Lifetime == ServiceLifetime.Singleton,
            "the DI factory hands LibraryService provider.GetService<IArtCache>(), which is null the moment "
            + "nothing registers one - and a scan that extracts no album art looks exactly like a scan");
    }

    /// <summary>
    /// Tells "wired and then lost" apart from "not built yet", which is the distinction nothing else in the
    /// repository makes, and which the <see cref="Approved"/> entry for <c>LibraryService.durationProbe</c>
    /// rests on entirely.
    /// </summary>
    /// <remarks>
    /// T-156 and T-179 were features that existed, worked, and were not reached. <see cref="IDurationProbe"/>
    /// looks identical from the outside — <c>provider.GetService&lt;IDurationProbe&gt;()</c> hands the DI factory
    /// a null in every shipped build — and is the opposite thing: nothing implements it, so no wiring could ever
    /// have reached it. docs/library-and-data.md says as much ("the engine's decode stream, bound by the host;
    /// absent until then"). This test pins the fact that makes the difference, so the day someone writes a
    /// duration probe the approval fails loudly and asks to be wired, instead of the null quietly changing
    /// meaning from "not built yet" to "built and lost".
    /// </remarks>
    [Fact]
    public void The_duration_probe_is_a_seam_with_no_implementation_not_a_feature_left_unwired()
    {
        string[] implementations =
        [
            .. Scanned()
                .Where(t => !t.IsInterface && !t.IsAbstract && typeof(IDurationProbe).IsAssignableFrom(t))
                .Select(t => t.FullName ?? t.Name),
        ];

        implementations.Should().BeEmpty(
            "LibraryService.durationProbe is an approved default ONLY because no shipped assembly implements "
            + "IDurationProbe. The moment one does, that approval is wrong: the scanner's slow path exists, and "
            + "the DI factory's provider.GetService<IDurationProbe>() returns null for an unregistered service "
            + "exactly as quietly as an omitted argument does, so it needs a registration in AddLibrary and this "
            + "test needs replacing with one that asserts the registration. Found:"
            + Environment.NewLine + string.Join(Environment.NewLine, implementations));
    }

    private static IReadOnlyList<ServiceDescriptor> Root() =>
        [.. new ServiceCollection().AddTunqio(new AppPaths(), uiContext: null)];

    /// <summary>Every type the scan actually loaded, so a test can assert the scan is looking at something.</summary>
    private static IEnumerable<Type> Scanned() => Shipped.SelectMany(TypesOf);

    private static IEnumerable<Seam> Seams()
    {
        foreach (Type type in Scanned())
        {
            if (IsRecord(type) || type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            {
                // A record's optional members are data defaults, not collaborators: ScannedTrack's twenty-three
                // and TrackQuery's ten describe a row and a query, and nothing is switched off by omitting one.
                continue;
            }

            MethodBase[] members;
            try
            {
                members =
                [
                    .. type.GetConstructors(Everything),
                    .. type.GetMethods(Everything),
                ];
            }
            catch (TypeLoadException)
            {
                continue;
            }

            foreach (MethodBase member in members)
            {
                if (member.IsPrivate || member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
                {
                    continue;
                }

                foreach (ParameterInfo p in member.GetParameters().Where(IsOptionalCollaborator))
                {
                    yield return new Seam(
                        type.Name,
                        type.FullName + "." + (member is ConstructorInfo ? "ctor" : member.Name),
                        p.Name ?? "?",
                        p.ParameterType);
                }
            }
        }
    }

    private const BindingFlags Everything =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Optional, defaulting to <c>null</c>, and typed as something one of the shipped assemblies declares.
    /// The type test is what keeps the ambient collaborators out without a judgement call: an
    /// <c>ILogger</c>, a <c>TimeProvider</c>, a <c>SynchronizationContext</c>, a <c>Random</c>, a
    /// <c>CancellationToken</c> or a <c>Func&lt;&gt;</c> is declared by the BCL, and none of them is a subsystem
    /// this app can silently lose. A <c>Tunqio</c> type is.
    /// </summary>
    private static bool IsOptionalCollaborator(ParameterInfo p) =>
        p.IsOptional
        && !p.ParameterType.IsValueType
        && p.RawDefaultValue is null
        && Array.Exists(Shipped, a => a == p.ParameterType.Assembly)
        && !IsRecord(p.ParameterType);

    /// <summary>A positional record: the compiler gives every one a <c>&lt;Clone&gt;$</c> method and nothing else has one.</summary>
    private static bool IsRecord(Type type) =>
        type.GetMethod("<Clone>$", Everything) is not null
        || type.GetProperty("EqualityContract", Everything) is not null;

    private static IEnumerable<Type> TypesOf(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.OfType<Type>();
        }
    }

    private static string TypeName(Type t) =>
        t.IsGenericType
            ? t.Name[..t.Name.IndexOf('`', StringComparison.Ordinal)]
                + "<" + string.Join(", ", t.GetGenericArguments().Select(TypeName)) + ">"
            : t.Name;

    private sealed record Seam(string DeclaringType, string Member, string Parameter, Type Type)
    {
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"  {Member}({TypeName(Type)}? {Parameter} = null)");
    }
}
