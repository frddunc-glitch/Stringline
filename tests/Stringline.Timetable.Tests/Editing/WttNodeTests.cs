using System.Xml.Linq;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>Typed views read and write through in the file's conventions (<c>08-…md</c> §3.1).</summary>
public sealed class WttNodeTests
{
    /// <summary><see cref="Wtt.Category"/> writes <c>False</c>, so this document is in the True/False style.</summary>
    private static WttDocument TrueFalse(string trains) => WttDocument.Parse(Wtt.Document(trains));

    private static WttDocument Delphi(string trains) => WttDocument.Parse(Wtt.Document(trains, Wtt.Category.Replace(">False<", ">0<", StringComparison.Ordinal)));

    private static string Children(XElement e) => string.Join(" ", e.Elements().Select(c => c.Name.LocalName));

    [Fact]
    public void The_boolean_style_is_the_files()
    {
        TrueFalse(Wtt.SimpleTrain("1A01")).BooleanStyle.Should().Be(WttBooleanStyle.TrueFalse);
        Delphi(Wtt.SimpleTrain("1A01")).BooleanStyle.Should().Be(WttBooleanStyle.Delphi);
        WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01"), categories: null)).BooleanStyle.Should().Be(WttBooleanStyle.Delphi, "a file with no yes/no values gets the newer style");
        WttDocument.Load(Fixtures.Path(WttFixtureTests.Waterloo)).BooleanStyle.Should().Be(WttBooleanStyle.TrueFalse);
    }

    [Theory]
    [InlineData(true, "True", "False")]
    [InlineData(false, "-1", "0")]
    public void Yes_is_written_in_the_files_style_and_no_is_left_out_where_SimSig_leaves_it_out(bool trueFalse, string yes, string no)
    {
        var document = trueFalse ? TrueFalse(Wtt.SimpleTrain("1A01")) : Delphi(Wtt.SimpleTrain("1A01"));
        var trip = document.Trains.Single().Trips.First();
        var category = document.Categories.Single();

        trip.IsPassTime = true;
        trip.Element.Element("IsPassTime")!.Value.Should().Be(yes);
        trip.IsPassTime.Should().BeTrue();
        trip.IsPassTime = false;
        trip.Element.Element("IsPassTime").Should().BeNull("SimSig never writes a trip's no");

        category.IsFreight = true;
        category.Element.Element("IsFreight")!.Value.Should().Be(yes);
        category.IsFreight = false;
        category.Element.Element("IsFreight")!.Value.Should().Be(no, "a category writes every value");
    }

