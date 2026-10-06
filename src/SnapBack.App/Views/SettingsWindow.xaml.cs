using System.Windows;
using System.Windows.Input;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // Commit a text box that still has focus.
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox box)
        {
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        }

        if (_vm.Save())
        {
            DialogResult = true;
        }
    }
}
