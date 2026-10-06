using SnapBack.Windows.Native;

namespace SnapBack.Windows;

/// <summary>
/// The notification-area ("tray") icon, via the documented <c>Shell_NotifyIcon</c> API. The owner
/// window receives <see cref="CallbackMessage"/> with the event in the low word of lParam
/// (NOTIFYICON_VERSION_4).
/// </summary>
public sealed unsafe class ShellNotifyIcon : IDisposable
{
    public const int NIN_SELECT = 0x0400;
    public const int NIN_KEYSELECT = 0x0401;
    public const int NIN_BALLOONUSERCLICK = 0x0405;
    public const int WM_CONTEXTMENU = 0x007B;
    public const int WM_LBUTTONDBLCLK = 0x0203;

    /// <summary>WM_APP + 1: the message the shell sends to the owner window.</summary>
    public const uint CallbackMessage = 0x8001;

    private const uint IconId = 1;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private string _tooltip = "";
    private bool _added;

    public ShellNotifyIcon(IntPtr ownerWindow, string iconSourcePath)
    {
        _hwnd = ownerWindow;
        _icon = LoadSmallIcon(iconSourcePath);
    }

    /// <summary>Broadcast when Explorer (re)starts; the icon must then be added again.</summary>
    public static uint TaskbarCreatedMessage { get; } = User32Shell.RegisterWindowMessage("TaskbarCreated");

    public bool IsShown => _added;

    /// <summary>Adds the icon. Returns false when there is no taskbar (e.g. Explorer is not running).</summary>
    public bool Show(string tooltip)
    {
        _tooltip = tooltip;
        var data = Data(Shell32.NIF_MESSAGE | Shell32.NIF_ICON | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        _added = Shell32.Shell_NotifyIcon(Shell32.NIM_ADD, &data);
        if (_added)
        {
            data.uVersion = Shell32.NOTIFYICON_VERSION_4;
            Shell32.Shell_NotifyIcon(Shell32.NIM_SETVERSION, &data);
        }

        return _added;
    }

    /// <summary>Re-adds the icon after Explorer restarted.</summary>
    public bool Recreate()
    {
        _added = false;
        return Show(_tooltip);
    }

    public void SetTooltip(string tooltip)
    {
        _tooltip = tooltip;
        if (!_added)
        {
            return;
        }

        var data = Data(Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, &data);
    }

    /// <summary>
    /// Shows a notification. Windows shows it as a toast, respects Focus Assist / Do Not Disturb,
    /// and (with NIIF_RESPECT_QUIET_TIME) stays quiet during the first hour after a new sign-in.
    /// </summary>
    public bool ShowNotification(string title, string text)
    {
        if (!_added)
        {
            return false;
        }

        var data = Data(Shell32.NIF_INFO | Shell32.NIF_TIP | Shell32.NIF_SHOWTIP);
        Copy(title, data.szInfoTitle, 64);
        Copy(text, data.szInfo, 256);
        data.dwInfoFlags = Shell32.NIIF_INFO | Shell32.NIIF_RESPECT_QUIET_TIME;
        return Shell32.Shell_NotifyIcon(Shell32.NIM_MODIFY, &data);
    }

    public void Hide()
    {
        if (!_added)
        {
            return;
        }

        var data = Data(0);
        Shell32.Shell_NotifyIcon(Shell32.NIM_DELETE, &data);
        _added = false;
    }

    /// <summary>Gives the owner window the foreground so a context menu closes when the user clicks elsewhere.</summary>
    public void PrepareForMenu() => User32Shell.SetForegroundWindow(_hwnd);

    public void Dispose()
    {
        Hide();
        if (_icon != IntPtr.Zero)
        {
            User32Shell.DestroyIcon(_icon);
        }
    }

    private NOTIFYICONDATAW Data(uint flags)
    {
        var data = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        Copy(_tooltip, data.szTip, 128);
        return data;
    }

    /// <summary>Copies a string into a fixed, NUL-terminated buffer, truncating it if necessary.</summary>
    private static void Copy(string value, char* buffer, int capacity)
    {
        var text = value ?? "";
        var length = Math.Min(text.Length, capacity - 1);
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        for (var i = 0; i < length; i++)
        {
            buffer[i] = text[i];
        }

        buffer[length] = '\0';
    }

    private static IntPtr LoadSmallIcon(string path)
    {
        var size = (uint)Math.Clamp(User32Shell.GetSystemMetrics(User32Shell.SM_CXSMICON), 16, 64);
        IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
        var hr = Shell32.SHDefExtractIcon(path, 0, 0, &large, &small, (size << 16) | 32);
        if (large != IntPtr.Zero)
        {
            User32Shell.DestroyIcon(large);
        }

        return hr == 0 ? small : IntPtr.Zero;
    }
}
