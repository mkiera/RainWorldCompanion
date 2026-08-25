using System.Text;

namespace RainWorldCompanion.Tests;

/// <summary>
/// A throwaway directory under <c>Path.GetTempPath()</c> that removes itself on dispose.
/// Every test that writes goes through one of these, so no test can reach the live
/// Rain World save folder.
/// </summary>
public sealed class TempDirectory : IDisposable
{
    public TempDirectory(string? prefix = null)
    {
        var name = (prefix ?? "rwsm") + "-" + Guid.NewGuid().ToString("N");
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), name);
        System.IO.Directory.CreateDirectory(Path);
    }

    /// <summary>Absolute path of the directory.</summary>
    public string Path { get; }

    /// <summary>Absolute path for a relative path inside this directory. Nothing is created.</summary>
    public string Resolve(string relativePath)
        => System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Absolute path for a relative path, with its parent directory created.</summary>
    public string ResolveWithParent(string relativePath)
    {
        var full = Resolve(relativePath);
        var parent = System.IO.Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(parent))
        {
            System.IO.Directory.CreateDirectory(parent);
        }

        return full;
    }

    public string WriteBytes(string relativePath, byte[] content)
    {
        var full = ResolveWithParent(relativePath);
        System.IO.File.WriteAllBytes(full, content);
        return full;
    }

    /// <summary>Writes UTF-8 text with no BOM. For save containers use the byte overload.</summary>
    public string WriteText(string relativePath, string content)
        => WriteBytes(relativePath, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));

    public string CopyFrom(string sourcePath, string relativePath)
    {
        var full = ResolveWithParent(relativePath);
        System.IO.File.Copy(sourcePath, full, overwrite: true);
        return full;
    }

    public string CreateSubdirectory(string relativePath)
    {
        var full = Resolve(relativePath);
        System.IO.Directory.CreateDirectory(full);
        return full;
    }

    public bool FileExists(string relativePath) => System.IO.File.Exists(Resolve(relativePath));

    public byte[] ReadBytes(string relativePath) => System.IO.File.ReadAllBytes(Resolve(relativePath));

    /// <summary>Every file below this directory, keyed by relative path with backslash separators.</summary>
    public Dictionary<string, byte[]> ReadTree()
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(Path))
        {
            return result;
        }

        foreach (var file in System.IO.Directory.GetFiles(Path, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(Path, file).Replace('/', '\\');
            result[relative] = System.IO.File.ReadAllBytes(file);
        }

        return result;
    }

    public void Dispose()
    {
        // A just-closed handle can still hold the directory for a moment on Windows, so retry.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Path))
                {
                    ClearReadOnlyAttributes(Path);
                    System.IO.Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (IOException)
            {
                Thread.Sleep(30);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(30);
            }
        }
    }

    private static void ClearReadOnlyAttributes(string root)
    {
        // Directories as well as files: a read-only directory refuses to be deleted, so one left
        // behind by a test would keep the whole temp tree alive.
        foreach (var entry in System.IO.Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                var attributes = System.IO.File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    System.IO.File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
