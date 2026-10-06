using System.Collections.ObjectModel;
using System.Windows.Input;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core.Localization;
using SnapBack.Core;
using SnapBack.Core.Execution;
using SnapBack.Core.Memory;
using SnapBack.Core.Persistence;
using SnapBack.Core.Text;

namespace SnapBack.App.ViewModels;

public sealed class HistoryViewModel : ObservableObject
{
    private readonly SnapBackEngine _engine;
    private readonly IDialogService _dialogs;
    private HistoryEntryViewModel? _selected;

    public HistoryViewModel(SnapBackEngine engine, IDialogService dialogs)
    {
        _engine = engine;
        _dialogs = dialogs;
        Load();
        ClearCommand = new RelayCommand(Clear, () => Entries.Count > 0);
    }

    public ObservableCollection<HistoryEntryViewModel> Entries { get; } = [];

    public ICommand ClearCommand { get; }

    public HistoryEntryViewModel? Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }

    public bool IsEmpty => Entries.Count == 0;

    private void Load()
    {
        Entries.Clear();
        foreach (var r in _engine.LoadHistory())
        {
            Entries.Add(new HistoryEntryViewModel(r));
        }

        Selected = Entries.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Clear()
    {
        if (_dialogs.Confirm(Tr.Get("Confirm_ClearHistory_Title"), Tr.Get("Confirm_ClearHistory_Text")))
        {
            _engine.ClearHistory();
            Load();
        }
    }
}

public sealed class HistoryEntryViewModel(OperationRecord record)
{
    public OperationRecord Record { get; } = record;

    public string When => Record.StartedAtUtc.ToLocalTime().ToString("g", Loc.Culture);

    public string Kind => Record.ProfileName is { } profile
        ? Tr.Format("History_ReturnTo", profile)
        : Tr.Get(Record.Kind == OperationKind.Force ? "History_Force" : "History_Restore");

    public string Summary => Tr.Format("History_Summary", Record.Closed, Record.Requested, ByteSize.Format(Record.MemoryFreedBytes))
        + (Record.StillRunning > 0 ? Tr.Format("History_StillRunning", Record.StillRunning) : "");

    public IReadOnlyList<OperationRecordItem> Items => Record.Items;

    /// <summary>Before → after lines; entries of older versions only have "available".</summary>
    public IReadOnlyList<ComparisonRowViewModel> Comparison
    {
        get
        {
            var r = Record;
            var rows = new List<ComparisonRow>();
            if (r.AppsBefore is not null || r.AppsAfter is not null)
            {
                rows.Add(new("Cmp_Apps", ComparisonValueKind.Count, r.AppsBefore, r.AppsAfter));
                rows.Add(new("Cmp_Processes", ComparisonValueKind.Count, r.ProcessesBefore, r.ProcessesAfter));
                rows.Add(new("Cmp_InUse", ComparisonValueKind.Bytes, r.InUseBeforeBytes, r.InUseAfterBytes));
            }

            if (r.AvailableBeforeBytes > 0 || r.AvailableAfterBytes > 0)
            {
                rows.Add(new("Cmp_Available", ComparisonValueKind.Bytes, r.AvailableBeforeBytes, r.AvailableAfterBytes));
            }

            return rows.Select(x => new ComparisonRowViewModel(x)).ToList();
        }
    }

    /// <summary>Facts of the operation, one per line. Memory released by apps and by Deep Cleanup stay separate.</summary>
    public IReadOnlyList<string> Facts
    {
        get
        {
            var r = Record;
            var list = new List<string>
            {
                Tr.Format("History_Released", ByteSize.Format(r.MemoryFreedBytes)),
                Tr.Format("History_AppsClosed", r.Closed, r.Requested),
                Tr.Format("History_Forced", r.ForceTerminations),
                r.DeepCleanupUsed
                    ? Tr.Format("History_DeepCleanupUsed", ByteSize.Format(r.DeepCleanupReclaimedBytes ?? 0))
                    : Tr.Get("History_DeepCleanupNotUsed"),
            };

            if (r.EnvironmentsStopped.Count > 0)
            {
                list.Add(Tr.Format("History_Environments", string.Join(", ", r.EnvironmentsStopped)));
            }

            list.Add(Tr.Format("History_Duration", MainViewModel.Duration(r.Duration)));
            return list;
        }
    }
}
