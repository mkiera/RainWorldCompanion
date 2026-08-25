// Usings sit above the namespace declaration on purpose. RainWorldCompanion.Core.System
// exists elsewhere in this assembly, so a using written inside the namespace body would bind
// "System" to that namespace instead of the BCL root.
using System.Text;

using RainWorldCompanion.Core.Editing;
using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Library;

/// <summary>
/// campaign.bin: one campaign stored on its own.
///
/// It holds the game's own records, joined the way the game joins them, and nothing else. That is
/// deliberate: a stored campaign is the characters the slot held, so anything a mod wrote into it
/// comes back out unchanged, and reading one back is the same code that reads a slot.
///
/// There is no container round it and no checksum on it, because it is not a save file and putting
/// one in the save folder would not give the game anything it could load. What checks it is the
/// library's own digest, recorded in entry.json the same way it is for a whole slot.
///
/// UTF-8 with no byte order mark. A save container always starts with one, so leaving it off keeps
/// the two apart on sight, and the first record is always the campaign, which is what makes the
/// leading "SAVE STATE&lt;progDivB&gt;" a signature worth reading.
/// </summary>
public static class CampaignFile
{
    /// <summary>What every campaign file starts with, and what tells one from a save container.</summary>
    public static readonly string Prefix = "SAVE STATE" + SavePayloadReader.HeaderSeparator;

    /// <summary>The largest campaign this will read. A whole slot with nine campaigns in it is a
    /// few megabytes, so one campaign is far below this; the cap is here so a file claiming to be
    /// enormous is refused before it is decoded.</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// The campaign written as a payload: the SAVE STATE record first, then its map records, joined
    /// and closed off with the separator the game writes after every record.
    /// </summary>
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

    /// <summary>
    /// Reads one campaign out of whatever kind of file this is.
    ///
    /// A campaign can be sitting in a live slot, in a backup, in a whole slot kept in the library,
    /// or in a campaign file stored on its own. The first three are save containers and the last is
    /// not, and this is the one place that knows the difference, so a caller pulling a campaign out
    /// of something does not have to.
    ///
    /// A save container is read through <see cref="SaveEditSession"/>, which refuses one whose
    /// checksum is already wrong. That refusal matters as much for a backup as for a live save: a
    /// campaign taken out of a damaged file and written into a slot would carry the damage in with
    /// it under a fresh, correct digest.
    /// </summary>
    /// <param name="slugcatId">
    /// Which campaign to take. Ignored for a campaign file, which holds exactly one.
    /// </param>
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

    /// <summary>Whether these bytes begin the way a campaign file begins.</summary>
    public static bool LooksLikeOne(ReadOnlySpan<byte> head)
    {
        Span<byte> prefix = stackalloc byte[64];
        int written = Utf8.GetBytes(Prefix, prefix);

        return head.Length >= written && head[..written].SequenceEqual(prefix[..written]);
    }

    /// <summary>
    /// Decodes with the mark stripped if one is there anyway. This app writes none, but a file that
    /// has been through an editor may have gained one, and refusing to read it would be refusing
    /// over a difference the game itself does not care about.
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}
