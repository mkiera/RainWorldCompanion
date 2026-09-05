namespace RainWorldCompanion.Core.Mods;

public enum MeadowStep
{
    // Nothing was read, so absence cannot be told from a folder nobody looked in.
    Unknown,

    NotInstalled,

    TurnedOff,

    Ready,
}

public sealed record MeadowReadiness(MeadowStep Step, string? Version)
{
    public const string WorkshopId = "3388224007";

    public static MeadowReadiness From(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        ModEntry? installed = Find(current.Installed);
        ModEntry? on = Find(current.Enabled.Mods);
        string? version = installed?.Version ?? on?.Version;

        if (on is not null)
        {
            return new MeadowReadiness(MeadowStep.Ready, Clean(version));
        }

        if (installed is not null)
        {
            return new MeadowReadiness(MeadowStep.TurnedOff, Clean(version));
        }

        // Saying "not installed" needs somewhere to have been looked at. Neither list being read
        // is a folder nobody could open, which is a different answer and a different fix.
        if (!current.Enabled.ReadTheEnabledList && !current.Enabled.CheckedTheInstall)
        {
            return new MeadowReadiness(MeadowStep.Unknown, null);
        }

        return current.Enabled.CheckedTheInstall
            ? new MeadowReadiness(MeadowStep.NotInstalled, null)
            : new MeadowReadiness(MeadowStep.Unknown, null);
    }

    public static ModListSnapshot TurnOn(CurrentMods current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var wanted = new List<ModEntry>(current.Enabled.Mods);
        var have = new HashSet<string>(
            wanted.Select(mod => mod.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (string id in Closure(current))
        {
            if (!have.Add(id))
            {
                continue;
            }

            ModEntry? known = current.Installed.FirstOrDefault(
                mod => string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase));

            wanted.Add(new ModEntry
            {
                Id = id,
                Name = known?.Name is { Length: > 0 } name ? name : id,
                Version = known?.Version,
                WorkshopId = known?.WorkshopId ?? (id == RainMeadowModId ? WorkshopId : null),
                FolderName = known?.FolderName,
                Origin = known?.Origin ?? "",
                Requirements = known is null ? new List<string>() : new List<string>(known.Requirements),
            });
        }

        for (int index = 0; index < wanted.Count; index++)
        {
            wanted[index].LoadOrder = index;
        }

        return new ModListSnapshot
        {
            GameVersion = current.Enabled.GameVersion,
            ReadTheEnabledList = current.Enabled.ReadTheEnabledList,
            CheckedTheInstall = current.Enabled.CheckedTheInstall,
            CheckedTheWorkshop = current.Enabled.CheckedTheWorkshop,
            Mods = wanted,
        };
    }

    private const string RainMeadowModId = MeadowModPolicy.MeadowModId;

    private static IEnumerable<string> Closure(CurrentMods current) =>
        new[] { RainMeadowModId }.Concat(ModRequirements.Closure(RainMeadowModId, current.Installed));

    private static ModEntry? Find(IEnumerable<ModEntry> mods) =>
        mods.FirstOrDefault(mod => string.Equals(mod.Id, RainMeadowModId, StringComparison.OrdinalIgnoreCase));

    private static string? Clean(string? version) =>
        string.IsNullOrWhiteSpace(version) ? null : version.Trim();
}
