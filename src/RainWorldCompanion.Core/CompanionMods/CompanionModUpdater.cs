using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Core.CompanionMods;

public sealed record CompanionModRelease(string Version, int ProtocolVersion, string MinimumAppVersion,
    string Channel, string PackageUrl, string Sha256);

public interface ICompanionModUpdateSource
{
    Task<IReadOnlyList<CompanionModRelease>> GetReleasesAsync(UpdateChannel channel, CancellationToken cancellationToken);
    Task<string> GetPackageAsync(CompanionModRelease release, CancellationToken cancellationToken);
}

public sealed class CompanionModUpdater(ICompanionModUpdateSource source, CompanionModManager manager)
{
    public static CompanionModRelease? Select(IEnumerable<CompanionModRelease> releases, string? installedVersion,
        string appVersion, int protocolVersion, UpdateChannel channel)
    {
        if (!SemVer.TryParse(appVersion, out var app)) return null;
        var hasInstalled = SemVer.TryParse(installedVersion, out var installed);
        return releases.Where(release =>
                release is not null && SemVer.TryParse(release.Version, out var candidate) && (!hasInstalled || candidate > installed) &&
                SemVer.TryParse(release.MinimumAppVersion, out var minimum) && app >= minimum &&
                release.ProtocolVersion == protocolVersion && release.Sha256 is { Length: 64 } && release.Sha256.All(Uri.IsHexDigit) &&
                (channel == UpdateChannel.Alpha ? release.Channel == "alpha" :
                    release.Channel == "stable" && !candidate.IsPreRelease ||
                    channel == UpdateChannel.Prerelease && release.Channel is "prerelease" or "stable"))
            .OrderByDescending(release => { SemVer.TryParse(release.Version, out var version); return version; })
            .FirstOrDefault();
    }

    public async Task<CompanionModInstallResult?> CheckAndInstallAsync(string? installedVersion, string appVersion,
        int protocolVersion, UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        var releases = await source.GetReleasesAsync(channel, cancellationToken).ConfigureAwait(false);
        var release = Select(releases, installedVersion, appVersion, protocolVersion, channel);
        if (release is null) return null;
        var package = await source.GetPackageAsync(release, cancellationToken).ConfigureAwait(false);
        return await manager.InstallAsync(package, release.Sha256, appVersion, protocolVersion, cancellationToken).ConfigureAwait(false);
    }
}
