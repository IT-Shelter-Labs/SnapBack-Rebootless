using System.Windows;
using Microsoft.Extensions.Logging;

namespace SnapBack.App.Services;

/// <summary>Logs unexpected errors and tells the user without crashing the application.</summary>
public static class ErrorReporter
{
    public static ILogger? Log { get; set; }

    public static string? LogDirectory { get; set; }

    public static void Report(Exception ex, string? context = null)
    {
        Log?.LogError(ex, "Unexpected error {Context}", context ?? "");
        var message = Localization.Tr.Format("Error_Text", ex.Message, LogDirectory ?? "");
        try
        {
            if (Application.Current?.Dispatcher is { } d && !d.HasShutdownStarted)
            {
                d.Invoke(() => MessageBox.Show(Application.Current.MainWindow ?? new Window(), message, Localization.Tr.Get("App_Name"), MessageBoxButton.OK, MessageBoxImage.Error));
            }
        }
        catch (Exception)
        {
            // Never let error reporting itself crash the app.
        }
    }
}
