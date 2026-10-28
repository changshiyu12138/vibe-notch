using System.IO;

namespace VibeNotch;

public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            var dir = Path.Combine(SettingsStore.DataDir, "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "app.log");
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            lock (Gate)
            {
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // logging must never break the app
        }
    }
}
