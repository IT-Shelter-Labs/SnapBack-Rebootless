namespace SnapBack.App.ViewModels;

/// <summary>Opens windows and message boxes; implemented by the views.</summary>
public interface IDialogService
{
    bool ShowPreview(PreviewViewModel preview);

    void ShowOperation(OperationViewModel operation);

    bool ShowSettings(SettingsViewModel settings);

    void ShowHistory(HistoryViewModel history);

    void ShowMemory(MemoryViewModel memory);

    bool Confirm(string title, string message, bool dangerous = false);

    void Inform(string title, string message);
}
