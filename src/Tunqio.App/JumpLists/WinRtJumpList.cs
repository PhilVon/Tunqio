using Tunqio.Core;
using Windows.UI.StartScreen;

namespace Tunqio.App.JumpLists;

/// <summary>
/// <see cref="IJumpList"/> over <c>Windows.UI.StartScreen.JumpList</c> (ADR-006, E7-S5). The API needs package identity: Microsoft
/// Learn ("Add items to the Windows jump list") says it is not available to unpackaged apps. <c>JumpList.IsSupported()</c> is not
/// the test, because it returns true in a process with no identity at all (T-78 probe), so <see cref="WhyUnavailable"/> asks for
/// the package itself. The unpackaged development build skips the jump list (Q-119); the installed package uses it (E8-S1).
/// </summary>
internal sealed class WinRtJumpList : IJumpList
{
    /// <summary>The picture beside each item: T-191's logo, which the package carries under Assets.</summary>
    public const string LogoUri = "ms-appx:///Assets/TunqioLogo.png";

    /// <summary>Why this process cannot have a jump list, or null when it can: it must run as the Tunqio package.</summary>
    public static string? WhyUnavailable()
    {
        string name;
        try
        {
            name = Windows.ApplicationModel.Package.Current.Id.Name;
        }
        catch (InvalidOperationException)
        {
            return "this process has no package identity (the unpackaged build)";
        }

        if (!string.Equals(name, Identity.PackageName, StringComparison.Ordinal))
        {
            return $"this process runs with the package identity {name}, not {Identity.PackageName}";
        }

        return JumpList.IsSupported() ? null : "Windows reports jump lists as not supported here";
    }

    public async Task WriteAsync(IReadOnlyList<JumpListEntry> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        JumpList list = await JumpList.LoadCurrentAsync().AsTask(ct).ConfigureAwait(false);
        // Tunqio's own groups only: Windows' Recent group lists files the shell saw opened, which is not what these are.
        list.SystemGroupKind = JumpListSystemGroupKind.None;
        list.Items.Clear();
        foreach (JumpListEntry entry in entries)
        {
            JumpListItem item = JumpListItem.CreateWithArguments(entry.Arguments, entry.DisplayName);
            item.GroupName = entry.Group;
            item.Description = entry.Description;
            item.Logo = new Uri(LogoUri);
            list.Items.Add(item);
        }

        await list.SaveAsync().AsTask(ct).ConfigureAwait(false);
    }
}
