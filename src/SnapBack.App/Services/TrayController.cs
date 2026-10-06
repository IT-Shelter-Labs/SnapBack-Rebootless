using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using SnapBack.App.Localization;
using SnapBack.Core;
using SnapBack.Windows;

namespace SnapBack.App.Services;

/// <summary>
/// SnapBack's notification-area icon: a hidden owner window, the shell icon, its tooltip, its
/// menu and notifications. Every menu action is forwarded to the application; nothing here closes
/// anything.
/// </summary>
public sealed class TrayController : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly SnapBackEngine _engine;
    private readonly ILogger _log;
    private readonly HwndSource _window;
    private readonly ShellNotifyIcon _icon;
    private ContextMenu? _menu;
    private bool _disposed;

    public TrayController(SnapBackEngine engine, string iconSourcePath, ILogger log)
    {
        _engine = engine;
        _log = log;

        // A hidden top-level window (not message-only: those miss the TaskbarCreated broadcast).
        _window = new HwndSource(new HwndSourceParameters("SnapBackTray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = WS_POPUP,
            ExtendedWindowStyle = WS_EX_TOOLWINDOW,
        });
        _window.AddHook(WndProc);
        _icon = new ShellNotifyIcon(_window.Handle, iconSourcePath);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? ReturnRequested;

    public event EventHandler? UpdateBaselineRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler<Guid>? ProfileSelected;

    public bool IsShown => _icon.IsShown;

    public string Tooltip { get; private set; } = "SnapBack";

    public bool Show()
    {
        var shown = _icon.Show(Tooltip);
        _log.LogInformation("Tray icon shown: {Shown}", shown);
        return shown;
    }

    public void SetTooltip(string text)
    {
        Tooltip = text.Length > 127 ? text[..127] : text;
        _icon.SetTooltip(Tooltip);
    }

    public bool Notify(string title, string text)
    {
        var shown = _icon.ShowNotification(title, text);
        _log.LogInformation("Notification shown: {Shown} ({Title})", shown, title);
        return shown;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_menu is not null)
        {
            _menu.IsOpen = false;
        }

        _icon.Dispose();
        _window.RemoveHook(WndProc);
        _window.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)ShellNotifyIcon.CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case ShellNotifyIcon.NIN_SELECT:
                case ShellNotifyIcon.NIN_KEYSELECT:
                case ShellNotifyIcon.NIN_BALLOONUSERCLICK:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case ShellNotifyIcon.WM_CONTEXTMENU:
                    ShowMenu();
                    break;
            }

            handled = true;
        }
        else if (msg == (int)ShellNotifyIcon.TaskbarCreatedMessage && ShellNotifyIcon.TaskbarCreatedMessage != 0)
        {
            // Explorer restarted: the icon must be added again.
            _log.LogInformation("Taskbar re-created; tray icon re-added: {Shown}", _icon.Recreate());
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (_menu is { IsOpen: true })
        {
            return;
        }

        _menu = BuildMenu();
        _icon.PrepareForMenu();
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    /// <summary>Built each time it opens, so it follows the current language and profiles.</summary>
    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu { FontFamily = Tr.Instance.UiFont };
        var profiles = _engine.Profiles;
        var current = profiles.Current;

        menu.Items.Add(Item(Tr.Get("Tray_Open"), OpenRequested, bold: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(current is null || profiles.Profiles.Count <= 1
            ? Tr.Get("Tray_Return")
            : Tr.Format("Tray_ReturnTo", current.Name), ReturnRequested, enabled: current is not null));

        var choose = new MenuItem { Header = Tr.Get("Tray_ChooseProfile"), IsEnabled = profiles.Profiles.Count > 0 };
        foreach (var p in profiles.Profiles)
        {
            var id = p.Id;
            var item = new MenuItem { Header = p.Name, IsCheckable = true, IsChecked = p.Id == current?.Id };
            item.Click += (_, _) => ProfileSelected?.Invoke(this, id);
            choose.Items.Add(item);
        }

        menu.Items.Add(choose);
        menu.Items.Add(Item(current is null ? Tr.Get("Tray_CreateBaseline") : Tr.Format("Tray_UpdateProfile", current.Name), UpdateBaselineRequested));
        menu.Items.Add(Item(Tr.Get("Tray_Refresh"), RefreshRequested));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Tr.Get("Tray_Exit"), ExitRequested));
        return menu;
    }

    private MenuItem Item(string header, EventHandler? handler, bool bold = false, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (bold)
        {
            item.FontWeight = System.Windows.FontWeights.SemiBold;
        }

        item.Click += (_, _) => handler?.Invoke(this, EventArgs.Empty);
        return item;
    }
}
