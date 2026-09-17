using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Core.Editing;

public sealed record DevourmentContents(IReadOnlyList<string?> Entries)
{
    private const string SaveStateHeader = "SAVE STATE";

    public int Count => Entries.Count;

    public static DevourmentContents Capture(CampaignSlice campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        SaveRecord? record = SavePayloadReader.SplitRecords(campaign.SaveStateRecord)
            .FirstOrDefault(candidate => string.Equals(candidate.Header, SaveStateHeader, StringComparison.Ordinal));

        if (record is null)
        {
            throw new SaveContainerException("The source campaign has no SAVE STATE record to read Devourment contents from.");
        }

        return new DevourmentContents(
            SavePayloadReader.SplitFields(record.Body)
                .Where(field => string.Equals(field.Key, DevourmentEditState.EntryField, StringComparison.Ordinal))
                .Select(field => field.Value)
                .ToArray());
    }
}
