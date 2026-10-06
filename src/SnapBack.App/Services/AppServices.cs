using Microsoft.Extensions.Logging;
using SnapBack.Core;
using SnapBack.Core.Logging;
using SnapBack.Core.Persistence;
using SnapBack.Windows;

namespace SnapBack.App.Services;

/// <summary>Composition root.</summary>
public sealed class AppServices : IDisposable
{
    private AppServices(AppPaths paths, ILoggerFactory loggers, FileLoggerProvider fileLog, WindowsSystemProbe probe, SnapBackEngine engine)
    {
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

    public static AppServices Create()
    {
        var paths = AppPaths.Default();
        var fileLog = new FileLoggerProvider(paths.Logs);
        var loggers = LoggerFactory.Create(b => b.AddProvider(fileLog).SetMinimumLevel(LogLevel.Information));

        var probe = new WindowsSystemProbe(loggers.CreateLogger<WindowsSystemProbe>());
        var controller = new WindowsProcessController(probe.BaseContext, loggers.CreateLogger<WindowsProcessController>());
        var (settings, baseline, history) = StoreFactory.Create(paths, loggers);
        var engine = new SnapBackEngine(probe, controller, settings, baseline, history, loggers);

        return new AppServices(paths, loggers, fileLog, probe, engine);
    }

    public void Dispose() => Loggers.Dispose();
}
