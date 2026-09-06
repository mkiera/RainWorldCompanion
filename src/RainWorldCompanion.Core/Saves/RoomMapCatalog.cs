using System.Text.Json;

namespace RainWorldCompanion.Core.Saves;

public sealed record RoomMapRect(double X, double Y, double Width, double Height);

public sealed record MappedRoom(
    string RoomId,
    string RegionCode,
    double X,
    double Y,
    IReadOnlyList<RoomMapRect> Bounds,
    string MatchKind);

public static class RoomMapCatalog
{
    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<MappedRoom>>> Catalog = new(Load);

    public static IReadOnlyList<MappedRoom> ForMap(string mapId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return Catalog.Value.TryGetValue(mapId, out var rooms) ? rooms : Array.Empty<MappedRoom>();
    }

    public static MappedRoom? Find(string mapId, string? roomId) => ForMap(mapId).FirstOrDefault(
        room => string.Equals(room.RoomId, roomId?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyDictionary<string, IReadOnlyList<MappedRoom>> Load()
    {
        using var stream = typeof(RoomMapCatalog).Assembly.GetManifestResourceStream(
            "RainWorldCompanion.Core.Saves.RoomMapCatalog.json")
            ?? throw new InvalidDataException("The room map catalog is missing.");
        var catalog = JsonSerializer.Deserialize<Dictionary<string, MappedRoom[]>>(stream)
            ?? throw new InvalidDataException("The room map catalog is empty.");
        var knownMaps = DenMapCatalog.Maps.ToDictionary(map => map.Id, StringComparer.OrdinalIgnoreCase);
        if (catalog.Count != knownMaps.Count || catalog.Keys.Any(mapId => !knownMaps.ContainsKey(mapId)))
        {
            throw new InvalidDataException("The room map catalog does not match the bundled maps.");
        }

        var result = new Dictionary<string, IReadOnlyList<MappedRoom>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (mapId, rooms) in catalog)
        {
            var map = knownMaps[mapId];
            if (rooms.Length == 0 || rooms.Select(room => room.RoomId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rooms.Length
                || rooms.Any(room => !IsValid(room, map.ImageWidth, map.ImageHeight)))
            {
                throw new InvalidDataException($"The {mapId} room map catalog contains invalid entries.");
            }
            result.Add(mapId, Array.AsReadOnly(rooms.OrderBy(room => room.RegionCode).ThenBy(room => room.RoomId).ToArray()));
        }
        return result;
    }

    private static bool IsValid(MappedRoom room, int imageWidth, int imageHeight) =>
        !string.IsNullOrWhiteSpace(room.RoomId)
        && !string.IsNullOrWhiteSpace(room.RegionCode)
        && !string.IsNullOrWhiteSpace(room.MatchKind)
        && double.IsFinite(room.X)
        && double.IsFinite(room.Y)
        && room.X >= 0
        && room.Y >= 0
        && room.X < imageWidth
        && room.Y < imageHeight
        && room.Bounds is not null
        && room.Bounds.All(bounds => double.IsFinite(bounds.X) && double.IsFinite(bounds.Y)
            && double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height)
            && bounds.X >= 0 && bounds.Y >= 0 && bounds.Width > 0 && bounds.Height > 0
            && bounds.X + bounds.Width <= imageWidth && bounds.Y + bounds.Height <= imageHeight);
}
