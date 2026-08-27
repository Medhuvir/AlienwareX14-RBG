using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlienRgb.Core;

public sealed class LightZone
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public static class ZoneMap
{
    // Deliberately NOT %APPDATA%: on this machine that path gets silently virtualized/hidden
    // from independently-launched processes (e.g. Task Scheduler), which broke wake-restore.
    // A folder next to the exe is visible identically to every launch context.
    private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "data");

    private static string ZonesPath => Path.Combine(ConfigDir, "zones.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Verified zone map for this Alienware x14 (discovered 2026-08 via color sweep on IDs 0-7).</summary>
    public static List<LightZone> Defaults() =>
    [
        new() { Id = 1, Name = "Alien Head" },
        new() { Id = 2, Name = "Keyboard" },
        new() { Id = 5, Name = "Power Button" },
    ];

    public static List<LightZone> Load()
    {
        try
        {
            if (File.Exists(ZonesPath))
            {
                var zones = JsonSerializer.Deserialize<List<LightZone>>(File.ReadAllText(ZonesPath), JsonOpts);
                if (zones is { Count: > 0 })
                    return zones;
            }
        }
        catch
        {
            // fall through to defaults on unreadable/corrupt file
        }
        return Defaults();
    }

    public static void Save(List<LightZone> zones)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ZonesPath, JsonSerializer.Serialize(zones, JsonOpts));
    }
}
