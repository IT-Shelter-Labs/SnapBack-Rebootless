using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class MemoryWindow : Window
{
    public MemoryWindow(MemoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += async (_, _) =>
        {
            try
            {
                await vm.RefreshAsync();
            }
            catch (Exception ex)
            {
                Services.ErrorReporter.Report(ex);
            }
        };
    }
}
