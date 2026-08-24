// Usings sit above the namespace declaration on purpose. RainWorldSaveManager.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Security.Cryptography;
using System.Text;

namespace RainWorldSaveManager.Core.Saves;

/// <summary>
/// The checksum the game stamps on the "save" and "save__Backup" values. A value is stored as
/// the 32 lowercase hex characters of md5(payload + Salt) followed by the payload itself.
/// A mismatch makes the game discard the save, so the digest has to match byte for byte.
/// </summary>
public static class SaveChecksum
{
    /// <summary>The 97 character salt the game appends before hashing.</summary>
    public const string Salt =
        "WY+Nhg+PuYNEz6WVOo9DpOoPZ11fT3DuTU9WigSP9yeKT8U+EQ/EghqPxKqbj8AAIA/pihwPzuncT9L2XI/In50PzpJdj9D4n";

    /// <summary>Length of the hex digest prefix.</summary>
    public const int PrefixLength = 32;

    /// <summary>
    /// MD5 over the UTF-8 bytes of payload + Salt, rendered as lowercase hex.
    /// MD5 is the file format the game itself writes, not a security choice.
    /// </summary>
    public static string Compute(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        byte[] input = Encoding.UTF8.GetBytes(payload + Salt);
        Span<byte> digest = stackalloc byte[16];
        MD5.HashData(input, digest);

        var hex = new StringBuilder(PrefixLength);
        foreach (byte b in digest)
        {
            hex.Append(HexDigits[b >> 4]);
            hex.Append(HexDigits[b & 0x0F]);
        }

        // The pad mirrors the game's own IL. A 16 byte digest is always 32 hex characters,
        // so this never fires in practice.
        return hex.ToString().PadLeft(PrefixLength, '0');
    }

    /// <summary>Returns the stored form: digest followed by the payload.</summary>
    public static string Wrap(string payload) => Compute(payload) + payload;

    /// <summary>True when the first 32 characters are lowercase hex.</summary>
    public static bool HasChecksumPrefix(string value)
    {
        if (value is null || value.Length < PrefixLength)
        {
            return false;
        }

        for (int i = 0; i < PrefixLength; i++)
        {
            char c = value[i];
            bool isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a stored value into digest and payload.
    /// Returns false when there is no digest prefix at all, which means the value is a raw
    /// unchecksummed payload such as the expCore "core" key rather than a corrupt one.
    /// Returns true with checksumValid false only when a digest is present but wrong.
    /// </summary>
    public static bool TryUnwrap(string value, out string payload, out bool checksumValid)
    {
        if (!HasChecksumPrefix(value))
        {
            payload = value ?? "";
            checksumValid = false;
            return false;
        }

        string stored = value.Substring(0, PrefixLength);
        payload = value.Substring(PrefixLength);
        checksumValid = string.Equals(stored, Compute(payload), StringComparison.Ordinal);
        return true;
    }

    private const string HexDigits = "0123456789abcdef";
}
