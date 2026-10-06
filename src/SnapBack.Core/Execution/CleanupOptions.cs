namespace SnapBack.Core.Execution;

public sealed record CleanupOptions
{
    /// <summary>How long to wait for applications to exit after asking them politely.</summary>
    public TimeSpan GracefulTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How often process state is re-checked while waiting.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long an app may stay alive without visible windows after a close request before it is
    /// asked again through Restart Manager (apps that hide to the tray).
    /// </summary>
    public TimeSpan EscalationDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long leftover background processes are waited for once the main process exited.</summary>
    public TimeSpan LeftoverGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for Restart Manager calls to wind down after cancellation.</summary>
    public TimeSpan RestartManagerDrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
