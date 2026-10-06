using Microsoft.Extensions.Logging;
using SnapBack.Core;
using SnapBack.Core.Logging;
using SnapBack.Core.Memory;
using SnapBack.Core.Persistence;
using SnapBack.Windows;

namespace SnapBack.App.Services;

/// <summary>Composition root.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(AppPaths paths, ILoggerFactory loggers, FileLoggerProvider fileLog, WindowsSystemProbe probe, SnapBackEngine engine,
        DeepCleanupService deepCleanup)
    {
        DeepCleanup = deepCleanup;
        Paths = paths;
        Loggers = loggers;
        FileLog = fileLog;
        Probe = probe;
        Engine = engine;
    }

    public AppPaths Paths { get; }

    public ILoggerFactory Loggers { get; }

    public FileLoggerProvider FileLog { get; }

    public WindowsSystemProbe Probe { get; }

    public SnapBackEngine Engine { get; }

    /// <summary>Optional Deep Memory Cleanup; only ever runs when the user asks for it.</summary>
    public DeepCleanupService DeepCleanup { get; }

    public static AppServices Create()
    {
        var paths = AppPaths.Default();
        var fileLog = new FileLoggerProvider(paths.Logs);
        var loggers = LoggerFactory.Create(b => b.AddProvider(fileLog).SetMinimumLevel(LogLevel.Information));

        var probe = new WindowsSystemProbe(loggers.CreateLogger<WindowsSystemProbe>());
        var controller = new WindowsProcessController(probe.BaseContext, loggers.CreateLogger<WindowsProcessController>());
        var (settings, baseline, history) = StoreFactory.Create(paths, loggers);
        var engine = new SnapBackEngine(probe, controller, settings, baseline, history, loggers);

        var cleaner = new MemoryCleaner(
            probe.BaseContext.SelfElevated,
            new StandbyListPurger(loggers.CreateLogger<StandbyListPurger>()),
            new ShellElevatedRunner(Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.")));
        var deepCleanup = new DeepCleanupService(probe.GetMemoryStatus, cleaner, TimeProvider.System, loggers.CreateLogger<DeepCleanupService>());

        return new AppServices(paths, loggers, fileLog, probe, engine, deepCleanup);
    }

    public void Dispose() => Loggers.Dispose();

    /// <summary>
    /// Entry point of the short-lived elevated helper (<c>SnapBack.exe --deep-cleanup …</c>). It
    /// accepts only the fixed protocol in <see cref="DeepCleanupHelper"/>, never shows a window,
    /// and reports through its exit code only.
    /// </summary>
    public static int RunDeepCleanupHelper(IReadOnlyList<string> args)
    {
        var paths = AppPaths.Default();
        using var fileLog = new FileLoggerProvider(paths.Logs);
        using var loggers = LoggerFactory.Create(b => b.AddProvider(fileLog).SetMinimumLevel(LogLevel.Information));
        var log = loggers.CreateLogger("DeepCleanupHelper");
        try
        {
            var elevated = WindowsSessionInfo.Create().SelfElevated;
            return DeepCleanupHelper.Run(args, elevated, new StandbyListPurger(loggers.CreateLogger<StandbyListPurger>()), log);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Deep cleanup helper failed");
            return DeepCleanupHelper.ExitFailed;
        }
    }
}
