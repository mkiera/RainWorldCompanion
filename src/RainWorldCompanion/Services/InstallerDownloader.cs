using System.IO;
using System.IO.Compression;
using System.Net.Http;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Services;

/// <summary>Fetches an installer to disk.</summary>
public interface IInstallerDownloader
{
    /// <summary>
    /// Downloads to the updates folder and returns the path written.
    /// </summary>
    /// <param name="progress">Fraction from 0 to 1, already throttled.</param>
    Task<string> DownloadAsync(
        string downloadUrl,
        string assetName,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Fetches a branch build and returns the installer taken out of it.
    ///
    /// Separate from <see cref="DownloadAsync"/> because a branch build arrives as a zip and its
    /// size is not known beforehand. GitHub's actions API always zips an artifact, one file or
    /// not, and the runs endpoint does not carry the artifact's length, so neither the wrapper
    /// nor the length check that guards a release download applies here.
    /// </summary>
    Task<string> DownloadBranchBuildAsync(
        string zipUrl,
        long runId,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Streams an installer to disk, and refuses to hand back anything it cannot prove is complete.
///
/// The file this produces is one the app goes on to execute, which is what shapes every decision
/// here. A stream that ends is indistinguishable from a connection that was cut, so the bytes
/// written are counted and checked against the length the release stated, and anything short is
/// deleted rather than reported as a finished download. A truncated installer that still runs is
/// the worst outcome available: it would replace a working app with a partial one.
/// </summary>
public sealed class InstallerDownloader : IInstallerDownloader, IDisposable
{
    // Emit at most one progress report per percent or per tenth of a second, whichever comes
    // first. Per chunk would be thousands of property changes a second to move a bar by less than
    // a pixel, on the thread drawing it.
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private const double ProgressStep = 0.01;

    private readonly HttpClient _client;

    public InstallerDownloader(string appVersion)
    {
        _client = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
        })
        {
            // No overall timeout on purpose. The body is a 43 MB installer, and a slow connection
            // is not an error: it is somebody on a slow connection. Reaching the server at all is
            // what has a deadline, which is the connect timeout above.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        _client.DefaultRequestHeaders.Add("User-Agent", UpdateUrls.UserAgent(appVersion));
    }

    public async Task<string> DownloadAsync(
        string downloadUrl,
        string assetName,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // The address came out of a JSON document fetched over the network, so it is data. Checked
        // here as well as in the picker, because this is the last point before the bytes land on
        // disk and this method is reachable from more than one caller.
        if (!UpdateUrls.IsAllowedDownload(downloadUrl))
        {
            throw new UpdateCheckException("That download is not hosted anywhere this app will fetch from.");
        }

        // Without a length there is nothing to check the finished file against, so there is no
        // point starting.
        if (expectedBytes <= 0)
        {
            throw new UpdateCheckException(
                "The release does not say how large its installer is, so the download cannot be checked.");
        }

        var destination = UpdatesFolder.PathFor(assetName)
            ?? throw new UpdateCheckException($"\"{assetName}\" is not a usable file name.");

        UpdatesFolder.Ensure();

        try
        {
            await StreamToFileAsync(downloadUrl, destination, expectedBytes, progress, cancellationToken);
        }
        catch
        {
            // Every failure takes the partial file with it. Leaving one behind means the next
            // attempt has to decide whether it is a resumable download or a broken one, and it
            // cannot tell.
            Delete(destination);
            throw;
        }

        progress?.Report(1.0);
        return destination;
    }

    /// <summary>
    /// The largest installer this will write out of a zip.
    ///
    /// The real one is around 43 MB. The cap is here because the entry header states its own
    /// uncompressed length and a zip can claim far less than it expands to, so extraction is
    /// counted as it goes rather than trusted up front.
    /// </summary>
    private const long MaxInstallerBytes = 300L * 1024 * 1024;

    public async Task<string> DownloadBranchBuildAsync(
        string zipUrl,
        long runId,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!UpdateUrls.IsAllowedDownload(zipUrl))
        {
            throw new UpdateCheckException("That download is not hosted anywhere this app will fetch from.");
        }

        UpdatesFolder.Ensure();

        // Named for the run, so two branch builds downloaded in one session cannot collide, and so
        // a file left behind says which run it came from.
        var archive = Path.Combine(UpdatesFolder.Location, $"branch-{runId}.zip");
        var destination = Path.Combine(UpdatesFolder.Location, $"branch-{runId}-setup.exe");

        try
        {
            // Length unknown: the runs endpoint does not carry it, so the response header is all
            // there is, and it only drives the bar.
            await StreamToFileAsync(zipUrl, archive, expectedBytes: 0, progress, cancellationToken);
            ExtractInstaller(archive, destination);
        }
        catch
        {
            Delete(archive);
            Delete(destination);
            throw;
        }
        finally
        {
            // The zip has served its purpose either way, and it is the larger of the two.
            Delete(archive);
        }

        progress?.Report(1.0);
        return destination;
    }

    /// <summary>
    /// Writes the one installer inside the archive to <paramref name="destination"/>.
    ///
    /// Entry names are treated as data. Every one is held to the same rule a release asset is,
    /// which admits letters, digits, dot, underscore and dash and nothing else, so a name carrying
    /// a directory separator or a drive letter never reaches a path at all. The entry's own name
    /// is never joined to a directory: the destination is decided here from the run id.
    /// </summary>
    private static void ExtractInstaller(string archivePath, string destination)
    {
        using var archive = OpenArchive(archivePath);

        ZipArchiveEntry? found = null;
        foreach (var entry in archive.Entries)
        {
            if (UpdateUrls.IsInstallerAsset(entry.Name) && entry.FullName == entry.Name)
            {
                found = entry;
                break;
            }
        }

        if (found is null)
        {
            throw new UpdateCheckException(
                "That branch build does not contain an installer. It may have been built before "
                + "the workflow published one, or its artifact may have expired.");
        }

        using var source = found.Open();
        using var file = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);

        var buffer = new byte[64 * 1024];
        long written = 0;

        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            written += read;
            if (written > MaxInstallerBytes)
            {
                throw new UpdateCheckException(
                    "The installer inside that branch build is larger than this app will write.");
            }

            file.Write(buffer, 0, read);
        }

