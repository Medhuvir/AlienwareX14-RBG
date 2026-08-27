using AlienRgb.Core;

if (args.Length == 0)
{
    PrintUsage();
    return 0;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "list":
        case "detect":
            return CmdList();
        case "set":
            return CmdSet(args);
        case "multi":
            return CmdMulti(args);
        case "flash":
            return CmdFlash(args);
        case "apply":
            return CmdApply(args);
        default:
            Console.Error.WriteLine($"Unknown command '{args[0]}'.");
            PrintUsage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    DiagLog.Write($"CLI error: {ex.Message}");
    return 1;
}

// Retries opening the device with backoff — used by the sleep/wake scheduled task, where
// the AW-ELC controller may take a while to re-enumerate after the system exits standby.
static AlienFxDevice OpenDeviceWithRetry()
{
    int[] delaysMs = { 0, 500, 500, 1000, 1000, 1000, 2000, 2000, 2000, 3000, 3000, 3000, 5000, 5000, 5000, 5000 };
    Exception? last = null;
    foreach (var delay in delaysMs)
    {
        if (delay > 0)
            Thread.Sleep(delay);
        try
        {
            var dev = AlienFxDevice.Open();
            DiagLog.Write($"CLI: device opened ({dev.Description})");
            return dev;
        }
        catch (Exception ex)
        {
            last = ex;
            DiagLog.Write($"CLI: device open failed: {ex.Message}");
        }
    }
    throw last ?? new InvalidOperationException("Could not open AlienFX device.");
}

static void PrintUsage()
{
    Console.WriteLine("""
        AlienRgb — Alienware x14 RGB control (APIv4)

        Usage:
          alienrgb list                     Show device and known zones
          alienrgb set all RRGGBB           Set every known zone to a color
          alienrgb set <ids> RRGGBB         Set light IDs (e.g. 2 or 0,1,3) to a color
          alienrgb multi <id>=RRGGBB ...    Set several zones at once (one batch), e.g. multi 0=FF0000 1=00FF00
          alienrgb flash <zoneId> [times]   Blink a light white to identify it
          alienrgb apply [profileName]      Apply saved profile (default: last applied)
        """);
}

static int CmdList()
{
    using var dev = AlienFxDevice.Open();
    Console.WriteLine($"Device : {dev.Description} (VID 187C, PID {dev.ProductId:X4}, APIv4)");
    Console.WriteLine("Zones  :");
    foreach (var z in ZoneMap.Load())
        Console.WriteLine($"  [{z.Id}] {z.Name}");
    return 0;
}

static int CmdSet(string[] args)
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: alienrgb set <all|ids> RRGGBB   (ids: 2 or 0,1,3)");
        return 2;
    }

    List<int> ids;
    if (args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        ids = ZoneMap.Load().Select(z => z.Id).ToList();
    }
    else
    {
        ids = new List<int>();
        foreach (var part in args[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var id) || id is < 0 or > 255)
            {
                Console.Error.WriteLine($"'{part}' is not a valid light ID (0..255).");
                return 2;
            }
            ids.Add(id);
        }
    }

    var (r, g, b) = ProfileStore.ParseHex(args[2]);
    using var dev = AlienFxDevice.Open();
    dev.SetColor(ids, r, g, b);
    Console.WriteLine($"Set light IDs [{string.Join(",", ids)}] to #{r:X2}{g:X2}{b:X2}.");
    return 0;
}

static int CmdMulti(string[] args)
{
    if (args.Length < 2)
    {
        Console.Error.WriteLine("Usage: alienrgb multi <id>=RRGGBB [<id>=RRGGBB ...]");
        return 2;
    }

    var wanted = new Dictionary<int, string>();
    foreach (var pair in args.Skip(1))
    {
        var parts = pair.Split('=');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var id) || id is < 0 or > 255)
        {
            Console.Error.WriteLine($"Bad zone spec '{pair}' — expected <id>=RRGGBB.");
            return 2;
        }
        wanted[id] = parts[1];
    }

    using var dev = AlienFxDevice.Open();
    foreach (var group in wanted.GroupBy(kv => kv.Value, kv => kv.Key))
    {
        var (r, g, b) = ProfileStore.ParseHex(group.Key);
        dev.StageColor(group.ToList(), r, g, b);
    }
    dev.Update();
    Console.WriteLine($"Applied {wanted.Count} zone colors in one batch.");
    return 0;
}

static int CmdFlash(string[] args)
{
    if (args.Length < 2 || !int.TryParse(args[1], out var id) || id is < 0 or > 255)
    {
        Console.Error.WriteLine("Usage: alienrgb flash <lightId 0..255> [times]");
        return 2;
    }
    int times = args.Length > 2 && int.TryParse(args[2], out var t) ? t : 5;

    using var dev = AlienFxDevice.Open();
    Console.WriteLine($"Flashing light ID {id} white {times} times — watch the machine...");
    for (int i = 0; i < times; i++)
    {
        dev.SetZoneColor(id, 255, 255, 255);
        Thread.Sleep(400);
        dev.SetZoneColor(id, 0, 0, 0);
        Thread.Sleep(400);
    }
    Console.WriteLine("Done.");
    return 0;
}

static int CmdApply(string[] args)
{
    DiagLog.Write($"CLI apply invoked, args=[{string.Join(" ", args)}]");
    var file = ProfileStore.Load();

    if (args.Length > 1)
    {
        var name = args[1];
        var profile = file.Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            Console.Error.WriteLine($"Profile '{name}' not found. Known: {string.Join(", ", file.Profiles.Select(p => p.Name))}");
            return 2;
        }

        using var dev = OpenDeviceWithRetry();
        var zoneCount = ProfileStore.Apply(dev, profile);
        file.LastApplied = profile.Name;
        ProfileStore.Save(file);
        Console.WriteLine($"Applied profile '{profile.Name}' ({zoneCount} zones).");
        DiagLog.Write($"CLI apply: applied profile '{profile.Name}' ({zoneCount} zones).");
        return 0;
    }

    // No name given: restore whatever was last actually showing (live state, falling back
    // to the last-applied named profile). This is what the sleep/wake trigger calls.
    using var device = OpenDeviceWithRetry();
    var zones = ProfileStore.ApplyLastKnownState(device, file);
    if (zones == 0)
    {
        Console.Error.WriteLine("No current state or last-applied profile recorded.");
        DiagLog.Write("CLI apply: nothing to restore.");
        return 2;
    }
    Console.WriteLine($"Restored last known state ({zones} zones).");
    DiagLog.Write($"CLI apply: restored last known state ({zones} zones).");
    return 0;
}