    [Fact]
    public void Zero_is_left_out_except_where_it_is_a_value()
    {
        var document = TrueFalse(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 36000) + Wtt.Trip("BBB", arr: 36300, extra: Wtt.Activity(3, "1A02"))) + Wtt.SimpleTrain("1A02"));
        var trip = document.Trains.First().Trips.Last();
        var activity = trip.Activities.Single();

        trip.EngAllowanceSeconds = 90;
        trip.Element.Element("EngAllowance")!.Value.Should().Be("3", "allowances are half minutes");
        trip.EngAllowanceSeconds = 45;
        trip.Element.Element("EngAllowance")!.Value.Should().Be("1.5");
        trip.EngAllowanceSeconds.Should().Be(45);
        trip.EngAllowanceSeconds = 0;
        trip.Element.Element("EngAllowance").Should().BeNull();
        trip.DwellTime = 0;
        trip.Element.Element("DwellTime").Should().BeNull();

        activity.Code = 0;
        activity.Element.Element("Activity")!.Value.Should().Be("0", "activity 0 is next working");
        document.Categories.Single().AccelBrakeIndex = 0;
        document.Categories.Single().Element.Element("AccelBrakeIndex")!.Value.Should().Be("0");
    }

    [Fact]
    public void A_new_field_goes_where_SimSig_puts_it()
    {
        var document = TrueFalse(Wtt.SimpleTrain("1A01"));
        var train = document.Trains.Single();
        var trip = train.Trips.Last();
        Children(trip.Element).Should().Be("Location DepPassTime ArrTime");

        trip.Line = "FL";
        trip.Platform = "2";
        trip.NewCategory = "CAT2";
        trip.Activities.Should().BeEmpty();
        trip.RequestPercent = 50;
        trip.SetDownOnly = true;

        Children(trip.Element).Should().Be("Location NewCategory DepPassTime SetDownOnly ArrTime Platform Line RequestPercent");

        train.Uid = "W12345";
        train.SeedPoint = "S21";
        train.Notes = "test";
        Children(train.Element).Should().Be("ID UID Description SeedPoint Notes Category Trips");

        document.Header.Version = new WttVersion(1, 2, 4);
        document.Header.TrainDescriptionTemplate = "$originTime";
        Children(document.Root).Should().StartWith("Name Description StartTime FinishTime VMajor VMinor VBuild TrainDescriptionTemplate TrainCategories");
    }

    [Fact]
    public void Text_set_to_nothing_is_removed_and_unknown_fields_are_kept()
    {
        var document = TrueFalse(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 36000, extra: "<Platform>1</Platform><Mystery>x</Mystery>")));
        var trip = document.Trains.Single().Trips.Single();

        trip.Platform = null;
        trip.Line = "";

        Children(trip.Element).Should().Be("Location DepPassTime Mystery");
        trip.GetText("Mystery").Should().Be("x");
    }

    [Fact]
    public void Every_typed_view_reads_Waterloo()
    {
        var document = WttDocument.Load(Fixtures.Path(WttFixtureTests.Waterloo));

        document.Header.AreaId.Should().Be("waterloo");
        document.Header.SimSigVersion.Should().Be("4.1.2");
        document.Header.StartTime.Should().Be(SimTime.FromClock(4, 30));
        document.Header.Version.Should().Be(new WttVersion(1, 0, 2));
        document.Categories.Should().HaveCount(24);
        var category = document.Categories.First();
        category.Id.Should().Be("36D7F791");
        category.MaxSpeed.Should().Be(100);
        category.GetDwell("StationReverse").Should().Be(360);
        category.IsFreight.Should().BeFalse();

        var trains = document.Trains.ToList();
        trains.Should().HaveCount(1768);
        var w51 = trains.First(t => t.Headcode == "1W51");
        w51.SeedPoint.Should().Be("S21");
        var w51Trips = w51.Trips.ToList();
        w51Trips[0].Location.Should().Be("WLOO");
        w51Trips.SelectMany(t => t.Activities).Should().Contain(a => a.AssociatedTrain == "2P05");

        document.Rules.Should().HaveCount(338);
        document.Rules.First().Code.Should().NotBeNull();
        document.SeedGroups.Should().BeEmpty();
        document.Decisions.Should().BeNull();
    }

    [Fact]
    public void Seed_groups_and_lists_are_read()
    {
        var document = WttDocument.Parse(Wtt.Document(
            Wtt.SimpleTrain("1A01"),
            header: "<SeedGroups><SeedGroup><ID>{G1}</ID><StartTime>50400</StartTime><Title>2pm Start</Title></SeedGroup></SeedGroups><Decisions><Decision><Description>Day</Description><Choices><Choice><Weight>2</Weight></Choice></Choices></Decision></Decisions>"));

        var group = document.SeedGroups.Single();
        group.Id.Should().Be("{G1}");
        group.StartTime.Should().Be(SimTime.FromClock(14, 0));
        group.Title.Should().Be("2pm Start");
        var decision = document.Decisions!.Items.Single();
        decision.GetText("Description").Should().Be("Day");
        decision.Element.Element("Choices")!.Elements().Should().ContainSingle();
    }

    [Fact]
    public void A_category_dwell_is_made_when_the_category_has_none()
    {
        var document = TrueFalse(Wtt.SimpleTrain("1A01"));
        var category = document.Categories.Single();
        category.Element.Element("DwellTimes")!.Remove();

        category.SetDwell("Join", 120);

        Children(category.Element).Should().Be("Description IsFreight MaxSpeed TrainLength AccelBrakeIndex SpeedClass DwellTimes Electrification");
        category.GetDwell("Join").Should().Be(120);
    }
}
