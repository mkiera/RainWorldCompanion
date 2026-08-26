// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldCompanion.Core.Backups;

/// <summary>Directory.CreateDirectory succeeds on a folder that already exists, so it cannot decide
/// who owns a name. Creating a file that must not exist yet is what the filesystem makes atomic.</summary>
internal static class TimestampedFolders
{
    private const int MaxAttempts = 1000;

    /// <summary>Creates a folder under <paramref name="root"/> named for the current local time,
    /// for example 2026-08-24_19-31-07, and returns its full path.</summary>
    /// <param name="claimFileName">Created inside the folder to claim it. The caller deletes it
    /// once the folder is filled.</param>
    internal static string Create(string root, string claimFileName, string describeFolder)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        Directory.CreateDirectory(root);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var name = attempt == 1 ? stamp : $"{stamp}_{attempt}";
            var path = Path.Combine(root, name);

            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(path);
            }
            catch (IOException)
            {
                continue;
            }

            if (TryClaim(path, claimFileName))
            {
                return path;
            }
        }

        throw new IOException(
            $"Could not create a {describeFolder} under {root}: too many folders share the name {stamp}.");
    }

    internal static bool TryClaim(string directory, string claimFileName)
    {
        try
        {
            using var claim = new FileStream(
                Path.Combine(directory, claimFileName),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            return true;
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

    internal static void ReleaseClaim(string directory, string claimFileName)
    {
        try
        {
            var path = Path.Combine(directory, claimFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
