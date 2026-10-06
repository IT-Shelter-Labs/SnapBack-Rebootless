using System.Collections.ObjectModel;
using System.Windows.Input;
using SnapBack.App.Localization;
using SnapBack.App.Mvvm;
using SnapBack.Core.Localization;
using SnapBack.Core;
using SnapBack.Core.Execution;
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

    public string Kind => Tr.Get(Record.Kind == OperationKind.Force ? "History_Force" : "History_Restore");

    public string Summary => Tr.Format("History_Summary", Record.Closed, Record.Requested, ByteSize.Format(Record.MemoryFreedBytes))
        + (Record.StillRunning > 0 ? Tr.Format("History_StillRunning", Record.StillRunning) : "");

    public IReadOnlyList<OperationRecordItem> Items => Record.Items;

    public string MemoryText => Record.AvailableBeforeBytes > 0
        ? Tr.Format("History_Available", ByteSize.Format(Record.AvailableBeforeBytes), ByteSize.Format(Record.AvailableAfterBytes))
        : "";
}
