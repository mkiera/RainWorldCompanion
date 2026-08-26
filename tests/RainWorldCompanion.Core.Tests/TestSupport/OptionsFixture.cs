using RainWorldCompanion.Core.Editing;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Builds options files for tests by splicing a chosen settings stream into the real
/// options.bin fixture. The container around the stream is therefore the game's own, so a test
/// that reads one is reading a real file shape and not a hand-written approximation of it.
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

    /// <summary>One <c>Key&lt;optB&gt;value</c> record.</summary>
    public static string Record(string key, string value) => key + KeyValueSeparator + value;

    /// <summary>The EnabledMods record. A vanilla install has no such record at all.</summary>
    public static string Enabled(params string[] ids)
        => Record("EnabledMods", string.Join(ListSeparator, ids));

    /// <summary>The ModLoadOrder record, written as the game writes it: id, position, repeat.</summary>
    public static string LoadOrder(params (string Id, string Position)[] pairs)
        => Record("ModLoadOrder", string.Join(
            ListSeparator,
            pairs.Select(pair => pair.Id + PairSeparator + pair.Position)));

    /// <summary>Container bytes holding a chosen settings stream.</summary>
    public static byte[] Bytes(string payload)
        => ContainerText.Load(FixtureFiles.Bytes(FixtureFiles.Options))
            .WithValue("options", payload)
            .ToBytes();

    /// <summary>Writes an options file into a directory that stands in for the save folder.</summary>
    public static string WriteInto(TempDirectory directory, string payload, string relativePath = "options")
        => directory.WriteBytes(relativePath, Bytes(payload));
}
