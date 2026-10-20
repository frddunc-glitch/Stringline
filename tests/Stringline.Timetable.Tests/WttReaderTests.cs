using System.Text;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Tests;

public sealed class WttReaderTests
{
    private static Train Single(WttImportResult result) => result.Timetable.Trains.Should().ContainSingle().Subject;

    [Fact]
    public void Reads_the_header()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.SimpleTrain("1A01"), header: "<TrainDescriptionTemplate>$operator</TrainDescriptionTemplate><SeedGroups><SeedGroup /></SeedGroups>"));

        var h = result.Timetable.Header;
        h.AreaId.Should().Be("testsim");
        result.Timetable.AreaId.Should().Be("testsim");
        h.SimSigVersion.Should().Be("5.18");
        h.Name.Should().Be("Test timetable");
        h.Description.Should().Be("For tests");
        h.TimetableVersion.Should().Be("1.2.3");
        h.StartTime.Should().Be(new SimTime(18000));
        h.FinishTime.Should().Be(new SimTime(90000));
        h.TrainDescriptionTemplate.Should().Be("$operator");
        result.Warnings.Should().BeEmpty();
        result.UnknownElements.Should().BeEmpty("SeedGroups is read, and an empty group is skipped");
    }

    [Fact]
    public void Reads_categories_with_dwell_times()
    {
        var category = Wtt.Read(Wtt.Document(Wtt.SimpleTrain("1A01"))).Timetable.Categories.Should().ContainSingle().Subject;

        category.Should().BeEquivalentTo(new TrainCategory("CAT1", "Class 999", false, 100, 120, 3, 8, "3", new DwellTimes(RedSignalMoveOff: 12, StationForward: 30, Join: 240)));
    }

    [Fact]
    public void Reads_a_train_and_its_timing_points()
    {
        var trips =
            Wtt.Trip("AAA", dep: 36000, extra: "<Platform>3</Platform><Line>FL</Line><DownDirection>True</DownDirection><EngAllowance>2</EngAllowance><PathAllowance>1</PathAllowance>") +
            Wtt.Trip("MID", dep: 36300, extra: "<IsPassTime>True</IsPassTime><Path>FL</Path>") +
            Wtt.Trip("BBB", arr: 36600, dep: 36660, extra: "<DwellTime>60</DwellTime><RequestPercent>50</RequestPercent><SetDownOnly>-1</SetDownOnly><NewCategory>CAT2</NewCategory><DownDirection>0</DownDirection>");
        var extra = "<DepartTime>35880</DepartTime><EntryPoint>EAAA</EntryPoint><MaxSpeed>75</MaxSpeed><TrainLength>200</TrainLength><IsFreight>-1</IsFreight><AsRequired>True</AsRequired>" +
                    "<OriginName>Somewhere</OriginName><DestinationName>Elsewhere</DestinationName><OriginTime>30000</OriginTime><DestinationTime>40000</DestinationTime>" +
                    "<OperatorCode>XX</OperatorCode><Notes>Note</Notes><StationForward>45</StationForward>";

        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("6M23", trips, extra, uid: "U6M23"))));

        train.Id.Should().Be(new TrainId("U6M23"));
        train.Headcode.Should().Be(new Headcode("6M23"));
        train.Uid.Should().Be("U6M23");
        train.Description.Should().Be("6M23 test train");
        train.CategoryId.Should().Be("CAT1");
        train.Start.Should().Be(TrainStart.EntryPoint);
        train.EntryPoint.Should().Be(new Tiploc("EAAA"));
        train.EntryTime.Should().Be(new SimTime(35880));
        train.SeedPoint.Should().BeNull();
        train.MaxSpeedMph.Should().Be(75);
        train.LengthMetres.Should().Be(200);
        train.IsFreight.Should().BeTrue();
        train.AsRequired.Should().BeTrue();
        train.OriginName.Should().Be("Somewhere");
        train.DestinationName.Should().Be("Elsewhere");
        train.OriginTime.Should().Be(new SimTime(30000));
        train.DestinationTime.Should().Be(new SimTime(40000));
        train.OperatorCode.Should().Be("XX");
        train.Notes.Should().Be("Note");
        train.Dwell.Should().Be(new DwellTimes(RedSignalMoveOff: 12, StationForward: 45, Join: 240), "the train's own values win, the category fills the gaps");
        train.Origin.Should().Be(new Tiploc("AAA"));
        train.Destination.Should().Be(new Tiploc("BBB"));
        train.FirstTime.Should().Be(new SimTime(36000));
        train.LastTime.Should().Be(new SimTime(36660));

        train.Points.Should().HaveCount(3);
        var a = train.Points[0];
        a.Index.Should().Be(0);
        a.Arrive.Should().BeNull();
        a.Depart.Should().Be(new SimTime(36000));
        a.Platform.Should().Be("3");
        a.Line.Should().Be("FL");
        a.DownDirection.Should().BeTrue();
        a.EngineeringAllowanceSeconds.Should().Be(60, "allowances are in half minutes");
        a.PathingAllowanceSeconds.Should().Be(30);
        a.AllowanceSeconds.Should().Be(90);

        var mid = train.Points[1];
        mid.IsPass.Should().BeTrue();
        mid.Depart.Should().Be(new SimTime(36300));
        mid.Path.Should().Be("FL");
        mid.DownDirection.Should().BeNull();

        var b = train.Points[2];
        b.Index.Should().Be(2);
        b.Arrive.Should().Be(new SimTime(36600));
        b.Depart.Should().Be(new SimTime(36660));
        b.DwellSeconds.Should().Be(60);
        b.RequestPercent.Should().Be(50);
        b.SetDownOnly.Should().BeTrue();
        b.NewCategoryId.Should().Be("CAT2");
        b.DownDirection.Should().BeFalse();
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("-1", true)]
    [InlineData("1", true)]
    [InlineData("False", false)]
    [InlineData("0", false)]
    public void Accepts_both_boolean_styles(string text, bool expected)
    {
        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100, extra: $"<IsPassTime>{text}</IsPassTime>")))));

        train.Points[0].IsPass.Should().Be(expected);
    }

    [Fact]
    public void A_bad_boolean_is_ignored_with_a_note()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100, extra: "<IsPassTime>maybe</IsPassTime>"))));

        Single(result).Points[0].IsPass.Should().BeFalse();
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Info && w.Message.Contains("IsPassTime 'maybe'"));
    }

    [Fact]
    public void A_pass_ignores_any_arrival_time()
    {
        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", arr: 90, dep: 100, extra: "<IsPassTime>-1</IsPassTime>")))));

        train.Points[0].Arrive.Should().BeNull();
        train.Points[0].Depart.Should().Be(new SimTime(100));
    }

    [Fact]
    public void Times_after_midnight_stay_on_the_next_day()
    {
        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 86_100) + Wtt.Trip("BBB", arr: 86_700, dep: 86_700)))));

        train.Points[1].Arrive.Should().Be(SimTime.FromClock(0, 5, day: 1));
    }

    [Fact]
    public void Untimed_points_are_kept()
    {
        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("5A01", Wtt.Trip("AAA", extra: "<Platform>1</Platform>")))));

        train.Points[0].IsTimed.Should().BeFalse();
        train.FirstTime.Should().BeNull();
        train.LastTime.Should().BeNull();
    }

    [Theory]
    [InlineData("3", 90)]
    [InlineData("1.5", 45)]
    [InlineData("0", 0)]
    public void Allowances_are_half_minutes(string text, int seconds)
    {
        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100, extra: $"<PerfAllowance>{text}</PerfAllowance>")))));

        train.Points[0].PerformanceAllowanceSeconds.Should().Be(seconds);
    }

    [Fact]
    public void A_bad_allowance_is_ignored_with_a_note()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100, extra: "<EngAllowance>lots</EngAllowance>"))));

        Single(result).Points[0].EngineeringAllowanceSeconds.Should().Be(0);
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Info && w.Message.Contains("EngAllowance 'lots'"));
    }

    [Fact]
    public void A_bad_time_is_dropped_with_a_note()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", "<Trip><Location>AAA</Location><DepPassTime>10:00</DepPassTime></Trip>")));

        Single(result).Points[0].Depart.Should().BeNull();
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Info && w.Message.Contains("DepPassTime '10:00'") && w.Train == "1A01 at AAA");
    }

    [Fact]
    public void A_time_going_backwards_is_kept_with_a_note()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 1000) + Wtt.Trip("BBB", arr: 900, dep: 950))));

        Single(result).Points[1].Arrive.Should().Be(new SimTime(900));
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Info && w.Message.StartsWith("Time goes backwards at BBB", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<SeedPoint>S21</SeedPoint><Started>True</Started>", TrainStart.Seeded)]
    [InlineData("", TrainStart.AtFirstLocation)]
    [InlineData("<EntryPoint>EAAA</EntryPoint>", TrainStart.EntryPoint)]
    public void Works_out_how_a_train_starts(string extra, TrainStart start)
    {
        var train = Single(Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100), extra))));

        train.Start.Should().Be(start);
        if (start == TrainStart.Seeded)
        {
            train.SeedPoint.Should().Be(new Tiploc("S21"));
        }

        if (start == TrainStart.EntryPoint)
        {
            train.EntryTime.Should().BeNull("SimSig works the entry time out when the file gives none");
            train.FirstTime.Should().Be(new SimTime(100));
        }
    }

    [Fact]
    public void A_train_without_a_headcode_is_skipped()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("", Wtt.Trip("AAA", dep: 100)) + Wtt.Train("1A 01", Wtt.Trip("AAA", dep: 100)) + Wtt.SimpleTrain("1A02")));

        result.Timetable.Trains.Should().ContainSingle().Which.Headcode.Value.Should().Be("1A02");
        result.SkippedTrains.Should().Be(2);
        result.Warnings.Should().HaveCount(2).And.OnlyContain(w => w.Severity == ImportSeverity.Warning && w.Message.Contains("no usable headcode"));
        result.Warnings[1].Message.Should().StartWith("Train 2 in the file");
    }

    [Fact]
    public void A_timing_point_without_a_location_is_skipped_and_indexes_close_up()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100) + Wtt.Trip("", dep: 200) + Wtt.Trip("CCC", arr: 300, dep: 300))));

        var train = Single(result);
        train.Points.Select(p => (p.Index, p.Location.Code)).Should().Equal((0, "AAA"), (1, "CCC"));
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Warning && w.Train == "1A01" && w.Message.StartsWith("Timing point 2 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_train_without_usable_points_is_skipped()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", "") + Wtt.SimpleTrain("1A02")));

        result.Timetable.Trains.Should().ContainSingle();
        result.SkippedTrains.Should().Be(1);
        result.Warnings.Should().ContainSingle(w => w.Train == "1A01" && w.Message.Contains("no usable timing points"));
    }

    [Fact]
    public void Train_values_fall_back_to_the_category()
    {
        var category = Wtt.Category.Replace("<IsFreight>False</IsFreight>", "<IsFreight>True</IsFreight>", StringComparison.Ordinal);

        var train = Single(Wtt.Read(Wtt.Document(Wtt.SimpleTrain("6A01"), category)));

        train.MaxSpeedMph.Should().Be(100);
        train.LengthMetres.Should().Be(120);
        train.IsFreight.Should().BeTrue();
        train.Dwell.Should().Be(new DwellTimes(RedSignalMoveOff: 12, StationForward: 30, Join: 240));
    }

    [Fact]
    public void An_unknown_category_is_kept_with_a_note()
    {
        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100), category: "NOPE")));

        var train = Single(result);
        train.CategoryId.Should().Be("NOPE");
        result.Timetable.FindCategory("NOPE").Should().BeNull();
        train.Dwell.Should().Be(DwellTimes.None);
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Info && w.Message.Contains("category NOPE"));
    }

    [Fact]
    public void A_category_defined_twice_keeps_the_first()
    {
        var second = Wtt.Category.Replace("Class 999", "Class 111", StringComparison.Ordinal);
        var result = Wtt.Read(Wtt.Document(Wtt.SimpleTrain("1A01"), Wtt.Category + second + "<TrainCategory><Description>No ID</Description></TrainCategory>"));

        result.Timetable.Categories.Should().ContainSingle().Which.Description.Should().Be("Class 999");
        result.Warnings.Should().Contain(w => w.Message.Contains("defined twice")).And.Contain(w => w.Message.Contains("no ID"));
    }

    [Fact]
    public void Expands_the_header_description_template()
    {
        var extra = "<OriginName>Penrith</OriginName><DestinationName>Central</DestinationName><OriginTime>90060</OriginTime><OperatorCode>SYT</OperatorCode>";
        var xml = Wtt.Document(
            Wtt.Train("1A01", Wtt.Trip("AAA", dep: 100), extra).Replace("1A01 test train", "$template", StringComparison.Ordinal),
            header: "<TrainDescriptionTemplate>$originTime $originName-$destName $operator ($stock)</TrainDescriptionTemplate>");

        Single(Wtt.Read(xml)).Description.Should().Be("01:01 Penrith-Central SYT (Class 999)");
    }

    [Fact]
    public void Counts_unknown_elements_but_not_known_ignored_ones()
    {
        var trips = Wtt.Trip("AAA", dep: 100, extra: "<AutoLine>-1</AutoLine><Mystery>1</Mystery>") + Wtt.Trip("BBB", dep: 200, extra: "<Mystery>2</Mystery>") + "<NotATrip />";
        var xml = Wtt.Document(
            Wtt.Train("1A01", trips, "<Delay>60</Delay><Gizmo /><Trips2 />") + "<NotATimetable />",
            rules: "<TimetableRule><Rule>1</Rule><Achieved>-1</Achieved><Odd /></TimetableRule><Other />",
            header: "<CautionSpeedSets /><Decisions /><NewHeaderThing>1</NewHeaderThing>");

        var result = Wtt.Read(xml);

        result.UnknownElements.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["NewHeaderThing"] = 1,
            ["Timetables/NotATimetable"] = 1,
            ["Timetable/Gizmo"] = 1,
            ["Timetable/Trips2"] = 1,
            ["Trip/Mystery"] = 2,
            ["Trips/NotATrip"] = 1,
            ["TimetableRule/Odd"] = 1,
            ["TimetableRules/Other"] = 1,
        });
        result.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Reads_activities_with_unknown_codes_and_missing_codes()
    {
        var trips = Wtt.Trip("AAA", dep: 100, extra: "<Activities><Activity><Activity>42</Activity></Activity><Activity><AssociatedTrain>1A02</AssociatedTrain></Activity><Odd /></Activities>");

        var result = Wtt.Read(Wtt.Document(Wtt.Train("1A01", trips)));

        var activity = Single(result).Points[0].Activities.Should().ContainSingle().Subject;
        activity.Kind.Should().Be(ActivityKind.Unknown);
        activity.Code.Should().Be(42);
        result.Warnings.Should().Contain(w => w.Severity == ImportSeverity.Info && w.Message.Contains("Activity code 42"))
            .And.Contain(w => w.Severity == ImportSeverity.Warning && w.Message.Contains("no activity code"));
        result.UnknownElements.Should().ContainKey("Activities/Odd");
    }

    [Fact]
    public void Reads_timetable_rules()
    {
        var rules =
            "<TimetableRule><Rule>1</Rule><Train>1A02</Train><Train2>1A01</Train2><Time>120</Time><Location>BBB</Location></TimetableRule>" +
            "<TimetableRule><Rule>99</Rule><TrainUID>U1</TrainUID><Train2UID>U9</Train2UID></TimetableRule>" +
            "<TimetableRule><Train>1A01</Train></TimetableRule>";

        var result = Wtt.Read(Wtt.Document(Wtt.SimpleTrain("1A01", uid: "U1") + Wtt.SimpleTrain("1A02"), rules: rules));

        result.Timetable.Rules.Should().HaveCount(2);
        result.Timetable.Rules[0].Should().Be(new TimetableRule(RuleKind.XAppearsAfterYLeaves, 1, "1A02", "1A01", new TrainId("1A02"), new TrainId("U1"), 120, new Tiploc("BBB")));
        result.Timetable.Rules[1].Should().Be(new TimetableRule(RuleKind.Unknown, 99, "U1", "U9", new TrainId("U1"), null, null, null));
        result.Warnings.Should().Contain(w => w.Message.Contains("no rule number"))
            .And.Contain(w => w.Message.StartsWith("1 of 2 timetable rules", StringComparison.Ordinal));
    }

    [Fact]
    public void Reads_a_wtt_zip()
    {
        var bytes = Wtt.Zip(Wtt.Document(Wtt.SimpleTrain("1A01")), "savedtimetable.XML");

        var result = new WttReader().Read(new MemoryStream(bytes));

        result.Timetable.Trains.Should().ContainSingle();
    }

    [Fact]
    public void Reads_a_file_from_disk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stringline-{Guid.NewGuid():N}.wtt");
        File.WriteAllBytes(path, Wtt.Zip(Wtt.Document(Wtt.SimpleTrain("1A01"))));
        try
        {
            new WttReader().Read(path).Timetable.Trains.Should().ContainSingle();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Reads_xml_with_a_byte_order_mark_and_declaration()
    {
        var xml = (char)0xFEFF + "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + Wtt.Document(Wtt.SimpleTrain("1A01"));

        new WttReader().Read(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Timetable.Trains.Should().ContainSingle();
    }

    [Fact]
    public void Reads_latin1_xml_with_a_note()
    {
        var xml = Wtt.Document(Wtt.SimpleTrain("1A01")).Replace("For tests", "Caf" + (char)0xE9 + " timetable", StringComparison.Ordinal);

        var result = new WttReader().Read(new MemoryStream(Encoding.Latin1.GetBytes(xml)));

        result.Timetable.Header.Description.Should().Be("Caf" + (char)0xE9 + " timetable");
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Info && w.Message.Contains("Latin-1"));
    }

    [Fact]
    public void Damaged_xml_keeps_the_trains_before_the_damage()
    {
        var xml = Wtt.Document(Wtt.SimpleTrain("1A01") + Wtt.SimpleTrain("1A02") + Wtt.SimpleTrain("1A03"));
        var cut = xml[..xml.IndexOf("<ID>1A03", StringComparison.Ordinal)];

        var result = Wtt.Read(cut);

        result.Timetable.Trains.Select(t => t.Headcode.Value).Should().Equal("1A01", "1A02");
        result.Warnings.Should().ContainSingle(w => w.Severity == ImportSeverity.Error && w.Message.Contains("The 2 trains read before"));
    }

    [Fact]
    public void An_empty_timetable_imports()
    {
        var result = Wtt.Read("<SimSigTimetable ID=\"x\"><Timetables /></SimSigTimetable>");

        result.Timetable.Trains.Should().BeEmpty();
        result.Timetable.Header.StartTime.Should().Be(SimTime.Zero);
        result.Timetable.Header.TimetableVersion.Should().BeNull();
        Wtt.Read("<SimSigTimetable ID=\"x\" />").Timetable.Trains.Should().BeEmpty();
    }

    [Fact]
    public void A_timetable_without_a_sim_id_warns()
    {
        Wtt.Read("<SimSigTimetable><Name>x</Name></SimSigTimetable>").Warnings.Should().ContainSingle(w => w.Message.Contains("no sim ID"));
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("")]
    [InlineData("<Other />")]
    [InlineData("<SimSigTimetable")]
    public void Rejects_what_is_not_a_timetable(string content)
    {
        var read = () => Wtt.Read(content);

        read.Should().Throw<WttFormatException>();
    }

    [Fact]
    public void Rejects_a_zip_without_the_timetable()
    {
        var read = () => new WttReader().Read(new MemoryStream(Wtt.Zip("<x />", "Other.xml")));

        read.Should().Throw<WttFormatException>().WithMessage("*no SavedTimetable.xml*");
    }

    [Fact]
    public void Rejects_a_damaged_zip()
    {
        byte[] bytes = [.. "PK\u0003\u0004"u8, .. new byte[40]];

        var read = () => new WttReader().Read(new MemoryStream(bytes));

        read.Should().Throw<WttFormatException>().WithInnerException<InvalidDataException>();
    }

    [Fact]
    public void Reads_seed_groups_and_each_seeded_trains_group()
    {
        const string groups = """
            <SeedGroupSummary>{G1},&quot;04:00 04:00&quot;,{G2},&quot;14:00 14:00&quot;</SeedGroupSummary>
            <SeedGroups>
              <SeedGroup><ID>{G1}</ID><StartTime>14400</StartTime><Title>04:00</Title></SeedGroup>
              <SeedGroup><ID>{G2}</ID><StartTime>50400</StartTime><Title>14:00</Title></SeedGroup>
              <SeedGroup><Title>no ID</Title></SeedGroup>
            </SeedGroups>
            """;
        var trains =
            Wtt.Train("1A01", Wtt.Trip("AAA", dep: 50460) + Wtt.Trip("BBB", arr: 50700), "<SeedPoint>S1</SeedPoint><SeedGroup>{G2}</SeedGroup><Started>-1</Started>") +
            Wtt.Train("1A02", Wtt.Trip("AAA", dep: 18060) + Wtt.Trip("BBB", arr: 18300), "<SeedPoint>S2</SeedPoint><Started>-1</Started>") +
            Wtt.Train("1A03", Wtt.Trip("AAA", dep: 18060) + Wtt.Trip("BBB", arr: 18300), "<SeedPoint>S3</SeedPoint><SeedGroup>{G9}</SeedGroup>") +
            Wtt.Train("2B01", Wtt.Trip("AAA", dep: 50000) + Wtt.Trip("BBB", arr: 50300), "<EntryPoint>EAAA</EntryPoint><DepartTime>49900</DepartTime><SeedGroup>{G1}</SeedGroup>");

        var result = Wtt.Read(Wtt.Document(trains, header: groups));
        var tt = result.Timetable;

        tt.SeedGroups.Should().Equal(new SeedGroup("{G1}", SimTime.FromClock(4, 0), "04:00"), new SeedGroup("{G2}", SimTime.FromClock(14, 0), "14:00"));
        tt.FindSeedGroup("{g2}")!.Title.Should().Be("14:00", "IDs are GUIDs, so case does not matter");
        tt.Get(new TrainId("1A01")).SeedGroup.Should().Be("{G2}");
        tt.Get(new TrainId("1A01")).Started.Should().BeTrue();
        tt.Get(new TrainId("1A02")).SeedGroup.Should().BeNull("seeded at the timetable's start");
        tt.Get(new TrainId("2B01")).SeedGroup.Should().BeNull("only seeded trains have a group");
        tt.Get(new TrainId("2B01")).Started.Should().BeFalse();
        tt.Get(new TrainId("2B01")).BookedStart.Should().Be(new SimTime(49900), "an entering train comes in at its entry time");
        tt.Get(new TrainId("1A01")).BookedStart.Should().Be(new SimTime(50460));
        result.UnknownElements.Should().BeEmpty();
        result.Warnings.Select(w => w.Message).Should().BeEquivalentTo(
            "Seed group 'no ID' has no ID or start time and was skipped.",
            "Is seeded in group {G9}, which is not in the timetable header; it is taken as seeded at any start.");
    }
}
