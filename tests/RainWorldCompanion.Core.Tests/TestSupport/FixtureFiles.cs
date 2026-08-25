using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

using RainWorldCompanion.Core.System;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Locates the byte-exact save fixtures copied next to the test assembly, and decodes them
/// with a parser written independently of the one under test. Real-data expectations therefore
/// cross-check the production reader instead of restating it.
/// </summary>
public static class FixtureFiles
{
    public const string Sav2 = "sav2.bin";
    public const string Sav3 = "sav3.bin";
    public const string Exp1 = "exp1.bin";
    public const string ExpCore1 = "expCore1.bin";
    public const string OnlineSav = "online_sav.bin";
    public const string Options = "options.bin";

    public const string ClosingTag = "</ArrayOfKeyValueOfanyTypeanyType>";

    public static string Root => System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string PathTo(string fixtureName) => System.IO.Path.Combine(Root, fixtureName);

    public static byte[] Bytes(string fixtureName) => File.ReadAllBytes(PathTo(fixtureName));

    /// <summary>Copies a fixture into a temp directory under a chosen name and returns the full path.</summary>
    public static string CopyTo(TempDirectory directory, string fixtureName, string relativePath)
        => directory.CopyFrom(PathTo(fixtureName), relativePath);

    /// <summary>UTF-8 text of a container with the BOM removed and the NUL padding still attached.</summary>
    public static string DecodeText(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    public static int PaddingCharCount(string fixtureName)
    {
        var text = DecodeText(Bytes(fixtureName));
        var end = text.LastIndexOf(ClosingTag, StringComparison.Ordinal) + ClosingTag.Length;
        return text.Length - end;
    }

    /// <summary>Hashtable entries of a fixture, key to raw value, parsed by local name.</summary>
    public static Dictionary<string, string> ReadEntries(string fixtureName)
    {
        var text = DecodeText(Bytes(fixtureName));
        var end = text.LastIndexOf(ClosingTag, StringComparison.Ordinal) + ClosingTag.Length;
        var root = XDocument.Parse(text[..end]).Root!;

        var keys = ChildValues(root, "Keys");
        var values = ChildValues(root, "Values");

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < keys.Count && i < values.Count; i++)
        {
            entries[keys[i]] = values[i];
        }

        return entries;
    }

    public static string? ReadVersion(string fixtureName)
    {
        var text = DecodeText(Bytes(fixtureName));
        var end = text.LastIndexOf(ClosingTag, StringComparison.Ordinal) + ClosingTag.Length;
        var root = XDocument.Parse(text[..end]).Root!;
        return root.Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
    }

    /// <summary>Value of a fixture key with any 32-character checksum prefix removed.</summary>
    public static string ReadPayload(string fixtureName, string key)
    {
        var value = ReadEntries(fixtureName)[key];
        return SyntheticSave.LooksChecksummed(value) ? value[32..] : value;
    }

    private static List<string> ChildValues(XElement root, string localName)
    {
        var container = root.Elements().FirstOrDefault(e => e.Name.LocalName == localName);
        return container is null
            ? new List<string>()
            : container.Elements().Select(e => e.Value).ToList();
    }
}

/// <summary>
/// Builds save containers byte-for-byte in the shape the game writes them, so edge cases
/// (missing closing tag, wrong digest, empty hashtable) come from generated files rather
/// than from mutated fixtures.
/// </summary>
public static class SyntheticSave
{
    public const string ClosingTag = "</ArrayOfKeyValueOfanyTypeanyType>";

    /// <summary>The salt is duplicated here on purpose so the production constant is checked, not trusted.</summary>
    public const string Salt = "WY+Nhg+PuYNEz6WVOo9DpOoPZ11fT3DuTU9WigSP9yeKT8U+EQ/EghqPxKqbj8AAIA/pihwPzuncT9L2XI/In50PzpJdj9D4n";

    public const string RecordSeparator = "<progDivA>";
    public const string HeaderSeparator = "<progDivB>";
    public const string FieldSeparator = "<svA>";
    public const string ValueSeparator = "<svB>";
    public const string DevourmentSeparator = "<dvD>";

