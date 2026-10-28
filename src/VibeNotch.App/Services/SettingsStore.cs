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
    public string? UpdateCheckUtc { get; set; }
}

public sealed class SettingsStore
{
    // Roaming data keeps settings outside the Velopack install root, so they
    // survive updates and uninstalls.
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VibeNotch");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    private static readonly string[] LegacyDirNames = { "VibeNotch", "QoderVibeNotch" };

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
            if (File.Exists(SettingsPath)) return;

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // Newest folder first: it provides the settings, older ones only fill gaps.
            foreach (var name in LegacyDirNames)
            {
                var legacyDir = Path.Combine(local, name);
                if (!Directory.Exists(legacyDir)) continue;

                Directory.CreateDirectory(DataDir);
                foreach (var file in new[] { "settings.json", "stats.json" })
                {
                    var from = Path.Combine(legacyDir, file);
                    var to = Path.Combine(DataDir, file);
                    if (File.Exists(from) && !File.Exists(to)) File.Copy(from, to);
                }

                Log.Write("migrated legacy data from " + legacyDir);
            }
        }
        catch (Exception ex)
        {
            Log.Write("legacy migration failed: " + ex.Message);
        }
    }
}
