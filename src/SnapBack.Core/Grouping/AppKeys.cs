using System.Text.RegularExpressions;
using SnapBack.Core.Model;
using SnapBack.Core.Text;

namespace SnapBack.Core.Grouping;

/// <summary>
/// Computes a stable identity for "the same application" that survives restarts, PID changes and
/// typical self-updates (versioned install folders).
/// </summary>
public static partial class AppKeys
{
    [GeneratedRegex(@"^app-\d+(\.\d+)+([-+][\w.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SquirrelSegment();

    [GeneratedRegex(@"^v?\d+(\.\d+){1,3}([-+][\w.]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSegment();

    [GeneratedRegex(@"^(?<name>[^_\\]+)_\d+(\.\d+){1,3}_(?<rest>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageSegment();

    public static string For(ProcessInfo p)
    {
        if (!string.IsNullOrEmpty(p.PackageFullName))
        {
            return "pkg:" + PackageFamily(p.PackageFullName).ToLowerInvariant() + "!" + p.ImageName.ToLowerInvariant();
        }

        if (!string.IsNullOrEmpty(p.ImagePath))
        {
            return "path:" + NormalizePath(p.ImagePath);
        }

        return "name:" + p.ImageName.ToLowerInvariant();
    }

    /// <summary>
    /// Lower-cases the path and replaces version-specific folder names
    /// (<c>app-1.0.9</c>, <c>130.0.6723.70</c>, <c>Pkg_1.2.3.0_x64__id</c>) with wildcards.
    /// </summary>
    public static string NormalizePath(string path)
    {
        var parts = PathUtil.Normalize(path).ToLowerInvariant().Split('\\');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            parts[i] = NormalizeSegment(parts[i]);
        }

        return string.Join('\\', parts);
    }

    private static string NormalizeSegment(string segment)
    {
        if (SquirrelSegment().IsMatch(segment))
        {
            return "app-*";
        }

        if (VersionSegment().IsMatch(segment))
        {
            return "*";
        }

        var m = PackageSegment().Match(segment);
        return m.Success ? m.Groups["name"].Value + "_*_" + m.Groups["rest"].Value : segment;
    }

    /// <summary>Whether a folder name is version-specific (used to find an app's install root).</summary>
    internal static bool IsVersionSegment(string segment) =>
        SquirrelSegment().IsMatch(segment) || VersionSegment().IsMatch(segment);

    /// <summary>Converts a package full name (Name_Version_Arch_Resource_Publisher) to its family name.</summary>
    public static string PackageFamily(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length >= 5 ? parts[0] + "_" + parts[^1] : packageFullName;
    }
}
