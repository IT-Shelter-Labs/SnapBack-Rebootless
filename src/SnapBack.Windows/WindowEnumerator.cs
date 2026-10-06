using System.Runtime.InteropServices;
using SnapBack.Core.Model;
using SnapBack.Windows.Native;

namespace SnapBack.Windows;

internal sealed record WindowInfo(nint Handle, int Pid, bool IsVisible, bool IsMainWindow, bool IsHung, string Title, string ClassName);

/// <summary>
/// Enumerates top-level windows and decides which are "main" windows - the ones Alt+Tab would show,
/// and the ones a close request is sent to.
/// </summary>
internal static unsafe class WindowEnumerator
{
    private static readonly HashSet<string> ConsoleClasses = new(StringComparer.Ordinal) { "ConsoleWindowClass", "PseudoConsoleWindow" };

    /// <summary>Hidden helper windows every GUI thread has; they say nothing about the app.</summary>
    private static readonly HashSet<string> IgnoredHiddenClasses = new(StringComparer.Ordinal) { "IME", "MSCTFIME UI", "ConsoleWindowClass", "PseudoConsoleWindow" };

    public static List<WindowInfo> Enumerate()
    {
        var handles = new List<nint>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            User32.EnumWindows(&Collect, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        var result = new List<WindowInfo>(handles.Count);
        foreach (var hwnd in handles)
        {
            try
            {
                if (Describe(hwnd) is { } info)
                {
                    result.Add(info);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A window can disappear while being inspected; ignore it.
            }
        }

        return result;
    }

    public static Dictionary<int, WindowSummary> Summarize(IEnumerable<WindowInfo> windows)
    {
        var result = new Dictionary<int, WindowSummary>();
        foreach (var byPid in windows.GroupBy(w => w.Pid))
        {
            var main = byPid.Where(w => w.IsMainWindow).ToList();
            var hidden = byPid.Count(w => !w.IsVisible && !IgnoredHiddenClasses.Contains(w.ClassName));
            if (main.Count == 0 && hidden == 0)
            {
                continue;
            }

            result[byPid.Key] = new WindowSummary
            {
                VisibleMainWindows = main.Count,
                HiddenTopLevelWindows = hidden,
                AnyHung = main.Any(w => w.IsHung),
                Titles = main.Select(w => w.Title).Where(t => t.Length > 0).ToArray(),
            };
        }

        return result;
    }

    public static Dictionary<int, WindowSummary> SummarizeAll() => Summarize(Enumerate());

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint state)
    {
        var list = (List<nint>)GCHandle.FromIntPtr(state).Target!;
        list.Add(hwnd);
        return 1;
    }

    private static WindowInfo? Describe(nint hwnd)
    {
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0)
        {
            return null;
        }

        var className = ClassName(hwnd);
        var visible = User32.IsWindowVisible(hwnd);

        // UWP apps: the visible frame belongs to ApplicationFrameHost; attribute it to the app that
        // owns the hosted CoreWindow, so that closing the frame closes the app (like clicking X).
        if (className == "ApplicationFrameWindow")
        {
            if (!visible || IsCloaked(hwnd))
            {
                return null;
            }

            var core = User32.FindWindowEx(hwnd, 0, "Windows.UI.Core.CoreWindow", null);
            if (core != 0)
            {
                User32.GetWindowThreadProcessId(core, out var appPid);
                if (appPid != 0)
                {
                    pid = appPid;
                }
            }

            return new WindowInfo(hwnd, (int)pid, true, true, User32.IsHungAppWindow(hwnd), Title(hwnd), className);
        }

        var isMain = visible && IsMainWindowCandidate(hwnd, className);
        return new WindowInfo(hwnd, (int)pid, visible, isMain, isMain && User32.IsHungAppWindow(hwnd), isMain ? Title(hwnd) : "", className);
    }

    private static bool IsMainWindowCandidate(nint hwnd, string className)
    {
        if (ConsoleClasses.Contains(className) || className == "Windows.UI.Core.CoreWindow")
        {
            // Console windows are never closed with WM_CLOSE: that would kill every program sharing the console.
            return false;
        }

        if (User32.GetWindow(hwnd, User32.GW_OWNER) != 0)
        {
            return false;
        }

        var ex = User32.GetExtendedStyle(hwnd);
        if ((ex & User32.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        return (ex & User32.WS_EX_APPWINDOW) != 0 || Title(hwnd).Length > 0;
    }

    private static bool IsCloaked(nint hwnd) =>
        Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static string Title(nint hwnd)
    {
        const int capacity = 512;
        var buffer = stackalloc char[capacity];
        var len = User32.GetWindowText(hwnd, buffer, capacity);
        return len > 0 ? new string(buffer, 0, len) : "";
    }

    private static string ClassName(nint hwnd)
    {
        const int capacity = 256;
        var buffer = stackalloc char[capacity];
        var len = User32.GetClassName(hwnd, buffer, capacity);
        return len > 0 ? new string(buffer, 0, len) : "";
    }
}
