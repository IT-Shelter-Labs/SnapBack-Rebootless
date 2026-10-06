using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core;
using SnapBack.Core.Baselines;
using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Planning;
using SnapBack.Core.Presentation;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly DeepCleanupService _deepCleanup;
    private readonly IDialogService _dialogs;
    private readonly ILogger _log;
    private readonly SelectionState _selection = new();
    private List<AppRowViewModel> _allCandidates = [];
    private List<AppRowViewModel> _allKept = [];
    private List<AppRowViewModel> _allProtected = [];
    private Analysis? _analysis;
    private bool _isBusy;
    private string _status = "";
    private string _searchText = "";
    private AppSortKey _sortKey = AppSortKey.Default;
    private bool _sortDescending;
    private DateTime _lastRefreshUtc;

    public MainViewModel(SnapBackEngine engine, DeepCleanupService deepCleanup, IDialogService dialogs, ILogger log)
    {
        _engine = engine;
        _deepCleanup = deepCleanup;
        _dialogs = dialogs;
        _log = log;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        CreateBaselineCommand = new AsyncRelayCommand(CreateBaselineAsync, () => !IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedCount > 0);
        KeepSelectedCommand = new RelayCommand(KeepSelected, () => !IsBusy && SelectedCount > 0);
        SelectSafeCommand = new RelayCommand(SelectSuggested, () => _allCandidates.Count > 0);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => !IsBusy);
        OpenHistoryCommand = new RelayCommand(() => _dialogs.ShowHistory(new HistoryViewModel(_engine, _dialogs)));
        OpenMemoryCommand = new RelayCommand(() => _dialogs.ShowMemory(new MemoryViewModel(_engine, _deepCleanup, _dialogs)));
        SortCommand = new RelayCommand(p => SortBy(p as string));
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);

        Loc.CultureChanged += async (_, _) =>
        {
            try
            {
                OnPropertyChanged(string.Empty);
                await RefreshAsync(); // re-plans, so step descriptions and summaries use the new language
            }
            catch (Exception ex)
            {
                Services.ErrorReporter.Report(ex);
            }
        };
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

    public ICommand OpenMemoryCommand { get; }

    public ICommand SortCommand { get; }

    public ICommand ClearSearchCommand { get; }

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

    // ---- Search and sorting -------------------------------------------------------------------

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value ?? ""))
            {
                ApplyView();
            }
        }
    }

    public AppSortKey SortKey => _sortKey;

    public bool SortDescending => _sortDescending;

    public string NameHeader => Header("Col_Application", AppSortKey.Name);

    public string MemoryHeader => Header("Col_Memory", AppSortKey.Memory);

    public string ProcessesHeader => Header("Col_Processes", AppSortKey.Processes);

    public string RiskHeader => Header("Col_Assessment", AppSortKey.Risk);

    private string Header(string key, AppSortKey column) =>
        Tr.Get(key) + (_sortKey == column ? (_sortDescending ? " ↓" : " ↑") : "");

    /// <summary>Clicking a column sorts by it; clicking it again reverses the direction.</summary>
    public void SortBy(string? column)
    {
        if (!Enum.TryParse<AppSortKey>(column, out var key))
        {
            return;
        }

        if (_sortKey == key)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortKey = key;
            _sortDescending = key is AppSortKey.Memory or AppSortKey.Processes;
        }

        OnPropertyChanged(nameof(NameHeader));
        OnPropertyChanged(nameof(MemoryHeader));
        OnPropertyChanged(nameof(ProcessesHeader));
        OnPropertyChanged(nameof(RiskHeader));
        ApplyView();
    }

    private void ApplyView()
    {
        Fill(Candidates, _allCandidates);
        Fill(Kept, _allKept);
        Fill(Protected, _allProtected);
        OnPropertyChanged(nameof(CandidatesHeader));
        OnPropertyChanged(nameof(KeptHeader));
        OnPropertyChanged(nameof(ProtectedHeader));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(HasNoVisibleCandidates));
        RaiseSelection();
    }

    private void Fill(ObservableCollection<AppRowViewModel> target, List<AppRowViewModel> all)
    {
        var byItem = all.ToDictionary(r => r.Item);
        var visible = AppListOrdering.Sort(AppListOrdering.Filter(all.Select(r => r.Item), _searchText), _sortKey, _sortDescending);
        target.Clear();
        foreach (var item in visible)
        {
            target.Add(byItem[item]);
        }
    }

    private static string Count(int visible, int total) =>
        visible == total ? total.ToString(Loc.Culture) : $"{visible.ToString(Loc.Culture)}/{total.ToString(Loc.Culture)}";

    public string CandidatesHeader => Tr.Format("Tab_New", Count(Candidates.Count, _allCandidates.Count));

    public string KeptHeader => Tr.Format("Tab_Kept", Count(Kept.Count, _allKept.Count));

    public string ProtectedHeader => Tr.Format("Tab_Protected", Count(Protected.Count, _allProtected.Count));

    public bool HasNoVisibleCandidates => _analysis?.HasUsableBaseline == true && Candidates.Count == 0;

    public string EmptyText => _allCandidates.Count > 0 && Candidates.Count == 0
        ? Tr.Format("Main_NoSearchResults", _searchText)
        : Tr.Get("Main_NothingNew");

    // ---- Stat cards ---------------------------------------------------------------------------

    public string BaselineText => _engine.Baseline is { } b ? Relative(b.CreatedAtUtc) : Tr.Get("Main_BaselineNone");

    public string BaselineHint => _engine.Baseline is { } b
        ? Tr.Format("Main_BaselineHint", b.CreatedAtUtc.ToLocalTime().ToString("g", Loc.Culture),
            Tr.Get(b.Origin == BaselineOrigin.Automatic ? "Origin_Automatic" : "Origin_Manual"),
            b.Apps.Count(a => !a.WasProtected))
        : Tr.Get("Main_BaselineHintNone");

    public string AppsRunningText => _analysis?.RunningAppCount.ToString(Loc.Culture) ?? "–";

    public string AppsRunningHint => _analysis is null ? "" : Tr.Format("Main_ProcessesTotal", _analysis.Snapshot.Processes.Count);

    public string MemoryText => _analysis is { Memory.IsKnown: true } a ? a.Memory.UsedPercent.ToString("0", Loc.Culture) + " %" : "–";

    public string MemoryHint => _analysis is { Memory.IsKnown: true } a
        ? Tr.Format("Main_MemoryHint", ByteSize.Format(a.Memory.UsedPhysicalBytes), ByteSize.Format(a.Memory.TotalPhysicalBytes))
        : "";

    public string NewSinceText => _analysis?.HasUsableBaseline == true ? _analysis.NewAppCount.ToString(Loc.Culture) : "–";

    public string NewSinceHint => _analysis?.Comparison is { } c && c.ClosedSinceBaseline.Count > 0
        ? Tr.Format("Main_ClosedSince", c.ClosedSinceBaseline.Count)
        : Tr.Get("Main_NewSinceHint");

    public string FreeableText => _analysis?.HasUsableBaseline == true ? "≈ " + ByteSize.Format(_analysis.FreeableBytes) : "–";

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
                return Tr.Get("Banner_NoBaseline");
            }

            if (_analysis.BaselineProblem is { } problem)
            {
                return problem + " " + Tr.Get("Banner_CreateNew");
            }

            if (_analysis.Comparison is { IsSameBoot: false })
            {
                return Tr.Get("Banner_PreviousBoot");
            }

            return _analysis.Snapshot.Context.SelfElevated ? Tr.Get("Banner_Elevated") : null;
        }
    }

    public bool BannerIsWarning => _analysis?.BaselineProblem is not null || _analysis?.Comparison is { IsSameBoot: false };

    // ---- Selection ---------------------------------------------------------------------------

    public int SelectedCount => _allCandidates.Count(c => c.IsSelected);

    public string SelectionText
    {
        get
        {
            var selected = _allCandidates.Where(c => c.IsSelected).ToList();
            if (selected.Count == 0)
            {
                return Tr.Get("Main_NothingSelected");
            }

            var text = Tr.Format("Main_Selected", selected.Count, ByteSize.Format(selected.Sum(s => s.MemoryBytes)));
            var hidden = selected.Count(s => !Candidates.Contains(s));
            return hidden > 0 ? text + Tr.Format("Main_SelectedHidden", hidden) : text;
        }
    }

    public string RestoreButtonText => SelectedCount > 0 ? Tr.Format("Main_RestoreCount", SelectedCount) : Tr.Get("Main_Restore");

    public async Task RefreshIfStaleAsync()
    {
        if (!IsBusy && DateTime.UtcNow - _lastRefreshUtc > TimeSpan.FromSeconds(15))
        {
            await RefreshAsync();
        }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = Tr.Get("Status_Analyzing");
        try
        {
            var analysis = await Task.Run(_engine.Analyze);
            Apply(analysis);
            Status = Tr.Format("Status_Updated", DateTime.Now.ToString("T", Loc.Culture));
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
        var plan = analysis.Plan;
        _allCandidates = Rows(plan?.Candidates);
        _allKept = Rows(plan?.Kept);
        _allProtected = Rows(plan?.Protected);
        _selection.Prune(_allCandidates.Select(c => c.Id));
        ApplyView();
        OnPropertyChanged(string.Empty);
    }

    private List<AppRowViewModel> Rows(IReadOnlyList<PlanItem>? items) =>
        (items ?? []).Select(i => new AppRowViewModel(i, _selection, RaiseSelection)).ToList();

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(RestoreButtonText));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task CreateBaselineAsync()
    {
        if (_engine.Baseline is not null && !_dialogs.Confirm(Tr.Get("Confirm_ReplaceBaseline_Title"), Tr.Get("Confirm_ReplaceBaseline_Text")))
        {
            return;
        }

        IsBusy = true;
        Status = Tr.Get("Status_CreatingBaseline");
        try
        {
            await Task.Run(() => _engine.CreateBaseline(BaselineOrigin.Manual));
            _selection.Clear();
        }
        finally
        {
            IsBusy = false;
        }

        await RefreshAsync();
        Status = Tr.Get("Status_BaselineCreated");
    }

    private async Task RestoreAsync()
    {
        if (_analysis is null)
        {
            return;
        }

        var selected = _allCandidates.Where(c => c.IsSelected && c.IsSelectable).Select(c => c.Item).ToList();
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
        var before = SessionStatistics.From(_analysis);
        var operation = new OperationViewModel(_engine, _engine.StartOperation(_analysis, selected), before,
            async () => SessionStatistics.From(await Task.Run(_engine.Analyze)), _deepCleanup, _dialogs);
        _dialogs.ShowOperation(operation);
        _selection.Clear();
        await RefreshAsync();
    }

    private void KeepSelected()
    {
        var selected = _allCandidates.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var names = string.Join("\n", selected.Select(s => "• " + s.Name).Distinct());
        if (!_dialogs.Confirm(Tr.Get("Confirm_Keep_Title"), Tr.Format("Confirm_Keep_Text", names)))
        {
            return;
        }

        _engine.UpdateSettings(s => selected.Aggregate(s, (acc, row) => acc.WithKept(row.Item.Group.AppKey, row.Name, DateTime.UtcNow)));
        _ = RefreshAsync();
    }

    private void SelectSuggested()
    {
        foreach (var c in _allCandidates)
        {
            _selection.Set(c.Item, c.Item.DefaultSelected);
            c.RefreshSelection();
        }

        RaiseSelection();
    }

    private void ClearSelection()
    {
        foreach (var c in _allCandidates)
        {
            _selection.Set(c.Item, false);
            c.RefreshSelection();
        }

        RaiseSelection();
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
            < 1 => Tr.Get("Time_JustNow"),
            < 60 => Tr.Format("Time_Minutes", (int)age.TotalMinutes),
            < 60 * 24 => Tr.Format("Time_HoursMinutes", (int)age.TotalHours, age.Minutes),
            _ => Tr.Format("Time_Days", (int)age.TotalDays),
        };
    }
}
