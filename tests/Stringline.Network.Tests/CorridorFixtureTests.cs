using System.Diagnostics;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Timetable;
using Xunit.Abstractions;

namespace Stringline.Network.Tests;

/// <summary>Corridors for real timetables: Waterloo and Llangollen (free, in git), Penrith and Fred's whole timetable folder (local only).</summary>
public sealed class CorridorFixtureTests(ITestOutputHelper output)
{
    /// <summary>Folder of <c>.wtt</c> files for <see cref="Every_timetable_in_the_corpus_gives_consistent_corridors"/>; overridden by <c>STRINGLINE_WTT_CORPUS</c>.</summary>
    private const string DefaultCorpus = @"C:\Users\Public\Documents\SimSig\Timetables";

    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);
    private static readonly Lazy<WorkingTimetable> Llangollen = new(() => new WttReader().Read(Fixtures.Path("llangollen 2012 B.WTT")).Timetable);

    private static List<string> Codes(Corridor c) => c.Locations.Select(l => l.Tiploc.Code).ToList();

    [Fact]
    public void Waterloo_has_the_main_and_windsor_lines()
    {
        var candidates = CorridorGenerator.Generate(Waterloo.Value);

        candidates.Select(c => (c.Corridor.Id, c.Corridor.Name)).Should().Equal(("main", "WLOO – WIMB"), ("wloo-barnes", "WLOO – BARNES"));
        Codes(candidates[0].Corridor).Should().Equal("WLOO", "INTJN", "VAUX", "NINELMJ", "QTRDBAT", "CJML", "EARLSFD", "WIMB");
        Codes(candidates[1].Corridor).Should().Equal("WLOO", "INTJN", "VAUX", "NINELMJ", "QTRDBAT", "WESTLON", "CJWIN", "WANDS", "PUTNEY", "BARNES");
        candidates[0].Measurement.Trains.Should().Be(1626);
        candidates[1].Measurement.Trains.Should().Be(1156);
    }

    [Fact]
    public void Waterloo_trains_booked_past_vauxhall_run_through_it()
    {
        var graph = LocationGraph.Build(Waterloo.Value);

        graph.Expansions.Should().HaveCount(15);
        graph.Expansions[new LocationPair(new Tiploc("CJML"), new Tiploc("WLOO"))].Select(t => t.Code).Should().Equal("CJML", "VAUX", "WLOO");
        graph.Weight(new Tiploc("CJML"), new Tiploc("WLOO")).Should().Be(0);
    }

    [Fact]
    public void Waterloo_main_line_has_run_times_from_every_train()
    {
        var main = CorridorGenerator.Generate(Waterloo.Value)[0];

        // Most trains are booked WLOO–VAUX–CJML; their runs are shared over the sections between, so none is thin.
        main.Measurement.Sections.Should().OnlyContain(s => s.Samples >= 500);
        main.Corridor.Sections.Should().OnlyContain(s => s.MinRunSeconds > 0 && s.MinRunSeconds <= 180);
        main.Measurement.Sections.Should().OnlyContain(s => !s.SingleLineHint, "Waterloo is double line or more throughout");
        main.Corridor.Locations.Select(l => l.AxisPosition).Should().BeInAscendingOrder().And.StartWith(0.0).And.EndWith(1.0);
        main.Corridor.Locations[0].Platforms.Should().Be(19);
        main.Corridor.Locations.Where(l => l.IsTimingPointOnly).Select(l => l.Tiploc.Code).Should().Equal("INTJN", "NINELMJ");
    }

    [Fact]
    public void Llangollen_is_one_single_line_corridor()
    {
        var candidates = CorridorGenerator.Generate(Llangollen.Value);

        candidates.Should().ContainSingle();
        var main = candidates[0];
        Codes(main.Corridor).Should().Equal("LLANGOLLEN", "GOODSJNC", "BERWYN", "DEESIDE", "GLYN", "CARROG");
        main.Measurement.Sections.Should().OnlyContain(s => s.SingleLineHint, "trains are booked to cross only at the loops");
        main.Corridor.Locations.Select(l => l.Platforms).Should().Equal(2, 0, 0, 2, 1, 1);
        main.Corridor.Sections.Select(s => s.MinRunSeconds).Should().Equal(180, 180, 300, 300, 420);
    }

    [Fact]
    public void Llangollen_run_rounds_are_never_corridors()
    {
        var graph = LocationGraph.Build(Llangollen.Value);

        graph.Reversing(new LocationPair(new Tiploc("CARROG"), new Tiploc("REVCA19"))).Should().Be(graph.Weight(new Tiploc("CARROG"), new Tiploc("REVCA19")));
        var everything = CorridorGenerator.Generate(Llangollen.Value, new CorridorGeneratorOptions { MinTrains = 1, MinLocations = 2 });
        everything.Should().Contain(c => c.Corridor.Name == "LLANGOLLEN – LOCOSHED", "one light engine runs to the depot");
        everything.SelectMany(c => c.Corridor.Locations).Should().NotContain(l => l.Tiploc.Code.StartsWith("REVCA", StringComparison.Ordinal));
    }

    [Fact]
    public void A_generated_profile_round_trips_through_the_store()
    {
        var directory = Path.Combine(Path.GetTempPath(), "stringline-fixture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            var profile = StringlineProfile.Create(Waterloo.Value);

            store.Save(profile);
            var loaded = store.Load("waterloo")!;

            loaded.Should().BeEquivalentTo(profile);
            loaded.Refresh(Waterloo.Value).Should().BeEquivalentTo(profile, "refreshing against the same WTT changes nothing");
            loaded.Categories.Should().HaveCount(Waterloo.Value.Categories.Count);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Penrith_runs_up_from_blacktown()
    {
        if (Fixtures.LocalPath("Penrith 2017-11-01.WTT") is not { } path)
        {
            return;
        }

        var main = CorridorGenerator.Generate(new WttReader().Read(path).Timetable)[0];

        main.Corridor.Name.Should().Be("BLAKTWN – VLYHGTS");
        Codes(main.Corridor).Should().HaveCount(14).And.Contain("PENRITH");
    }

    [Fact]
    public void Every_timetable_in_the_corpus_gives_consistent_corridors()
    {
        var folder = Environment.GetEnvironmentVariable("STRINGLINE_WTT_CORPUS") ?? DefaultCorpus;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"Skipped: no corpus at {folder}.");
            return;
        }

        var files = Directory.GetFiles(folder, "*.wtt", SearchOption.AllDirectories);
        var slowest = (Name: "", Ms: 0L);
        var corridors = 0;
        foreach (var file in files)
        {
            var tt = new WttReader().Read(file).Timetable;
            var watch = Stopwatch.StartNew();
            var candidates = CorridorGenerator.Generate(tt);
            if (watch.ElapsedMilliseconds > slowest.Ms)
            {
                slowest = (Path.GetFileName(file), watch.ElapsedMilliseconds);
            }

            candidates.Should().NotBeEmpty(file);
            candidates[0].Corridor.Id.Should().Be("main");
            candidates.Select(c => c.Corridor.Id).Should().OnlyHaveUniqueItems(file);
            foreach (var c in candidates.Select(c => c.Corridor))
            {
                c.Sections.Should().HaveCount(c.Locations.Count - 1, file);
                c.Locations.Select(l => l.Tiploc).Should().OnlyHaveUniqueItems(file);
                c.Locations.Select(l => l.AxisPosition).Should().BeInAscendingOrder(file).And.StartWith(0.0).And.EndWith(1.0);
                c.Sections.Should().OnlyContain(s => s.MinRunSeconds > 0, file);
                ProfileJson.Deserialize(ProfileJson.Serialize(new StringlineProfile { AreaId = tt.AreaId, Corridors = [c] })).Corridors[0].Should().BeEquivalentTo(c);
            }

            corridors += candidates.Count;
        }

        output.WriteLine($"{files.Length} timetables, {corridors} corridors; slowest {slowest.Name} at {slowest.Ms} ms.");
        slowest.Ms.Should().BeLessThan(5000);
    }
}
