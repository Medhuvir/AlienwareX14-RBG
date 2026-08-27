using System.Text.Json;

namespace AlienRgb.Core;

public sealed class RgbProfile
{
    public string Name { get; set; } = "";
    public byte Brightness { get; set; } = 100;
    /// <summary>Zone light ID → "RRGGBB" hex color.</summary>
    public Dictionary<int, string> ZoneColors { get; set; } = new();
}

public sealed class ProfileFile
{
    public string? LastApplied { get; set; }
    public List<RgbProfile> Profiles { get; set; } = new();
    /// <summary>Exact zone colors last pushed to the device — live editing included, not just named profiles.
    /// Used to restore state after sleep/wake, since the user may not have saved a profile.</summary>
    public Dictionary<int, string>? CurrentState { get; set; }
}

public static class ProfileStore
{
    // Deliberately NOT %APPDATA%: on this machine that path gets silently virtualized/hidden
    // from independently-launched processes (e.g. Task Scheduler), which broke wake-restore.
    // A folder next to the exe is visible identically to every launch context.
    private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "data");

    private static string ProfilesPath => Path.Combine(ConfigDir, "profiles.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ProfileFile Load()
    {
        try
        {
            if (File.Exists(ProfilesPath))
                return JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(ProfilesPath), JsonOpts)
                       ?? new ProfileFile();
        }
        catch
        {
            // corrupt file: start fresh rather than crash
        }
        return new ProfileFile();
    }

    public static void Save(ProfileFile file)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(file, JsonOpts));
    }

    public static (byte r, byte g, byte b) ParseHex(string hex)
    {
        hex = hex.TrimStart('#');
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v))
            throw new FormatException($"'{hex}' is not an RRGGBB hex color.");
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    /// <summary>Apply a profile to the device in one staged batch. Returns the number of zones set.</summary>
    public static int Apply(AlienFxDevice device, RgbProfile profile)
    {
        int count = ApplyZoneColors(device, profile.ZoneColors);
        SaveCurrentState(profile.ZoneColors);
        return count;
    }

    private static int ApplyZoneColors(AlienFxDevice device, IDictionary<int, string> zoneColors)
    {
        int count = 0;
        foreach (var group in zoneColors.GroupBy(kv => kv.Value, kv => kv.Key))
        {
            var (r, g, b) = ParseHex(group.Key);
            var ids = group.ToList();
            device.StageColor(ids, r, g, b);
            count += ids.Count;
        }
        device.Update();
        return count;
    }

    /// <summary>Record the exact colors currently on the device, independent of any named profile.
    /// Call this after every successful live apply so a later restore (e.g. after sleep) is exact.</summary>
    public static void SaveCurrentState(IDictionary<int, string> zoneColors)
    {
        var file = Load();
        file.CurrentState = new Dictionary<int, string>(zoneColors);
        Save(file);
    }

    /// <summary>Restore whatever was last showing: the live current state if recorded, otherwise
    /// the last-applied named profile. Returns the number of zones set, or 0 if nothing to restore.</summary>
    public static int ApplyLastKnownState(AlienFxDevice device, ProfileFile file)
    {
        if (file.CurrentState is { Count: > 0 } state)
            return ApplyZoneColors(device, state);

        var profile = file.Profiles.FirstOrDefault(p =>
            p.Name.Equals(file.LastApplied, StringComparison.OrdinalIgnoreCase));
        return profile is not null ? ApplyZoneColors(device, profile.ZoneColors) : 0;
    }
}
