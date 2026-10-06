using SnapBack.Core.Environments;

namespace SnapBack.Windows.Environments;

/// <summary>Finds the command-line tools of WSL and Docker Desktop in their standard locations.</summary>
public static class EnvironmentToolLocator
{
    public static EnvironmentTools Find() => new(FindWsl(), FindDocker());

    private static string? FindWsl()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var path = Path.Combine(string.IsNullOrEmpty(system) ? @"C:\Windows\System32" : system, "wsl.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? FindDocker()
    {
        // Docker Desktop's own copy first, then the PATH (what the user gets when typing "docker").
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var installed = Path.Combine(programFiles, "Docker", "Docker", "resources", "bin", "docker.exe");
        if (File.Exists(installed))
        {
            return installed;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "docker.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }

        return null;
    }
}
