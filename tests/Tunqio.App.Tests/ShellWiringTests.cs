using Microsoft.Extensions.DependencyInjection;
using Tunqio.App.Library;
using Tunqio.App.Playback;
using Tunqio.App.Shell;
using Tunqio.Core.Library;
using Tunqio.Library;

namespace Tunqio.App.Tests;

/// <summary>
/// T-120: the container really does hand out the <see cref="ShellNotices"/> the tag editor asks it for, hands out
/// the same one every time, and hands out one that is wired to something.
/// </summary>
/// <remarks>
/// <para>
/// This is a test of the wiring and not another test of the object. <see cref="ShellNoticesTests"/> constructs a
/// <see cref="ShellNotices"/> itself and proves what it does — which is exactly why it could not catch this
/// defect. <c>TagEditorDialog.ShowAsync</c> does not construct one; it resolves one, because a row menu in a list
/// control has no container of its own to be injected from. Nothing registered it, so every successful batch threw
/// after the files had already been written and the session's Undo bar never reached the screen. A test that
/// injects the dependency can never see that; only one that asks the container can.
/// </para>
/// <para>
/// Registration is pure — <see cref="AppServices.AddTunqio"/> adds descriptors and opens nothing, no database, no
/// settings file, no audio device — so the whole composition root can be built here and read back. The descriptor
/// assertions are therefore about the root the app actually launches with, and not about a subset copied into the
/// test, which would only be a second place for the same omission to hide.
/// </para>
/// </remarks>
public sealed class ShellWiringTests
{
    private static ServiceDescriptor Notices() =>
        new ServiceCollection().AddTunqio(new AppPaths(), uiContext: null)
            .Single(d => d.ServiceType == typeof(ShellNotices));

    [Fact]
    public void The_composition_root_registers_the_shell_notices_the_tag_editor_resolves()
    {
        ServiceDescriptor[] registered =
        [
            .. new ServiceCollection()
                .AddTunqio(new AppPaths(), uiContext: null)
                .Where(d => d.ServiceType == typeof(ShellNotices)),
        ];

        registered.Should().ContainSingle(
            "TagEditorDialog.ShowAsync resolves ShellNotices from App.Services to leave the Undo bar behind, and an "
            + "unregistered service throws there — after the tag write has already touched the files");
    }

    [Fact]
    public void The_shell_notices_are_one_object_the_container_owns()
    {
        ServiceDescriptor notices = Notices();

        notices.Lifetime.Should().Be(
            ServiceLifetime.Singleton,
            "the NoticePanel is bound to the instance MainWindow was given, so a transient registration would build "
            + "the dialog a second set of notices that nobody can see — the same defect wearing a registration");
        notices.ImplementationInstance.Should().BeNull(
            "ShellNotices is IDisposable and the container is what builds it, so the container is what disposes it; "
            + "MainWindow now disposes only the one it builds for itself");
    }

    [Fact]
    public async Task The_container_hands_everyone_the_same_notices_and_they_are_wired_to_the_scans_Async()
    {
        var scanner = new FakeScanner();
        var services = new ServiceCollection();
        services.AddSingleton<IPlaybackSessionSource>(new StubSessionSource());
        services.AddSingleton(new LibraryScanCoordinator(scanner));
        services.AddShell(uiContext: null);
        using ServiceProvider provider = services.BuildServiceProvider();

        ShellNotices first = provider.GetRequiredService<ShellNotices>();
        ShellNotices second = provider.GetRequiredService<ShellNotices>();
        first.Should().BeSameAs(second, "the window's panel and the tag editor's Undo bar are the same bar");

        // Resolved, not injected: a factory that forgot its arguments would still satisfy every assertion above.
        Task<ScanReport?> scan = provider.GetRequiredService<LibraryScanCoordinator>().ScanAsync(ScanRequest.All);
        await WaitUntilAsync(() => scanner.Requests.Count == 1);
        scanner.Finish(FakeScanner.Report(added: 4));
        await scan;

        first.Items.Should().Contain(
            n => n.Kind == NoticeKind.Scan,
            "the notices the container built are subscribed to the scan coordinator the container built");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the scan should have reached the fake scanner");
    }
}
