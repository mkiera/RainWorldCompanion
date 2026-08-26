// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text;

using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

public enum SizePolicy
{
    /// <summary>Refuses when the new text no longer fits the length the file was read at.</summary>
    PreserveLength,

    /// <summary>Keeps the original length while the text fits, and grows past it when it does not.</summary>
    GrowIfNeeded,
}

/// <summary>
/// Re-serialising a container through an XML writer would not land on the same bytes: the game's
/// serializer stamps <c>z:Id</c> bookkeeping whose numbering, attribute order and namespace
/// placement would all have to be reproduced. XML text content can never hold a raw <c>&lt;</c>, so
/// a value runs from the end of its opening tag to the very next <c>&lt;</c>, with no parsing.
/// </summary>
public sealed class ContainerText
{
    private const string ClosingTag = "</ArrayOfKeyValueOfanyTypeanyType>";

    /// <summary>A tripwire for an edit gone wrong in a way that produces text rather than an
    /// exception. The biggest real container measured is a 6 MB sav.</summary>
    public const int MaximumLength = 32 * 1024 * 1024;

    // Decoding throws rather than substituting U+FFFD: a container that is not valid UTF-8 cannot
    // be re-encoded to the bytes it came from.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _preamble;
    private readonly string _xml;
    private readonly int _paddingByteCount;
    private readonly List<string> _keys;
    private readonly List<TextSpan> _valueSpans;

    private ContainerText(byte[] preamble, string xml, int paddingByteCount, int originalLength)
    {
        _preamble = preamble;
        _xml = xml;
        _paddingByteCount = paddingByteCount;
        OriginalLength = originalLength;

        _keys = new List<string>();
        _valueSpans = new List<TextSpan>();
        ScanEntries(xml, _keys, _valueSpans);
    }

    /// <summary>Length in bytes of the file this was loaded from.</summary>
    public int OriginalLength { get; }

    public int PaddingByteCount => _paddingByteCount;

    /// <summary>Keys in the order the file stores them, which is not alphabetical and not stable
    /// between files: a real sav2 stores save__Backup before save.</summary>
    public IReadOnlyList<string> Keys => _keys;

    /// <exception cref="SaveContainerException">The bytes are not a container this can edit.</exception>
    public static ContainerText Load(byte[] fileBytes)
    {
        if (fileBytes is null)
        {
            throw new SaveContainerException("No save bytes were given.");
        }

        byte[] preamble = HasUtf8Preamble(fileBytes) ? fileBytes[..3] : Array.Empty<byte>();

        // The cut is made in the byte array: decoding first would decode megabytes of padding to
        // find out it was padding.
        int tagIndex = LastIndexOf(fileBytes, ClosingTag);
        if (tagIndex < 0)
        {
            throw new SaveContainerException("These bytes are not a save container: closing tag missing.");
        }

        int xmlEnd = tagIndex + ClosingTag.Length;
        int padding = fileBytes.Length - xmlEnd;

        string xml;
        try
        {
            xml = StrictUtf8.GetString(fileBytes, preamble.Length, xmlEnd - preamble.Length);
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            throw new SaveContainerException($"The save container is not valid UTF-8, so it cannot be rewritten: {ex.Message}", ex);
        }

        return new ContainerText(preamble, xml, padding, fileBytes.Length);
    }

    /// <summary>The value exactly as the file stores it, escaping included.</summary>
    public string GetValueRaw(string key)
    {
        TextSpan span = SpanFor(key);
        return _xml.Substring(span.Start, span.Length);
    }

    public string GetValue(string key) => XmlValueText.Unescape(GetValueRaw(key));

    public bool ContainsKey(string key) => IndexOfKey(key) >= 0;

    /// <exception cref="SaveContainerException">The key is not in this container.</exception>
    public ContainerText WithValue(string key, string newValue)
    {
        TextSpan span = SpanFor(key);
        string escaped = XmlValueText.Escape(newValue ?? "");

        var builder = new StringBuilder(_xml.Length - span.Length + escaped.Length);
        builder.Append(_xml, 0, span.Start);
        builder.Append(escaped);
        builder.Append(_xml, span.Start + span.Length, _xml.Length - span.Start - span.Length);

        return new ContainerText(_preamble, builder.ToString(), _paddingByteCount, OriginalLength);
    }

    /// <summary>The preamble, the XML, then NUL padding. The padding carries no meaning to the game,
    /// which stops at the closing tag, but keeping it means Steam Cloud sees the same file size.</summary>
    /// <exception cref="SaveContainerException">The text no longer fits the policy.</exception>
    public byte[] ToBytes(SizePolicy policy = SizePolicy.GrowIfNeeded)
    {
        byte[] body = StrictUtf8.GetBytes(_xml);
        int contentLength = _preamble.Length + body.Length;

        if (contentLength > MaximumLength)
        {
            throw new SaveContainerException(
                $"The edited save would be {contentLength} bytes, past the {MaximumLength} byte limit this app will write. Nothing was written.");
        }

        int totalLength;
        if (contentLength <= OriginalLength)
        {
            totalLength = OriginalLength;
        }
        else if (policy == SizePolicy.PreserveLength)
        {
            throw new SaveContainerException(
                $"The edited save is {contentLength} bytes and the file it came from is {OriginalLength}, so it cannot be written at the original length.");
        }
        else
        {
            totalLength = contentLength + _paddingByteCount;
        }

        var result = new byte[totalLength];
        Buffer.BlockCopy(_preamble, 0, result, 0, _preamble.Length);
        Buffer.BlockCopy(body, 0, result, _preamble.Length, body.Length);
        return result;
    }

