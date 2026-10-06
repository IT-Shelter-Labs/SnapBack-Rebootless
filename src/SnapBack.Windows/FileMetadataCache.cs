using System.Collections.Concurrent;
using System.Diagnostics;
using SnapBack.Core.Model;

namespace SnapBack.Windows;

internal sealed record FileMetadata(string? Description, string? Product, string? Company, ImageSubsystem Subsystem);

/// <summary>Version-resource and PE-header information per executable path, cached for the app's lifetime.</summary>
internal sealed class FileMetadataCache
{
    private const int MaxEntries = 4096;
    private readonly ConcurrentDictionary<string, FileMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileMetadata Get(string path)
    {
        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        var meta = Read(path);
        if (_cache.Count >= MaxEntries)
        {
            _cache.Clear();
        }

        _cache[path] = meta;
        return meta;
    }

    private static FileMetadata Read(string path)
    {
        string? description = null, product = null, company = null;
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            description = vi.FileDescription;
            product = vi.ProductName;
            company = vi.CompanyName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The file may have been replaced by an update or be inaccessible.
        }

        var subsystem = ImageSubsystem.Unknown;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
            subsystem = PeSubsystemReader.Read(fs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Unknown subsystem is handled conservatively by the caller.
        }

        return new FileMetadata(description, product, company, subsystem);
    }
}
