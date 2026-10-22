using System.Text;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;
using Xunit.Abstractions;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>Open → save → open gives the same timetable (<c>08-…md</c> §3.3), on the fixtures and Fred's whole timetable folder.</summary>
public sealed class WttRoundTripTests(ITestOutputHelper output)
{
    private const string DefaultCorpus = @"C:\Users\Public\Documents\SimSig\Timetables";

    public static TheoryData<string> Fixtures_ => new() { WttFixtureTests.Waterloo, WttFixtureTests.Llangollen };

    [Theory]
    [MemberData(nameof(Fixtures_))]
    public void A_fixture_saved_with_no_edits_is_the_same_timetable(string name)
    {
        var problem = Check(Fixtures.Path(name), out var sameBytes);

        problem.Should().BeNull();
        sameBytes.Should().BeTrue("SimSig's own layout and escaping are kept");
    }

    [Fact]
    public void Every_timetable_in_the_corpus_round_trips()
    {
        var folder = Environment.GetEnvironmentVariable("STRINGLINE_WTT_CORPUS") is { Length: > 0 } configured ? configured : DefaultCorpus;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"No timetable corpus at {folder}; set STRINGLINE_WTT_CORPUS to run this test.");
            return;
        }

        var files = Directory.EnumerateFiles(folder, "*.wtt", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList();
        var problems = new List<string>();
        var identical = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            if (Check(file, out var sameBytes, importDocument: false) is { } problem)
            {
                problems.Add($"{file}: {problem}");
            }

            identical += sameBytes ? 1 : 0;
        }

        output.WriteLine($"{files.Count} timetables round-tripped in {clock.Elapsed.TotalSeconds:0.0} s; {identical} with byte-identical XML.");
        problems.Should().BeEmpty();
    }

    /// <returns>Why the round trip is not the same, or null.</returns>
    /// <param name="sameBytes">The saved XML has the same bytes as the original's.</param>
    /// <param name="importDocument">Also import the document itself (the corpus leaves it to the fixtures, for time).</param>
    private static string? Check(string path, out bool sameBytes, bool importDocument = true)
    {
        var original = File.ReadAllBytes(path);
        var document = WttDocument.Load(path);
        var saved = WttWriter.ToBytes(document);
        sameBytes = RoundTrip.EntryBytes(original, "SavedTimetable.xml").AsSpan().SequenceEqual(RoundTrip.EntryBytes(saved, "SavedTimetable.xml"));

        var originalXml = RoundTrip.EntryXml(original, "SavedTimetable.xml");
        if (RoundTrip.FirstDifference(originalXml, RoundTrip.EntryXml(saved, "SavedTimetable.xml")) is { } tree)
        {
            return "XML differs at " + tree;
        }

        // The header is the timetable's [TH] fields. Three Newport files have a header that disagrees with their own
        // timetable (VBuild 0 against 1); there the timetable is what SimSig loads.
        var originalHeader = RoundTrip.EntryXml(original, "TimetableHeader.xml");
        var header = RoundTrip.EntryXml(saved, "TimetableHeader.xml");
        var fromTimetable = WttWriter.HeaderOf(document);
        if (RoundTrip.FirstDifference(fromTimetable, header) is { } h)
        {
            return "header differs from the timetable's fields at " + h;
        }

        if (RoundTrip.FirstDifference(originalHeader, fromTimetable) is null && RoundTrip.FirstDifference(originalHeader, header) is { } o)
        {
            return "header differs from the original at " + o;
        }

        var reader = new WttReader();
        var before = RoundTrip.Canonical(reader.Read(new MemoryStream(original)));
        var after = RoundTrip.Canonical(reader.Read(new MemoryStream(saved)));
        if (before != after)
        {
            return "the import differs";
        }

        return importDocument && RoundTrip.Canonical(document.Import().Result) != before ? "the document's own import differs from the file's" : null;
    }

    [Fact]
    public void The_xml_is_written_as_SimSig_writes_it()
    {
        var saved = WttWriter.ToBytes(WttDocument.Load(Fixtures.Path(WttFixtureTests.Waterloo)));
        var xml = RoundTrip.EntryBytes(saved, "SavedTimetable.xml");
        var text = Encoding.UTF8.GetString(xml);

        xml[0].Should().Be((byte)'<', "no byte order mark");
        text.Should().StartWith("<SimSigTimetable ID=\"waterloo\" Version=\"4.1.2\">\r\n  <Name>SX May 2010 (Updated - Loader) v1.0.1</Name>\r\n");
        text.Should().Contain("\r\n  <TrainCategories>\r\n    <TrainCategory ID=\"36D7F791\">\r\n      <Description>[Electric - DC] 444 - 05 cars</Description>\r\n");
        text.Should().NotContain("<?xml");
        text.Replace("\r\n", "", StringComparison.Ordinal).Should().NotContain("\n");
        text.Should().EndWith("</SimSigTimetable>\r\n");

        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(saved));
        zip.Entries.Select(e => e.FullName).Should().Equal("SavedTimetable.xml", "TimetableHeader.xml");
    }

    [Fact]
    public void The_header_is_the_timetables_TH_fields()
    {
        var saved = WttWriter.ToBytes(WttDocument.Load(Fixtures.Path(WttFixtureTests.Waterloo)));
        var header = Encoding.UTF8.GetString(RoundTrip.EntryBytes(saved, "TimetableHeader.xml"));
        var original = Encoding.UTF8.GetString(RoundTrip.EntryBytes(File.ReadAllBytes(Fixtures.Path(WttFixtureTests.Waterloo)), "TimetableHeader.xml"));

        header.Should().StartWith("<SimSigTimetable ID=\"waterloo\" Version=\"4.1.2\">\r\n  <Name>");
        header.Should().EndWith("  <VMajor>1</VMajor>\r\n  <VMinor>0</VMinor>\r\n  <VBuild>2</VBuild>\r\n</SimSigTimetable>\r\n");
        RoundTrip.FirstDifference(RoundTrip.EntryXml(saved, "TimetableHeader.xml"), RoundTrip.EntryXml(File.ReadAllBytes(Fixtures.Path(WttFixtureTests.Waterloo)), "TimetableHeader.xml")).Should().BeNull();

        header.Should().Be(original, "SimSig escapes apostrophes and quotes in text, and so does the writer");
        header.Should().Contain("You&apos;ll");
    }

    [Fact]
    public void A_header_with_template_seed_summary_and_scenarios_keeps_them_and_nothing_else()
    {
        var document = WttDocument.Parse(Wtt.Document(
            Wtt.SimpleTrain("1A01"),
            header: "<TrainDescriptionTemplate>$originTime</TrainDescriptionTemplate><SeedGroupSummary></SeedGroupSummary><ScenarioOptions>NVIS,1</ScenarioOptions><CautionSpeedSets><CautionSpeedSet><Name>P</Name></CautionSpeedSet></CautionSpeedSets>"));

        var header = WttWriter.HeaderOf(document);

        header.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "Name", "Description", "StartTime", "FinishTime", "VMajor", "VMinor", "VBuild", "TrainDescriptionTemplate", "SeedGroupSummary", "ScenarioOptions");
        header.Element("SeedGroupSummary")!.IsEmpty.Should().BeFalse("an empty <X></X> stays as it was");
        header.Attributes().Select(a => a.Value).Should().Equal("testsim", "5.18");
    }

    [Fact]
    public void Text_is_escaped_as_SimSig_escapes_it()
    {
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01")));
        const string description = "Tom's \"½\" & <b>\tline\none \U0001F600";
        document.Header.Description = description;

        var text = document.XmlText();

        text.Should().Contain("<Description>Tom&apos;s &quot;&#x00BD;&quot; &amp; &lt;b&gt;\tline&#x000A;one &#x1F600;</Description>\r\n");
        text.Should().Contain("<DwellTimes>\r\n", "a dwell element with children is indented");
        WttDocument.Parse(text).Header.Description.Should().Be(description);
    }

    [Fact]
    public void Empty_elements_keep_their_form()
    {
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01"), header: "<SeedGroupSummary></SeedGroupSummary><ScenarioOptions/>"));

        var text = document.XmlText();

        text.Should().Contain("\r\n  <SeedGroupSummary></SeedGroupSummary>\r\n  <ScenarioOptions/>\r\n");
    }

    [Fact]
    public void A_bare_xml_file_is_saved_as_a_zip()
    {
        var document = WttDocument.Load(new MemoryStream(Encoding.UTF8.GetBytes(Wtt.Document(Wtt.SimpleTrain("1A01")))));

        var saved = WttWriter.ToBytes(document);

        saved.AsSpan(0, 2).ToArray().Should().Equal((byte)'P', (byte)'K');
        new WttReader().Read(new MemoryStream(saved)).Timetable.Trains.Should().ContainSingle();
    }

    [Fact]
    public void A_zips_other_entries_are_kept()
    {
        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("SavedTimetable.xml").Open()))
            {
                writer.Write(Wtt.Document(Wtt.SimpleTrain("1A01")));
            }

            using (var writer = new StreamWriter(zip.CreateEntry("TimetableHeader.xml").Open()))
            {
                writer.Write("<SimSigTimetable ID=\"old\"/>");
            }

            using (var writer = new StreamWriter(zip.CreateEntry("notes/readme.txt").Open()))
            {
                writer.Write("hello");
            }
        }

        var document = WttDocument.Load(new MemoryStream(buffer.ToArray()));
        var saved = WttWriter.ToBytes(document);

        document.OtherEntries.Select(e => e.Key).Should().Equal("notes/readme.txt");
        Encoding.UTF8.GetString(RoundTrip.EntryBytes(saved, "readme.txt")).Should().Be("hello");
        RoundTrip.EntryXml(saved, "TimetableHeader.xml").Attribute("ID")!.Value.Should().Be("testsim", "the header is regenerated");
    }

    [Fact]
    public void Latin1_xml_is_read_and_saved_as_utf8()
    {
        var xml = Wtt.Document(Wtt.SimpleTrain("1A01")).Replace("For tests", "Café", StringComparison.Ordinal);
        var document = WttDocument.Load(new MemoryStream(Encoding.Latin1.GetBytes(xml)));

        var saved = WttWriter.ToBytes(document);

        document.ReadAsLatin1.Should().BeTrue();
        Encoding.UTF8.GetString(RoundTrip.EntryBytes(saved, "SavedTimetable.xml")).Should().Contain("<Description>Caf&#x00E9;</Description>");
    }

    [Fact]
    public void Damaged_xml_cannot_be_opened_for_editing()
    {
        var act = () => WttDocument.Load(new MemoryStream(Encoding.UTF8.GetBytes(Wtt.Document(Wtt.SimpleTrain("1A01"))[..300])));

        act.Should().Throw<WttFormatException>().WithMessage("*cannot be read*");
    }

    [Fact]
    public void The_documents_timetable_is_the_files()
    {
        var path = Fixtures.Path(WttFixtureTests.Waterloo);

        var fromDocument = WttDocument.Load(path).ToWorkingTimetable();
        var fromFile = new WttReader().Read(path).Timetable;

        fromDocument.Trains.Should().HaveCount(1768);
        RoundTrip.Canonical(new WttImportResult(fromDocument, [], new Dictionary<string, int>(), 0))
            .Should().Be(RoundTrip.Canonical(new WttImportResult(fromFile, [], new Dictionary<string, int>(), 0)));
    }
}
