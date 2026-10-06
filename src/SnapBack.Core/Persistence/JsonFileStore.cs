using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace SnapBack.Core.Persistence;

/// <summary>
/// Stores one JSON document on disk. Writes are atomic (temp file + replace), and a corrupt or
/// unreadable file never crashes the application: it is moved aside and defaults are used.
/// </summary>
public sealed class JsonFileStore<T>
    where T : class
{
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public JsonFileStore(string path, ILogger log)
    {
        _path = path;
        _log = log;
    }

    public string FilePath => _path;

    /// <summary>Loads the document, or returns <c>null</c> when it does not exist or is unreadable.</summary>
    public T? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream, SerializerOptions);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                QuarantineCorruptFile(ex);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.LogWarning(ex, "Could not read {Path}", _path);
                return null;
            }
        }
    }

    /// <summary>Saves the document atomically.</summary>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = _path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, value, SerializerOptions);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temp, _path, overwrite: true);
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            TryDelete(_path);
        }
    }

    private void QuarantineCorruptFile(Exception ex)
    {
        var aside = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
        _log.LogError(ex, "{Path} is corrupt; moved to {Aside} and using defaults", _path, aside);
        try
        {
            File.Move(_path, aside, overwrite: true);
        }
        catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(moveEx, "Could not move corrupt file aside");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }
}
