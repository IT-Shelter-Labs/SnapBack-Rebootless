using System.Windows;
using System.Windows.Input;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (_, _) => await SafeRefresh(() => _vm.RefreshAsync());
        Activated += async (_, _) => await SafeRefresh(() => _vm.RefreshIfStaleAsync());
        InputBindings.Add(new KeyBinding(vm.RefreshCommand, Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new Mvvm.RelayCommand(() => SearchBox.Focus()), Key.F, ModifierKeys.Control));
    }

    private static async Task SafeRefresh(Func<Task> refresh)
    {
        try
        {
            await refresh();
        }
        catch (Exception ex)
        {
            Services.ErrorReporter.Report(ex, "analyzing running applications");
        }
    }

    /// <summary>Brings the window to the front (second launch of SnapBack).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }
}
