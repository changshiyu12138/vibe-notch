using System.IO;
using System.Text.Json;

namespace VibeNotch;

public sealed class AppSettings
{
    public string? MonitorDeviceName { get; set; }
    public string MonitorMode { get; set; } = "fixed";
    public bool AutoStart { get; set; }
    public bool SoundOnComplete { get; set; } = true;
    public bool SoundOnPermission { get; set; } = true;
    public string SoundName { get; set; } = "crystal";
    public bool NotifyOnComplete { get; set; } = true;
    public bool NotifyOnPermission { get; set; } = true;
    public int AutoHideSeconds { get; set; } = 6;
    public int PermissionTimeoutSeconds { get; set; } = 90;
    public string PermissionOutput { get; set; } = "qoder";
    public List<string> AutoApprove { get; set; } = new();
    public bool HooksInstalled { get; set; }
}

public sealed class SettingsStore
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VibeNotch");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    private const string LegacyDirName = "QoderVibeNotch";

    public AppSettings Value { get; private set; } = new();

    public static SettingsStore Load()
    {
        MigrateLegacyData();

        var store = new SettingsStore();
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) store.Value = loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }

        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Value, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }

    private static void MigrateLegacyData()
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                LegacyDirName);

            if (File.Exists(SettingsPath) || !Directory.Exists(legacyDir)) return;

            Directory.CreateDirectory(DataDir);
            foreach (var name in new[] { "settings.json", "stats.json" })
            {
                var from = Path.Combine(legacyDir, name);
                var to = Path.Combine(DataDir, name);
                if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
            }

            Log.Write("migrated legacy settings from " + legacyDir);
        }
        catch (Exception ex)
        {
            Log.Write("legacy migration failed: " + ex.Message);
        }
    }
}
