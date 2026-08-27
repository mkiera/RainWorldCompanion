// RainWorldCompanion.Core.System exists in this assembly, so a using written inside the namespace
// body would bind "System" to that namespace instead of the BCL root.
using System.Text;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Library;

/// <summary>
/// campaign.bin: the game's own records and nothing else, with no container round them and no
/// checksum on them. It is checked by the library's own digest in entry.json. Written as UTF-8 with
/// no byte order mark, which is what tells one from a save container on sight.
/// </summary>
public static class CampaignFile
{
    /// <summary>What every campaign file starts with, and what tells one from a save container.</summary>
    public static readonly string Prefix = "SAVE STATE" + SavePayloadReader.HeaderSeparator;

    /// <summary>A cap so a file claiming to be enormous is refused before it is decoded.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The SAVE STATE record first, then its map records, closed off with the separator the
    /// game writes after every record.</summary>
    public static string ToPayload(CampaignSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        var records = new List<string>(slice.MapRecords.Count + 1) { slice.SaveStateRecord };
        records.AddRange(slice.MapRecords);

        return string.Join(SavePayloadReader.RecordSeparator, records) + SavePayloadReader.RecordSeparator;
    }

    public static byte[] ToBytes(CampaignSlice slice) => Utf8.GetBytes(ToPayload(slice));

    /// <summary>Reads a campaign back, or null when the text holds no campaign at all.</summary>
    public static CampaignSlice? FromPayload(string? payload)
    {
        if (CampaignSplicer.Campaigns(payload) is not [string slugcat, ..])
        {
            return null;
        }

        return CampaignSplicer.Extract(payload, slugcat);
    }

    /// <summary>Reads a campaign file. Null when the bytes are not one.</summary>
    public static CampaignSlice? Read(byte[]? bytes)
        => bytes is null ? null : FromPayload(Decode(bytes));

    /// <summary>Reads one campaign out of either kind of file. A save container goes through
    /// <see cref="SaveEditSession"/>, which refuses one whose checksum is already wrong, or the
    /// campaign would carry the damage into a slot under a fresh and correct digest.</summary>
    /// <param name="slugcatId">Ignored for a campaign file, which holds exactly one.</param>
    /// <exception cref="SaveContainerException">The file is a save container that cannot be read.</exception>
    public static CampaignSlice? ReadFrom(string filePath, string? slugcatId = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        if (IsOne(filePath))
        {
            return Read(File.ReadAllBytes(filePath));
        }

        return string.IsNullOrWhiteSpace(slugcatId)
            ? null
            : SaveEditSession.Open(filePath).TakeCampaign(slugcatId);
    }

    /// <summary>Whether this file is a campaign on its own rather than a whole save container.</summary>
    public static bool IsOne(string filePath)
    {
        Span<byte> head = stackalloc byte[32];

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return LooksLikeOne(head[..read]);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool LooksLikeOne(ReadOnlySpan<byte> head)
    {
        Span<byte> prefix = stackalloc byte[64];
        int written = Utf8.GetBytes(Prefix, prefix);

        return head.Length >= written && head[..written].SequenceEqual(prefix[..written]);
    }

    /// <summary>Strips a byte order mark if one is there anyway: this app writes none, but a file
    /// that has been through an editor may have gained one.</summary>
    private static string Decode(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
