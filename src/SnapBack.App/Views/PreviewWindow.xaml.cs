using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class PreviewWindow : Window
{
    public PreviewWindow(PreviewViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
