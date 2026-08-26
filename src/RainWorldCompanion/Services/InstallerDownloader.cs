using System.IO;
using System.IO.Compression;
using System.Net.Http;
using RainWorldCompanion.Core.Updates;

namespace RainWorldCompanion.Services;

public interface IInstallerDownloader
{
    /// <param name="progress">Fraction from 0 to 1, already throttled.</param>
    Task<string> DownloadAsync(
        string downloadUrl,
        string assetName,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Separate from <see cref="DownloadAsync"/> because GitHub's actions API always zips an
    /// artifact and the runs endpoint does not carry its length, so the wrapper and the length
    /// check that guards a release download do not apply.
    /// </summary>
    Task<string> DownloadBranchBuildAsync(
        string zipUrl,
        long runId,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// The file this produces is one the app goes on to execute. A stream that ends is
/// indistinguishable from a connection that was cut, so the bytes written are counted against the
/// length the release stated and anything short is deleted rather than handed back.
/// </summary>
public sealed class InstallerDownloader : IInstallerDownloader, IDisposable
{
    // At most one report per percent or per tenth of a second. Per chunk would be thousands of
    // property changes a second, on the thread drawing the bar.
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
            // No overall timeout on purpose. The body is a 43 MB installer and a slow connection is
            // not an error. Reaching the server has the deadline, which is the connect timeout.
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
        // Checked here as well as in the picker, this being the last point before the bytes land
        // on disk.
        if (!UpdateUrls.IsAllowedDownload(downloadUrl))
        {
            throw new UpdateCheckException("That download is not hosted anywhere this app will fetch from.");
        }

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
            // The next attempt cannot tell a partial file from a resumable one.
            Delete(destination);
            throw;
        }

        progress?.Report(1.0);
        return destination;
    }

    /// <summary>
    /// A zip entry header can claim far less than the entry expands to, so extraction is counted
    /// as it goes rather than trusted up front. The real installer is around 43 MB.
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

        // Named for the run, so two branch builds downloaded in one session cannot collide.
        var archive = Path.Combine(UpdatesFolder.Location, $"branch-{runId}.zip");
        var destination = Path.Combine(UpdatesFolder.Location, $"branch-{runId}-setup.exe");

        try
        {
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
            Delete(archive);
        }

        progress?.Report(1.0);
        return destination;
    }

    /// <summary>
    /// Entry names are treated as data: each is held to the release-asset rule, and the entry's own
    /// name is never joined to a directory. The destination comes from the run id instead.
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

    /// <param name="expectedBytes">
    /// A positive value is checked against the bytes written and a short file is a failure. Zero
    /// leaves the response header to drive the bar and nothing to check.
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
            // ResponseHeadersRead, so the body is streamed rather than buffered whole in memory.
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

            // An absent header leaves the bar still and the download finishing, which is not a
            // reason to refuse the file.
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
        }
    }

    public void Dispose() => _client.Dispose();
}
