using RainWorldCompanion.Core.Editing;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Builds options files by splicing a chosen settings stream into the real options.bin fixture,
/// so a test reads a real container shape rather than a hand-written approximation of one.
/// </summary>
public static class OptionsFixture
{
    public const string RecordSeparator = "<optA>";
    public const string KeyValueSeparator = "<optB>";
    public const string ListSeparator = "<optC>";
    public const string PairSeparator = "<optD>";

    /// <summary>Joins records into a settings stream, terminating the last one as the game does.</summary>
    public static string Payload(params string[] records)
        => records.Length == 0 ? "" : string.Join(RecordSeparator, records) + RecordSeparator;

    public static string Record(string key, string value) => key + KeyValueSeparator + value;

    /// <summary>The EnabledMods record. A vanilla install has no such record at all.</summary>
    public static string Enabled(params string[] ids)
        => Record("EnabledMods", string.Join(ListSeparator, ids));

    public static string LoadOrder(params (string Id, string Position)[] pairs)
        => Record("ModLoadOrder", string.Join(
            ListSeparator,
            pairs.Select(pair => pair.Id + PairSeparator + pair.Position)));

    public static byte[] Bytes(string payload)
        => ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Options))
            .WithValue("options", payload)
            .ToBytes();

    public static string WriteInto(TempDirectory directory, string payload, string relativePath = "options")
        => directory.WriteBytes(relativePath, Bytes(payload));
}
