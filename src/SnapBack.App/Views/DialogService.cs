using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public sealed class DialogService(Func<Window?> owner) : IDialogService
{
    public bool ShowPreview(PreviewViewModel preview) => new PreviewWindow(preview) { Owner = owner() }.ShowDialog() == true;

    public void ShowOperation(OperationViewModel operation) => new OperationWindow(operation) { Owner = owner() }.ShowDialog();

    public bool ShowSettings(SettingsViewModel settings) => new SettingsWindow(settings) { Owner = owner() }.ShowDialog() == true;

    public void ShowHistory(HistoryViewModel history) => new HistoryWindow(history) { Owner = owner() }.ShowDialog();

    public bool Confirm(string title, string message, bool dangerous = false) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.YesNo,
            dangerous ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public void Inform(string title, string message) =>
        MessageBox.Show(Owner(), message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    private Window Owner() => owner() ?? Application.Current.MainWindow;
}
