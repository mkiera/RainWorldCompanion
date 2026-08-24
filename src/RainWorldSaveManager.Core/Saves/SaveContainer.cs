// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// One Rain World save container file (sav, sav2, exp1, options, ...) decoded into its
/// hashtable entries. Values are returned raw, with any checksum prefix still attached.
/// </summary>
public sealed class SaveContainer
{
    /// <summary>
    /// Closing tag of the serialized hashtable. Everything after it is NUL padding, because
    /// the game writes these files at a fixed size.
    /// </summary>
    private const string ClosingTag = "</ArrayOfKeyValueOfanyTypeanyType>";

    private static readonly byte[] ClosingTagBytes = Encoding.ASCII.GetBytes(ClosingTag);

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        IgnoreWhitespace = false,
        CloseInput = false,
    };

    private SaveContainer(
        string filePath,
        IReadOnlyDictionary<string, string> entries,
        int paddingByteCount,
        string? formatVersion,
        string? structureProblem)
    {
        FilePath = filePath;
        Entries = entries;
        PaddingByteCount = paddingByteCount;
        FormatVersion = formatVersion;
        StructureProblem = structureProblem;
    }

    public string FilePath { get; }

    /// <summary>Hashtable key to raw value. Empty is a legitimate result: exp1 has no keys.</summary>
    public IReadOnlyDictionary<string, string> Entries { get; }

    /// <summary>Count of NUL padding bytes following the closing tag.</summary>
    public int PaddingByteCount { get; }

    /// <summary>Text of the &lt;Version&gt; child element, null when absent.</summary>
    public string? FormatVersion { get; }

    /// <summary>
    /// Non-null when the hashtable itself is damaged: Keys and Values hold different numbers of
    /// children, or one of the two is missing. Keys and Values are matched by index, so a file
    /// where they disagree has lost entries. Pairing what can be paired and saying nothing makes
    /// a slot that lost its "save" entry to a truncated write look exactly like a slot the
    /// player never used.
    /// </summary>
    public string? StructureProblem { get; }

    /// <exception cref="SaveContainerException">The file cannot be read or parsed.</exception>
    public static SaveContainer Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new SaveContainerException("No save file path was given.");
        }

        byte[] bytes;
        try
        {
            bytes = ReadAllBytesShared(path);
        }
        catch (Exception ex)
        {
            throw new SaveContainerException($"Could not read '{path}': {ex.Message}", ex);
        }

        // The closing tag is ASCII and the padding after it is NUL bytes, so the cut is made in
        // the byte array. Decoding the whole file to a string first, then slicing the XML out of
        // it, costs two more copies of a file that runs to megabytes, and the reader is on the
        // path that refreshes the window.
        int tagIndex = LastIndexOf(bytes, ClosingTagBytes);
        if (tagIndex < 0)
        {
            throw new SaveContainerException($"'{path}' is not a save container: closing tag missing.");
        }

        int xmlLength = tagIndex + ClosingTagBytes.Length;
        int padding = bytes.Length - xmlLength;

        XDocument document;
        try
        {
            // XmlReader detects the UTF-8 BOM itself, so the preamble stays in the slice.
            using var stream = new MemoryStream(bytes, 0, xmlLength, writable: false);
            using var reader = XmlReader.Create(stream, ReaderSettings);
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is XmlException or ArgumentException or InvalidOperationException)
        {
            throw new SaveContainerException($"'{path}' contains malformed XML: {ex.Message}", ex);
        }

        XElement? root = document.Root;
        if (root is null)
        {
            throw new SaveContainerException($"'{path}' has no root element.");
        }

        // The document carries a default namespace, so every lookup goes through LocalName.
        string? formatVersion = FindChild(root, "Version")?.Value;
        XElement? keysElement = FindChild(root, "Keys");
        XElement? valuesElement = FindChild(root, "Values");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        string? structureProblem;

        if (keysElement is null || valuesElement is null)
        {
            structureProblem = keysElement is null && valuesElement is null
                ? "neither a Keys nor a Values element is present"
                : $"the {(keysElement is null ? "Keys" : "Values")} element is missing";
        }
        else
        {
            List<XElement> keys = keysElement.Elements().ToList();
            List<XElement> values = valuesElement.Elements().ToList();

            // A length mismatch means the tail of the longer list has no partner. Pair what
            // can be paired rather than rejecting the whole file, and report the mismatch so a
            // damaged slot does not read as an unused one.
            int pairs = Math.Min(keys.Count, values.Count);
            for (int i = 0; i < pairs; i++)
            {
                entries[keys[i].Value] = values[i].Value;
            }

            structureProblem = keys.Count == values.Count
                ? null
                : $"Keys holds {keys.Count} entries and Values holds {values.Count}, so {Math.Abs(keys.Count - values.Count)} of them have no partner";
        }

        return new SaveContainer(path, entries, padding, formatVersion, structureProblem);
    }

    public static bool TryRead(string path, out SaveContainer? container, out string? error)
    {
        try
        {
            container = Read(path);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            container = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Index of the last occurrence of a byte sequence, or -1.</summary>
    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (int start = haystack.Length - needle.Length; start >= 0; start--)
        {
            int i = 0;
            while (i < needle.Length && haystack[start + i] == needle[i])
            {
                i++;
            }

            if (i == needle.Length)
            {
                return start;
            }
        }

        return -1;
    }

    private static XElement? FindChild(XElement parent, string localName)
    {
        foreach (XElement child in parent.Elements())
        {
            if (string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
            {
                return child;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens read-only and shares read and write access so a running game holding the file
    /// does not make the read fail.
    /// </summary>
    private static byte[] ReadAllBytesShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);

        long length = stream.Length;
        if (length > int.MaxValue)
        {
            throw new IOException($"'{path}' is {length} bytes, too large to read into memory.");
        }

        var buffer = new byte[(int)length];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                // The file shrank under us. Keep what was actually read.
                Array.Resize(ref buffer, offset);
                break;
            }

            offset += read;
        }

        return buffer;
    }
}
