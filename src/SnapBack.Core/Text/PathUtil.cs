namespace SnapBack.Core.Text;

/// <summary>Windows path helpers that behave identically on every OS (for testability).</summary>
public static class PathUtil
{
    /// <summary>Normalizes separators, removes trailing separators. Does not touch case.</summary>
    public static string Normalize(string path)
    {
        var p = path.Replace('/', '\\');
        while (p.Length > 3 && p.EndsWith('\\'))
        {
            p = p[..^1];
        }

        return p;
    }

    public static bool PathEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="path"/> is strictly inside <paramref name="directory"/>.</summary>
    public static bool IsUnder(string? path, string? directory)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var dir = Normalize(directory);
        if (!dir.EndsWith('\\'))
        {
            dir += "\\";
        }

        return Normalize(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Directory part of a Windows path (independent of the host OS).</summary>
    public static string? GetDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var p = Normalize(path);
        var i = p.LastIndexOf('\\');
        return i <= 0 ? null : p[..i];
    }

    /// <summary>File name part of a Windows path (independent of the host OS).</summary>
    public static string GetFileName(string path)
    {
        var p = Normalize(path);
        var i = p.LastIndexOf('\\');
        return i < 0 ? p : p[(i + 1)..];
    }
}
