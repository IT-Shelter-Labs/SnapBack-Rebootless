using SnapBack.Core.Model;

namespace SnapBack.Core.Tests.Fakes;

/// <summary>Builders for realistic process data.</summary>
internal static class TestData
{
    public const string User = "S-1-5-21-1000-2000-3000-1001";
    public const string OtherUser = "S-1-5-21-1000-2000-3000-1002";
    public const string SystemSid = "S-1-5-18";
    public const int Session = 1;
    public const int SelfPid = 9000;

    public static readonly DateTime Boot = new(2026, 10, 6, 8, 0, 0, DateTimeKind.Utc);

    public static SessionContext Context(IReadOnlySet<int>? ancestors = null) => new()
    {
        SelfPid = SelfPid,
        SelfImagePath = @"C:\Users\me\AppData\Local\Programs\SnapBack\SnapBack.exe",
        SessionId = Session,
        UserSid = User,
        WindowsDirectory = @"C:\Windows",
        GenericDirectories =
        [
            @"C:\Program Files",
            @"C:\Program Files (x86)",
            @"C:\Program Files\WindowsApps",
            @"C:\Users\me",
            @"C:\Users\me\AppData\Local",
            @"C:\Users\me\AppData\Local\Programs",
            @"C:\Users\me\AppData\Roaming",
        ],
        BootTimeUtc = Boot,
        SelfAncestorPids = ancestors ?? new HashSet<int>(),
    };

    /// <summary>Creation time <paramref name="minutes"/> after boot.</summary>
    public static long At(double minutes) => Boot.AddMinutes(minutes).ToFileTimeUtc();

    /// <summary>A normal user GUI process with every safety attribute verified.</summary>
    public static ProcessInfo App(int pid, string path, double startedMinutes, int parent = 0, int visibleWindows = 0,
        int hiddenWindows = 0, string? company = null, string? description = null, long memoryMb = 100,
        ImageSubsystem subsystem = ImageSubsystem.Gui, params string[] titles) => new()
        {
            Identity = new ProcessIdentity(pid, At(startedMinutes)),
            ParentPid = parent,
            ImageName = Path.GetFileName(path.Replace('\\', '/')),
            ImagePath = path,
            SessionId = Session,
            OwnerSid = User,
            IsElevated = false,
            Integrity = IntegrityLevel.Medium,
            IsCritical = false,
            IsProtectedProcess = false,
            IsServiceHost = false,
            CouldOpen = true,
            Subsystem = subsystem,
            CompanyName = company,
            FileDescription = description,
            PrivateWorkingSet = memoryMb * 1024 * 1024,
            Windows = new WindowSummary
            {
                VisibleMainWindows = visibleWindows,
                HiddenTopLevelWindows = hiddenWindows,
                Titles = titles,
            },
        };

    /// <summary>A typical set of session processes right after sign-in.</summary>
    public static List<ProcessInfo> FreshSession() =>
    [
        new ProcessInfo { Identity = new(4, 0), ImageName = "System", SessionId = 0 },
        App(600, @"C:\Windows\System32\csrss.exe", 0.1) with { OwnerSid = SystemSid, IsCritical = true },
        App(700, @"C:\Windows\System32\winlogon.exe", 0.1) with { OwnerSid = SystemSid },
        App(1000, @"C:\Windows\System32\userinit.exe", 1, parent: 700),
        App(1100, @"C:\Windows\explorer.exe", 1.1, parent: 1000, visibleWindows: 1),
        App(1200, @"C:\Windows\System32\sihost.exe", 1.0),
        App(SelfPid, @"C:\Users\me\AppData\Local\Programs\SnapBack\SnapBack.exe", 2, parent: 1100, visibleWindows: 1),
    ];

    public static SystemSnapshot Snapshot(IEnumerable<ProcessInfo> processes, SessionContext? ctx = null, DateTime? at = null) =>
        new(ctx ?? Context(), processes, new MemoryStatus(16L << 30, 8L << 30), at ?? Boot.AddHours(3));
}
