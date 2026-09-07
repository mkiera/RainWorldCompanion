using System.Security.Cryptography;
using System.Text.Json;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Core.CompanionMods;

public sealed record CompanionModManifest
{
    public string ModId { get; init; } = "rwcompanion";
    public string Version { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string MinimumAppVersion { get; init; } = "0.0.0";
    public Dictionary<string, string> Files { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCompatible(string appVersion, int protocolVersion) =>
        ModId == "rwcompanion" && ProtocolVersion == protocolVersion &&
        SemVer.TryParse(Version, out _) && SemVer.TryParse(MinimumAppVersion, out var minimum) &&
        SemVer.TryParse(appVersion, out var app) && app >= minimum;

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);
}

public sealed record CompanionModStatus(bool Installed, bool Enabled, bool Compatible, string? Version, string? Problem)
{
    public bool Ready => Installed && Enabled && Compatible && Problem is null;
}

public enum CompanionModInstallOutcome { Installed, Deferred, Failed }

public sealed record CompanionModInstallResult(CompanionModInstallOutcome Outcome, string? Version, string? Problem);

public static class CompanionModPackage
{
    public const string ManifestName = "companion-manifest.json";

    public static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static CompanionModManifest ReadManifest(string folder) =>
        JsonSerializer.Deserialize<CompanionModManifest>(File.ReadAllText(Path.Combine(folder, ManifestName)), CompanionModManifest.JsonOptions)
        ?? throw new InvalidDataException("The Companion Game Hook manifest is empty.");

    public static string ContainedPath(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') ||
            Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("The Companion Game Hook package contains an invalid path.");
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(folder, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Companion Game Hook package contains a path outside its folder.");
        return full;
    }

    public static void ValidateFolder(string folder, CompanionModManifest manifest)
    {
        if (manifest.ModId != "rwcompanion" || !SemVer.TryParse(manifest.Version, out _) ||
            manifest.Files is null || manifest.Files.Count is 0 or > 100 || !manifest.Files.ContainsKey("modinfo.json") ||
            !manifest.Files.Keys.Any(path => path.StartsWith("plugins/", StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The Companion Game Hook package manifest is invalid.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var path = ContainedPath(folder, entry.Key);
            if (!paths.Add(path) || entry.Value is null || entry.Value.Length != 64 || !entry.Value.All(Uri.IsHexDigit) ||
                !File.Exists(path) || !Hash(path).Equals(entry.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Companion Game Hook file verification failed: {entry.Key}");
        }
        using var modInfo = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "modinfo.json")));
        if (modInfo.RootElement.GetProperty("id").GetString() != "rwcompanion" ||
            modInfo.RootElement.GetProperty("version").GetString() != manifest.Version)
            throw new InvalidDataException("The Companion Game Hook metadata does not match its manifest.");
    }
}
