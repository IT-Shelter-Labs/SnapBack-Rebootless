using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnapBack.App.Localization;
using SnapBack.App.Services;
using SnapBack.App.ViewModels;
using SnapBack.App.Views;
using SnapBack.Core.Persistence;
using SnapBack.Windows;

namespace SnapBack.App;

public partial class App : Application
{
    private readonly CancellationTokenSource _lifetime = new();
    private SingleInstance? _instance;
    private AppServices? _services;
    private ILogger? _log;
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayController? _tray;
    private BackgroundMonitor? _monitor;
    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Elevated helper for Deep Memory Cleanup: no window, no single-instance lock, exit code only.
        if (Core.Memory.DeepCleanupHelper.IsHelperInvocation(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(AppServices.RunDeepCleanupHelper(e.Args));
            return;
        }

        var options = StartupOptions.Parse(e.Args);
        _instance = new SingleInstance();
        if (!_instance.IsFirstInstance)
        {
            if (options.Quit)
            {
                _instance.SignalExit();
            }
            else if (!options.Autostart)
            {
                _instance.SignalFirstInstance();
            }

            Shutdown();
            return;
        }

        if (options.Quit)
        {
            Shutdown(); // Nothing is running.
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

        // SnapBack lives in the notification area; windows come and go.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _log = _services.Loggers.CreateLogger("App");
        var engine = _services.Engine;
        LanguageManager.Apply(engine.Settings.Language);
        ErrorReporter.Log = _log;
        ErrorReporter.LogDirectory = _services.Paths.Logs;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _log.LogCritical(args.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log.LogError(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        _log.LogInformation("SnapBack {Version} started (elevated: {Elevated}, autostart: {Autostart}, tray: {Tray}, language: {Language})",
            typeof(App).Assembly.GetName().Version, _services.Probe.BaseContext.SelfElevated, options.Autostart, options.Tray,
            Core.Localization.Loc.Culture.Name);

        SyncAutostart(engine.Settings);
        engine.SettingsChanged += (_, _) => Dispatcher.BeginInvoke(() => SyncAutostart(engine.Settings));

        var dialogs = new DialogService(() => _window);
        _vm = new MainViewModel(engine, _services.DeepCleanup, _services.Environments, dialogs, _log);

        var exe = Environment.ProcessPath ?? "";
        _tray = new TrayController(engine, exe, _log);
        _tray.OpenRequested += (_, _) => ShowMainWindow();
        _tray.ReturnRequested += async (_, _) =>
        {
            ShowMainWindow();
            await Guard(() => _vm.ReturnFromTrayAsync());
        };
        _tray.UpdateBaselineRequested += async (_, _) =>
        {
            ShowMainWindow();
            await Guard(() => _vm.CaptureFromTrayAsync());
        };
        _tray.RefreshRequested += async (_, _) => await Guard(() => _monitor!.AnalyzeAsync());
        _tray.ProfileSelected += async (_, id) =>
        {
            engine.SelectProfile(id);
            await Guard(() => IsWindowVisible ? _vm.RefreshAsync() : _monitor!.AnalyzeAsync());
        };
        _tray.ExitRequested += (_, _) => ExitApp();
        _tray.Show();

        _monitor = new BackgroundMonitor(engine, _tray, () => IsWindowVisible, _log);
        _monitor.Start();

        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(ShowMainWindow));
        _instance.ListenForExit(() => Dispatcher.BeginInvoke(ExitApp));
        SessionEnding += (_, _) => ExitApp();

        var startHidden = options.Tray || (options.Autostart && engine.Settings.StartMinimizedToTray);
        if (startHidden && _tray.IsShown)
        {
            _log.LogInformation("Started in the notification area");
            ResourceModes.SetEfficiencyMode(true);
            _ = _monitor.AnalyzeAsync();
        }
        else
        {
            ShowMainWindow();
        }

        if (options.Autostart && engine.Settings.AutoBaselineOnSignIn)
        {
            _ = RunAutoBaselineAsync();
        }
    }

    private bool IsWindowVisible => _window is { IsVisible: true };

    private void ShowMainWindow()
    {
        if (_exiting || _vm is null)
        {
            return;
        }

        if (_window is null)
        {
            _window = new MainWindow(_vm);
            _window.Closing += OnMainWindowClosing;
            MainWindow = _window;
        }

        ResourceModes.SetEfficiencyMode(false);
        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.BringToFront();
    }

    /// <summary>The close button hides the window to the notification area unless the user chose "Exit".</summary>
    private void OnMainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exiting || _services is null || _tray is null)
        {
            return;
        }

        var settings = _services.Engine.Settings;
        if (settings.CloseButton == CloseButtonAction.Exit || !_tray.IsShown)
        {
            e.Cancel = true;
            Dispatcher.BeginInvoke(ExitApp);
            return;
        }

        e.Cancel = true;
        _window!.Hide();
        ResourceModes.SetEfficiencyMode(true);
        _log?.LogInformation("Window hidden to the notification area");
        if (!settings.TrayHintShown)
        {
            _tray.Notify(Tr.Get("Tray_Hint_Title"), Tr.Get("Tray_Hint_Text"));
            _services.Engine.UpdateSettings(s => s with { TrayHintShown = true });
        }
    }

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        if (_vm is { IsOperationRunning: true })
        {
            // Never abandon an operation half-way; let the user finish or stop it first.
            ShowMainWindow();
            _vm.ShowOperationRunningNotice();
            return;
        }

        _exiting = true;
        _lifetime.Cancel();
        _monitor?.Dispose();
        _tray?.Dispose();
        foreach (var w in Windows.OfType<Window>().ToList())
        {
            w.Close();
        }

        Shutdown();
    }

    private async Task RunAutoBaselineAsync()
    {
        try
        {
            await AutoBaselineRunner.RunAsync(_services!, _lifetime.Token);
            await (IsWindowVisible ? _vm!.RefreshAsync() : _monitor!.AnalyzeAsync());
        }
        catch (OperationCanceledException)
        {
            // Exiting.
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Automatic baseline failed");
        }
    }

    private void SyncAutostart(Settings settings)
    {
        try
        {
            if (Environment.ProcessPath is { } exe && AutostartRegistration.Sync(settings.StartWithWindows, exe))
            {
                _log?.LogInformation("Start with Windows: {Enabled}", settings.StartWithWindows);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
        {
            _log?.LogWarning(ex, "Could not update the autostart entry");
        }
    }

    private static async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.LogInformation("SnapBack exiting");
        _monitor?.Dispose();
        _tray?.Dispose();
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
