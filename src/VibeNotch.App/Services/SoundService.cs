using System.IO;
using System.Media;

namespace VibeNotch;

public static class SoundService
{
    public sealed record Option(string Key, string Label);

    public static readonly Option[] Options =
    {
        new("crystal", "清铃"),
        new("drop", "水滴"),
        new("chord", "琶音")
    };

    private static readonly Dictionary<string, SoundPlayer> Cache = new();

    public static string Normalize(string? key) =>
        Options.Any(o => o.Key == key) ? key! : Options[0].Key;

    public static string LabelOf(string? key) =>
        Options.First(o => o.Key == Normalize(key)).Label;

    public static string Next(string? key)
    {
        var index = Array.FindIndex(Options, o => o.Key == Normalize(key));
        return Options[(index + 1) % Options.Length].Key;
    }

    public static void Play(string? key)
    {
        var normalized = Normalize(key);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Sounds", normalized + ".wav");
            if (!File.Exists(path)) return;

            lock (Cache)
            {
                if (!Cache.TryGetValue(normalized, out var player))
                {
                    player = new SoundPlayer(path);
                    player.Load();
                    Cache[normalized] = player;
                }

                player.Play();
            }
        }
        catch (Exception ex)
        {
            Log.Write("sound failed: " + ex.Message);
        }
    }
}
