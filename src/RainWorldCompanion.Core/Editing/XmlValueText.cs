// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;

namespace RainWorldCompanion.Core.Editing;

/// <summary>
/// The escaping the game's serializer applies to a hashtable value, reproduced exactly.
///
/// A save payload is delimited text whose delimiters are angle brackets, so every value in the
/// container arrives escaped: one real sav2 holds 1474 <c>&amp;lt;</c> and 1474 <c>&amp;gt;</c>.
/// An editor that writes a value back has to escape it the same way the writer that produced the
/// rest of the file did, or the file stops being byte-identical everywhere it was not edited.
///
/// The rules are those of <c>XmlUtf8RawTextWriter</c> writing element content: ampersand, less
/// than and greater than become entities, and a carriage return becomes a numeric reference
/// because an unescaped one would be normalised away to a newline by any reader. Quotes and tabs
/// are attribute-only escapes and are left alone here, which is why round-tripping a real file
/// through <see cref="Unescape"/> and <see cref="Escape"/> returns the original characters.
/// </summary>
public static class XmlValueText
{
    /// <summary>Escapes text for use as XML element content.</summary>
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

        // Most values need escaping somewhere, but scanning first keeps the common short value
        // (a slugcat name, a cycle number) from allocating a builder it never appends to.
        if (value.AsSpan().IndexOfAny('&', '<', '>') < 0 && !value.Contains('\r'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 32);
        foreach (char c in value)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '\r':
                    builder.Append("&#xD;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Turns XML element content back into the text it stands for.
    ///
    /// This accepts more than <see cref="Escape"/> produces, because it reads files written by the
    /// game rather than by this app: named entities the writer never emits and numeric references
    /// in either base still have to decode. An ampersand that starts nothing recognisable is left
    /// as a literal ampersand, which is what a reader does with it.
    /// </summary>
    public static string Unescape(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('&'))
        {
            return text ?? "";
        }

        var builder = new StringBuilder(text.Length);
        int index = 0;

        while (index < text.Length)
        {
            char c = text[index];
            if (c != '&')
            {
                builder.Append(c);
                index++;
                continue;
            }

            int semicolon = text.IndexOf(';', index + 1);

            // A run without a semicolon inside the length any real entity could occupy is a
            // literal ampersand, not the start of one that was cut off.
            if (semicolon < 0 || semicolon - index > 12)
            {
                builder.Append(c);
                index++;
                continue;
            }

            string entity = text.Substring(index + 1, semicolon - index - 1);
            if (TryDecodeEntity(entity, out string decoded))
            {
                builder.Append(decoded);
                index = semicolon + 1;
                continue;
            }

            builder.Append(c);
            index++;
        }

        return builder.ToString();
    }

    private static bool TryDecodeEntity(string entity, out string decoded)
    {
        switch (entity)
        {
            case "lt":
                decoded = "<";
                return true;
            case "gt":
                decoded = ">";
                return true;
            case "amp":
                decoded = "&";
                return true;
            case "quot":
                decoded = "\"";
                return true;
            case "apos":
                decoded = "'";
                return true;
        }

        if (entity.Length > 1 && entity[0] == '#')
        {
            bool hex = entity[1] is 'x' or 'X';
            string digits = hex ? entity[2..] : entity[1..];

            if (digits.Length > 0
                && int.TryParse(
                    digits,
                    hex ? NumberStyles.HexNumber : NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out int code)
                && code is >= 0 and <= 0x10FFFF
                && !(code is >= 0xD800 and <= 0xDFFF))
            {
                decoded = char.ConvertFromUtf32(code);
                return true;
            }
        }

        decoded = "";
        return false;
    }
}
