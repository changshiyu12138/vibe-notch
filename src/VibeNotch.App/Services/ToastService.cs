using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace VibeNotch;

/// <summary>
/// Windows 11 silently drops the legacy NotifyIcon balloon tips this app used
/// before, so completion / permission alerts go through the real toast
/// pipeline. An unpackaged Win32 app may only send toasts when a Start Menu
/// shortcut carries its AppUserModelID, so that shortcut is written (or
/// repaired) once at startup. Toast clicks come back through protocol
/// activation, since a COM activator would need its own CLSID registration.
/// </summary>
public static class ToastService
{
    public const string Aumid = "VibeNotch.Desktop";
    public const string Protocol = "vibe-notch";
    public const string LaunchUri = Protocol + ":focus";
    public const string ActivateMessage = "VibeNotch.ToastActivate";

    private const string ShortcutName = "Vibe Notch.lnk";

    private static bool _ready;

    public static void Initialize()
    {
        try
        {
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(Aumid);
            _ready = true;
        }
        catch (Exception ex)
        {
            Log.Write("toast init failed: " + ex.Message);
        }

        try
        {
            EnsureShortcut();
        }
        catch (Exception ex)
        {
            // A .lnk the shell has mapped can refuse the rewrite; toasts still
            // work as long as an earlier run registered the AUMID.
            Log.Write("toast shortcut failed: " + ex.Message);
        }

        try
        {
            EnsureProtocol();
        }
        catch (Exception ex)
        {
            Log.Write("toast protocol failed: " + ex.Message);
        }

        try
        {
            CleanupLegacy();
        }
        catch (Exception ex)
        {
            Log.Write("toast legacy cleanup failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The app was called QoderVibeNotch before the rename; drop the old start
    /// menu entry and protocol registration so only one of each survives.
    /// </summary>
    private static void CleanupLegacy()
    {
        var legacyShortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "Qoder Vibe Notch.lnk");
        if (File.Exists(legacyShortcut))
        {
            File.Delete(legacyShortcut);
            Log.Write("toast legacy shortcut removed");
        }

        const string legacyProtocol = @"Software\Classes\qoder-vibe-notch";
        using (var key = Registry.CurrentUser.OpenSubKey(legacyProtocol))
        {
            if (key == null) return;
        }

        Registry.CurrentUser.DeleteSubKeyTree(legacyProtocol, throwOnMissingSubKey: false);
        Log.Write("toast legacy protocol removed");
    }

    public static bool Show(string title, string body)
    {
        if (!_ready) return false;

        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast launch=\"{LaunchUri}\" activationType=\"protocol\" duration=\"short\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{SecurityElement.Escape(title)}</text>" +
                $"<text>{SecurityElement.Escape(body)}</text>" +
                "</binding></visual>" +
                "<audio silent=\"true\"/>" +
                "</toast>");

            ToastNotificationManager.CreateToastNotifier(Aumid).Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("toast show failed: " + ex.Message);
            return false;
        }
    }

    // A second copy started by the protocol handler cannot show an island, so it
    // pokes the running instance and exits.
    public static bool ForwardActivation()
    {
        try
        {
            var hwnd = NativeMethods.FindWindow(null, "Vibe Notch");
            if (hwnd == IntPtr.Zero) return false;

            var msg = NativeMethods.RegisterWindowMessage(ActivateMessage);
            if (msg == 0) return false;

            return NativeMethods.PostMessage(hwnd, msg, UIntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Log.Write("toast forward failed: " + ex.Message);
            return false;
        }
    }

    private static void EnsureProtocol()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Protocol);
        if (key == null) throw new InvalidOperationException("cannot open protocol key");

        key.SetValue(null, "URL:Vibe Notch");
        key.SetValue("URL Protocol", "");

        using var command = key.CreateSubKey(@"shell\open\command");
        var expected = $"\"{exe}\" \"%1\"";
        if (command.GetValue(null) as string == expected) return;

        command.SetValue(null, expected);
        Log.Write("toast protocol registered: " + expected);
    }

    private static void EnsureShortcut()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var path = Path.Combine(programs, ShortcutName);
        Directory.CreateDirectory(programs);

        var exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) throw new InvalidOperationException("no process path");

        if (File.Exists(path) && ShortcutIsCurrent(path, exe)) return;

        // Read and write through separate ShellLink objects: a link that was
        // IPersistFile.Load-ed stays memory-mapped, and saving over it in the
        // same object fails with STG_E_ACCESSDENIED.
        object? com = null;
        try
        {
            com = new ShellLink();
            var link = (IShellLinkW)com;
            var persist = (IPersistFile)com;
            var store = (IPropertyStore)com;
            var key = PkeyAppUserModelId;

            link.SetPath(exe);
            link.SetWorkingDirectory(Path.GetDirectoryName(exe) ?? "");
            link.SetIconLocation(exe, 0);
            link.SetDescription("Vibe Notch - floating island for AI coding sessions");

            var value = new PropVariant { vt = VtLpwstr, data = Marshal.StringToCoTaskMemUni(Aumid) };
            try
            {
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                Marshal.FreeCoTaskMem(value.data);
            }

            try
            {
                persist.Save(path, true);
            }
            catch
            {
                // The installer may have just written the shortcut through
                // WScript.Shell, which keeps the file mapped for a moment.
                File.Delete(path);
                persist.Save(path, true);
            }

            Log.Write("toast shortcut registered: " + path);
        }
        finally
        {
            if (com != null) Marshal.ReleaseComObject(com);
        }
    }

    private static bool ShortcutIsCurrent(string path, string exe)
    {
        object? com = null;
        try
        {
            com = new ShellLink();
            var link = (IShellLinkW)com;
            var persist = (IPersistFile)com;
            var store = (IPropertyStore)com;

            if (!TryLoad(persist, path)) return false;

            var key = PkeyAppUserModelId;
            var hasAumid = store.GetValue(ref key, out var existing) == 0 &&
                existing.vt == VtLpwstr &&
                Marshal.PtrToStringUni(existing.data) == Aumid;
            if (hasAumid) PropVariantClear(ref existing);
            return hasAumid && GetTarget(link) == exe;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (com != null) Marshal.ReleaseComObject(com);
        }
    }

    private static bool TryLoad(IPersistFile persist, string path)
    {
        try
        {
            persist.Load(path, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetTarget(IShellLinkW link)
    {
        var buffer = new StringBuilder(1024);
        return link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, 0) == 0 ? buffer.ToString() : "";
    }

    private const ushort VtLpwstr = 31;

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

    private static readonly PropertyKey PkeyAppUserModelId = new()
    {
        FormatId = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        PropertyId = 5
    };

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int max, IntPtr findData, uint flags);
        void GetIDList(out IntPtr pidl);
        void SetIDList(IntPtr pidl);
        [PreserveSig] int GetDescription(IntPtr name, int max);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory(IntPtr dir, int max);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        [PreserveSig] int GetArguments(IntPtr args, int max);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        [PreserveSig] int GetIconLocation(IntPtr path, int max, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data;
        public IntPtr padding;
    }
}
