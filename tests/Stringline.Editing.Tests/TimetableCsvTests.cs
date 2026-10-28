using System.Xml.Linq;
using Stringline.Core;
using Stringline.Editing.Csv;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>S47: a timetable as a spreadsheet and back.</summary>
public sealed class TimetableCsvTests
{
    private static WttDocument Waterloo() => WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));

    [Fact]
    public void Waterloo_exported_and_imported_changes_nothing()
    {
        var waterloo = Waterloo();
        var csv = TimetableCsv.Export(waterloo);
        csv.Split("\r\n")[0].Should().StartWith("Headcode,UID,Category");

        var sheet = TimetableCsv.Read(csv);
        sheet.Problems.Should().BeEmpty();
        sheet.Trains.Should().HaveCount(waterloo.Trains.Count());

        var merged = TimetableCsv.Merge(waterloo, sheet);
        merged.Changes.Should().BeEmpty();
        merged.Problems.Should().BeEmpty();
        merged.Edit.Should().BeNull();
    }

    [Fact]
    public void A_sheet_is_read_tolerantly_and_unreadable_rows_are_reported_by_line()
    {
        var sheet = TimetableCsv.Read(
            (char)0xFEFF + "Train;TIPLOC;Arrival;Departure;Platform;Engineering;Request\n" +
            "2a01;aaa;;1000;\"1\";1H;\n" +
            "2A01;BBB;;10/05;;;\n" +
            "2A01;CCC;1010;10r12;2;;\n" +
            "# a comment\n" +
            "2A01;DDD;10:20;;3;x;\n" +
            ";EEE;;1030;;;\n");

        sheet.Trains.Should().ContainSingle();
        var train = sheet.Trains[0];
        train.Headcode.Should().Be("2A01");
        train.Points.Select(p => p.Location).Should().Equal("AAA", "BBB", "CCC");
        train.Points[0].Eng.Should().Be(90);
        train.Points[1].Depart!.Value.Mark.Should().Be(TimeMark.Pass);
        train.Points[2].Depart!.Value.Mark.Should().Be(TimeMark.Request);
        sheet.Problems.Should().HaveCount(2);
        sheet.Problems[0].Should().StartWith("Line 6:").And.Contain("allowance");
        sheet.Problems[1].Should().StartWith("Line 7:").And.Contain("headcode");
    }

    [Fact]
    public void A_merge_changes_a_time_replaces_a_route_and_adds_a_train()
    {
        var d = Doc(
            Train("2A01", Field("UID", "U1"), Trips(Stop("A", null, "10:00"), Pass("B", "10:05"), Stop("C", "10:10", null))),
            Train("2A02", Trips(Stop("C", null, "11:00"), Stop("A", "11:10", null))));
        var csv = TimetableCsv.Export(d)
            .Replace("U1,,EIN,09:59,2,B,,10:05", "U1,,,,2,B,,10/06", StringComparison.Ordinal);
        var lines = csv.Split("\r\n").ToList();
        var b = lines.FindIndex(l => l.Contains(",B,", StringComparison.Ordinal));
        lines[b] = lines[b].Replace("10/05", "10/06", StringComparison.Ordinal);
        var a2 = lines.FindIndex(l => l.StartsWith("2A02", StringComparison.Ordinal) && l.Contains(",A,", StringComparison.Ordinal));
        lines.Insert(a2, "2A02,,,,,2,X,,11:05,,,,,,,,,,,");
        lines.Add("9Z99,,,,,1,A,,12:00,,,,,,,,,,,");
        lines.Add("9Z99,,,,,2,C,12:10,,,,,,,,,,,,");

        var result = TimetableCsv.Merge(d, TimetableCsv.Read(string.Join("\r\n", lines)));

        result.Problems.Should().BeEmpty();
        result.Changes.Should().HaveCount(3);
        var before = d.Root.ToString(SaveOptions.DisableFormatting);
        var undo = d.Apply(result.Edit!);
        d.Trains.Single(t => t.Headcode == "2A01").Trips.ElementAt(1).DepPassTime.Should().Be(new SimTime(T("10:06")));
        d.Trains.Single(t => t.Headcode == "2A02").Trips.Select(t => t.Location).Should().Equal("C", "X", "A");
        d.Trains.Single(t => t.Headcode == "9Z99").Trips.Should().HaveCount(2);
        d.Apply(undo);
        d.Root.ToString(SaveOptions.DisableFormatting).Should().Be(before);
    }
}
