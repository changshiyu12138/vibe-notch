using System.Diagnostics;
using System.Text;

namespace VibeNotch;

public static class QoderFocuser
{
    public static bool OpenOrFocus(string? projectHint = null)
    {
        if (Focus(projectHint)) return true;

        try
        {
            var process = Process.GetProcessesByName("Qoder").FirstOrDefault();
            var exe = process?.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write("open qoder failed: " + ex.Message);
        }

        return false;
    }

    public static bool Focus(string? projectHint = null)
    {
        var windows = new List<(IntPtr Handle, string Title)>();

        try
        {
            var qoderPids = Process.GetProcessesByName("Qoder").Select(p => (uint)p.Id).ToHashSet();

            NativeMethods.EnumWindows((hwnd, _) =>
            {
                if (!NativeMethods.IsWindowVisible(hwnd)) return true;

                NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                if (!qoderPids.Contains(pid)) return true;

                var length = NativeMethods.GetWindowTextLength(hwnd);
                if (length <= 0) return true;

                var buffer = new StringBuilder(length + 1);
                NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
                windows.Add((hwnd, buffer.ToString()));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write("enum windows failed: " + ex.Message);
        }

        var target = FindTarget(windows, projectHint);
        if (target == IntPtr.Zero) return false;

        ForceForeground(target);
        return true;
    }

    private static IntPtr FindTarget(List<(IntPtr Handle, string Title)> windows, string? projectHint)
    {
        bool IsPet(string title) => title.Contains("Desktop Pet", StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(projectHint))
        {
            foreach (var window in windows)
            {
                if (!IsPet(window.Title) && window.Title.Contains(projectHint, StringComparison.OrdinalIgnoreCase))
                    return window.Handle;
            }
        }

        foreach (var window in windows)
        {
            if (!IsPet(window.Title)) return window.Handle;
        }

        return windows.Count > 0 ? windows[0].Handle : IntPtr.Zero;
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        try
        {
            var foreground = NativeMethods.GetForegroundWindow();
            var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
            var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

            var attached = false;
            if (foregroundThread != targetThread)
                attached = NativeMethods.AttachThreadInput(foregroundThread, targetThread, true);

            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_MENU, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.SetForegroundWindow(hwnd);

            if (attached) NativeMethods.AttachThreadInput(foregroundThread, targetThread, false);
        }
        catch (Exception ex)
        {
            Log.Write("focus qoder failed: " + ex.Message);
        }
    }
}
