using System.Net.Http;
using System.Text.Json;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Core.CompanionMods;

public sealed class CompanionModUpdateSource(HttpClient client, string cacheFolder,
    Func<UpdateChannel, Uri?> manifestUri, IReadOnlyList<CompanionModRelease> bundledReleases) : ICompanionModUpdateSource
{
    public async Task<IReadOnlyList<CompanionModRelease>> GetReleasesAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        var releases = bundledReleases.ToList();
        var uri = manifestUri(channel);
        if (uri is null) return releases;
        if (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback) throw new InvalidDataException("Mod update manifests require HTTPS.");
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response.Content, 256 * 1024, cancellationToken).ConfigureAwait(false);
            releases.AddRange(JsonSerializer.Deserialize<CompanionModRelease[]>(bytes, CompanionModManifest.JsonOptions) ?? []);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }
        return releases;
    }

    public async Task<string> GetPackageAsync(CompanionModRelease release, CancellationToken cancellationToken)
    {
        var bundled = bundledReleases.FirstOrDefault(candidate => candidate == release);
        if (bundled is not null && Uri.TryCreate(bundled.PackageUrl, UriKind.Absolute, out var bundledUri) && bundledUri.IsFile)
            return bundledUri.LocalPath;
        if (!Uri.TryCreate(release.PackageUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && uri.Scheme == Uri.UriSchemeHttp))
            throw new InvalidDataException("Mod package downloads require HTTPS.");
        if (release.Sha256 is not { Length: 64 } || !release.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("The mod package hash is invalid.");
        Directory.CreateDirectory(cacheFolder);
        var destination = Path.Combine(cacheFolder, release.Sha256.ToLowerInvariant() + ".zip");
        if (File.Exists(destination) && CompanionModPackage.Hash(destination).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) return destination;
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadLimitedAsync(response.Content, 32 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
        var hash = Convert.ToHexString(global::System.Security.Cryptography.SHA256.HashData(bytes));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The companion mod download failed verification.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return destination;
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximum) throw new InvalidDataException("The mod update response exceeds the size limit.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("The mod update response exceeds the size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
