using System.ComponentModel;
using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class OperationWindow : Window
{
    private readonly OperationViewModel _vm;

    public OperationWindow(OperationViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            try
            {
                await _vm.RunAsync();
            }
            catch (Exception ex)
            {
                Services.ErrorReporter.Report(ex, "closing applications");
            }
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Closing the window while waiting only stops the waiting; nothing is forced.
        if (_vm.IsRunning)
        {
            _vm.CancelIfRunning();
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
