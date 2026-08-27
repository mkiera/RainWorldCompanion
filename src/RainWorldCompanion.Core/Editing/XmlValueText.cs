// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Globalization;
using System.Text;

namespace RainWorldCompanion.Core.Editing;

/// <summary>
/// The escaping <c>XmlUtf8RawTextWriter</c> applies to element content, reproduced exactly so the
/// file stays byte-identical everywhere it was not edited. A carriage return becomes a numeric
/// reference, because an unescaped one would be normalised away to a newline by any reader. Quotes
/// and tabs are attribute-only escapes and are left alone.
/// </summary>
public static class XmlValueText
{
    public static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? "";
        }

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

    /// <summary>Accepts more than <see cref="Escape"/> produces, because it reads files the game
    /// wrote. An ampersand that starts nothing recognisable is left as a literal ampersand.</summary>
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

            // No semicolon within the length any real entity could occupy: a literal ampersand.
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
