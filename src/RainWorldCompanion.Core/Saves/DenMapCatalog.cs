using System.Text.Json;

namespace RainWorldCompanion.Core.Saves;

public sealed record MappedDen(string RoomId, string RegionCode, double X, double Y)
{
    public string RegionName => RegionCatalog.ForCode(RegionCode).DisplayName;
    public string Label => $"{RoomId}  ({RegionName})";
}

public static class DenMapCatalog
{
    public const int ImageWidth = 11600;
    public const int ImageHeight = 5000;

    private static readonly Lazy<IReadOnlyList<MappedDen>> Entries = new(Load);

    public static IReadOnlyList<MappedDen> All => Entries.Value;

    public static MappedDen? Find(string? roomId) => All.FirstOrDefault(
        den => string.Equals(den.RoomId, roomId?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<MappedDen> Load()
    {
        using var stream = typeof(DenMapCatalog).Assembly.GetManifestResourceStream(
            "RainWorldCompanion.Core.Saves.DownpourDens.json")
            ?? throw new InvalidDataException("The Downpour den catalog is missing.");
        var dens = JsonSerializer.Deserialize<MappedDen[]>(stream)
            ?? throw new InvalidDataException("The Downpour den catalog is empty.");
        if (dens.Length == 0 || dens.Select(d => d.RoomId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != dens.Length
            || dens.Any(d => string.IsNullOrWhiteSpace(d.RoomId) || string.IsNullOrWhiteSpace(d.RegionCode)
                || !double.IsFinite(d.X) || !double.IsFinite(d.Y)
                || d.X < 0 || d.Y < 0 || d.X >= ImageWidth || d.Y >= ImageHeight))
        {
            throw new InvalidDataException("The Downpour den catalog contains invalid entries.");
        }

        return Array.AsReadOnly(dens.OrderBy(d => d.RegionName).ThenBy(d => d.RoomId).ToArray());
    }
}
