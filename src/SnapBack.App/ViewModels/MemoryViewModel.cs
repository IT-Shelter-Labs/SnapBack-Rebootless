using System.Collections.ObjectModel;
using System.Windows.Input;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core;
using SnapBack.Core.Localization;
using SnapBack.Core.Memory;
using SnapBack.Core.Model;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

/// <summary>
/// Shows what memory is really used versus reusable cache, and offers the optional Deep Memory
/// Cleanup as a separate, explicitly confirmed action.
/// </summary>
public sealed class MemoryViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly DeepCleanupService _service;
    private readonly IDialogService _dialogs;
    private MemoryStatus _memory = MemoryStatus.Unknown;
    private DeepCleanupMode _mode;
    private bool _isBusy;
    private string _status = "";

    public MemoryViewModel(SnapBackEngine engine, DeepCleanupService service, IDialogService dialogs)
    {
        _engine = engine;
        _service = service;
        _dialogs = dialogs;
        _mode = engine.Settings.DeepCleanupMode;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !_isBusy);
        RunCleanupCommand = new AsyncRelayCommand(RunCleanupAsync, () => !_isBusy);
    }

    public ObservableCollection<MemoryRowViewModel> Rows { get; } = [];

    public ObservableCollection<ComparisonRowViewModel> ResultRows { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand RunCleanupCommand { get; }

    /// <summary>The report of a cleanup run from this window, if any.</summary>
    public DeepCleanupReport? LastReport { get; private set; }

    public bool IsBusy => _isBusy;

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public bool ModeStandby
    {
        get => _mode == DeepCleanupMode.Standby;
        set
        {
            if (value)
            {
                SetMode(DeepCleanupMode.Standby);
            }
        }
    }

    public bool ModeLowPriority
    {
        get => _mode == DeepCleanupMode.LowPriorityStandby;
        set
        {
            if (value)
            {
                SetMode(DeepCleanupMode.LowPriorityStandby);
            }
        }
    }

    public string AdviceTitle => MemoryAdvisor.Advise(_memory).Title;

    public string AdviceText => MemoryAdvisor.Advise(_memory).Text;

    public MemoryPressure Pressure => MemoryAdvisor.Assess(_memory);

    public string ElevationNote => Tr.Get(_service.PromptsForElevation ? "Memory_UacNote" : "Memory_AlreadyElevated");

    public string? ResultText => LastReport is { } r
        ? r.Outcome.Succeeded
            ? Tr.Format("Memory_Result", ByteSize.Format(r.CacheReclaimedBytes))
            : r.Outcome.Message
        : null;

    public async Task RefreshAsync()
    {
        _isBusy = true;
        try
        {
            _memory = await Task.Run(() => _engine.ReadMemory());
            Rebuild();
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void Rebuild()
    {
        Rows.Clear();
        var m = _memory;
        if (m.IsKnown)
        {
            Rows.Add(new("Cmp_Total", ByteSize.Format(m.TotalPhysicalBytes), null));
            Rows.Add(new("Cmp_InUse", ByteSize.Format(m.UsedPhysicalBytes), Percent(m.UsedPercent)));
            Rows.Add(new("Cmp_Available", ByteSize.Format(m.AvailablePhysicalBytes), Percent(m.AvailablePercent)));
            AddOptional("Cmp_Standby", m.StandbyBytes, "Memory_StandbyHint");
            AddOptional("Cmp_Modified", m.ModifiedBytes, "Memory_ModifiedHint");
            AddOptional("Cmp_Free", m.FreeBytes, null);
            if (m.CommitLimitBytes > 0)
            {
                Rows.Add(new("Cmp_Commit", Tr.Format("Memory_OfLimit", ByteSize.Format(m.CommitTotalBytes), ByteSize.Format(m.CommitLimitBytes)), null));
            }
        }

        OnPropertyChanged(string.Empty);
    }

    private void AddOptional(string key, long? value, string? hintKey)
    {
        if (value is { } v)
        {
            Rows.Add(new(key, ByteSize.Format(v), hintKey is null ? null : Tr.Get(hintKey)));
        }
    }

    private static string Percent(double value) => value.ToString("0", Loc.Culture) + " %";

    private void SetMode(DeepCleanupMode mode)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        _engine.UpdateSettings(s => s with { DeepCleanupMode = mode });
        OnPropertyChanged(nameof(ModeStandby));
        OnPropertyChanged(nameof(ModeLowPriority));
    }

    private async Task RunCleanupAsync()
    {
        var text = Tr.Format("Memory_Confirm_Text", Loc.Get("Deep_Mode_" + _mode));
        if (!_dialogs.Confirm(Tr.Get("Memory_Confirm_Title"), text))
        {
            return;
        }

        _isBusy = true;
        Status = Tr.Get("Memory_Running");
        CommandManager.InvalidateRequerySuggested();
        try
        {
            LastReport = await _service.RunAsync(_mode);
            ResultRows.Clear();
            if (LastReport.Outcome.Succeeded)
            {
                foreach (var row in LastReport.Rows)
                {
                    ResultRows.Add(new ComparisonRowViewModel(row));
                }
            }

            _memory = LastReport.After.IsKnown ? LastReport.After : await Task.Run(() => _engine.ReadMemory());
            Status = "";
            Rebuild();
        }
        finally
        {
            _isBusy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public sealed class MemoryRowViewModel(string labelKey, string value, string? hint)
{
    public string Label => Tr.Get(labelKey);

    public string Value { get; } = value;

    public string? Hint { get; } = hint;
}
