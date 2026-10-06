namespace SnapBack.Core.Persistence;

/// <summary>Where SnapBack keeps its files. Everything is per-user; nothing is written system-wide.</summary>
public sealed class AppPaths
{
    public const string DataDirectoryVariable = "SNAPBACK_DATA_DIR";

    public AppPaths(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public string Settings => Path.Combine(Root, "settings.json");

    /// <summary>The single baseline of version 0.2; read once to migrate it into a profile.</summary>
    public string Baseline => Path.Combine(Root, "baseline.json");

    public string Profiles => Path.Combine(Root, "profiles.json");

    public string History => Path.Combine(Root, "history.json");

    public string Logs => Path.Combine(Root, "logs");

    /// <summary>%LOCALAPPDATA%\SnapBack, or the directory named by SNAPBACK_DATA_DIR.</summary>
    public static AppPaths Default()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            return new AppPaths(Path.GetFullPath(overridden));
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local))
        {
            local = Path.GetTempPath();
        }

        return new AppPaths(Path.Combine(local, "SnapBack"));
    }
}
