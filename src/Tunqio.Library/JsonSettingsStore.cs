using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tunqio.Core;

namespace Tunqio.Library;

/// <summary>
/// <see cref="ISettingsStore"/> over a JSON file (docs/solution-structure.md: JSON until E3 lands the
/// <c>setting</c> table). Whole-file atomic writes: serialise to <c>settings.json.tmp</c>, then replace.
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

    public void Flush()
    {
        if (!TryTakeSnapshot(out string json))
        {
            return;
        }

        string temp = PrepareTemp();
        File.WriteAllText(temp, json);
        Commit(temp);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (!TryTakeSnapshot(out string json))
        {
            return;
        }

        string temp = PrepareTemp();
        await File.WriteAllTextAsync(temp, json, cancellationToken).ConfigureAwait(false);
        Commit(temp);
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
