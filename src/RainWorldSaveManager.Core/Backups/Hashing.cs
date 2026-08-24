using System.Security.Cryptography;

namespace RainWorldSaveManager.Core.Backups;

/// <summary>
/// SHA-256 helpers for backup integrity checks.
/// </summary>
public static class Hashing
{
    // The sav file is about 6 MB, so hashing is streamed rather than loading the file into memory.
    private const int ReadBufferSize = 1 << 20;

    /// <summary>
    /// SHA-256 of a file as 64 lowercase hex characters.
    /// </summary>
    public static string ComputeFileSha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path must not be empty.", nameof(path));
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            ReadBufferSize,
            FileOptions.SequentialScan);

        return ToHex(SHA256.HashData(stream));
    }

    /// <summary>
    /// SHA-256 of a stream read from its current position to the end.
    /// </summary>
    public static string ComputeSha256(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return ToHex(SHA256.HashData(stream));
    }

    /// <summary>
    /// SHA-256 of a byte sequence.
    /// </summary>
    public static string ComputeSha256(ReadOnlySpan<byte> bytes) => ToHex(SHA256.HashData(bytes));

    /// <summary>
    /// True when both files exist and have identical content. Length is compared first so
    /// differently sized files never pay for a hash.
    /// </summary>
    public static bool FilesMatch(string leftPath, string rightPath)
    {
        if (!File.Exists(leftPath) || !File.Exists(rightPath))
        {
            return false;
        }

        if (new FileInfo(leftPath).Length != new FileInfo(rightPath).Length)
        {
            return false;
        }

        return string.Equals(
            ComputeFileSha256(leftPath),
            ComputeFileSha256(rightPath),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the file on disk hashes to the expected digest. A missing file or a read
    /// failure answers false rather than throwing, so callers can report it as a mismatch.
    /// </summary>
    public static bool FileMatchesHash(string path, string expectedSha256)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            return string.Equals(ComputeFileSha256(path), expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string ToHex(byte[] hash) => Convert.ToHexString(hash).ToLowerInvariant();
}
