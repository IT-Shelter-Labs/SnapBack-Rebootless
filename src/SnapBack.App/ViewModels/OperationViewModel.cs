using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core;
using SnapBack.Core.Execution;
using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Model;
using SnapBack.Core.Safety;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

/// <summary>Runs one cleanup operation and shows its progress, results and before/after numbers.</summary>
public sealed class OperationViewModel : ObservableObject
{
    /// <summary>Memory counters need a moment to reflect processes that just exited.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1.5);

    private readonly SnapBackEngine _engine;
    private readonly CleanupOperation _operation;
    private readonly SessionStatistics _before;
    private readonly Func<Task<SessionStatistics>> _measure;
    private readonly DeepCleanupService _deepCleanup;
    private readonly IDialogService _dialogs;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private bool _isRunning;
    private bool _isMeasuring;
    private string _headerKey = "Op_Header_Closing";
    private RestoreComparison? _comparison;
    private DeepCleanupReport? _deepReport;

    public OperationViewModel(SnapBackEngine engine, CleanupOperation operation, SessionStatistics before,
        Func<Task<SessionStatistics>> measure, DeepCleanupService deepCleanup, IDialogService dialogs)
    {
        _engine = engine;
        _operation = operation;
        _before = before;
        _measure = measure;
        _deepCleanup = deepCleanup;
        _dialogs = dialogs;
        foreach (var r in operation.Results)
        {
            Rows.Add(new OperationRowViewModel(r));
        }

        StopCommand = new RelayCommand(() => _cts.Cancel(), () => IsRunning && !_cts.IsCancellationRequested);
        RecheckCommand = new AsyncRelayCommand(RecheckAsync, () => !IsRunning && Rows.Any(r => r.CanForce));
        ForceCommand = new AsyncRelayCommand(ForceAsync, () => !IsRunning && Rows.Any(r => r.CanForce && r.ForceSelected));
        DeepCleanupCommand = new RelayCommand(OpenDeepCleanup, () => !IsRunning && !_isMeasuring);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
    }

    public ObservableCollection<OperationRowViewModel> Rows { get; } = [];

    public ObservableCollection<ComparisonRowViewModel> Comparison { get; } = [];

    public ObservableCollection<ComparisonRowViewModel> DeepCleanupRows { get; } = [];

    public ICommand StopCommand { get; }

    public ICommand RecheckCommand { get; }

    public ICommand ForceCommand { get; }

