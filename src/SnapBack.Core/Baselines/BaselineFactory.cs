using SnapBack.Core.Grouping;
using SnapBack.Core.Model;

namespace SnapBack.Core.Baselines;

public static class BaselineFactory
{
    /// <summary>Creates a baseline describing every application of the current user session.</summary>
    public static Baseline Create(SystemSnapshot snapshot, IReadOnlyList<AppGroup> groups, BaselineOrigin origin, string machineName)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(groups);

        var apps = groups
            .GroupBy(g => g.AppKey, StringComparer.Ordinal)
            .Select(kg =>
            {
                var instances = kg.OrderBy(g => g.Root.Identity.CreationTime).ToList();
                var first = instances[0];
                return new BaselineApp
                {
                    AppKey = kg.Key,
                    DisplayName = first.DisplayName,
                    ExecutablePath = first.ExecutablePath,
                    Instances = instances.Select(g => g.Id).ToList(),
                    ProcessCount = instances.Sum(g => g.ProcessCount),
                    PrivateWorkingSet = instances.Sum(g => g.PrivateWorkingSet),
                    WasProtected = instances.All(g => g.IsProtected),
                };
            })
            .OrderBy(a => a.WasProtected)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new Baseline
        {
            CreatedAtUtc = snapshot.TakenAtUtc,
            Origin = origin,
            UserSid = snapshot.Context.UserSid,
            SessionId = snapshot.Context.SessionId,
            BootTimeUtc = snapshot.Context.BootTimeUtc,
            MachineName = machineName,
            Apps = apps,
            ProcessCount = groups.Sum(g => g.ProcessCount),
            PrivateWorkingSet = groups.Sum(g => g.PrivateWorkingSet),
            AvailableMemoryBytes = snapshot.Memory.AvailablePhysicalBytes,
        };
    }

    /// <summary>Returns a description of why a stored baseline cannot be used here, or <c>null</c> if it can.</summary>
    public static string? Validate(Baseline baseline, SessionContext ctx)
    {
        if (baseline.SchemaVersion > Baseline.CurrentSchemaVersion)
        {
            return "The baseline was created by a newer version of SnapBack.";
        }

        if (!string.Equals(baseline.UserSid, ctx.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return "The baseline belongs to a different user account.";
        }

        return null;
    }
}
