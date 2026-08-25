// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using RainWorldSaveManager.Core.Saves;

namespace RainWorldSaveManager.Core.Editing;

/// <summary>Where one field sits inside a delimited body, and what it holds.</summary>
/// <param name="Start">Index of the first character of the field.</param>
/// <param name="Length">Characters the field occupies, separators excluded.</param>
/// <param name="Key">The field key.</param>
/// <param name="ValueStart">Index of the first character of the value, or -1 for a bare flag.</param>
/// <param name="ValueLength">Characters the value occupies, or 0 for a bare flag.</param>
internal readonly record struct FieldSpan(int Start, int Length, string Key, int ValueStart, int ValueLength)
{
    /// <summary>True when the field is a bare token such as HASTHEGLOW, which means true by being present.</summary>
    public bool IsFlag => ValueStart < 0;

    public int End => Start + Length;
}

/// <summary>
/// Finds and replaces fields inside one delimited body, by position, leaving every other character
/// of the body where it was.
///
/// Rain World nests the same shape at three levels with three sets of delimiters: a record body
/// splits on &lt;svA&gt;, the DEATHPERSISTENTSAVEDATA value inside it splits on &lt;dpA&gt;, and
/// each splits key from value on the first separator only, because values carry their own angle
/// bracket delimiters. One implementation with the separators passed in keeps the awkward parts,
/// which are the ones about separators, written once.
///
/// Nothing here rebuilds a body from parsed fields. That is the difference between an edit that
/// changes one number and an edit that silently drops whatever this app does not model, which on a
/// save touched by a mod is most of it.
/// </summary>
internal sealed class DelimitedFields
{
    /// <summary>The fields of a record body, such as a SAVE STATE.</summary>
    public static readonly DelimitedFields Record =
        new(SavePayloadReader.FieldSeparator, SavePayloadReader.ValueSeparator);

    /// <summary>The fields inside a DEATHPERSISTENTSAVEDATA value.</summary>
    public static readonly DelimitedFields DeathPersistent =
        new(DeathPersistentReader.FieldSeparator, DeathPersistentReader.ValueSeparator);

    private readonly string _fieldSeparator;
    private readonly string _valueSeparator;

    private DelimitedFields(string fieldSeparator, string valueSeparator)
    {
        _fieldSeparator = fieldSeparator;
        _valueSeparator = valueSeparator;
    }

    /// <summary>Every field of the body, in stored order. Empty fields are skipped, as the readers skip them.</summary>
    public List<FieldSpan> Locate(string body)
    {
        var spans = new List<FieldSpan>();
        if (string.IsNullOrEmpty(body))
        {
            return spans;
        }

        int start = 0;

        while (true)
        {
            int next = body.IndexOf(_fieldSeparator, start, StringComparison.Ordinal);
            int end = next < 0 ? body.Length : next;
            int length = end - start;

            if (length > 0)
            {
                int valueSeparator = body.IndexOf(_valueSeparator, start, length, StringComparison.Ordinal);

                spans.Add(valueSeparator < 0
                    ? new FieldSpan(start, length, body.Substring(start, length), -1, 0)
                    : new FieldSpan(
                        start,
                        length,
                        body.Substring(start, valueSeparator - start),
                        valueSeparator + _valueSeparator.Length,
                        end - valueSeparator - _valueSeparator.Length));
            }

            if (next < 0)
            {
                return spans;
            }

            start = next + _fieldSeparator.Length;
        }
    }

    /// <summary>The nth field with this key, or null when the body has fewer than that many.</summary>
    public FieldSpan? Find(string body, string key, int occurrence = 0)
    {
        int seen = 0;

        foreach (FieldSpan span in Locate(body))
        {
            if (!string.Equals(span.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen == occurrence)
            {
                return span;
            }

            seen++;
        }

        return null;
    }

    public string? GetValue(string body, string key, int occurrence = 0)
    {
        FieldSpan? span = Find(body, key, occurrence);
        if (span is null)
        {
            return null;
        }

        return span.Value.IsFlag ? null : body.Substring(span.Value.ValueStart, span.Value.ValueLength);
    }

    public bool Has(string body, string key) => Find(body, key) is not null;

    /// <summary>
    /// Sets a keyed field, replacing the occurrence if the body has it and appending it if not.
    /// A key stored as a bare flag becomes a keyed field, because that is what was asked for.
    /// </summary>
    public string SetValue(string body, string key, string value, int occurrence = 0)
    {
        FieldSpan? found = Find(body, key, occurrence);

        if (found is null)
        {
            return Append(body, key + _valueSeparator + value);
        }

        FieldSpan span = found.Value;

        return span.IsFlag
            ? Splice(body, span.Start, span.Length, key + _valueSeparator + value)
            : Splice(body, span.ValueStart, span.ValueLength, value);
    }

    /// <summary>
    /// Adds or removes a bare flag. Setting one the body already carries as a keyed field rewrites
    /// it to the bare form, so what the game reads back is a flag either way.
    /// </summary>
    public string SetFlag(string body, string key, bool present)
    {
        FieldSpan? found = Find(body, key);

        if (!present)
        {
            return found is null ? body : RemoveAt(body, found.Value);
        }

        if (found is null)
        {
            return Append(body, key);
        }

        return found.Value.IsFlag ? body : Splice(body, found.Value.Start, found.Value.Length, key);
    }

    /// <summary>Removes one occurrence of a field, along with exactly one of the separators beside it.</summary>
    public string Remove(string body, string key, int occurrence = 0)
    {
        FieldSpan? found = Find(body, key, occurrence);
        return found is null ? body : RemoveAt(body, found.Value);
    }

    /// <summary>Inserts a field directly after one occurrence of a key, or appends when that occurrence is absent.</summary>
    public string InsertAfter(string body, string key, int occurrence, string newField)
    {
        FieldSpan? found = Find(body, key, occurrence);

        return found is null
            ? Append(body, newField)
            : Splice(body, found.Value.End, 0, _fieldSeparator + newField);
    }

    /// <summary>
    /// Appends a field, keeping whatever the body does about a trailing separator. A body that
    /// ends with one still ends with one afterwards, so the shape the game wrote is the shape it
    /// reads back.
    /// </summary>
    public string Append(string body, string field)
    {
        if (string.IsNullOrEmpty(body))
        {
            return field;
        }

        return body.EndsWith(_fieldSeparator, StringComparison.Ordinal)
            ? body + field + _fieldSeparator
            : body + _fieldSeparator + field;
    }

    /// <summary>Builds one field, keyed or bare.</summary>
    public string Field(string key, string? value) => value is null ? key : key + _valueSeparator + value;

    /// <summary>The key half of a field built by <see cref="Field"/>.</summary>
    public string KeyOf(string field)
    {
        int separator = field.IndexOf(_valueSeparator, StringComparison.Ordinal);
        return separator < 0 ? field : field[..separator];
    }

    private string RemoveAt(string body, FieldSpan span)
    {
        // Taking the separator after the field keeps the field before it attached to the one after
        // it. Only the last field has none, and then the separator before it goes instead.
        if (span.End < body.Length)
        {
            return Splice(body, span.Start, span.Length + _fieldSeparator.Length, "");
        }

        if (span.Start >= _fieldSeparator.Length)
        {
            return Splice(body, span.Start - _fieldSeparator.Length, span.Length + _fieldSeparator.Length, "");
        }

        return Splice(body, span.Start, span.Length, "");
    }

    private static string Splice(string text, int start, int length, string replacement)
        => string.Concat(text.AsSpan(0, start), replacement, text.AsSpan(start + length));
}
