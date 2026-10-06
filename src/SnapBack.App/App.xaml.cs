using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnapBack.App.Localization;
using SnapBack.App.Services;
using SnapBack.App.ViewModels;
using SnapBack.App.Views;
using SnapBack.Windows;

namespace SnapBack.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private ILogger? _log;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper for Deep Memory Cleanup: no window, no single-instance lock, exit code only.
        if (Core.Memory.DeepCleanupHelper.IsHelperInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(AppServices.RunDeepCleanupHelper(e.Args));
            return;
        }

        var autoBaseline = e.Args.Contains(AutostartRegistration.AutoBaselineArgument, StringComparer.OrdinalIgnoreCase);

        _instance = new SingleInstance();
        if (!_instance.IsFirstInstance)
        {
            if (!autoBaseline)
            {
                _instance.SignalFirstInstance();
            }

            Shutdown();
            return;
        }

        try
        {
            _services = AppServices.Create();
        }
        catch (Exception ex)
        {
            MessageBox.Show(Tr.Format("Error_Startup", ex.Message), Tr.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _log = _services.Loggers.CreateLogger("App");
        LanguageManager.Apply(_services.Engine.Settings.Language);
        ErrorReporter.Log = _log;
        ErrorReporter.LogDirectory = _services.Paths.Logs;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _log.LogInformation("SnapBack {Version} started (elevated: {Elevated}, auto-baseline: {Auto}, language: {Language})",
            typeof(App).Assembly.GetName().Version, _services.Probe.BaseContext.SelfElevated, autoBaseline, Core.Localization.Loc.Culture.Name);

        if (autoBaseline)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                await AutoBaselineRunner.RunAsync(_services, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Automatic baseline failed");
            }

            Shutdown();
            return;
        }

        var dialogs = new DialogService(() => MainWindow);
        var window = new MainWindow(new MainViewModel(_services.Engine, _services.DeepCleanup, dialogs, _log));
        MainWindow = window;
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(window.BringToFront));
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("SnapBack exiting");
        _services?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Every action already passed its own safety checks; keeping the window alive is safe.
        ErrorReporter.Report(e.Exception);
        e.Handled = true;
    }
}
