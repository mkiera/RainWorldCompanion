using System.Text.Json;

namespace RainWorldCompanion.Core.Saves;

public sealed record MappedDen(string RoomId, string RegionCode, double X, double Y, bool HasMapIcon = true)
{
    public string RegionName => RegionCatalog.ForCode(RegionCode).DisplayName;
    public string Label => $"{RoomId}  ({RegionName})";
}

public sealed class DenMapDefinition(string id, int imageWidth, int imageHeight)
{
    public string Id { get; } = id;
    public int ImageWidth { get; } = imageWidth;
    public int ImageHeight { get; } = imageHeight;
    public string Title => $"{Id.ToUpperInvariant()} WORLD MAP";
    private readonly Lazy<IReadOnlyList<MappedDen>> _entries = new(() => Load(id, imageWidth, imageHeight));

    public IReadOnlyList<MappedDen> Dens => _entries.Value;

    public MappedDen? Find(string? roomId) => Dens.FirstOrDefault(
        den => string.Equals(den.RoomId, roomId?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<MappedDen> Load(string id, int width, int height)
    {
        using var stream = typeof(DenMapCatalog).Assembly.GetManifestResourceStream(
            $"RainWorldCompanion.Core.Saves.{id}Dens.json")
            ?? throw new InvalidDataException($"The {id} den catalog is missing.");
        var dens = JsonSerializer.Deserialize<MappedDen[]>(stream)
            ?? throw new InvalidDataException($"The {id} den catalog is empty.");
        if (dens.Length == 0 || dens.Select(d => d.RoomId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != dens.Length
            || dens.Any(d => string.IsNullOrWhiteSpace(d.RoomId) || string.IsNullOrWhiteSpace(d.RegionCode)
                || !double.IsFinite(d.X) || !double.IsFinite(d.Y)
                || d.X < 0 || d.Y < 0 || d.X >= width || d.Y >= height))
        {
            throw new InvalidDataException($"The {id} den catalog contains invalid entries.");
        }

        return Array.AsReadOnly(dens.OrderBy(d => d.RegionName).ThenBy(d => d.RoomId).ToArray());
    }
}

public static class DenMapCatalog
{
    public static DenMapDefinition Vanilla { get; } = new("Vanilla", 6166, 4509);
    public static DenMapDefinition Downpour { get; } = new("Downpour", 11600, 5000);
    public static DenMapDefinition Artificer { get; } = new("Artificer", 8110, 6200);
    public static DenMapDefinition Spearmaster { get; } = new("Spearmaster", 8770, 5000);
    public static DenMapDefinition Rivulet { get; } = new("Rivulet", 9420, 5000);
    public static DenMapDefinition Saint { get; } = new("Saint", 9370, 4550);
    public static IReadOnlyList<DenMapDefinition> Maps { get; } = Array.AsReadOnly(
        new[] { Vanilla, Downpour, Artificer, Spearmaster, Rivulet, Saint });

    public static DenMapDefinition? ForTimeline(string timeline, bool downpourEnabled) => timeline switch
    {
        "White" or "Yellow" or "Red" => downpourEnabled ? Downpour : Vanilla,
        "Gourmand" or "Inv" when downpourEnabled => Downpour,
        "Artificer" when downpourEnabled => Artificer,
        "Spear" when downpourEnabled => Spearmaster,
        "Rivulet" when downpourEnabled => Rivulet,
        "Saint" when downpourEnabled => Saint,
        _ => null,
    };
}
