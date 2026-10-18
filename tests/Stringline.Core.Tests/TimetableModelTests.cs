using Stringline.Core.Timetables;

namespace Stringline.Core.Tests;

public sealed class TimetableModelTests
{
    private static readonly TimetableHeader Header = new("sim", "5.0", "Name", "", null, SimTime.Zero, SimTime.Zero);

    private static TimingPoint Point(int index, string location, int? arrive = null, int? depart = null, bool pass = false, params TrainActivity[] activities) => new()
    {
        Index = index,
        Location = new Tiploc(location),
        Arrive = arrive is null ? null : new SimTime(arrive.Value),
        Depart = depart is null ? null : new SimTime(depart.Value),
        IsPass = pass,
        Activities = activities,
    };

    private static Train Train(string id, string headcode, params TimingPoint[] points) => new()
    {
        Id = new TrainId(id),
        Headcode = new Headcode(headcode),
        Points = points,
    };

    [Fact]
    public void Timetable_looks_up_trains_and_categories()
    {
        var a = Train("A", "1A01", Point(0, "AAA", depart: 100));
        var b = Train("B", "1A01", Point(0, "BBB", depart: 200));
        var c = Train("C", "2B02", Point(0, "CCC", depart: 300));
        var category = new TrainCategory("CAT", "Class 1", false, null, null, null, null, null, DwellTimes.None);

        var tt = new WorkingTimetable(Header, [category, category with { Description = "Second" }], [a, b, c], []);

        tt.AreaId.Should().Be("sim");
        tt.Find(new TrainId("B")).Should().BeSameAs(b);
        tt.Find(new TrainId("Z")).Should().BeNull();
        tt.Get(new TrainId("C")).Should().BeSameAs(c);
        var get = () => tt.Get(new TrainId("Z"));
        get.Should().Throw<KeyNotFoundException>();
        tt.WithHeadcode(new Headcode("1a01")).Should().Equal(a, b);
        tt.WithHeadcode(new Headcode("9Z99")).Should().BeEmpty();
        tt.FindCategory("CAT")!.Description.Should().Be("Class 1");
        tt.FindCategory(null).Should().BeNull();
        tt.FindCategory("cat").Should().BeNull("category IDs are exact");
    }

    [Fact]
    public void Timetable_rejects_duplicate_ids()
    {
        var create = () => new WorkingTimetable(Header, [], [Train("A", "1A01", Point(0, "AAA")), Train("A", "1A02", Point(0, "BBB"))], []);

        create.Should().Throw<ArgumentException>().WithMessage("*A is used twice*");
    }

    [Fact]
    public void Train_derives_ends_times_and_next_working()
    {
        var next = new TrainActivity(ActivityKind.Next, 0, new Headcode("2B02"), null, new TrainId("2B02"));
        var crew = new TrainActivity(ActivityKind.CrewChange, 10, null, "7", null);
        var train = Train("A", "1A01", Point(0, "AAA"), Point(1, "BBB", depart: 100, pass: true), Point(2, "CCC", arrive: 200, depart: 260, activities: [next, crew]), Point(3, "DDD"));

        train.Origin.Should().Be(new Tiploc("AAA"));
        train.Destination.Should().Be(new Tiploc("DDD"));
        train.FirstTime.Should().Be(new SimTime(100));
        train.LastTime.Should().Be(new SimTime(260));
        train.NextWorking.Should().Be(new TrainId("2B02"));
        train.ToString().Should().Be("1A01 A");
    }

    [Fact]
    public void An_untimed_train_falls_back_to_its_entry_time()
    {
        var train = Train("A", "1A01", Point(0, "AAA")) with { EntryTime = new SimTime(50) };

        train.FirstTime.Should().Be(new SimTime(50));
        train.LastTime.Should().BeNull();
        train.NextWorking.Should().BeNull();
    }

    [Fact]
    public void Timing_point_derives_times_and_allowances()
    {
        var stop = Point(0, "AAA", arrive: 100, depart: 160) with { EngineeringAllowanceSeconds = 60, PathingAllowanceSeconds = 30, PerformanceAllowanceSeconds = 90 };
        var pass = Point(1, "BBB", depart: 200, pass: true);
        var arriveOnly = Point(2, "CCC", arrive: 300);
        var untimed = Point(3, "DDD");

        stop.FirstTime.Should().Be(new SimTime(100));
        stop.LastTime.Should().Be(new SimTime(160));
        stop.AllowanceSeconds.Should().Be(180);
        pass.FirstTime.Should().Be(new SimTime(200));
        arriveOnly.LastTime.Should().Be(new SimTime(300));
        untimed.IsTimed.Should().BeFalse();
        arriveOnly.IsTimed.Should().BeTrue();
        stop.ToString().Should().Be("AAA 00:01:40/00:02:40");
        pass.ToString().Should().Be("BBB pass 00:03:20");
        untimed.ToString().Should().Be("DDD -/-");
    }

    [Theory]
    [InlineData(0, ActivityKind.Next)]
    [InlineData(5, ActivityKind.DetachEngineFront)]
    [InlineData(10, ActivityKind.CrewChange)]
    [InlineData(11, ActivityKind.Unknown)]
    [InlineData(-1, ActivityKind.Unknown)]
    public void Activity_kinds_come_from_codes(int code, ActivityKind kind)
    {
        TrainActivity.KindFromCode(code).Should().Be(kind);
    }

    [Theory]
    [InlineData(0, RuleKind.XAppearsAfterYEnters)]
    [InlineData(14, RuleKind.Alternatives)]
    [InlineData(15, RuleKind.Unknown)]
    [InlineData(-3, RuleKind.Unknown)]
    public void Rule_kinds_come_from_codes(int code, RuleKind kind)
    {
        TimetableRule.KindFromCode(code).Should().Be(kind);
    }

    [Fact]
    public void Activity_shows_what_the_file_named()
    {
        new TrainActivity(ActivityKind.Next, 0, new Headcode("1A01"), null, null).AssociatedText.Should().Be("1A01");
        new TrainActivity(ActivityKind.Next, 0, new Headcode("1A01"), "U1", null).AssociatedText.Should().Be("U1");
        new TrainActivity(ActivityKind.CrewChange, 10, null, null, null).AssociatedText.Should().BeNull();
    }

    [Fact]
    public void Dwell_times_fill_gaps_from_a_fallback()
    {
        var own = new DwellTimes(StationForward: 45, CrewChange: 60);
        var category = new DwellTimes(1, 2, 3, 4, 5, 6, 7, 8);

        own.Or(category).Should().Be(new DwellTimes(1, 45, 3, 4, 5, 6, 7, 60));
        DwellTimes.None.Or(DwellTimes.None).Should().Be(DwellTimes.None);
    }
}
