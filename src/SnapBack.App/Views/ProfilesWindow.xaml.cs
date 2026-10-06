using System.Windows;
using SnapBack.App.ViewModels;

namespace SnapBack.App.Views;

public partial class ProfilesWindow : Window
{
    public ProfilesWindow(ProfilesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
