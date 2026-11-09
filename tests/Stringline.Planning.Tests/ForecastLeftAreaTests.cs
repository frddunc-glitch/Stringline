using Stringline.Core;
using Stringline.Core.Timetables;
using static Stringline.Planning.Tests.ForecastTesting;
using static Stringline.Timetable.Tests.Live.TestTimetable;

namespace Stringline.Planning.Tests;

/// <summary>
/// #34: SimSig reports nothing at the timing points a train books beyond the area (Three Bridges' STRENJN, Carlisle's
/// CRNFNJN). Once the TD has cancelled its description on the way out, the train is finished, and the points beyond
/// are forecast as it would have run them rather than held at now.
/// </summary>
public sealed class ForecastLeftAreaTests
{
    private static readonly TrainId Train1 = new("1A01");

    /// <summary>1A01 A 06:00, pass C 06:10, D 06:15.</summary>
    private static WorkingTimetable Timetable() =>
        Of(Train("1A01", points: [Call("A", null, "06:00"), Pass("C", "06:10"), Call("D", "06:15", null)]));

    [Fact]
    public void A_train_that_left_the_area_is_finished_and_not_held_at_now()
    {
        var live = Running("10", "1A01", Dep("A", "06:02")) with { DescriptionCancelled = SimTime.Parse("06:03") };

        var forecast = Run(WorkingPlan.FromTimetable(Timetable()), "06:40", interaction: false, live: live).Find(Train1)!;

        forecast.Status.Should().Be(TrainForecastStatus.Finished);
        forecast.LeftArea.Should().Be(SimTime.Parse("06:03"));
        forecast.PossiblyStopped.Should().BeFalse();
        Times(forecast).Should().Equal("A -/06:02:00 A", "C pass 06:12:00 F", "D 06:17:00/- F");
    }

    [Fact]
    public void Without_the_cancel_the_train_is_held_at_now_and_possibly_stopped()
    {
        var live = Running("10", "1A01", Dep("A", "06:02"));

        var forecast = Run(WorkingPlan.FromTimetable(Timetable()), "06:40", interaction: false, live: live).Find(Train1)!;

        forecast.Status.Should().Be(TrainForecastStatus.Running);
        forecast.LeftArea.Should().BeNull();
        forecast.PossiblyStopped.Should().BeTrue();
        Times(forecast)[1].Should().Be("C pass 06:40:00 F");
    }

    [Fact]
    public void A_train_that_left_after_arriving_somewhere_goes_on_from_its_booked_dwell()
    {
        var tt = Of(Train("1A01", points: [Call("A", null, "06:00"), Call("C", "06:10", "06:11"), Call("D", "06:15", null)]));
        var live = Running("10", "1A01", Dep("A", "06:00"), Arr("C", "06:10")) with { DescriptionCancelled = SimTime.Parse("06:11") };

        var forecast = Run(WorkingPlan.FromTimetable(tt), "06:40", interaction: false, live: live).Find(Train1)!;

        forecast.Status.Should().Be(TrainForecastStatus.Finished);
        Times(forecast).Should().Equal("A -/06:00:00 A", "C 06:10:00/06:11:00 R", "D 06:15:00/- F");
    }
}
