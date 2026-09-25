using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;

namespace RainWorldCompanion.Core.Saves;

public sealed record MapArtworkFeature(string Kind, string[] Rooms, string? Region, string? Text, int[][] Rectangles)
{
    public bool IsVisible(IReadOnlySet<string> visited, IReadOnlySet<string> regions) =>
        Region is not null ? regions.Contains(Region) : Rooms.Any(visited.Contains);
}

public static class MapArtworkCatalog
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<MapArtworkFeature>> Catalog = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<MapArtworkFeature> ForMap(string mapId) => Catalog.GetOrAdd(mapId, Load);

    public static IReadOnlySet<string> VisitedRegions(IEnumerable<string> visited) => visited
        .Select(room => room.Split('_')[0])
        .Where(RegionCatalog.IsKnown)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<MapArtworkFeature> Load(string mapId)
    {
        var map = DenMapCatalog.Maps.SingleOrDefault(map => map.Id.Equals(mapId, StringComparison.OrdinalIgnoreCase));
        if (map is null) return [];
        using var resource = typeof(MapArtworkCatalog).Assembly.GetManifestResourceStream(
            $"RainWorldCompanion.Core.Saves.{map.Id}Artwork.json.gz")
            ?? throw new InvalidDataException($"The {map.Id} map artwork catalog is missing.");
        using var decompressed = new GZipStream(resource, CompressionMode.Decompress);
        var features = JsonSerializer.Deserialize<MapArtworkFeature[]>(decompressed)
            ?? throw new InvalidDataException($"The {map.Id} map artwork catalog is empty.");
        if (features.Length == 0 || features.Any(feature =>
            feature.Kind is not ("region" or "gate" or "pipe" or "label" or "detail" or "room")
            || feature.Rooms is null || feature.Rectangles is null || feature.Rectangles.Length == 0
            || (feature.Region is null && feature.Rooms.Length == 0)
            || (feature.Region is not null && !RegionCatalog.IsKnown(feature.Region))
            || feature.Rooms.Any(string.IsNullOrWhiteSpace)
            || feature.Rectangles.Any(rect => rect.Length != 4 || rect[0] < 0 || rect[1] < 0 || rect[2] <= 0 || rect[3] <= 0
                || (long)rect[0] + rect[2] > map.ImageWidth || (long)rect[1] + rect[3] > map.ImageHeight)))
            throw new InvalidDataException($"The {map.Id} map artwork catalog contains invalid entries.");
        return Array.AsReadOnly(features);
    }
}
