using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

// Stands in for the Windows Recycle Bin, so a test run never fills the real one.
internal sealed class FakeRecycleBin : IRecycleBin
{
    public List<string> Recycled { get; } = new();

    public List<string> RecycledFileText { get; } = new();

    public Exception? Failure { get; set; }

    public void Recycle(string path)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Recycled.Add(path);

        if (File.Exists(path))
        {
            RecycledFileText.Add(File.ReadAllText(path));
            File.Delete(path);
        }
        else if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
