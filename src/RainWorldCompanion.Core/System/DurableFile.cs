namespace RainWorldCompanion.Core.System;

internal static class DurableFile
{
    // Unflushed, NTFS can commit the rename before the data, so a power loss leaves an empty file.
    public static void ReplaceText(string path, string contents)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static void Copy(string source, string destination)
    {
        File.Copy(source, destination);
        using var stream = new FileStream(destination, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Flush(flushToDisk: true);
    }
}