    private TextSpan SpanFor(string key)
    {
        int index = IndexOfKey(key);
        if (index < 0)
        {
            throw new SaveContainerException($"The save container has no '{key}' entry.");
        }

        if (index >= _valueSpans.Count)
        {
            throw new SaveContainerException($"The save container has a '{key}' key with no value beside it.");
        }

        return _valueSpans[index];
    }

    private int IndexOfKey(string key)
    {
        for (int i = 0; i < _keys.Count; i++)
        {
            if (string.Equals(_keys[i], key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static void ScanEntries(string xml, List<string> keys, List<TextSpan> valueSpans)
    {
        foreach (TextSpan span in ChildSpans(xml, "Keys"))
        {
            keys.Add(XmlValueText.Unescape(xml.Substring(span.Start, span.Length)));
        }

        // An empty hashtable is legitimate: exp1 holds no keys at all.
        valueSpans.AddRange(ChildSpans(xml, "Values"));
    }

    private static List<TextSpan> ChildSpans(string xml, string arrayName)
    {
        var spans = new List<TextSpan>();

        if (!TryFindElement(xml, 0, arrayName, out ElementSpan array) || array.SelfClosing)
        {
            return spans;
        }

        int cursor = array.ContentStart;
        while (TryFindElement(xml, cursor, "anyType", out ElementSpan child) && child.TagStart < array.ContentEnd)
        {
            spans.Add(child.SelfClosing
                ? new TextSpan(child.ContentStart, 0)
                : new TextSpan(child.ContentStart, child.ContentEnd - child.ContentStart));

            cursor = child.SelfClosing ? child.ContentStart : child.ContentEnd;
        }

        return spans;
    }

    /// <summary>The name is matched against a tag opening rather than searched for as text, so a
    /// value holding "&lt;Values" cannot be mistaken for the element. Attribute quoting is respected,
    /// because an attribute is the one place a &gt; can sit inside a tag without ending it.</summary>
    private static bool TryFindElement(string xml, int from, string localName, out ElementSpan element)
    {
        int cursor = from;

        while (cursor < xml.Length)
        {
            int open = xml.IndexOf('<', cursor);
            if (open < 0 || open + 1 >= xml.Length)
            {
                break;
            }

            if (!MatchesName(xml, open + 1, localName))
            {
                cursor = open + 1;
                continue;
            }

            int tagEnd = EndOfTag(xml, open);
            if (tagEnd < 0)
            {
                break;
            }

            bool selfClosing = xml[tagEnd - 1] == '/';
            int contentStart = tagEnd + 1;

            if (selfClosing)
            {
                element = new ElementSpan(open, contentStart, contentStart, true);
                return true;
            }

            // The close is located by name rather than by the first <, which may begin a child.
            int close = IndexOfCloseTag(xml, contentStart, localName);
            if (close < 0)
            {
                break;
            }

            element = new ElementSpan(open, contentStart, close, false);
            return true;
        }

        element = default;
        return false;
    }

    private static bool MatchesName(string xml, int start, string localName)
    {
        if (start + localName.Length > xml.Length
            || string.CompareOrdinal(xml, start, localName, 0, localName.Length) != 0)
        {
            return false;
        }

        // The name has to end here. Without this, "Keys" would match the opening of a "KeysExtra".
        int after = start + localName.Length;
        if (after >= xml.Length)
        {
            return false;
        }

        char c = xml[after];
        return c is '>' or '/' || char.IsWhiteSpace(c);
    }

    private static int EndOfTag(string xml, int tagStart)
    {
        char quote = '\0';

        for (int i = tagStart + 1; i < xml.Length; i++)
        {
            char c = xml[i];

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c == '>')
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfCloseTag(string xml, int from, string localName)
    {
        int cursor = from;

        while (cursor < xml.Length)
        {
            int candidate = xml.IndexOf("</", cursor, StringComparison.Ordinal);
            if (candidate < 0)
            {
                return -1;
            }

            if (MatchesName(xml, candidate + 2, localName))
            {
                return candidate;
            }

            cursor = candidate + 2;
        }

        return -1;
    }

    private static bool HasUtf8Preamble(byte[] bytes)
        => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static int LastIndexOf(byte[] haystack, string asciiNeedle)
    {
        for (int start = haystack.Length - asciiNeedle.Length; start >= 0; start--)
        {
            int i = 0;
            while (i < asciiNeedle.Length && haystack[start + i] == (byte)asciiNeedle[i])
            {
                i++;
            }

            if (i == asciiNeedle.Length)
            {
                return start;
            }
        }

        return -1;
    }

    private readonly record struct TextSpan(int Start, int Length);

    private readonly record struct ElementSpan(int TagStart, int ContentStart, int ContentEnd, bool SelfClosing);
}