    public static KeyValuePair<string, string> Entry(string key, string value) => new(key, value);

    public static string ComputeChecksum(string payload)
    {
        var digest = MD5.HashData(Encoding.UTF8.GetBytes(payload + Salt));
        var hex = new StringBuilder(32);
        foreach (var b in digest)
        {
            hex.Append(b.ToString("x2"));
        }

        return hex.ToString();
    }

    public static string Wrap(string payload) => ComputeChecksum(payload) + payload;

    public static bool LooksChecksummed(string value)
    {
        if (value.Length < 32)
        {
            return false;
        }

        for (var i = 0; i < 32; i++)
        {
            var c = value[i];
            var isHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isHex)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A value with a well-formed hex prefix that does not match the payload.</summary>
    public static string WrapWithBadChecksum(string payload)
    {
        var good = ComputeChecksum(payload);
        var firstChar = good[0] == '0' ? '1' : '0';
        return firstChar + good[1..] + payload;
    }

    public static string Xml(IReadOnlyList<KeyValuePair<string, string>> entries, string version = "8", int? hashSize = null)
    {
        var size = hashSize ?? (entries.Count == 0 ? 3 : 7);
        var sb = new StringBuilder();

        sb.Append("<ArrayOfKeyValueOfanyTypeanyType xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"")
          .Append(" xmlns:x=\"http://www.w3.org/2001/XMLSchema\" z:Id=\"1\" z:Type=\"System.Collections.Hashtable\"")
          .Append(" z:Assembly=\"0\" xmlns:z=\"http://schemas.microsoft.com/2003/10/Serialization/\"")
          .Append(" xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\">");
        sb.Append("<LoadFactor z:Id=\"2\" z:Type=\"System.Single\" z:Assembly=\"0\" xmlns=\"\">0.72</LoadFactor>");
        sb.Append("<Version z:Id=\"3\" z:Type=\"System.Int32\" z:Assembly=\"0\" xmlns=\"\">").Append(version).Append("</Version>");
        sb.Append("<Comparer i:nil=\"true\" xmlns=\"\" />");
        sb.Append("<HashCodeProvider i:nil=\"true\" xmlns=\"\" />");
        sb.Append("<HashSize z:Id=\"4\" z:Type=\"System.Int32\" z:Assembly=\"0\" xmlns=\"\">").Append(size).Append("</HashSize>");

        var nextId = 7;
        AppendArray(sb, "Keys", 5, entries.Select(e => e.Key).ToList(), ref nextId);
        AppendArray(sb, "Values", 6, entries.Select(e => e.Value).ToList(), ref nextId);

        sb.Append(ClosingTag);
        return sb.ToString();
    }

    public static byte[] Bytes(IReadOnlyList<KeyValuePair<string, string>> entries, int paddingBytes = 0, string version = "8", int? hashSize = null)
        => Encode(Xml(entries, version, hashSize), paddingBytes);

    /// <summary>A container whose text stops before the closing tag, which the reader has to reject.</summary>
    public static byte[] BytesWithoutClosingTag(IReadOnlyList<KeyValuePair<string, string>> entries, int paddingBytes = 0)
    {
        var xml = Xml(entries);
        var cut = xml.LastIndexOf(ClosingTag, StringComparison.Ordinal);
        return Encode(xml[..cut], paddingBytes);
    }

    /// <summary>
    /// A container whose Values element has lost its last child while Keys kept all of its own,
    /// which is the shape a write truncated part way through leaves behind. The z:Size attribute
    /// still claims the original count, exactly as it would on a real file.
    /// </summary>
    public static byte[] BytesWithAnUnpairedKey(IReadOnlyList<KeyValuePair<string, string>> entries, int paddingBytes = 0)
    {
        var xml = Xml(entries);
        var valuesStart = xml.IndexOf("<Values", StringComparison.Ordinal);
        var valuesEnd = xml.IndexOf("</Values>", valuesStart, StringComparison.Ordinal);
        var lastChild = xml.LastIndexOf("<anyType", valuesEnd, StringComparison.Ordinal);

        return Encode(xml[..lastChild] + xml[valuesEnd..], paddingBytes);
    }

    /// <summary>Has the closing tag, so truncation succeeds and the XML parse is what fails.</summary>
    public static byte[] MalformedXmlBytes()
        => Encode("<ArrayOfKeyValueOfanyTypeanyType><Keys z:Size=\"oops\"</ArrayOfKeyValueOfanyTypeanyType>", 0);

    public static byte[] GarbageBytes()
    {
        var bytes = new byte[512];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 37) % 251 + 1);
        }

