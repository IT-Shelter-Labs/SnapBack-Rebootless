using SnapBack.Core.Baselines;

namespace SnapBack.Core.Profiles;

/// <summary>What switching to a profile suggests for a managed environment (WSL, Docker).</summary>
public enum EnvironmentPolicy
{
    /// <summary>Leave it running (the default).</summary>
    Keep,

    /// <summary>Offer to stop it with its own command; the user still confirms every time.</summary>
    Stop,
}

/// <summary>
/// A named state of the session ("Clean", "Work", "Gaming", ...). The captured <see cref="Baseline"/>
/// lists the applications that belong to the profile; switching to the profile offers to close the
/// others. Profiles are user data: they are created, renamed, edited and deleted by the user only.
/// </summary>
public sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public DateTime CreatedAtUtc { get; init; }

    public DateTime UpdatedAtUtc { get; init; }

    /// <summary>The applications of this profile, captured from a real session and optionally edited.</summary>
    public required Baseline Baseline { get; init; }

    public EnvironmentPolicy Wsl { get; init; } = EnvironmentPolicy.Keep;

    public EnvironmentPolicy Docker { get; init; } = EnvironmentPolicy.Keep;

    /// <summary>User applications of the profile (protected system components excluded).</summary>
    public int AppCount => Baseline.Apps.Count(a => !a.WasProtected);
}

/// <summary>All profiles of the user plus which one is the default and which one is selected.</summary>
public sealed record ProfileSet
{
    public const int CurrentSchemaVersion = 1;

    public const int MaxProfiles = 20;

    public const int MaxNameLength = 40;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public List<Profile> Profiles { get; init; } = [];

    /// <summary>The profile used at startup and by the automatic baseline.</summary>
    public Guid? DefaultProfileId { get; init; }

    /// <summary>The profile the session is compared with (selected in the main window).</summary>
    public Guid? CurrentProfileId { get; init; }

    public static ProfileSet Empty { get; } = new();

    public Profile? Find(Guid? id) => id is { } v ? Profiles.FirstOrDefault(p => p.Id == v) : null;

    public Profile? FindByName(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.CurrentCultureIgnoreCase));

    public Profile? Default => Find(DefaultProfileId) ?? Profiles.FirstOrDefault();

    public Profile? Current => Find(CurrentProfileId) ?? Default;

    /// <summary>
    /// Repairs a loaded set: drops invalid entries and duplicate IDs, trims names, makes names
    /// unique and points the default/current IDs at existing profiles.
    /// </summary>
    public ProfileSet Normalize()
    {
        var seen = new HashSet<Guid>();
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        var list = new List<Profile>();
        foreach (var p in Profiles ?? [])
        {
            if (p?.Baseline is null || p.Id == Guid.Empty || !seen.Add(p.Id) || list.Count >= MaxProfiles)
            {
                continue;
            }

            var name = UniqueName(CleanName(p.Name), names);
            names.Add(name);
            list.Add(p with
            {
                Name = name,
                Wsl = Enum.IsDefined(p.Wsl) ? p.Wsl : EnvironmentPolicy.Keep,
                Docker = Enum.IsDefined(p.Docker) ? p.Docker : EnvironmentPolicy.Keep,
                Baseline = p.Baseline with { Apps = p.Baseline.Apps ?? [] },
            });
        }

        var defaultId = list.Any(p => p.Id == DefaultProfileId) ? DefaultProfileId : list.FirstOrDefault()?.Id;
        var currentId = list.Any(p => p.Id == CurrentProfileId) ? CurrentProfileId : defaultId;
        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            Profiles = list,
            DefaultProfileId = defaultId,
            CurrentProfileId = currentId,
        };
    }

    /// <summary>Trims a profile name, removes control characters and limits its length.</summary>
    public static string CleanName(string? name)
    {
        var chars = (name ?? "").Where(c => !char.IsControl(c)).ToArray();
        var clean = new string(chars).Trim();
        if (clean.Length > MaxNameLength)
        {
            clean = clean[..MaxNameLength].TrimEnd();
        }

        return clean.Length == 0 ? "Profile" : clean;
    }

    /// <summary>Appends " (2)", " (3)", ... until the name is not taken.</summary>
    public static string UniqueName(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        for (var i = 2; ; i++)
        {
            var candidate = $"{name} ({i})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
