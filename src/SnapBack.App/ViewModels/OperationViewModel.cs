using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SnapBack.App.Mvvm;
using SnapBack.App.Services;
using SnapBack.Core;
using SnapBack.Core.Execution;
using SnapBack.Core.Model;
using SnapBack.Core.Safety;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

/// <summary>Runs one cleanup operation and shows its progress and results.</summary>
public sealed class OperationViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly CleanupOperation _operation;
    private readonly IDialogService _dialogs;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private bool _isRunning;
    private string _header = "Asking apps to close…";
    private string _summary = "";
    private OperationReport? _lastReport;

    public OperationViewModel(SnapBackEngine engine, CleanupOperation operation, IDialogService dialogs)
    {
        _engine = engine;
        _operation = operation;
        _dialogs = dialogs;
        foreach (var r in operation.Results)
        {
            Rows.Add(new OperationRowViewModel(r));
        }

        StopCommand = new RelayCommand(() => _cts.Cancel(), () => IsRunning && !_cts.IsCancellationRequested);
        RecheckCommand = new RelayCommand(Recheck, () => !IsRunning && Rows.Any(r => r.CanForce));
        ForceCommand = new AsyncRelayCommand(ForceAsync, () => !IsRunning && Rows.Any(r => r.CanForce && r.ForceSelected));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnPropertyChanged(nameof(ElapsedText));
    }

    public ObservableCollection<OperationRowViewModel> Rows { get; } = [];

    public ICommand StopCommand { get; }

    public ICommand RecheckCommand { get; }

    public ICommand ForceCommand { get; }

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

    public string Header
    {
        get => _header;
        private set => Set(ref _header, value);
    }

    public string Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public string ElapsedText => IsRunning ? $"{(int)(DateTime.UtcNow - _startedUtc).TotalSeconds} s" : "";

    public bool HasStillRunning => Rows.Any(r => r.CanForce);

    public async Task RunAsync()
    {
        IsRunning = true;
        _timer.Start();
        try
        {
            var progress = new Progress<ItemResult>(Update);
            _lastReport = await Task.Run(() => _operation.RunGracefulAsync(progress, _cts.Token));
            foreach (var r in _lastReport.Items)
            {
                Update(r);
            }

            _engine.RecordHistory(_lastReport);
        }
        finally
        {
            _timer.Stop();
            IsRunning = false;
        }

        Header = "Done";
        UpdateSummary();
    }

    private void Update(ItemResult result)
    {
        var row = Rows.FirstOrDefault(r => r.Id == result.Item.Group.Id);
        row?.Update(result);
        OnPropertyChanged(nameof(HasStillRunning));
    }

    private void UpdateSummary()
    {
        var results = _operation.Results;
        var closed = results.Count(r => r.Outcome is ItemOutcome.Closed or ItemOutcome.PartiallyClosed or ItemOutcome.Terminated);
        var freed = results.Sum(r => r.MemoryFreedBytes);
        var sb = new StringBuilder($"{closed} of {results.Count} app(s) closed, about {ByteSize.Format(freed)} of memory released.");
        if (_lastReport is { } report && report.MemoryBefore.TotalPhysicalBytes > 0)
        {
            sb.Append($" Available memory: {ByteSize.Format(report.MemoryBefore.AvailablePhysicalBytes)} → {ByteSize.Format(report.MemoryAfter.AvailablePhysicalBytes)}.");
        }

        var still = results.Count(r => r.CanForce);
        if (still > 0)
        {
            sb.Append($"\n{still} app(s) are still running. Nothing was forced. You can switch to them, then press \"Check again\" — or select them and choose force-termination.");
        }

        Summary = sb.ToString();
        OnPropertyChanged(nameof(HasStillRunning));
        CommandManager.InvalidateRequerySuggested();
    }

    private void Recheck()
    {
        foreach (var r in _operation.Recheck())
        {
            Update(r);
        }

        UpdateSummary();
    }

    private async Task ForceAsync()
    {
        var rows = Rows.Where(r => r.CanForce && r.ForceSelected).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var names = string.Join("\n", rows.Select(r => $"• {r.Name} ({r.Result.ProcessesRemaining} process(es))"));
        var confirmed = _dialogs.Confirm(
            "Force-terminate apps?",
            $"These apps did not close when asked:\n\n{names}\n\n"
            + "Force-terminating ends them immediately. Any unsaved work in them will be lost, and they cannot clean up.\n\n"
            + "Protected and system processes are never affected.\n\nForce-terminate them now?",
            dangerous: true);
        if (!confirmed)
        {
            return;
        }

        // The authorization covers exactly the process instances the user just confirmed.
        var approved = rows.SelectMany(r => r.Result.RemainingProcesses).Select(p => p.Identity).ToList();
        var authorization = ForceAuthorization.FromUserConfirmation(approved, DateTime.UtcNow);
        var ids = rows.Select(r => r.Id).ToList();

        IsRunning = true;
        Header = "Force-terminating…";
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

        Header = "Done";
        UpdateSummary();
    }

    public void CancelIfRunning()
    {
        if (IsRunning)
        {
            _cts.Cancel();
        }
    }
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
        ItemOutcome.Closed or ItemOutcome.Terminated or ItemOutcome.AlreadyExited => "",  // CheckMark
        ItemOutcome.PartiallyClosed => "",
        ItemOutcome.StillRunning or ItemOutcome.Failed => "",  // Warning
        ItemOutcome.Skipped => "",  // Lock
        _ => "",  // Stopwatch
    };

    public string DetailText
    {
        get
        {
            var parts = new List<string>();
            if (Result.ProcessesExited > 0)
            {
                parts.Add($"{Result.ProcessesExited} process(es) exited, ≈ {ByteSize.Format(Result.MemoryFreedBytes)} released");
            }

            if (Result.ProcessesRemaining > 0 && Result.IsFinished)
            {
                parts.Add($"{Result.ProcessesRemaining} still running");
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
