namespace Tunqio.App.JumpLists;

/// <summary>
/// One item of Tunqio's jump list (E7-S5): the group it is listed under, what it says, and the launch arguments Windows starts
/// Tunqio with when it is chosen. The arguments are a <c>tunqio://</c> command <see cref="Activation.CommandRouter"/> reads.
/// </summary>
public sealed record JumpListEntry(string Group, string DisplayName, string Description, string Arguments);

/// <summary>
/// The taskbar jump list, as far as <see cref="JumpListController"/> needs it: replace what Tunqio put there with these entries.
/// <see cref="WinRtJumpList"/> is the app's, over <c>Windows.UI.StartScreen.JumpList</c> (ADR-006); tests use a fake.
/// </summary>
public interface IJumpList
{
    /// <summary>Replaces every item Tunqio added with <paramref name="entries"/>, in order. Throws when Windows will not take them.</summary>
    Task WriteAsync(IReadOnlyList<JumpListEntry> entries, CancellationToken ct);
}
