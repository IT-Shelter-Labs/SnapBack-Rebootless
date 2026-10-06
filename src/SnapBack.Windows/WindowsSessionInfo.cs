using System.Security.Principal;
using SnapBack.Core.Model;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>Builds the <see cref="SessionContext"/> for the current process.</summary>
public static class WindowsSessionInfo
{
    public static SessionContext Create()
    {
        var selfPid = Environment.ProcessId;
        var session = ProcessQueries.SessionOf(selfPid)
            ?? throw new InvalidOperationException("Could not determine the current Windows session.");

        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User?.Value ?? throw new InvalidOperationException("Could not determine the current user.");

        bool elevated;
        using (var self = ProcessQueries.Open(selfPid, Kernel32.PROCESS_QUERY_LIMITED_INFORMATION))
        {
            elevated = self is not null && ProcessQueries.Token(self).IsElevated == true;
        }

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windows))
        {
            windows = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        }

        return new SessionContext
        {
            SelfPid = selfPid,
            SelfImagePath = Environment.ProcessPath,
            SessionId = session,
            UserSid = userSid,
            SelfElevated = elevated,
            WindowsDirectory = windows,
            GenericDirectories = GenericDirectories(),
            BootTimeUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    private static IReadOnlyList<string> GenericDirectories()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);

        var programFiles = F(Environment.SpecialFolder.ProgramFiles);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var profile = F(Environment.SpecialFolder.UserProfile);
        var dirs = new[]
        {
            programFiles,
            F(Environment.SpecialFolder.ProgramFilesX86),
            string.IsNullOrEmpty(programFiles) ? "" : Path.Combine(programFiles, "WindowsApps"),
            F(Environment.SpecialFolder.CommonProgramFiles),
            F(Environment.SpecialFolder.CommonProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            local,
            string.IsNullOrEmpty(local) ? "" : Path.Combine(local, "Programs"),
            string.IsNullOrEmpty(local) ? "" : Path.Combine(local, "Temp"),
            F(Environment.SpecialFolder.ApplicationData),
            profile,
            string.IsNullOrEmpty(profile) ? "" : Path.Combine(profile, "Downloads"),
            F(Environment.SpecialFolder.Desktop),
            F(Environment.SpecialFolder.MyDocuments),
            Path.GetTempPath(),
        };

        return dirs.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
