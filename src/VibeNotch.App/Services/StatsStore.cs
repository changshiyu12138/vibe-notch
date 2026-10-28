using System.IO;
using System.Text.Json;

namespace VibeNotch;

public sealed class DailyStats
{
    public int Tasks { get; set; }
    public int Allowed { get; set; }
    public int Denied { get; set; }
    public int AutoAllowed { get; set; }
}

// Per-day counters so the footer can answer "今天干了多少活". Qoder only ever
// hands us events, never a running total, and transcript parsing is too heavy
// to repeat, so the numbers are accumulated here and persisted.
public sealed class StatsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly Dictionary<string, DailyStats> _days = new();
    private bool _dirty;

    public static string StatsPath => Path.Combine(SettingsStore.DataDir, "stats.json");

    public DailyStats Today => ForDay(DateTime.Now);

    public static StatsStore Load()
    {
        var store = new StatsStore();
        try
        {
            if (File.Exists(StatsPath))
            {
                var days = JsonSerializer.Deserialize<Dictionary<string, DailyStats>>(File.ReadAllText(StatsPath));
                if (days != null)
                {
                    foreach (var pair in days) store._days[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("stats load failed: " + ex.Message);
        }

        return store;
    }

    public void Bump(Action<DailyStats> change)
    {
        change(ForDay(DateTime.Now));
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty) return;

        try
        {
            Directory.CreateDirectory(SettingsStore.DataDir);
            var cutoff = DateTime.Now.AddDays(-14).ToString("yyyy-MM-dd");
            foreach (var key in _days.Keys.Where(k => string.CompareOrdinal(k, cutoff) < 0).ToList())
            {
                _days.Remove(key);
            }

            File.WriteAllText(StatsPath, JsonSerializer.Serialize(_days, Json));
            _dirty = false;
        }
        catch (Exception ex)
        {
            Log.Write("stats save failed: " + ex.Message);
        }
    }

    private DailyStats ForDay(DateTime when)
    {
        var key = when.ToString("yyyy-MM-dd");
        if (_days.TryGetValue(key, out var stats)) return stats;

        stats = new DailyStats();
        _days[key] = stats;
        return stats;
    }
}
