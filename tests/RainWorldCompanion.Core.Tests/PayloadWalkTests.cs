using RainWorldCompanion.Core.Saves;

namespace RainWorldCompanion.Tests;

/// <summary>
/// Reading a slot pulls five scalar fields out of two records in a payload that runs to over a
/// million characters, most of it MAP_ bodies that are thrown away unread. These tests hold the
/// walk to the same answers as the copying split, and hold it to not copying the payload to get
/// them, because this runs on every window refresh and inside every backup.
/// </summary>
public class PayloadWalkTests
{
    [Theory]
    [InlineData(FixtureFiles.Sav2)]
    [InlineData(FixtureFiles.Sav3)]
    [InlineData(FixtureFiles.OnlineSav)]
    public void The_walk_finds_the_same_records_as_the_copying_split(string fixture)
    {
        var payload = FixtureFiles.ReadPayload(fixture, "save");

        var split = SavePayloadReader.SplitRecords(payload);
        var walked = SavePayloadReader.EnumerateRecords(payload).ToList();

        Assert.Equal(split.Count, walked.Count);
        for (var i = 0; i < split.Count; i++)
        {
            Assert.Equal(split[i].Header, walked[i].Header());
            Assert.Equal(split[i].Body, walked[i].Body());
            Assert.True(walked[i].HeaderIs(split[i].Header));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("MISCPROG<progDivB>CYCLES<misA>4")]
    [InlineData("<progDivA>")]
    [InlineData("no separators at all")]
    [InlineData("A<progDivB>1<progDivA>B<progDivB>2<progDivA>")]
    [InlineData("<progDivB>body only")]
    public void The_walk_matches_the_copying_split_on_the_awkward_shapes(string payload)
    {
        var split = SavePayloadReader.SplitRecords(payload);
        var walked = SavePayloadReader.EnumerateRecords(payload).ToList();

        Assert.Equal(split.Select(r => r.Header).ToArray(), walked.Select(r => r.Header()).ToArray());
        Assert.Equal(split.Select(r => r.Body).ToArray(), walked.Select(r => r.Body()).ToArray());
    }

    [Fact]
    public void HeaderIs_is_an_exact_ordinal_comparison()
    {
        var record = SavePayloadReader.EnumerateRecords("SAVE STATE<progDivB>x").First();

        Assert.True(record.HeaderIs("SAVE STATE"));
        Assert.False(record.HeaderIs("SAVE STAT"));
        Assert.False(record.HeaderIs("SAVE STATES"));
        Assert.False(record.HeaderIs("save state"));
    }

    /// <summary>
    /// The point of the walk. A payload carrying two megabytes of MAP_ bodies is read for its
    /// headers without any of those bodies being copied out.
    /// </summary>
    [Fact]
    public void Reading_headers_does_not_copy_the_bodies()
    {
        var payload = PayloadWithLargeMapRecords(mapRecords: 8, bodyChars: 128 * 1024);

        // Warm the enumerator's own machinery so the measurement is of the walk, not of first use.
        CountSaveStates(payload);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var wanted = CountSaveStates(payload);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(2, wanted);
        Assert.True(
            allocated < 64 * 1024,
            $"walking the records allocated {allocated} bytes for a {payload.Length * 2} byte payload");
    }

    [Fact]
    public void The_walk_still_hands_back_the_body_of_a_record_that_is_wanted()
    {
        var payload = PayloadWithLargeMapRecords(mapRecords: 2, bodyChars: 4096);

        var body = SavePayloadReader.EnumerateRecords(payload).First(r => r.HeaderIs("SAVE STATE")).Body();

        Assert.Contains("CYCLENUM<svB>17", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The container reader used to decode the whole file to a string, strip the BOM off that
    /// string, and then slice the XML section out of the result, which is three copies of a file
    /// that runs to megabytes before the XML is even parsed. The measurement is against the same
    /// work done the old way rather than a fixed number, so it says what it means.
    /// </summary>
    [Fact]
    public void Reading_a_container_does_not_take_several_copies_of_the_file()
    {
        var path = FixtureFiles.PathTo(FixtureFiles.Sav2);

        SaveContainer.Read(path);
        DecodeThenSlice(path);

        var before = GC.GetAllocatedBytesForCurrentThread();
        SaveContainer.Read(path);
        var reader = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        DecodeThenSlice(path);
        var decodeThenSlice = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            reader < decodeThenSlice * 0.75,
            $"the reader allocated {reader} bytes where decoding the whole file first takes {decodeThenSlice}");
    }

    /// <summary>The shape the reader used to have: decode everything, then cut the string up.</summary>
    private static int DecodeThenSlice(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text.Substring(1);
        }

        var end = text.LastIndexOf(FixtureFiles.ClosingTag, StringComparison.Ordinal) + FixtureFiles.ClosingTag.Length;
        return System.Xml.Linq.XDocument.Parse(text.Substring(0, end)).Root!.Elements().Count();
    }

    private static int CountSaveStates(string payload)
    {
        var found = 0;
        foreach (var record in SavePayloadReader.EnumerateRecords(payload))
        {
            if (record.HeaderIs("SAVE STATE"))
            {
                found++;
            }
        }

        return found;
    }

    private static string PayloadWithLargeMapRecords(int mapRecords, int bodyChars)
    {
        var records = new List<(string Header, string Body)>
        {
            ("SAVE STATE", SyntheticSave.SaveStateBody()),
            ("SAVE STATE", SyntheticSave.SaveStateBody(slugcat: "Saint", cycle: 3)),
        };

        for (var i = 0; i < mapRecords; i++)
        {
            records.Add(("MAP_White_" + i, new string('m', bodyChars)));
        }

        return SyntheticSave.Progression(records);
    }
}
