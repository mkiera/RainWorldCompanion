// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;

namespace RainWorldSaveManager.Core.Backups;

/// <summary>
/// Names and claims the timestamped folders that backups and library entries are both kept in.
///
/// Directory.CreateDirectory succeeds on a folder that already exists, so it cannot decide who owns
/// a name: two operations starting in the same second would both be handed the same folder and
/// write over each other's copies. Creating a file that must not exist yet is the step the
/// filesystem makes atomic, so that is what settles the race.
/// </summary>
internal static class TimestampedFolders
{
    private const int MaxAttempts = 1000;

    /// <summary>
    /// Creates a folder under <paramref name="root"/> named for the current local time, holding a
    /// claim file, and returns its full path. Local time because the folder name is what the user
    /// reads in the list.
    /// </summary>
    /// <param name="claimFileName">
    /// The file created inside the folder to claim it. It is the caller's job to delete it once the
    /// folder is filled.
    /// </param>
    /// <param name="describeFolder">
    /// What the folder is called in the exhaustion message, for example "backup folder".
    /// </param>
    internal static string Create(string root, string claimFileName, string describeFolder)
    {
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

        Directory.CreateDirectory(root);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var name = attempt == 1 ? stamp : $"{stamp}_{attempt}";
            var path = Path.Combine(root, name);

            // A folder that is already there belongs to an earlier operation, finished or not.
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

    /// <summary>
    /// Deletes the claim file. The claim only matters while the folder is being filled, so a
    /// leftover one is reported by the caller's own checks rather than raised here.
    /// </summary>
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