        if (written == 0)
        {
            throw new UpdateCheckException("The installer inside that branch build is empty.");
        }
    }

    private static ZipArchive OpenArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            throw new UpdateCheckException(
                "That branch build could not be read as a zip. Its artifact may have expired.", e);
        }
    }

    /// <summary>
    /// Streams a body to a file.
    /// </summary>
    /// <param name="expectedBytes">
    /// The length the caller was promised, or zero when nobody stated one. A positive value is
    /// checked against the bytes written and a short file is a failure, because a truncated
    /// installer that still runs is the worst outcome available. Zero leaves the response header
    /// to drive the bar and nothing to check, which is the branch-build case.
    /// </param>
    private async Task StreamToFileAsync(
        string downloadUrl,
        string destination,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            // ResponseHeadersRead so the body is streamed rather than buffered whole into memory
            // before a single byte reaches the disk.
            response = await _client.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            throw new UpdateCheckException("Could not reach the download.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateCheckException(
                    $"The download answered with HTTP {(int)response.StatusCode}.");
            }

            // With nothing stated up front, the response header is the only length there is. It
            // can be absent, in which case the bar simply does not move and the download still
            // finishes: a missing header is not a reason to refuse the file.
            var total = expectedBytes > 0
                ? expectedBytes
                : response.Content.Headers.ContentLength ?? 0;

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var file = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);

            var buffer = new byte[64 * 1024];
            long written = 0;
            var lastFraction = 0.0;
            var lastReport = DateTime.UtcNow;

            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, cancellationToken);
                }
                catch (Exception e) when (e is HttpRequestException or IOException)
                {
                    throw new UpdateCheckException("The download stopped early.", e);
                }

                if (read == 0)
                {
                    break;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;

                var fraction = total > 0 ? Math.Min(1.0, (double)written / total) : 0.0;
                var now = DateTime.UtcNow;
                if (progress is not null && total > 0
                    && (fraction - lastFraction >= ProgressStep || now - lastReport >= ProgressInterval))
                {
                    lastFraction = fraction;
                    lastReport = now;
                    progress.Report(fraction);
                }
            }

            await file.FlushAsync(cancellationToken);

            if (written == 0)
            {
                throw new UpdateCheckException("The download arrived empty. Please try again.");
            }

            if (expectedBytes > 0 && written != expectedBytes)
            {
                throw new UpdateCheckException(
                    $"The download stopped early. It got {written:N0} bytes of the "
                    + $"{expectedBytes:N0} the release said it would send, so the incomplete file "
                    + "was deleted. Please try again.");
            }
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind at worst, and the next launch clears the folder anyway.
        }
    }

    public void Dispose() => _client.Dispose();
}
