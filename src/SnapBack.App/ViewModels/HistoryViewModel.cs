using System.Collections.ObjectModel;
using System.Windows.Input;
using SnapBack.App.Mvvm;
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
        if (_dialogs.Confirm("Clear history", "Delete the history of past operations?"))
        {
            _engine.ClearHistory();
            Load();
        }
    }
}

public sealed class HistoryEntryViewModel(OperationRecord record)
{
    public OperationRecord Record { get; } = record;

    public string When => Record.StartedAtUtc.ToLocalTime().ToString("g");

    public string Kind => Record.Kind == OperationKind.Force ? "Force-terminate" : "Return to baseline";

    public string Summary => $"{Record.Closed} of {Record.Requested} closed · ≈ {ByteSize.Format(Record.MemoryFreedBytes)}"
        + (Record.StillRunning > 0 ? $" · {Record.StillRunning} still running" : "");

    public IReadOnlyList<OperationRecordItem> Items => Record.Items;

    public string MemoryText => Record.AvailableBeforeBytes > 0
        ? $"Available memory: {ByteSize.Format(Record.AvailableBeforeBytes)} → {ByteSize.Format(Record.AvailableAfterBytes)}"
        : "";
}
