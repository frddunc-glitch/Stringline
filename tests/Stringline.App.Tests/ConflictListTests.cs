using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>The conflicts list: its rows' wording, and keeping rows and selection in step with the engine's conflicts.</summary>
public sealed class ConflictListTests
{
    private static Conflict C(string id, string when, int severity = 150, ConflictKind kind = ConflictKind.Headway) =>
        new(id, kind, new TrainId("2B02"), new TrainId("1A01"), new Tiploc("CJML"), SimTime.Parse(when), severity)
        {
            HeadcodeA = new Headcode("2B02"),
            HeadcodeB = new Headcode("1A01"),
            To = new Tiploc("EARLSFD"),
        };

    [Fact]
    public void A_row_gives_kind_time_place_trains_and_severity()
    {
        var row = ConflictRowViewModel.Of(C("H1", "07:12:30") with { KnockOnSeconds = 90 }, null);

        (row.Badge, row.Kind, row.Time, row.Where, row.Trains, row.Severity).Should().Be(("HW", "Headway", "07:12½", "CJML – EARLSFD", "2B02 / 1A01", "3 min"));
        row.Tip.Should().Be($"Headway: 2B02 too close behind 1A01{Environment.NewLine}Resolve: delay 2B02 2 min 30 s{Environment.NewLine}Knock-on: 2B02 1 min 30 s late by the forecast");
        ConflictRowViewModel.Of(C("H2", "07:00", 45), null).Severity.Should().Be("45 s");
        ConflictRowViewModel.Of(C("P1", "07:00", kind: ConflictKind.Platform) with { To = null, Platform = "3" }, null).Where.Should().Be("CJML P3");
        Enum.GetValues<ConflictKind>().Select(ConflictRowViewModel.BadgeOf).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Updates_keep_the_rows_in_order_and_the_selection()
    {
        var list = new ConflictsViewModel();
        var picked = new List<Conflict>();
        list.Picked += (_, c) => picked.Add(c);
        list.Update(new ConflictSet([C("A", "07:00"), C("B", "07:10"), C("C", "07:20")]), null);
        list.Summary.Should().Be("3 conflicts");
        list.Header.Should().Be("Conflicts (3)");

        list.Selected = list.Rows[1];
        picked.Select(c => c.Id).Should().Equal("B");
        var kept = list.Rows[0];

        // B moves after C and gets worse; A is unchanged; D is new.
        list.Update(new ConflictSet([C("A", "07:00"), C("B", "07:25", 300), C("C", "07:20"), C("D", "07:30")]), null);

        list.Rows.Select(r => r.Id).Should().Equal("A", "C", "B", "D");
        list.Rows[0].Should().BeSameAs(kept, "an unchanged row stays");
        list.Selected!.Id.Should().Be("B");
        list.Selected.Severity.Should().Be("5 min");
        picked.Should().ContainSingle("a refresh is not a pick");

        list.Update(new ConflictSet([C("D", "07:30")]), null);
        list.Rows.Select(r => r.Id).Should().Equal("D");
        list.Selected.Should().BeNull();
        list.Summary.Should().Be("1 conflict");

        list.Update(ConflictSet.Empty, null);
        (list.Summary, list.Header).Should().Be(("No conflicts", "Conflicts"));
    }

    [Fact]
    public void Selecting_by_id_does_not_count_as_a_pick()
    {
        var list = new ConflictsViewModel();
        var picked = 0;
        list.Picked += (_, _) => picked++;
        list.Update(new ConflictSet([C("A", "07:00"), C("B", "07:10")]), null);

        list.Select("B");
        list.Selected!.Id.Should().Be("B");
        list.Select(null);
        list.Selected.Should().BeNull();
        picked.Should().Be(0);
    }
}
