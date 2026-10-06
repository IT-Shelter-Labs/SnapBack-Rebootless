using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class PreviewWindow : Window
{
    public PreviewWindow(PreviewViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            try
            {
                await vm.LoadEnvironmentDetailsAsync();
            }
            catch (Exception ex)
            {
                Services.ErrorReporter.Report(ex);
            }
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
