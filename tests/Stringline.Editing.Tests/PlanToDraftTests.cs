using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Gateway.Tests;
using Stringline.Planning;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Tests;

/// <summary>S47: a working plan written into a draft of the timetable it was made from.</summary>
public sealed class PlanToDraftTests
{
    private static WttDocument Waterloo() => WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));

    [Fact]
    public void A_retime_platform_cancellation_termination_and_clone_go_into_the_draft_and_the_rest_is_listed()
    {
        var timetable = Waterloo().ToWorkingTimetable();
        var plan = WorkingPlan.FromTimetable(timetable);
        var trains = timetable.Trains.Where(t => t.Points.Count >= 4 && t.Points[0].Depart is not null && t.Points[^1].Arrive is not null).Take(4).ToList();
        var (retimed, cancelled, terminated, cloned) = (trains[0], trains[1], trains[2], trains[3]);
        var cloneId = plan.NewCloneId(new Headcode("1Z77"));
        IPlanCommand[] commands =
        [
            new ShiftTrain(retimed.Id, 0, 300),
            new ChangePlatform(retimed.Id, retimed.Points.Count - 1, "9"),
            new CancelTrain(cancelled.Id),
            new TerminateAt(terminated.Id, 2),
            new CloneTrain(cloned.Id, new Headcode("1Z77"), 3600, cloneId),
            new AddNote(new PlanNote("N1", retimed.Points[0].Location, SimTime.Parse("08:00"), "Driver change")),
        ];
        var (made, results) = plan.ApplyAll(commands);
        results.Should().OnlyContain(r => r.Applied, string.Join("; ", results.Select(r => r.Error)));

        var draft = Waterloo();
        var count = draft.Trains.Count();
        var result = PlanToDraft.Build(draft, made, removeCancelled: true);

        result.Changes.Should().HaveCount(4);
        result.Problems.Should().Contain(p => p.Contains("Driver change", StringComparison.Ordinal));
        draft.Apply(result.Edit!);
        var after = draft.ToWorkingTimetable();
        after.Find(retimed.Id)!.Points[0].Depart.Should().Be(retimed.Points[0].Depart + 300);
        after.Find(retimed.Id)!.Points[^1].Platform.Should().Be("9");
        after.Find(cancelled.Id).Should().BeNull();
        after.Find(terminated.Id)!.Points.Should().HaveCount(3);
        draft.Trains.Count().Should().Be(count, "one removed, one added");
        var clone = after.Trains.Single(t => t.Headcode.Value == "1Z77");
        clone.Points[0].Depart.Should().Be(cloned.Points[0].Depart + 3600);
        clone.Points.Should().OnlyContain(p => p.Activities.All(a => !a.FormsAssociatedTrain));

        var kept = PlanToDraft.Build(Waterloo(), made, removeCancelled: false);
        kept.Changes.Should().Contain(c => c.EndsWith("cancelled, kept as booked", StringComparison.Ordinal));
    }
}
