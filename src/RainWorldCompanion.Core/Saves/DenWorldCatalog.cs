namespace RainWorldCompanion.Core.Saves;

public sealed record DenAccess(string RoomId, bool Available, string Reason);

public sealed class DenWorldCatalog
{
    public static IReadOnlyList<string> Timelines { get; } = Array.AsReadOnly(
        new[] { "Spear", "Artificer", "Red", "Gourmand", "White", "Yellow", "Rivulet", "Saint", "Inv" });

    public static DenWorldCatalog Unknown { get; } = new(new());
    private readonly Dictionary<string, Dictionary<string, DenAccess>> _access;

    private DenWorldCatalog(Dictionary<string, Dictionary<string, DenAccess>> access) => _access = access;

    public static string EffectiveTimeline(string campaign, string? timeline) =>
        string.IsNullOrWhiteSpace(timeline) ? campaign : timeline.Trim();

    public DenAccess Check(string room, string timeline) =>
        _access.TryGetValue(timeline, out var rooms) && rooms.TryGetValue(room.Trim(), out var access)
            ? access : new(room.Trim(), false, "The installed world data does not verify this den for this timeline.");

    public IReadOnlyList<string> AvailableTimelines(string room) => Timelines.Where(t => Check(room, t).Available).ToArray();

    public string Explanation(string room, string timeline)
    {
        var access = Check(room, timeline);
        if (access.Available) return $"Available in the {SlugcatCatalog.ForId(timeline).DisplayName} timeline.";
        string alternatives = string.Join(", ", Timelines.Where(t => Check(room, t).Available)
            .Select(t => SlugcatCatalog.ForId(t).DisplayName == t ? t : $"{SlugcatCatalog.ForId(t).DisplayName} ({t})"));
        return access.Reason + (alternatives.Length == 0 ? "" :
            $" Available timelines: {alternatives}. Choosing another timeline changes the whole campaign world.");
    }

    public static DenWorldCatalog Load(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath)) return Unknown;
        try
        {
            string assets = Path.Combine(installPath, "RainWorld_Data", "StreamingAssets");
            return Read(relative =>
            {
                string merged = Path.Combine(assets, "mergedmods", relative);
                if (File.Exists(merged)) return File.ReadAllText(merged);
                string modification = Path.Combine(assets, "mods", "moreslugcats", "modify", relative);
                if (File.Exists(modification)) throw new IOException("The game has not merged its modified world files.");
                foreach (string root in new[] { Path.Combine(assets, "mods", "moreslugcats"), assets })
                {
                    string path = Path.Combine(root, relative);
                    if (File.Exists(path)) return File.ReadAllText(path);
                }
                return null;
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Unknown;
        }
    }

    public static DenWorldCatalog Read(Func<string, string?> readFile)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in Lines(readFile("world/indexmaps/roomindexmap2.txt")))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], out _)) index[parts[1]] = parts[1];
        }
        var result = Timelines.ToDictionary(t => t,
            _ => new Dictionary<string, DenAccess>(StringComparer.OrdinalIgnoreCase), StringComparer.Ordinal);
        var dens = ShelterCatalog.All.Concat(DenMapCatalog.All.Select(d => d.RoomId))
            .Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(d => ShelterCatalog.RegionOf(d)!);
        foreach (var region in dens)
        {
            string prefix = $"world/{region.Key.ToLowerInvariant()}/";
            string? world = readFile(prefix + $"world_{region.Key.ToLowerInvariant()}.txt");
            string? properties = readFile(prefix + "properties.txt");
            foreach (string timeline in Timelines)
            {
                var active = ActiveLines(world, timeline).ToArray();
                var rooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var exclusive = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                bool roomSection = false;
                foreach (string line in active)
                {
                    if (line == "ROOMS") { roomSection = true; continue; }
                    if (line == "END ROOMS") { roomSection = false; continue; }
                    string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
                    if (roomSection && parts.Length >= 3 && parts.Skip(2).Any(p => p is "SHELTER" or "ANCIENTSHELTER"))
                        rooms.Add(parts[0]);
                    if (roomSection || parts.Length != 3) continue;
                    if (parts[1] == "EXCLUSIVEROOM")
                    {
                        if (!exclusive.TryGetValue(parts[2], out var allowed))
                            exclusive[parts[2]] = allowed = new(StringComparer.Ordinal);
                        allowed.UnionWith(parts[0].Split(',', StringSplitOptions.TrimEntries));
                    }
                    if (parts[1] == "HIDEROOM" && parts[0].Split(',', StringSplitOptions.TrimEntries).Contains(timeline))
                        hidden.Add(parts[2]);
                }
                foreach (var entry in exclusive.Where(e => !e.Value.Contains(timeline))) hidden.Add(entry.Key);
                var broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? variant = readFile(prefix + $"properties-{timeline.ToLowerInvariant()}.txt");
                foreach (string line in Lines(variant ?? properties))
                {
                    string[] parts = line.Split(':', StringSplitOptions.TrimEntries);
                    if (parts.Length == 3 && parts[0] == "Broken Shelters" && parts[1] == timeline)
                        broken.UnionWith(parts[2].Split(',', StringSplitOptions.TrimEntries));
                }
                bool replaced = (region.Key == "SL" && timeline is "Spear" or "Artificer")
                    || (region.Key is "DS" or "SH" && timeline == "Saint")
                    || (region.Key == "SS" && timeline is "Rivulet" or "Saint");
                bool uncertain = world is null || properties is null || Lines(world).Any(l => l.StartsWith('{'));
                foreach (string den in region)
                {
                    string canonical = index.GetValueOrDefault(den, den);
                    string reason = uncertain ? "The installed world data could not be verified. Launch the game after changing mods to rebuild its world data."
                        : replaced ? "This map region is replaced by a different region in this timeline."
                        : !index.ContainsKey(den) ? "This den is absent from the game's room index."
                        : !rooms.Contains(den) || hidden.Contains(den) ? "This den is not an active shelter in this timeline."
                        : broken.Contains(den) ? "This shelter is broken in this timeline."
                        : "";
                    result[timeline][den] = new(canonical, reason.Length == 0, reason);
                }
            }
        }
        return new(result);
    }

    private static IEnumerable<string> Lines(string? text) => (text ?? "").Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));

    private static IEnumerable<string> ActiveLines(string? text, string timeline)
    {
        foreach (string raw in Lines(text))
        {
            string line = raw;
            if (line.StartsWith('('))
            {
                int end = line.IndexOf(')');
                if (end < 0) continue;
                string condition = line[1..end];
                bool inverse = condition.StartsWith("X-", StringComparison.Ordinal);
                if (inverse) condition = condition[2..];
                var names = condition.Split(',', StringSplitOptions.TrimEntries)
                    .Select(n => n switch { "0" => "White", "1" => "Yellow", "2" => "Red", _ => n });
                if (names.Contains(timeline) == inverse) continue;
                line = line[(end + 1)..].Trim();
            }
            yield return line;
        }
    }
}
