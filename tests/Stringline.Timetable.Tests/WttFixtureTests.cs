using Stopwatch = System.Diagnostics.Stopwatch;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Gateway.Tests;
using Xunit.Abstractions;

namespace Stringline.Timetable.Tests;

/// <summary>Real timetables: Waterloo and Llangollen (free, in git), Penrith and Fred's whole timetable folder (local only).</summary>
public sealed class WttFixtureTests(ITestOutputHelper output)
{
    public const string Waterloo = "waterloo 2010.WTT";
    public const string Llangollen = "llangollen 2012 B.WTT";
    public const string Penrith = "Penrith 2017-11-01.WTT";

    /// <summary>Folder of <c>.wtt</c> files read by <see cref="Every_timetable_in_the_corpus_imports_cleanly"/>; overridden by <c>STRINGLINE_WTT_CORPUS</c>.</summary>
    private const string DefaultCorpus = @"C:\Users\Public\Documents\SimSig\Timetables";

    private static readonly Lazy<WttImportResult> WaterlooResult = new(() => new WttReader().Read(Fixtures.Path(Waterloo)));

    private static WorkingTimetable WaterlooTt => WaterlooResult.Value.Timetable;

    private static Train Train(WorkingTimetable tt, string id) => tt.Get(new TrainId(id));

    [Fact]
    public void Waterloo_imports_cleanly()
    {
        var result = WaterlooResult.Value;

        result.Warnings.Should().BeEmpty();
        result.UnknownElements.Should().BeEmpty();
        result.SkippedTrains.Should().Be(0);
        WaterlooTt.Header.Should().Be(new TimetableHeader("waterloo", "4.1.2", "SX May 2010 (Updated - Loader) v1.0.1", WaterlooTt.Header.Description, "1.0.2", SimTime.FromClock(4, 30), SimTime.FromClock(1, 30, day: 1)));
        WaterlooTt.Header.Description.Should().StartWith("A 23 1/2 hour weekday");
    }

