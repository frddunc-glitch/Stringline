using System.Diagnostics;
using Xunit.Abstractions;
using Stringline.Core;
using Stringline.Editing.Knowledge;
using Stringline.Gateway.Tests;
using Stringline.Persistence;
using Stringline.Timetable;

namespace Stringline.Editing.Tests;

/// <summary>Sim knowledge from the free sims in <c>tests/fixtures</c>, and from the timetable corpus if it is here.</summary>
public sealed class SimKnowledgeFixtureTests(ITestOutputHelper output)
{
    private const string DefaultCorpus = @"C:\Users\Public\Documents\SimSig\Timetables";

    private static readonly Lazy<SimKnowledge> WaterlooKnowledge = new(() => Build("waterloo", Fixtures.Path(Fixtures.Waterloo1x)));

    private static string FixtureFolder => Path.GetDirectoryName(Fixtures.Path("waterloo 2010.WTT"))!;

    private static SimKnowledge Build(string area, params string[] play)
    {
        var request = new SimKnowledgeRequest { AreaId = area, TimetableFolders = [FixtureFolder], PlayFiles = play, ReadPlay = path => PlayFiles.Read(path) };
        var knowledge = SimKnowledgeStore.Build(request, SimKnowledgeStore.FindSources(request), out var problems);
        problems.Should().BeEmpty();
        return knowledge;
    }

    private static Tiploc T(string code) => new(code);

    [Fact]
    public void Waterloo_gives_its_codes_links_and_run_times()
    {
        var k = WaterlooKnowledge.Value;

        k.Timetables.Should().Be(1, "only the Waterloo timetable of the fixtures");
        k.Trains.Should().Be(1768);
        k.Location(T("WLOO"))!.Roles.Should().HaveFlag(LocationRoles.Station | LocationRoles.Reported);
        k.Location(T("WLOO"))!.Platforms.Should().NotBeEmpty();
        k.Location(T("WIMB"))!.Roles.Should().HaveFlag(LocationRoles.Station);
        k.Location(T("EUPMNS"))!.Roles.Should().Be(LocationRoles.Entry);

        var link = k.Link(T("WLOO"), T("VAUX"))!;
        link.Count.Should().Be(452);
        link.Lines.Should().ContainKeys("DMS", "DWF", "DMF");
        link.Lines!.MaxBy(l => l.Value).Key.Should().Be("DMS");
        var fast = k.RunTime(T("WLOO"), T("VAUX"), new RunBand(SpeedBand.UpTo100, false))!;
        (fast.LeastSeconds, fast.UsualSeconds).Should().Be((180, 210));
        k.Chain(T("WLOO"), T("VAUX")).Should().Equal([T("WLOO"), T("INTJN"), T("VAUX")], "some trains are booked through Waterloo's interlocking junction");
    }

    [Fact]
    public void Waterloos_recording_gives_signals_and_the_signals_trains_enter_at()
    {
        var k = WaterlooKnowledge.Value;

        k.Plays.Should().Be(1);
        k.Signals.Should().HaveCountGreaterThan(50);
        k.Signals.Should().OnlyContain(s => k.Location(new Tiploc(s.Id)) == null || !k.Location(new Tiploc(s.Id))!.Is(LocationRoles.Timing), "a booked timing location is never taken for a signal");
        var entered = k.Signals.Where(s => s.Entries > 0).ToList();
        entered.Should().NotBeEmpty();
        entered.Should().OnlyContain(s => s.EntryPoints != null && s.EntryPoints.Keys.All(p => k.Location(new Tiploc(p))!.Is(LocationRoles.Entry)));
        k.Signal("S4304")?.EntryPoints?.Keys.Should().Equal("EUPMNS");
    }

    [Fact]
    public void Llangollen_gives_its_reversing_sidings_and_single_line_chains()
    {
        var k = Build("llangollen");

        k.Trains.Should().Be(24);
        k.Location(T("CARROG"))!.Roles.Should().Be(LocationRoles.Timing | LocationRoles.Station | LocationRoles.Key | LocationRoles.Reversing);
        k.Location(T("REVCA19"))!.Roles.Should().Be(LocationRoles.Timing | LocationRoles.Reversing);
        k.Location(T("CARROG"))!.Platforms.Should().Equal(new Dictionary<string, int> { ["1"] = 20 });
        k.Link(T("LLANGOLLEN"), T("BERWYN")).Should().BeNull();
        k.Chain(T("LLANGOLLEN"), T("BERWYN")).Should().Equal(T("LLANGOLLEN"), T("GOODSJNC"), T("BERWYN"));
        k.Links.Where(l => l.RunTimes is not null).Should().HaveCountGreaterThan(10);
    }

    /// <summary>The corpus: every sim builds in a few seconds. The times go in <c>progress.md</c>.</summary>
    [Fact]
    public void Every_sim_in_the_corpus_builds_in_a_few_seconds()
    {
        var folder = Environment.GetEnvironmentVariable("STRINGLINE_WTT_CORPUS") is { Length: > 0 } configured ? configured : DefaultCorpus;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"No timetable corpus at {folder}; set STRINGLINE_WTT_CORPUS to run this test.");
            return;
        }

        var areas = Directory.EnumerateFiles(folder, "*.wtt", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(WttCatalogue.ReadInfo).OfType<WttFileInfo>()
            .GroupBy(i => i.AreaId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var slow = new List<string>();
        var total = Stopwatch.StartNew();
        foreach (var area in areas)
        {
            var request = new SimKnowledgeRequest { AreaId = area.Key, TimetableFolders = [folder] };
            var clock = Stopwatch.StartNew();
            var k = SimKnowledgeStore.Build(request, SimKnowledgeStore.FindSources(request), out var problems);
            clock.Stop();
            output.WriteLine($"{area.Key}: {k.Timetables} timetables, {k.Trains} trains, {k.Locations.Count} locations, {k.Links.Count} links in {clock.Elapsed.TotalSeconds:0.00} s{(problems.Count > 0 ? "; left out " + string.Join("; ", problems) : string.Empty)}");
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                slow.Add($"{area.Key} {clock.Elapsed.TotalSeconds:0.0} s");
            }

            k.Links.Should().NotBeEmpty(area.Key);
        }

        output.WriteLine($"{areas.Count} sims in {total.Elapsed.TotalSeconds:0.0} s.");
        slow.Should().BeEmpty();
    }
}
