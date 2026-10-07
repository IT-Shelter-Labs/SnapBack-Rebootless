using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core;
using SnapBack.Core.Baselines;
using SnapBack.Core.Environments;
using SnapBack.Core.Insights;
using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Planning;
using SnapBack.Core.Presentation;
using SnapBack.Core.Profiles;
using SnapBack.Core.Rules;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly DeepCleanupService _deepCleanup;
    private readonly EnvironmentService _environments;
    private readonly IDialogService _dialogs;
    private readonly ILogger _log;
    private readonly SelectionState _selection = new();
    private List<AppRowViewModel> _allCandidates = [];
    private List<AppRowViewModel> _allKept = [];
    private List<AppRowViewModel> _allProtected = [];
    private Analysis? _analysis;
    private SessionDrift _drift = SessionDrift.Unknown;
    private bool _isBusy;
    private bool _isOperationRunning;
    private bool _isRestoring;
    private bool _updatingProfiles;
    private string _status = "";
    private string _searchText = "";
    private AppSortKey _sortKey = AppSortKey.Default;
    private bool _sortDescending;
    private DateTime _lastRefreshUtc;
    private ProfileOption? _selectedProfile;

    public MainViewModel(SnapBackEngine engine, DeepCleanupService deepCleanup, EnvironmentService environments, IDialogService dialogs, ILogger log)
    {
        _engine = engine;
        _deepCleanup = deepCleanup;
        _environments = environments;
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
        OpenProfilesCommand = new AsyncRelayCommand(OpenProfilesAsync, () => !IsBusy);
        SortCommand = new RelayCommand(p => SortBy(p as string));
        ClearSearchCommand = new RelayCommand(() => SearchText = "", () => SearchText.Length > 0);

        RebuildProfileOptions();
        _engine.ProfilesChanged += (_, _) => RunOnUi(RebuildProfileOptions);

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

    public ObservableCollection<ProfileOption> ProfileOptions { get; } = [];

    public ObservableCollection<EnvironmentCardViewModel> Environments { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand CreateBaselineCommand { get; }

    public ICommand RestoreCommand { get; }

    public ICommand KeepSelectedCommand { get; }

    public ICommand SelectSafeCommand { get; }

    public ICommand ClearSelectionCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public ICommand OpenHistoryCommand { get; }

    public ICommand OpenMemoryCommand { get; }

    public ICommand OpenProfilesCommand { get; }

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

    /// <summary>Whether apps are being closed right now (SnapBack must not exit then).</summary>
    public bool IsOperationRunning => _isOperationRunning;

    /// <summary>Whether a preview or an operation is open.</summary>
    public bool IsRestoring => _isRestoring;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    // ---- Profiles -----------------------------------------------------------------------------

    /// <summary>The profile the session is compared with. Choosing one closes nothing.</summary>
    public ProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || _updatingProfiles || !Set(ref _selectedProfile, value))
            {
                return;
            }

            _engine.SelectProfile(value.Id);
            _selection.Clear();
            _ = SafeRefresh();
        }
    }

    public bool HasProfiles => ProfileOptions.Count > 0;

    public bool HasSeveralProfiles => ProfileOptions.Count > 1;

    private void RebuildProfileOptions()
    {
        _updatingProfiles = true;
        try
        {
            var profiles = _engine.Profiles;
            ProfileOptions.Clear();
            foreach (var p in profiles.Profiles)
            {
                ProfileOptions.Add(new ProfileOption(p.Id, p.Name, p.Id == profiles.DefaultProfileId));
            }

            _selectedProfile = ProfileOptions.FirstOrDefault(o => o.Id == profiles.Current?.Id);
            OnPropertyChanged(nameof(SelectedProfile));
            OnPropertyChanged(nameof(HasProfiles));
            OnPropertyChanged(nameof(HasSeveralProfiles));
            OnPropertyChanged(nameof(RestoreButtonText));
            OnPropertyChanged(nameof(BaselineText));
            OnPropertyChanged(nameof(BaselineHint));
        }
        finally
        {
            _updatingProfiles = false;
        }
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

    // ---- Dashboard ----------------------------------------------------------------------------

    public string BaselineText => _engine.Baseline is { } b ? Relative(b.CreatedAtUtc) : Tr.Get("Main_BaselineNone");

    public string BaselineHint => _engine.CurrentProfile is { } p
        ? Tr.Format("Main_BaselineHint", p.Baseline.CreatedAtUtc.ToLocalTime().ToString("g", Loc.Culture),
            Tr.Get(p.Baseline.Origin == BaselineOrigin.Automatic ? "Origin_Automatic" : "Origin_Manual"), p.AppCount)
        : Tr.Get("Main_BaselineHintNone");

    public string UptimeText => _drift.SessionUptime is { } up ? Tr.Format("Main_Uptime", Duration(up)) : "";

    public DriftLevel DriftLevel => _drift.Level;

    public string DriftText => Tr.Get("Drift_" + _drift.Level);

    public string DriftHint
    {
        get
        {
            if (_drift.Level == DriftLevel.Unknown)
            {
                return Tr.Get("Drift_Hint_Unknown");
            }

            if (_drift.NewApps == 0 && _drift.ProcessDelta <= 0)
            {
                return Tr.Get("Drift_Hint_InSync");
            }

            return Tr.Format("Drift_Hint", Signed(_drift.NewApps), Signed(_drift.ProcessDelta), "+" + ByteSize.Format(_drift.NewAppMemoryBytes));
        }
    }

    public string DriftTooltip => _drift.Level == DriftLevel.Unknown
        ? Tr.Get("Drift_Explain")
        : Tr.Format("Drift_Tooltip", _drift.Score, Tr.Get("Drift_Factor_" + _drift.MainFactor),
            _drift.BaselineAge is { } age ? Duration(age) : "–") + "\n\n" + Tr.Get("Drift_Explain");

    public string AppsRunningText => _analysis?.RunningAppCount.ToString(Loc.Culture) ?? "–";

    public string AppsRunningHint => _analysis is null ? "" : Tr.Format("Main_ProcessesTotal", _analysis.Snapshot.Processes.Count);

    public string MemoryText => _analysis is { Memory.IsKnown: true } a ? ByteSize.Format(a.Memory.UsedPhysicalBytes) : "–";

    public string MemoryHint => _analysis is { Memory.IsKnown: true } a
        ? Tr.Format("Main_MemoryAvailable", ByteSize.Format(a.Memory.AvailablePhysicalBytes), ByteSize.Format(a.Memory.TotalPhysicalBytes))
        : "";

    public string MemoryTooltip => _analysis is { Memory.IsKnown: true } a
        ? Tr.Format("Main_MemoryTooltip", ByteSize.Format(a.Memory.UsedPhysicalBytes), ByteSize.Format(a.Memory.AvailablePhysicalBytes),
            a.Memory.CachedBytes is { } c ? ByteSize.Format(c) : "–")
        : "";

    public string NewSinceText => _analysis?.HasUsableBaseline == true
        ? Tr.Format("Main_NewApps", _analysis.NewAppCount)
        : "–";

    public string NewSinceHint
    {
        get
        {
            if (_analysis?.HasUsableBaseline != true)
            {
                return Tr.Get("Main_NewSinceHint");
            }

            var text = ByteSize.Format(_analysis.FreeableBytes);
            return _analysis.Comparison is { } c && c.ClosedSinceBaseline.Count > 0
                ? text + " · " + Tr.Format("Main_ClosedSince", c.ClosedSinceBaseline.Count)
                : text;
        }
    }

    /// <summary>Expected effect of closing the currently selected apps.</summary>
    public ImpactEstimate? Impact => _analysis is null ? null : ImpactEstimate.Compute(_analysis, SelectedItems());

    public ImpactLevel ImpactLevel => Impact?.Level ?? ImpactLevel.Unknown;

    public string ImpactText => Tr.Get("Impact_" + ImpactLevel);

    public string ImpactHint => Impact is { Level: not ImpactLevel.Unknown } i
        ? Tr.Format("Impact_Hint", ByteSize.Format(i.EstimatedReclaimBytes), i.AppsToClose)
        : Tr.Get("Main_NewSinceHint");

    public string ImpactTooltip => Impact is { } i ? ImpactFormatter.Describe(i) : "";

    // ---- Environments -------------------------------------------------------------------------

    public bool HasEnvironments => Environments.Count > 0;

    private void UpdateEnvironments(Analysis analysis)
    {
        Environments.Clear();
        foreach (var e in EnvironmentDetector.Detect(analysis.Snapshot).Where(e => e.IsRunning))
        {
            Environments.Add(new EnvironmentCardViewModel(e, StopEnvironmentAsync, () => !IsBusy));
        }

        OnPropertyChanged(nameof(HasEnvironments));
    }

    /// <summary>Stops WSL or Docker with its own command, after showing exactly what will stop.</summary>
    private async Task StopEnvironmentAsync(EnvironmentKind kind)
    {
        IsBusy = true;
        Status = Tr.Get("Env_Checking");
        try
        {
            var details = await _environments.InspectAsync(kind);
            if (!details.IsRunning)
            {
                _dialogs.Inform(details.Name, Tr.Format("Env_Result_NotRunning", details.Name));
                return;
            }

            if (!details.CanStop)
            {
                _dialogs.Inform(details.Name, details.CannotStopReason ?? "");
                return;
            }

            var title = Tr.Get(kind == EnvironmentKind.Wsl ? "Env_Wsl_Confirm_Title" : "Env_Docker_Confirm_Title");
            if (!_dialogs.Confirm(title, details.Warning, dangerous: true))
            {
                return;
            }

            var confirmation = EnvironmentStopConfirmation.FromUserConfirmation(details, DateTime.UtcNow);
            Status = Tr.Format("Env_Stopping", details.Name);
            _log.LogInformation("User confirmed stopping {Environment}", details.Name);
            var result = await _environments.StopAsync(confirmation);
            _dialogs.Inform(details.Name, result.Message);
        }
        finally
        {
            Status = "";
            IsBusy = false;
        }

        await RefreshAsync();
    }

    // ---- Banner ------------------------------------------------------------------------------

    public string? Banner
    {
        get
        {
            if (_analysis is null)
            {
                return null;
            }

            if (_analysis.Profile is null)
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

    private IReadOnlyList<PlanItem> SelectedItems() => _allCandidates.Where(c => c.IsSelected && c.IsSelectable).Select(c => c.Item).ToList();

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

    public string RestoreButtonText
    {
        get
        {
            var profile = _engine.CurrentProfile;
            var count = SelectedCount;
            if (profile is not null && _engine.Profiles.Profiles.Count > 1)
            {
                return count > 0 ? Tr.Format("Main_RestoreToCount", profile.Name, count) : Tr.Format("Main_RestoreTo", profile.Name);
            }

            return count > 0 ? Tr.Format("Main_RestoreCount", count) : Tr.Get("Main_Restore");
        }
    }

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

    private async Task SafeRefresh()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Report(ex);
        }
    }

    private void Apply(Analysis analysis)
    {
        _analysis = analysis;
        _drift = analysis.Drift(DateTime.UtcNow);
        var plan = analysis.Plan;
        _allCandidates = Rows(plan?.Candidates);
        _allKept = Rows(plan?.Kept);
        _allProtected = Rows(plan?.Protected);
        _selection.Prune(_allCandidates.Select(c => c.Id));
        UpdateEnvironments(analysis);
        ApplyView();
        OnPropertyChanged(string.Empty);
    }

    private List<AppRowViewModel> Rows(IReadOnlyList<PlanItem>? items) =>
        (items ?? []).Select(i => new AppRowViewModel(i, _selection, RaiseSelection, SetRule, GrowthOf(i))).ToList();

    private GrowthAssessment? GrowthOf(PlanItem item) =>
        _engine.Tracker.For(item.Group.Id) is { } series ? MemoryTracker.Assess(series) : null;

    private void RaiseSelection()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(RestoreButtonText));
        OnPropertyChanged(nameof(Impact));
        OnPropertyChanged(nameof(ImpactLevel));
        OnPropertyChanged(nameof(ImpactText));
        OnPropertyChanged(nameof(ImpactHint));
        OnPropertyChanged(nameof(ImpactTooltip));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- Actions -----------------------------------------------------------------------------

    private async Task CreateBaselineAsync()
    {
        if (_isRestoring)
        {
            return; // not while apps are being closed
        }

        var profile = _engine.CurrentProfile;
        if (profile is not null && !_dialogs.Confirm(Tr.Get("Confirm_ReplaceBaseline_Title"), Tr.Format("Confirm_ReplaceProfile_Text", profile.Name)))
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

    /// <summary>Tray: "Return to baseline" — refreshes, then shows the usual preview (nothing happens without it).</summary>
    public async Task ReturnFromTrayAsync()
    {
        // The window may have just started its own first analysis; wait for it, then refresh.
        while (IsBusy)
        {
            await Task.Delay(100);
        }

        await RefreshAsync();
        if (SelectedCount == 0)
        {
            _dialogs.Inform(Tr.Get("App_Name"), _analysis?.HasUsableBaseline == true && _allCandidates.Count > 0
                ? Tr.Get("Tray_NothingSelected")
                : Tr.Get("Main_NothingNew"));
            return;
        }

        await RestoreAsync();
    }

    /// <summary>Tray: capture the selected profile (asks before replacing it).</summary>
    public async Task CaptureFromTrayAsync()
    {
        while (IsBusy)
        {
            await Task.Delay(100);
        }

        await CreateBaselineAsync();
    }

    public void ShowOperationRunningNotice() => _dialogs.Inform(Tr.Get("App_Name"), Tr.Get("Exit_OperationRunning"));

    private async Task RestoreAsync()
    {
        // Never two previews/operations at once (e.g. the tray menu while one is open).
        if (_analysis is null || _isRestoring)
        {
            return;
        }

        _isRestoring = true;
        try
        {
            await RestoreCoreAsync();
        }
        finally
        {
            _isRestoring = false;
        }
    }

    private async Task RestoreCoreAsync()
    {
        if (_analysis is null)
        {
            return;
        }

        var selected = SelectedItems();
        if (selected.Count == 0)
        {
            return;
        }

        var preview = new PreviewViewModel(_engine, _analysis, selected, _environments);
        if (!_dialogs.ShowPreview(preview))
        {
            return;
        }

        var stops = preview.ConfirmEnvironmentStops(_dialogs);
        if (stops is null)
        {
            return; // The user declined the environment warning; nothing happens.
        }

        _log.LogInformation("User confirmed closing {Count} app(s) and stopping {Environments} environment(s)", selected.Count, stops.Count);
        var before = SessionStatistics.From(_analysis);
        var operation = new OperationViewModel(_engine, _engine.StartOperation(_analysis, selected), before,
            async () => SessionStatistics.From(await Task.Run(_engine.Analyze)), _deepCleanup, _dialogs, _analysis.Profile, stops, _environments);

        _isOperationRunning = true;
        try
        {
            _dialogs.ShowOperation(operation);
        }
        finally
        {
            _isOperationRunning = false;
        }

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
        _ = SafeRefresh();
    }

    /// <summary>Sets (or clears) the standing rule of an app from its context menu.</summary>
    private void SetRule(AppRowViewModel row, AppRuleKind? kind)
    {
        _engine.UpdateSettings(s => s.WithRule(row.Item.Group.AppKey, row.Name, kind, DateTime.UtcNow));
        _selection.Clear();
        Status = kind is { } k ? Tr.Format("Status_RuleSet", row.Name, Tr.Get("Rule_" + k)) : Tr.Format("Status_RuleCleared", row.Name);
        _ = SafeRefresh();
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

    private async Task OpenProfilesAsync()
    {
        _dialogs.ShowProfiles(new ProfilesViewModel(_engine, _dialogs, () => _analysis));
        _selection.Clear();
        await RefreshAsync();
    }

    /// <summary>
    /// Runs on the UI thread, always deferred: profile changes can be raised while a ComboBox is
    /// still committing its selection, and the list must not be rebuilt underneath it.
    /// </summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private static string Signed(int value) => (value >= 0 ? "+" : "−") + Math.Abs(value).ToString(Loc.Culture);

    public static string Duration(TimeSpan span) => span.TotalMinutes switch
    {
        < 1 => Tr.Get("Time_LessThanMinute"),
        < 60 => Tr.Format("Duration_Minutes", (int)span.TotalMinutes),
        < 60 * 24 => Tr.Format("Duration_HoursMinutes", (int)span.TotalHours, span.Minutes),
        _ => Tr.Format("Duration_DaysHours", (int)span.TotalDays, span.Hours),
    };

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

/// <summary>An entry of the profile picker.</summary>
public sealed record ProfileOption(Guid Id, string Name, bool IsDefault)
{
    public string DisplayName => IsDefault ? Name + " ★" : Name;

    public override string ToString() => DisplayName;
}

/// <summary>A running WSL or Docker environment on the dashboard, with its own stop action.</summary>
public sealed class EnvironmentCardViewModel
{
    public EnvironmentCardViewModel(EnvironmentPresence presence, Func<EnvironmentKind, Task> stop, Func<bool> canStop)
    {
        Presence = presence;
        StopCommand = new AsyncRelayCommand(() => stop(presence.Kind), canStop);
    }

    public EnvironmentPresence Presence { get; }

    public string Name => Presence.Name;

    public string AutomationId => "Env" + Presence.Kind;

    public string MemoryText => Presence.MemoryBytes > 0 ? ByteSize.Format(Presence.MemoryBytes) : "";

    public string ActionText => Tr.Get(Presence.Kind == EnvironmentKind.Wsl ? "Env_Wsl_Stop" : "Env_Docker_Stop");

    public string Hint => Tr.Get(Presence.Kind == EnvironmentKind.Wsl ? "Env_Wsl_Hint" : "Env_Docker_Hint");

    public ICommand StopCommand { get; }
}

/// <summary>Texts for an <see cref="ImpactEstimate"/>.</summary>
public static class ImpactFormatter
{
    public static string Describe(ImpactEstimate impact)
    {
        var b = impact.Breakdown;
        var sb = new StringBuilder();
        sb.AppendLine(Tr.Format("Impact_Line_InUse", ByteSize.Format(b.InUseBytes)));
        sb.AppendLine(Tr.Format("Impact_Line_NewApps", ByteSize.Format(b.NewAppsBytes)));
        sb.AppendLine(Tr.Format("Impact_Line_ProfileApps", ByteSize.Format(b.ProfileAppsBytes)));
        sb.AppendLine(Tr.Format("Impact_Line_System", ByteSize.Format(b.SystemAndOtherBytes)));
        if (b.CacheBytes is { } cache)
        {
            sb.AppendLine(Tr.Format("Impact_Line_Cache", ByteSize.Format(cache)));
        }

        sb.AppendLine();
        sb.AppendLine(Tr.Format("Impact_Line_Estimate", ByteSize.Format(impact.EstimatedReclaimBytes)));
        sb.AppendLine(Tr.Format("Impact_Line_Level", Tr.Get("Impact_" + impact.Level)));
        foreach (var e in impact.Explanations)
        {
            sb.AppendLine();
            sb.Append(e);
        }

        return sb.ToString().TrimEnd();
    }
}
