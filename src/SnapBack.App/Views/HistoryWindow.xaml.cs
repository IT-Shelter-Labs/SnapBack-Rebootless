using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow(HistoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
