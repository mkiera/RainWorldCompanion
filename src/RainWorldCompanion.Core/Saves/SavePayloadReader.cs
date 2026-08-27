namespace RainWorldCompanion.Core.Saves;

/// <summary>One record inside a progression payload: a header and the body that follows it.</summary>
public sealed record SaveRecord(string Header, string Body);

/// <summary>Where one record sits inside a payload, without having copied it out. A live sav file is
/// a 1.4 million character payload, most of it MAP_ bodies a reader discards, so the header is
/// compared where it lies and only a record that matches is materialised.</summary>
public readonly struct RecordSpan
{
    private readonly string _payload;
    private readonly int _headerStart;
    private readonly int _headerLength;
    private readonly int _bodyStart;
    private readonly int _bodyLength;

    internal RecordSpan(string payload, int headerStart, int headerLength, int bodyStart, int bodyLength)
    {
        _payload = payload;
        _headerStart = headerStart;
        _headerLength = headerLength;
        _bodyStart = bodyStart;
        _bodyLength = bodyLength;
    }

    public int HeaderStart => _headerStart;

    public int HeaderLength => _headerLength;

    /// <summary>An editor splices records by these offsets rather than by splitting the payload up
    /// and joining it back, so the characters it does not mean to change are never rewritten.</summary>
    public int BodyStart => _bodyStart;

    public int BodyLength => _bodyLength;

    /// <summary>True when the header is exactly this text. Allocates nothing.</summary>
    public bool HeaderIs(string header)
        => _payload is not null
        && _headerLength == header.Length
        && string.CompareOrdinal(_payload, _headerStart, header, 0, header.Length) == 0;

    public string Header() => _payload is null ? "" : _payload.Substring(_headerStart, _headerLength);

    /// <summary>Copied out, so only call this on a record you want.</summary>
    public string Body() => _payload is null ? "" : _payload.Substring(_bodyStart, _bodyLength);
}

/// <summary>The payload is delimiter separated text, not XML, so every split is ordinal.</summary>
public static class SavePayloadReader
{
    public const string RecordSeparator = "<progDivA>";
    public const string HeaderSeparator = "<progDivB>";
    public const string FieldSeparator = "<svA>";
    public const string ValueSeparator = "<svB>";

    /// <summary>Splits on &lt;progDivA&gt;, then header from body on the first &lt;progDivB&gt;. A
    /// part with no &lt;progDivB&gt; is all header. Empty headers are kept: every sav file starts
    /// with one and it has to round-trip.</summary>
    public static IReadOnlyList<SaveRecord> SplitRecords(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            return Array.Empty<SaveRecord>();
        }

        string[] parts = payload.Split(RecordSeparator, StringSplitOptions.None);
        var records = new List<SaveRecord>(parts.Length);

        foreach (string part in parts)
        {
            int split = part.IndexOf(HeaderSeparator, StringComparison.Ordinal);
            if (split < 0)
            {
                records.Add(new SaveRecord(part, ""));
                continue;
            }

            string header = part.Substring(0, split);
            string body = part.Substring(split + HeaderSeparator.Length);
            records.Add(new SaveRecord(header, body));
        }

        return records;
    }

    /// <summary>The same split as <see cref="SplitRecords"/>, reported as positions inside the
    /// payload rather than copies of it.</summary>
    public static IEnumerable<RecordSpan> EnumerateRecords(string payload)
    {
        if (string.IsNullOrEmpty(payload))
        {
            yield break;
        }

        int start = 0;

        while (true)
        {
            int next = payload.IndexOf(RecordSeparator, start, StringComparison.Ordinal);
            int end = next < 0 ? payload.Length : next;

            int split = payload.IndexOf(HeaderSeparator, start, end - start, StringComparison.Ordinal);
            if (split < 0)
            {
                yield return new RecordSpan(payload, start, end - start, end, 0);
            }
            else
            {
                int bodyStart = split + HeaderSeparator.Length;
                yield return new RecordSpan(payload, start, split - start, bodyStart, end - bodyStart);
            }

            if (next < 0)
            {
                yield break;
            }

            start = next + RecordSeparator.Length;
        }
    }

    /// <summary>Splits on &lt;svA&gt;, then each field on its FIRST &lt;svB&gt; only, because values
    /// carry their own angle bracket delimiters. A field with no &lt;svB&gt; is a bare flag and gets
    /// a null value. Keys can repeat, hence a list rather than a dictionary.</summary>
    public static IReadOnlyList<KeyValuePair<string, string?>> SplitFields(string body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return Array.Empty<KeyValuePair<string, string?>>();
        }

        string[] parts = body.Split(FieldSeparator, StringSplitOptions.None);
        var fields = new List<KeyValuePair<string, string?>>(parts.Length);

        foreach (string part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            int split = part.IndexOf(ValueSeparator, StringComparison.Ordinal);
            if (split < 0)
            {
                fields.Add(new KeyValuePair<string, string?>(part, null));
                continue;
            }

            string key = part.Substring(0, split);
            string value = part.Substring(split + ValueSeparator.Length);
            fields.Add(new KeyValuePair<string, string?>(key, value));
        }

        return fields;
    }
}