    public ICommand DeepCleanupCommand { get; }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(IsFinished));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public bool IsFinished => !IsRunning;

    public string Header => Tr.Get(_headerKey);

    public string ElapsedText => IsRunning ? $"{(int)(DateTime.UtcNow - _startedUtc).TotalSeconds} s" : "";

    public bool HasStillRunning => Rows.Any(r => r.CanForce);

    public bool HasComparison => _comparison is not null;

    public bool IsMeasuring => _isMeasuring;

    public string Summary
    {
        get
        {
            if (IsRunning)
            {
                return "";
            }

            var results = _operation.Results;
            var closed = results.Count(r => r.Outcome is ItemOutcome.Closed or ItemOutcome.PartiallyClosed or ItemOutcome.Terminated);
            var sb = new StringBuilder(Tr.Format("Op_Summary", closed, results.Count));
            var still = results.Count(r => r.CanForce);
            if (still > 0)
            {
                sb.Append('\n').Append(Tr.Format("Op_SummaryStillRunning", still));
            }

            return sb.ToString();
        }
    }

    /// <summary>Memory released by the apps that closed (their private memory), independent of any cleanup.</summary>
    public string ReleasedByAppsText => Tr.Format("Op_ReleasedByApps", ByteSize.Format(_operation.Results.Sum(r => r.MemoryFreedBytes)));

    public string? DeepCleanupText => _deepReport is { } r
        ? r.Outcome.Succeeded
            ? Tr.Format("Op_DeepCleanupReclaimed", ByteSize.Format(r.CacheReclaimedBytes))
            : r.Outcome.Message
        : null;

    public string? AdviceTitle => _comparison is null ? null : MemoryAdvisor.Advise(_comparison.After.Memory).Title;

    public string? AdviceText => _comparison is null ? null : MemoryAdvisor.Advise(_comparison.After.Memory).Text;

    public async Task RunAsync()
    {
        IsRunning = true;
        _timer.Start();
        try
        {
            var progress = new Progress<ItemResult>(Update);
            var report = await Task.Run(() => _operation.RunGracefulAsync(progress, _cts.Token));
            foreach (var r in report.Items)
            {
                Update(r);
            }

            _engine.RecordHistory(report);
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
        }

        _headerKey = "Op_Header_Done";
        OnPropertyChanged(string.Empty);
        await MeasureAsync();
    }

    private async Task MeasureAsync()
    {
        _isMeasuring = true;
        OnPropertyChanged(nameof(IsMeasuring));
        try
        {
            await Task.Delay(SettleDelay);
            var after = await _measure();
            _comparison = new RestoreComparison(_before, after, _operation.Results.Sum(r => r.MemoryFreedBytes)) { DeepCleanup = _deepReport };
            Fill(Comparison, _comparison.Rows);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report(ex);
        }
        finally
        {
            _isMeasuring = false;
            OnPropertyChanged(string.Empty);
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static void Fill(ObservableCollection<ComparisonRowViewModel> target, IEnumerable<ComparisonRow> rows)
    {
        target.Clear();
        foreach (var row in rows)
        {
            target.Add(new ComparisonRowViewModel(row));
        }
    }

    private void Update(ItemResult result)
    {
        Rows.FirstOrDefault(r => r.Id == result.Item.Group.Id)?.Update(result);
        OnPropertyChanged(nameof(HasStillRunning));
    }

    private async Task RecheckAsync()
    {
        foreach (var r in _operation.Recheck())
        {
            Update(r);
        }

        OnPropertyChanged(string.Empty);
        await MeasureAsync();
    }

    private async Task ForceAsync()
    {
        var rows = Rows.Where(r => r.CanForce && r.ForceSelected).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var names = string.Join("\n", rows.Select(r => Tr.Format("Force_Item", r.Name, r.Result.ProcessesRemaining)));
        if (!_dialogs.Confirm(Tr.Get("Force_Title"), Tr.Format("Force_Text", names), dangerous: true))
        {
            return;
        }

        // The authorization covers exactly the process instances the user just confirmed.
        var approved = rows.SelectMany(r => r.Result.RemainingProcesses).Select(p => p.Identity).ToList();
        var authorization = ForceAuthorization.FromUserConfirmation(approved, DateTime.UtcNow);
        var ids = rows.Select(r => r.Id).ToList();

        IsRunning = true;
        _headerKey = "Op_Header_Forcing";
        OnPropertyChanged(nameof(Header));
        try
        {
            var progress = new Progress<ItemResult>(Update);
            var report = await Task.Run(() => _operation.ForceTerminateAsync(authorization, ids, progress));
            foreach (var r in report.Items)
            {
                Update(r);
            }

            _engine.RecordHistory(report);
        }
        finally
        {
            IsRunning = false;
        }

        _headerKey = "Op_Header_Done";
        OnPropertyChanged(string.Empty);
        await MeasureAsync();
    }

    /// <summary>Deep Memory Cleanup is a separate, explicit choice; its result is shown apart from the restore.</summary>
    private void OpenDeepCleanup()
    {
        var memory = new MemoryViewModel(_engine, _deepCleanup, _dialogs);
        _dialogs.ShowMemory(memory);
        if (memory.LastReport is { } report)
        {
            _deepReport = report;
            if (_comparison is not null)
            {
                _comparison = _comparison with { DeepCleanup = report };
            }

            Fill(DeepCleanupRows, report.Rows);
            OnPropertyChanged(string.Empty);
        }
    }

    public void CancelIfRunning()
    {
        if (IsRunning)
        {
            _cts.Cancel();
        }
    }
}

/// <summary>"Label: before → after (change)".</summary>
public sealed class ComparisonRowViewModel(ComparisonRow row)
{
    public string Label => Tr.Get(row.LabelKey);

    public string BeforeText => Format(row.Before);

    public string AfterText => Format(row.After);

    public string DeltaText => row.Delta is { } d && d != 0
        ? row.Kind == ComparisonValueKind.Bytes ? ByteSize.FormatDelta(d) : (d > 0 ? "+" : "−") + Math.Abs(d).ToString(Loc.Culture)
        : "";

    private string Format(long? value) => value is not { } v
        ? "—"
        : row.Kind == ComparisonValueKind.Bytes ? ByteSize.Format(v) : v.ToString(Loc.Culture);
}

public sealed class OperationRowViewModel : ObservableObject
{
    private bool _forceSelected;

    public OperationRowViewModel(ItemResult result)
    {
        Result = result;
    }

    public ItemResult Result { get; private set; }

    public ProcessIdentity Id => Result.Item.Group.Id;

    public string Name => Result.Item.Group.DisplayName;

    public ImageSource? Icon => IconCache.Get(Result.Item.Group.ExecutablePath);

    public string StatusText => Result.Message;

    public string Glyph => Result.Outcome switch
    {
        ItemOutcome.Closed or ItemOutcome.Terminated or ItemOutcome.AlreadyExited or ItemOutcome.PartiallyClosed => "",
        ItemOutcome.StillRunning or ItemOutcome.Failed => "",
        ItemOutcome.Skipped => "",
        _ => "",
    };

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Result.ProcessesExited > 0)
            {
                parts.Add(Tr.Format("Op_RowExited", Result.ProcessesExited, ByteSize.Format(Result.MemoryFreedBytes)));
            }

            if (Result.ProcessesRemaining > 0 && Result.IsFinished)
            {
                parts.Add(Tr.Format("Op_RowRemaining", Result.ProcessesRemaining));
            }

            parts.AddRange(Result.Notes);
            return string.Join("\n", parts);
        }
    }

    public bool CanForce => Result.CanForce;

    public bool ForceSelected
    {
        get => _forceSelected;
        set
        {
            if (Set(ref _forceSelected, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public void Update(ItemResult result)
    {
        Result = result;
        if (!result.CanForce)
        {
            _forceSelected = false;
        }

        OnPropertyChanged(string.Empty);
    }
}
