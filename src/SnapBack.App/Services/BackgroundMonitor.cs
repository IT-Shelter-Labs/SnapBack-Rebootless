using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using SnapBack.App.Localization;
using SnapBack.Core;
using SnapBack.Core.Insights;
using SnapBack.Core.Text;
using SnapBack.Windows;

namespace SnapBack.App.Services;

/// <summary>
/// Keeps the tray tooltip and notifications up to date while SnapBack runs in the background.
/// </summary>
/// <remarks>
/// It does not poll: one analysis every <see cref="Interval"/> while the window is hidden, run on
/// a background-priority thread, plus whatever analyses the window does anyway. Each analysis
/// also feeds the memory tracker. Between analyses SnapBack does nothing at all.
/// </remarks>
public sealed class BackgroundMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    private readonly SnapBackEngine _engine;
    private readonly TrayController _tray;
    private readonly Func<bool> _windowVisible;
    private readonly ILogger _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly NotificationPolicy _policy = new();
    private int _running;

    public BackgroundMonitor(SnapBackEngine engine, TrayController tray, Func<bool> windowVisible, ILogger log)
    {
        _engine = engine;
        _tray = tray;
        _windowVisible = windowVisible;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = Interval };
        _timer.Tick += (_, _) =>
        {
            if (!_windowVisible())
            {
                _ = AnalyzeAsync();
            }
        };
        _engine.Analyzed += OnAnalyzed;
    }

    public void Start() => _timer.Start();

    /// <summary>Runs one background analysis now (unless one is already running).</summary>
    public async Task AnalyzeAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                using var _ = ResourceModes.BackgroundThread();
                _engine.Analyze();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Background analysis failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _engine.Analyzed -= OnAnalyzed;
    }

    /// <summary>Called on the analyzing thread after every analysis.</summary>
    private void OnAnalyzed(object? sender, Analysis analysis)
    {
        var now = DateTime.UtcNow;
        var drift = analysis.Drift(now);
        var impact = analysis.DefaultImpact();
        var unusual = _engine.Tracker.Unusual();
        _dispatcher.BeginInvoke(() =>
        {
            try
            {
                _tray.SetTooltip(TooltipFor(analysis, drift, impact));
                var request = _policy.Evaluate(drift, impact, unusual, _engine.Settings.ShowNotifications, _windowVisible(), now);
                if (request is not null)
                {
                    _tray.Notify(request.Title, request.Text);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not update the tray status");
            }
        });
    }

    public static string TooltipFor(Analysis analysis, SessionDrift drift, ImpactEstimate impact)
    {
        var header = analysis.Profile is { } p ? Tr.Format("Tray_Tooltip_Header", p.Name) : Tr.Get("App_Name");
        string status;
        if (!analysis.HasUsableBaseline)
        {
            status = Tr.Get("Tray_Tooltip_NoBaseline");
        }
        else if (drift.NewApps == 0)
        {
            status = Tr.Get("Tray_Tooltip_InSync");
        }
        else
        {
            status = Tr.Format("Tray_Tooltip_Extra", drift.NewApps, ByteSize.Format(impact.EstimatedReclaimBytes));
        }

        return header + "\n" + status;
    }
}
