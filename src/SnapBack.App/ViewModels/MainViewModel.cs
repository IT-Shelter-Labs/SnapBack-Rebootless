using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SnapBack.App.Mvvm;
using SnapBack.Core;
using SnapBack.Core.Baselines;
using SnapBack.Core.Model;
using SnapBack.Core.Planning;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly IDialogService _dialogs;
    private readonly ILogger _log;
    private readonly Dictionary<ProcessIdentity, bool> _userChoices = new();
    private Analysis? _analysis;
    private bool _isBusy;
    private string _status = "";
    private DateTime _lastRefreshUtc;

    public MainViewModel(SnapBackEngine engine, IDialogService dialogs, ILogger log)
    {
        _engine = engine;
        _dialogs = dialogs;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        CreateBaselineCommand = new AsyncRelayCommand(CreateBaselineAsync, () => !IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedCount > 0);
        KeepSelectedCommand = new RelayCommand(KeepSelected, () => !IsBusy && SelectedCount > 0);
        SelectSafeCommand = new RelayCommand(SelectSafe, () => Candidates.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => !IsBusy);
        OpenHistoryCommand = new RelayCommand(() => _dialogs.ShowHistory(new HistoryViewModel(_engine, _dialogs)));
    }

    public ObservableCollection<AppRowViewModel> Candidates { get; } = [];

    public ObservableCollection<AppRowViewModel> Kept { get; } = [];

    public ObservableCollection<AppRowViewModel> Protected { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand CreateBaselineCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand KeepSelectedCommand { get; }

    public ICommand SelectSafeCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenHistoryCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value))
            {
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    // ---- Stat cards ---------------------------------------------------------------------------

    public string BaselineText => _engine.Baseline is { } b ? Relative(b.CreatedAtUtc) : "Not created";

    public string BaselineHint => _engine.Baseline is { } b
        ? $"{b.CreatedAtUtc.ToLocalTime():g} · {(b.Origin == BaselineOrigin.Automatic ? "automatic" : "manual")} · {b.Apps.Count(a => !a.WasProtected)} apps"
        : "Make the current state the baseline";

    public string AppsRunningText => _analysis?.RunningAppCount.ToString(CultureInfo.CurrentCulture) ?? "–";

    public string AppsRunningHint => _analysis is null ? "" : $"{_analysis.Snapshot.Processes.Count} processes in total";

    public string MemoryText => _analysis is { Memory.TotalPhysicalBytes: > 0 } a ? $"{a.Memory.UsedPercent:0}%" : "–";

    public string MemoryHint => _analysis is { Memory.TotalPhysicalBytes: > 0 } a
        ? $"{ByteSize.Format(a.Memory.UsedPhysicalBytes)} of {ByteSize.Format(a.Memory.TotalPhysicalBytes)} in use"
        : "";

    public string NewSinceText => _analysis?.HasUsableBaseline == true ? _analysis.NewAppCount.ToString(CultureInfo.CurrentCulture) : "–";

    public string NewSinceHint => _analysis?.Comparison is { } c && c.ClosedSinceBaseline.Count > 0
        ? $"{c.ClosedSinceBaseline.Count} baseline app(s) closed since"
        : "apps started after the baseline";

    public string FreeableText => _analysis?.HasUsableBaseline == true ? "≈ " + ByteSize.Format(_analysis.FreeableBytes) : "–";

    public string FreeableHint => "memory used by those apps";

    // ---- Banner ------------------------------------------------------------------------------

    public string? Banner
    {
        get
        {
            if (_analysis is null)
            {
                return null;
            }

            if (_analysis.Baseline is null)
            {
                return "No baseline yet. When your computer is in the state you consider \"clean\" (for example shortly after starting Windows), click \"Make current state the baseline\".";
            }

            if (_analysis.BaselineProblem is { } problem)
            {
                return problem + " Create a new baseline.";
            }

            if (_analysis.Comparison is { IsSameBoot: false })
            {
                return "The baseline was created before the last restart, so apps are matched by name and location only. Consider creating a fresh baseline.";
            }

            if (_analysis.Snapshot.Context.SelfElevated)
            {
                return "SnapBack is running as administrator. This is not needed, and apps that run as administrator are never closed. "
                    + "If User Account Control is turned off, all your apps run elevated and SnapBack will show them as protected.";
            }

            return null;
        }
    }

    public bool BannerIsWarning => _analysis?.BaselineProblem is not null || _analysis?.Comparison is { IsSameBoot: false };

    // ---- Selection ---------------------------------------------------------------------------

    public int SelectedCount => Candidates.Count(c => c.IsSelected);

    public string SelectionText
    {
        get
        {
            var selected = Candidates.Where(c => c.IsSelected).ToList();
            return selected.Count == 0
                ? "Nothing selected"
                : $"{selected.Count} app{(selected.Count == 1 ? "" : "s")} selected · ≈ {ByteSize.Format(selected.Sum(s => s.MemoryBytes))}";
        }
    }

    public string RestoreButtonText => SelectedCount > 0 ? $"Return to baseline ({SelectedCount})…" : "Return to baseline…";

    public string CandidatesHeader => $"New since baseline ({Candidates.Count})";

    public string KeptHeader => $"Kept ({Kept.Count})";

    public string ProtectedHeader => $"Protected ({Protected.Count})";

    public bool HasNoCandidates => _analysis?.HasUsableBaseline == true && Candidates.Count == 0;

    public async Task RefreshIfStaleAsync()
    {
        if (!IsBusy && DateTime.UtcNow - _lastRefreshUtc > TimeSpan.FromSeconds(15))
        {
            await RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        IsBusy = true;
        Status = "Analyzing running applications…";
        try
        {
            var analysis = await Task.Run(_engine.Analyze);
            Apply(analysis);
            Status = $"Updated {DateTime.Now:T}";
        }
        finally
        {
            _lastRefreshUtc = DateTime.UtcNow;
            IsBusy = false;
        }
    }

    private void Apply(Analysis analysis)
    {
        _analysis = analysis;
        Fill(Candidates, analysis.Plan?.Candidates ?? []);
        Fill(Kept, analysis.Plan?.Kept ?? []);
        Fill(Protected, analysis.Plan?.Protected ?? []);

        // Forget choices for apps that are gone.
        var live = Candidates.Select(c => c.Id).ToHashSet();
        foreach (var gone in _userChoices.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _userChoices.Remove(gone);
        }

        OnPropertyChanged(string.Empty);
    }

    private void Fill(ObservableCollection<AppRowViewModel> target, IEnumerable<PlanItem> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            var selected = _userChoices.TryGetValue(item.Group.Id, out var choice) ? choice : item.DefaultSelected;
            AppRowViewModel? row = null;
            row = new AppRowViewModel(item, selected, () => OnSelectionChanged(row!));
            target.Add(row);
        }
    }

    private void OnSelectionChanged(AppRowViewModel row)
    {
        _userChoices[row.Id] = row.IsSelected;
        RaiseSelection();
    }

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(RestoreButtonText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CreateBaselineAsync()
    {
        if (_engine.Baseline is not null && !_dialogs.Confirm("Replace baseline",
                "Make the current state the new baseline?\n\nEverything that is running right now will be considered part of the clean state."))
        {
            return;
        }

        IsBusy = true;
        Status = "Creating baseline…";
        try
        {
            await Task.Run(() => _engine.CreateBaseline(BaselineOrigin.Manual));
            _userChoices.Clear();
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        Status = "Baseline created.";
    }

    private async Task RestoreAsync()
    {
        if (_analysis is null)
        {
            return;
        }

        var selected = Candidates.Where(c => c.IsSelected && c.IsSelectable).Select(c => c.Item).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var preview = new PreviewViewModel(_engine, _analysis, selected);
        if (!_dialogs.ShowPreview(preview))
        {
            return;
        }

        _log.LogInformation("User confirmed closing {Count} app(s)", selected.Count);
        var operation = new OperationViewModel(_engine, _engine.StartOperation(_analysis, selected), _dialogs);
        _dialogs.ShowOperation(operation);
        _userChoices.Clear();
        await RefreshAsync();
    }

    private void KeepSelected()
    {
        var selected = Candidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var names = string.Join("\n", selected.Select(s => "• " + s.Name).Distinct());
        if (!_dialogs.Confirm("Always keep", $"Always keep these apps? They will never be suggested for closing:\n\n{names}\n\nYou can undo this in Settings."))
        {
            return;
        }

        _engine.UpdateSettings(s => selected.Aggregate(s, (acc, row) => acc.WithKept(row.Item.Group.AppKey, row.Name, DateTime.UtcNow)));
        _ = RefreshAsync();
    }

    private void SelectSafe()
    {
        foreach (var c in Candidates)
        {
            c.IsSelected = c.Item.DefaultSelected;
        }
    }

    private void ClearSelection()
    {
        foreach (var c in Candidates)
        {
            c.IsSelected = false;
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (_dialogs.ShowSettings(new SettingsViewModel(_engine, _dialogs)))
        {
            await RefreshAsync();
        }
    }

    private static string Relative(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        return age.TotalMinutes switch
        {
            < 1 => "Just now",
            < 60 => $"{(int)age.TotalMinutes} min ago",
            < 60 * 24 => $"{(int)age.TotalHours} h {age.Minutes} min ago",
            _ => $"{(int)age.TotalDays} day(s) ago",
        };
    }
}
