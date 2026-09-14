using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core;

namespace Tunqio.Library;

/// <summary>
/// <see cref="ISettingsStore"/> over a JSON file: the settings store for good (decision Q-15: settings survive a
/// database reset and are readable without SQLite). Whole-file atomic writes: serialise to <c>settings.json.tmp</c>, then replace.
/// A corrupt file is renamed aside and treated as empty, so a bad write never blocks start-up.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore, IAsyncDisposable, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly JsonObject _values;
    private bool _dirty;

    public JsonSettingsStore(IAppPaths paths, ILogger<JsonSettingsStore>? logger = null)
        : this(paths.SettingsPath, logger)
    {
    }

    public JsonSettingsStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        _values = Load();
    }

    public event EventHandler<string>? Changed;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _values.Count;
            }
        }
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (!_values.TryGetPropertyValue(key, out JsonNode? node) || node is null)
            {
                return defaultValue;
            }

            try
            {
                return node.Deserialize<T>(SerializerOptions) ?? defaultValue;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Setting {Key} is not a {Type}; using the default", key, typeof(T).Name);
                return defaultValue;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex, "Setting {Key} is not a {Type}; using the default", key, typeof(T).Name);
                return defaultValue;
            }
        }
    }

    public void SetValue<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (value is null)
            {
                _values.Remove(key);
            }
            else
            {
                _values[key] = JsonSerializer.SerializeToNode(value, SerializerOptions);
            }

            _dirty = true;
        }

        Changed?.Invoke(this, key);
    }

    public bool Contains(string key)
    {
        lock (_gate)
        {
            return _values.ContainsKey(key);
        }
    }

    public IReadOnlyList<string> KeysStartingWith(string prefix)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        lock (_gate)
        {
            var keys = new List<string>();
            foreach (KeyValuePair<string, JsonNode?> pair in _values)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    keys.Add(pair.Key);
                }
            }

            return keys;
        }
    }

    // Flushes are serialised (T-157). Callers fire FlushAsync and forget it after every change, so two can overlap, and an
    // unserialised pair is last-writer-wins: each snapshots under _gate, then writes settings.json.tmp and commits after
    // an await. Reset on Settings > Visualization writes a default (flush 1, snapshot holds the key) then removes the key
    // (flush 2, snapshot without it); when flush 1 committed last, the removal was undone on disk. The two also shared
    // the one temp path, so an overlap could fail outright after its snapshot had been marked clean.
    //
    // _flushGate is held across snapshot, write and commit, and the snapshot is taken only once the gate is held, so a
    // flush queued behind another writes the newest state rather than the state when it was called, and one that finds
    // nothing dirty by then returns without writing. The file therefore always ends holding the newest in-memory state.
    // Flush (used by Dispose) takes the same gate, synchronously. A write that fails marks the store dirty again so the
    // next flush retries it. The gate is never disposed: it has no wait handle to release, and a flush forgotten by a
    // caller may still arrive after Dispose.
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    public void Flush()
    {
        _flushGate.Wait();
        try
        {
            if (!TryTakeSnapshot(out string json))
            {
                return;
            }

            try
            {
                string temp = PrepareTemp();
                File.WriteAllText(temp, json);
                Commit(temp);
            }
            catch
            {
                MarkDirty();
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Test seam: awaited by <see cref="FlushAsync"/> after it takes its snapshot and before it writes it.</summary>
    internal Func<Task>? SnapshotTakenForTests { get; set; }

    /// <summary>Test seam: invoked when a <see cref="FlushAsync"/> has to wait because another flush holds the file.</summary>
    internal Action? FlushQueuedForTests { get; set; }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_flushGate.CurrentCount == 0)
        {
            FlushQueuedForTests?.Invoke();
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (!TryTakeSnapshot(out string json))
            {
                return;
            }

            try
            {
                if (SnapshotTakenForTests is { } snapshotTaken)
                {
                    await snapshotTaken().ConfigureAwait(false);
                }

                string temp = PrepareTemp();
                await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
                Commit(temp);
            }
            catch
            {
                MarkDirty();
                throw;
            }
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose() => Flush();

    public ValueTask DisposeAsync() => new(FlushAsync());

    private bool TryTakeSnapshot(out string json)
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                json = string.Empty;
                return false;
            }

            json = _values.ToJsonString(SerializerOptions);
            _dirty = false;
            return true;
        }
    }

    private void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
        }
    }

    private string PrepareTemp()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        return _path + ".tmp";
    }

    private void Commit(string temp)
    {
        if (File.Exists(_path))
        {
            File.Replace(temp, _path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temp, _path);
        }

        _logger.LogDebug("Settings flushed to {Path}", _path);
    }

    private JsonObject Load()
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        try
        {
            using FileStream stream = File.OpenRead(_path);
            return JsonNode.Parse(stream) as JsonObject ?? [];
        }
        catch (JsonException ex)
        {
            string aside = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            _logger.LogError(ex, "Settings file is not valid JSON; moving it to {Aside} and starting empty", aside);
            File.Move(_path, aside, overwrite: true);
            return [];
        }
    }
}