    [Fact]
    public void Waterloo_has_the_expected_counts()
    {
        var tt = WaterlooTt;

        tt.Trains.Should().HaveCount(1768);
        tt.Categories.Should().HaveCount(24);
        tt.Rules.Should().HaveCount(338);
        tt.Trains.Sum(t => t.Points.Count).Should().Be(7834);
        tt.Trains.Count(t => t.Start == TrainStart.EntryPoint).Should().Be(898);
        tt.Trains.Count(t => t.Start == TrainStart.Seeded).Should().Be(11);
        tt.Trains.Should().OnlyContain(t => t.Uid == null && t.Id.Value == t.Headcode.Value, "this WTT has no UIDs and unique headcodes");
        var activities = tt.Trains.SelectMany(t => t.Points).SelectMany(p => p.Activities).ToList();
        activities.GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count()).Should().BeEquivalentTo(new Dictionary<ActivityKind, int>
        {
            [ActivityKind.Next] = 823,
            [ActivityKind.DivideRear] = 34,
            [ActivityKind.DivideFront] = 2,
            [ActivityKind.Join] = 58,
        });
        activities.Should().OnlyContain(a => a.Train != null, "every link resolves");
        tt.Rules.Should().OnlyContain(r => r.Train != null && r.Train2 != null);
        tt.Rules.Count(r => r.Kind == RuleKind.XAppearsAfterYLeaves).Should().Be(331);
    }

    [Fact]
    public void Waterloo_divide_from_the_wtt_notes()
    {
        // "Platform 11: 1W51 (Rear) - 2P05 (Front)": 1W51 is seeded and divides, forming 2P05.
        var w51 = Train(WaterlooTt, "1W51");
        w51.Start.Should().Be(TrainStart.Seeded);
        w51.SeedPoint.Should().Be(new Tiploc("S21"));
        w51.Description.Should().Be("05:30 London Waterloo - Weymouth (5 444)");
        w51.Points[0].Activities.Should().ContainSingle().Which.Should().Be(new TrainActivity(ActivityKind.DivideFront, 2, new Headcode("2P05"), null, new TrainId("2P05")));
        w51.Points.Select(p => p.Location.Code).Should().Equal("WLOO", "CJML", "WIMB");
        w51.Points[2].Arrive.Should().Be(SimTime.Parse("05:42:30"));

        var p05 = Train(WaterlooTt, "2P05");
        p05.Start.Should().Be(TrainStart.AtFirstLocation);
        p05.FormedFrom.Should().Be(new TrainId("1W51"));
        p05.MaxSpeedMph.Should().Be(100);
        WaterlooTt.FindCategory(p05.CategoryId)!.Description.Should().Be("[Electric - DC] 444 - 05 cars");
    }

    [Fact]
    public void Waterloo_suffixed_headcodes_are_separate_workings()
    {
        // The -1 suffix seen on live headcodes is in the WTT itself.
        WaterlooTt.WithHeadcode(new Headcode("2O03")).Should().ContainSingle();
        WaterlooTt.WithHeadcode(new Headcode("2O03-1")).Should().ContainSingle();
        WaterlooTt.Trains.Count(t => t.Headcode.Value.EndsWith("-1", StringComparison.Ordinal)).Should().Be(160);

        var z94 = Train(WaterlooTt, "0Z94-1");
        z94.Start.Should().Be(TrainStart.EntryPoint);
        z94.EntryPoint.Should().Be(new Tiploc("ESHEEP"));
        z94.EntryTime.Should().Be(SimTime.Parse("11:07"));
        z94.NextWorking.Should().Be(new TrainId("0Z95"));
        Train(WaterlooTt, "0Z95").FormedFrom.Should().Be(new TrainId("0Z94-1"));
    }

    [Fact]
    public void Waterloo_runs_past_midnight()
    {
        WaterlooTt.Trains.Max(t => t.LastTime!.Value).Should().Be(new SimTime(91200));
        WaterlooTt.Trains.Min(t => t.FirstTime!.Value).Should().Be(SimTime.Parse("04:43"));
        WaterlooTt.Trains.Min(t => t.EntryTime ?? SimTime.Parse("23:59")).Should().Be(SimTime.Parse("04:37"));
    }

    [Fact]
    public void Llangollen_single_line_with_engine_movements()
    {
        var result = new WttReader().Read(Fixtures.Path(Llangollen));
        var tt = result.Timetable;

        result.Warnings.Should().BeEmpty();
        result.UnknownElements.Should().BeEmpty();
        tt.AreaId.Should().Be("llangollen");
        tt.Trains.Should().HaveCount(24);
        tt.Categories.Should().HaveCount(4);
        tt.Trains.SelectMany(t => t.Points).Select(p => p.Location).Distinct().Should().HaveCount(15);
        var kinds = tt.Trains.SelectMany(t => t.Points).SelectMany(p => p.Activities).GroupBy(a => a.Kind).ToDictionary(g => g.Key, g => g.Count());
        kinds.Should().BeEquivalentTo(new Dictionary<ActivityKind, int> { [ActivityKind.Next] = 15, [ActivityKind.Join] = 12, [ActivityKind.DetachEngineFront] = 6 });

        // The loco runs round at Carrog: 1C02 detaches its engine (0C02), which joins the other end, and 1C02 becomes 1L02.
        var c02 = Train(tt, "1C02");
        c02.FormedFrom.Should().Be(new TrainId("5C02"));
        c02.NextWorking.Should().Be(new TrainId("1L02"));
        c02.Points[^1].Activities.Select(a => (a.Kind, a.Train)).Should().Contain((ActivityKind.DetachEngineFront, new TrainId("0C02")));
        Train(tt, "0C02").FormedFrom.Should().Be(new TrainId("1C02"));
        Train(tt, "5C02").Start.Should().Be(TrainStart.Seeded);
    }

    [Fact]
    public void Penrith_uids_templates_and_freight()
    {
        if (Fixtures.LocalPath(Penrith) is not { } path)
        {
            return;
        }

        var result = new WttReader().Read(path);
        var tt = result.Timetable;

        result.Warnings.Should().BeEmpty();
        result.UnknownElements.Should().BeEmpty();
        tt.AreaId.Should().Be("penrith");
        tt.Trains.Should().HaveCount(306);
        tt.Categories.Should().HaveCount(26);
        tt.Rules.Should().HaveCount(10);
        tt.Trains.Count(t => t.Start == TrainStart.EntryPoint).Should().Be(220);
        tt.Trains.Should().OnlyContain(t => t.Uid != null);

        // Two LS80 workings, told apart by UID.
        tt.WithHeadcode(new Headcode("LS80")).Select(t => t.Id.Value).Should().BeEquivalentTo("LS80", "LS80TU");
        var coal = Train(tt, "LS80TU");
        coal.IsFreight.Should().BeTrue();
        coal.Description.Should().Be("21:15 Lidsdale-Kooragang Island PNC (Coal Loaded 4x82 + 45 (845m 80kph))");
        coal.OriginTime.Should().Be(new SimTime(76500), "origin is the evening before, off the sim");
        coal.Points.Should().OnlyContain(p => p.IsPass && p.Arrive == null);

        var a175 = Train(tt, "175A");
        a175.Description.Should().Be("03:43 Penrith Down Yard-Emu Plains SYT (EMU A Set [8] 'Waratah')");
        a175.NextWorking.Should().Be(new TrainId("175B"));
        Train(tt, "175B").FormedFrom.Should().Be(new TrainId("175A"));
        tt.Rules.Should().OnlyContain(r => r.Kind == RuleKind.XAppearsAfterYLeaves && r.Train != null && r.Train2 != null && r.DelaySeconds >= 480);
    }

    [Fact]
    public void Every_timetable_in_the_corpus_imports_cleanly()
    {
        var folder = Environment.GetEnvironmentVariable("STRINGLINE_WTT_CORPUS") is { Length: > 0 } configured ? configured : DefaultCorpus;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"No timetable corpus at {folder}; set STRINGLINE_WTT_CORPUS to run this test.");
            return;
        }

        var reader = new WttReader();
        var files = Directory.EnumerateFiles(folder, "*.wtt", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }).ToList();
        var problems = new List<string>();
        var trains = 0;
        var warnings = 0;
        var clock = Stopwatch.StartNew();
        foreach (var file in files)
        {
            try
            {
                var result = reader.Read(file);
                trains += result.Timetable.Trains.Count;
                warnings += result.Warnings.Count(w => w.Severity == ImportSeverity.Warning);
                if (result.SkippedTrains > 0 || result.UnknownElements.Count > 0 || result.Warnings.Any(w => w.Severity == ImportSeverity.Error))
                {
                    problems.Add($"{file}: {result.SkippedTrains} skipped, unknown [{string.Join(", ", result.UnknownElements.Keys)}], {string.Join("; ", result.WarningsAtLeast(ImportSeverity.Error))}");
                }
            }
            catch (Exception ex) when (ex is WttFormatException or IOException)
            {
                problems.Add($"{file}: {ex.Message}");
            }
        }

        output.WriteLine($"{files.Count} timetables, {trains} trains, {warnings} link warnings, {clock.Elapsed.TotalSeconds:0.0} s");
        problems.Should().BeEmpty();
    }
}
