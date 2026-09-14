namespace Tunqio.Core;

/// <summary>
/// Typed key/value settings (keys in <see cref="SettingsKeys"/>). Reads are in-memory; writes are kept in
/// memory until <see cref="Flush"/> or <see cref="FlushAsync"/> persists them (the host flushes after
/// start-up bookkeeping and on shutdown). Values are JSON-serialisable.
/// </summary>
public interface ISettingsStore
{
    /// <summary>Returns the stored value, or <paramref name="defaultValue"/> when absent or unreadable as <typeparamref name="T"/>.</summary>
    T GetValue<T>(string key, T defaultValue);

    /// <summary>Stores a value in memory. Passing <c>null</c> removes the key.</summary>
    void SetValue<T>(string key, T value);

    /// <summary>True when <paramref name="key"/> has a stored value.</summary>
    bool Contains(string key);

    /// <summary>
    /// Every stored key that begins with <paramref name="prefix"/> (ordinal). For a family of keys whose members are not
    /// known in advance: T-157's <c>viz.params.&lt;preset&gt;.&lt;name&gt;</c>, where a stored name may be one the preset's
    /// manifest has since dropped, and Reset has to remove it anyway.
    /// </summary>
    IReadOnlyList<string> KeysStartingWith(string prefix);

    /// <summary>Writes pending changes to storage synchronously (shutdown path). Safe to call when nothing changed.</summary>
    void Flush();

    /// <summary>Writes pending changes to storage. Safe to call when nothing changed.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised after <see cref="SetValue{T}"/> with the key that changed.</summary>
    event EventHandler<string>? Changed;
}