        return bytes;
    }

    /// <summary>Joins records the way the game does, including the trailing separator that yields an empty record.</summary>
    public static string Progression(IEnumerable<(string Header, string Body)> records, bool trailingEmptyRecord = true)
    {
        var joined = string.Join(RecordSeparator, records.Select(r => r.Header + HeaderSeparator + r.Body));
        return trailingEmptyRecord ? joined + RecordSeparator : joined;
    }

    public static string SaveStateBody(
        string slugcat = "White",
        int cycle = 17,
        int food = 3,
        string den = "SU_S04",
        string seed = "8840",
        int devourmentStates = 0,
        bool hasGlow = false)
    {
        var fields = new List<string>
        {
            "SAV STATE NUMBER" + ValueSeparator + slugcat,
            "TIMELINE" + ValueSeparator + slugcat,
            "SEED" + ValueSeparator + seed,
            "DENPOS" + ValueSeparator + den,
            "CYCLENUM" + ValueSeparator + cycle.ToString(),
            "FOOD" + ValueSeparator + food.ToString(),
        };

        if (hasGlow)
        {
            fields.Add("HASTHEGLOW");
        }

        for (var i = 0; i < devourmentStates; i++)
        {
            fields.Add("DEVOURMENTSTATE" + ValueSeparator + string.Join(
                DevourmentSeparator, "Pred" + i, "Prey" + i, "Digesting", (i + 1).ToString()));
        }

        return string.Join(FieldSeparator, fields);
    }

    /// <summary>A payload shaped like a real sav file: a SAVE STATE plus map and misc records.</summary>
    public static string SavePayload(
        string slugcat = "White",
        int cycle = 17,
        int food = 3,
        string den = "SU_S04",
        string seed = "8840",
        int devourmentStates = 0,
        bool hasGlow = false)
        => Progression(new[]
        {
            ("SAVE STATE", SaveStateBody(slugcat, cycle, food, den, seed, devourmentStates, hasGlow)),
            ("MAP_" + slugcat, "SU_A01<mapA>1"),
            ("MAPUPDATE_" + slugcat, "SU_A01"),
            ("MISCPROG", "CYCLES<misA>" + cycle),
        });

    /// <summary>A whole sav container holding the payload under both save and save__Backup.</summary>
    public static byte[] SaveFile(string payload, int paddingBytes = 0)
        => Bytes(new[] { Entry("save__Backup", Wrap(payload)), Entry("save", Wrap(payload)) }, paddingBytes);

    private static void AppendArray(StringBuilder sb, string name, int arrayId, IReadOnlyList<string> items, ref int nextId)
    {
        sb.Append('<').Append(name)
          .Append(" z:Id=\"").Append(arrayId)
          .Append("\" z:Type=\"System.Object[]\" z:Assembly=\"0\" z:Size=\"").Append(items.Count)
          .Append("\" xmlns=\"\"");

        if (items.Count == 0)
        {
            sb.Append(" />");
            return;
        }

        sb.Append('>');
        foreach (var item in items)
        {
            sb.Append("<anyType i:type=\"x:string\" z:Id=\"").Append(nextId++)
              .Append("\" xmlns=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\">")
              .Append(Escape(item))
              .Append("</anyType>");
        }

        sb.Append("</").Append(name).Append('>');
    }

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static byte[] Encode(string text, int paddingBytes)
    {
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = encoding.GetPreamble();
        var body = encoding.GetBytes(text);

        var result = new byte[preamble.Length + body.Length + Math.Max(0, paddingBytes)];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }
}

