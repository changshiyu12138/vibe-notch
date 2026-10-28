using System.Diagnostics;
using System.Text;

namespace VibeNotch;

// Sends keystrokes to the Qoder window without stealing focus: the messages go
// straight into its queue, so whatever app the user is in keeps the keyboard.
public static class QoderRemote
{
    public static bool Interrupt()
    {
        var hwnd = FindQoderWindow();
        if (hwnd == IntPtr.Zero) return false;

        PostKey(hwnd, NativeMethods.VK_ESCAPE);
        Log.Write("remote: escape posted");
        return true;
    }

    public static bool SendPrompt(string text)
    {
        var hwnd = FindQoderWindow();
        if (hwnd == IntPtr.Zero) return false;

        foreach (var ch in text)
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_CHAR, new UIntPtr(ch), IntPtr.Zero);
        }

        PostKey(hwnd, NativeMethods.VK_RETURN);
        Log.Write($"remote: prompt posted ({text.Length} chars)");
        return true;
    }

    // Screen-following needs the Qoder window rectangle; the same lookup as the
    // key injection so "跟随 Qoder 窗口" lands on the right display.
    public static System.Drawing.Rectangle? FocusBounds()
    {
        var hwnd = FindQoderWindow();
        if (hwnd == IntPtr.Zero) return null;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return null;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        return new System.Drawing.Rectangle(rect.Left, rect.Top, width, height);
    }

    private static void PostKey(IntPtr hwnd, byte vk)
    {
        var scan = NativeMethods.MapVirtualKey(vk, NativeMethods.MAPVK_VK_TO_VSC);
        var down = new IntPtr(0x00000001 | (scan << 16));
        var up = new IntPtr(0x00000001 | (scan << 16) | 0xC0000000);

        NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, new UIntPtr(vk), down);
        NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYUP, new UIntPtr(vk), up);
    }

    private static IntPtr FindQoderWindow()
    {
        var target = IntPtr.Zero;

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
                var title = buffer.ToString();
                if (title.Contains("Desktop Pet", StringComparison.OrdinalIgnoreCase)) return true;

                target = hwnd;
                return false;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write("remote: find window failed: " + ex.Message);
        }

        return target;
    }
}
