using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using SnapBack.App.Localization;

namespace SnapBack.App.Services;

/// <summary>Community links opened only when the user activates a hyperlink.</summary>
public static class CommunityLinks
{
    public static Uri Telegram { get; } = new("https://t.me/+ihTcvubt-_BhZTQ6");

    public static Uri GitHub { get; } = new("https://github.com/IT-Shelter-Labs");

    public static void Open(Uri destination, Window owner)
    {
        // Only the two built-in contact URLs can be passed to the system browser.
        if (destination != Telegram && destination != GitHub)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(destination.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(owner, Tr.Format("Contact_OpenFailed", destination.AbsoluteUri),
                Tr.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