/// <summary>
/// Builds a temp directory laid out like the live save folder, decoys included. Backup scope,
/// backup creation and restore all run against this same tree.
/// </summary>
public static class SaveTree
{
    /// <summary>What a backup must copy.</summary>
    public static readonly string[] InScope =
    {
        "sav",
        "sav2",
        "sav3",
        "exp1",
        "expCore1",
        "online_sav",
        @"ModConfigs\devourment.txt",
        @"ModConfigs\moreslugcats.txt",
        @"ModConfigs\DvrmentConfs\current.json",
        @"dvrmentSaveStates\contents_0_White_story.txt",
        @"dvrmentSaveStates\contents_2_White_story.txt",
    };

    /// <summary>What a backup must leave alone, decoys first.</summary>
    public static readonly string[] OutOfScope =
    {
        "sav - Copy",
        "sav - Copy (2)",
        "sav.bak",
        "options",
        "steam_autocloud.vdf",
        @"ModConfigs\steam_autocloud.vdf",
        @"backup\2026-08-24_120000\sav",
    };

    /// <summary>The file that starts out empty, which a size filter would wrongly drop.</summary>
    public const string EmptyStoryFile = @"dvrmentSaveStates\contents_2_White_story.txt";

    public static void Populate(TempDirectory directory)
    {
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav2");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav3, "sav3");
        FixtureFiles.CopyTo(directory, FixtureFiles.Exp1, "exp1");
        FixtureFiles.CopyTo(directory, FixtureFiles.ExpCore1, "expCore1");
        FixtureFiles.CopyTo(directory, FixtureFiles.OnlineSav, "online_sav");
        FixtureFiles.CopyTo(directory, FixtureFiles.Options, "options");

        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav - Copy");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav - Copy (2)");
        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, "sav.bak");

        directory.WriteText("steam_autocloud.vdf", "\"RootPaths\"\n{\n}\n");
        directory.WriteText(@"ModConfigs\steam_autocloud.vdf", "\"RootPaths\"\n{\n}\n");
        directory.WriteText(@"ModConfigs\devourment.txt", "PredatorMode<optB>true<optA>Difficulty<optB>2");
        directory.WriteText(@"ModConfigs\moreslugcats.txt", "SomeOtherMod<optB>1");
        directory.WriteText(@"ModConfigs\DvrmentConfs\current.json", "{\"preset\":\"default\",\"struggle\":0.5}");
        directory.WriteText(@"dvrmentSaveStates\contents_0_White_story.txt", "White|Slugcat|0|stomach");
        directory.WriteBytes(EmptyStoryFile, Array.Empty<byte>());

        FixtureFiles.CopyTo(directory, FixtureFiles.Sav2, @"backup\2026-08-24_120000\sav");
    }

    public static string Normalize(string relativePath) => relativePath.Replace('/', '\\');

    public static string[] Sorted(IEnumerable<string> paths)
        => paths.Select(Normalize).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Stands in for the real process detector so results never depend on what is running.</summary>
public sealed class FakeGameDetector : IGameProcessDetector
{
    public FakeGameDetector(string? runningProcessName = null) => RunningProcessName = runningProcessName;

    public string? RunningProcessName { get; set; }

    public bool IsGameRunning(out string? processName)
    {
        processName = RunningProcessName;
        return RunningProcessName is not null;
    }

    public static FakeGameDetector NotRunning() => new();

    public static FakeGameDetector Running(string processName = "RainWorld") => new(processName);
}

/// <summary>Collects progress messages synchronously, unlike Progress&lt;T&gt; which posts to the pool.</summary>
public sealed class CollectingProgress : IProgress<string>
{
    private readonly List<string> _messages = new();
    private readonly object _gate = new();

    public void Report(string value)
    {
        lock (_gate)
        {
            _messages.Add(value);
        }
    }

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }
}
