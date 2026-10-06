namespace SnapBack.Core.Model;

/// <summary>
/// Facts about the interactive session SnapBack runs in. Every safety decision is made relative to it:
/// SnapBack only ever acts on processes of this user in this session.
/// </summary>
public sealed record SessionContext
{
    public required int SelfPid { get; init; }

    public string? SelfImagePath { get; init; }

    /// <summary>Terminal Services session ID of SnapBack itself.</summary>
    public required int SessionId { get; init; }

    /// <summary>String SID of the user running SnapBack.</summary>
    public required string UserSid { get; init; }

    public bool SelfElevated { get; init; }

    /// <summary>The Windows directory, e.g. <c>C:\Windows</c>.</summary>
    public required string WindowsDirectory { get; init; }

    /// <summary>
    /// Directories that contain many unrelated applications (Program Files, AppData, ...). They are
    /// never treated as the install root of a single application when grouping processes.
    /// </summary>
    public IReadOnlyList<string> GenericDirectories { get; init; } = [];

    /// <summary>Approximate boot time (UTC).</summary>
    public DateTime BootTimeUtc { get; init; }

    /// <summary>PIDs of the processes SnapBack was launched from (parent chain).</summary>
    public IReadOnlySet<int> SelfAncestorPids { get; init; } = new HashSet<int>();

    /// <summary>False when the service list could not be read from the Service Control Manager.</summary>
    public bool ServicesKnown { get; init; } = true;
}
