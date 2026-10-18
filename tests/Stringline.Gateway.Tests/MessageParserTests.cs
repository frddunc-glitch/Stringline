using System.Text;
using Stringline.Core;
using Stringline.Core.Events;

namespace Stringline.Gateway.Tests;

public class MessageParserTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private readonly MessageParser _parser = new();

    private IReadOnlyList<GatewayEvent> Parse(string body, string topic = GatewayTopics.TrainMovements) =>
        _parser.Parse(new GatewayMessage(At, topic, body));

    private T Single<T>(string body)
        where T : GatewayEvent
    {
        var events = Parse(body);
        events.Should().ContainSingle();
        return events[0].Should().BeOfType<T>().Subject;
    }

    // ---- train_location ----

    [Fact]
    public void Parses_train_location_at_a_timing_point()
    {
        var e = Single<TrainLocationEvent>("""{"train_location":{"headcode":"5M01","uid":"4","action":"pass","location":"INTJN","platform":"5","time":15445}}""");

        e.Should().Be(new TrainLocationEvent(At, new Headcode("5M01"), "4", TrainLocationAction.Pass, new Tiploc("INTJN"), "5", new SimTime(15445)));
    }

    [Fact]
    public void Parses_train_location_at_a_signal_with_aspects()
    {
        var e = Single<TrainLocationEvent>("""{"train_location":{"headcode":"1O28","uid":"5","action":"pass","location":"SVC92","platform":"","time":15310,"aspPass":6,"aspAppr":2}}""");

        e.Location.Should().Be(new Tiploc("SVC92"));
        e.Platform.Should().BeEmpty();
        e.AspectPassed.Should().Be(6);
        e.AspectApproach.Should().Be(2);
    }

    [Theory]
    [InlineData("arrive", TrainLocationAction.Arrive)]
    [InlineData("DEPART", TrainLocationAction.Depart)]
    [InlineData(" Pass ", TrainLocationAction.Pass)]
    [InlineData("split", TrainLocationAction.Unknown)]
    public void Maps_train_location_actions(string action, TrainLocationAction expected) =>
        Single<TrainLocationEvent>($$$"""{"train_location":{"headcode":"1A01","uid":"9","action":"{{{action}}}","location":"X","time":1}}""")
            .Action.Should().Be(expected);

    [Fact]
    public void Accepts_numbers_as_strings_and_strings_as_numbers()
    {
        var e = Single<TrainLocationEvent>("""{"train_location":{"headcode":"2r02","uid":42,"action":"arrive","location":"wloo","platform":3,"time":"15445","aspPass":"6"}}""");

        e.Headcode.Should().Be(new Headcode("2R02"));
        e.Uid.Should().Be("42");
        e.Location.Code.Should().Be("WLOO");
        e.Platform.Should().Be("3");
        e.Time.Should().Be(new SimTime(15445));
        e.AspectPassed.Should().Be(6);
        e.AspectApproach.Should().BeNull();
    }

    [Fact]
    public void Train_location_without_headcode_still_parses()
    {
        Single<TrainLocationEvent>("""{"train_location":{"uid":"9","action":"pass","location":"X","time":1}}""")
            .Headcode.IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"train_location":{"headcode":"1A01","action":"pass","location":"X","time":1}}""", "uid")]
    [InlineData("""{"train_location":{"headcode":"1A01","uid":" ","action":"pass","location":"X","time":1}}""", "uid")]
    [InlineData("""{"train_location":{"headcode":"1A01","uid":"1","action":"pass","time":1}}""", "location")]
    [InlineData("""{"train_location":{"headcode":"1A01","uid":"1","action":"pass","location":"X"}}""", "time")]
    [InlineData("""{"train_location":{"headcode":"1A01","uid":"1","action":"pass","location":"X","time":"soon"}}""", "time")]
    [InlineData("""{"train_delay":{"headcode":"1A01","uid":""}}""", "delay")]
    [InlineData("""{"clock_msg":{"area_id":"x","interval":500}}""", "clock")]
    [InlineData("""{"clock_msg":{"area_id":"x","clock":1}}""", "interval")]
    [InlineData("""{"CA_MSG":{"to":"0235","descr":"2R02"}}""", "from")]
    [InlineData("""{"CA_MSG":{"from":"0243","descr":"2R02"}}""", "to")]
    [InlineData("""{"CB_MSG":{"descr":""}}""", "from")]
    [InlineData("""{"CC_MSG":{"descr":"1A30"}}""", "to")]
    [InlineData("""{"SG_MSG":{"obj_type":"track","clear":"True"}}""", "obj_id")]
    public void Missing_required_fields_give_an_unknown_event(string body, string field)
    {
        var e = Single<UnknownEvent>(body);

        e.Reason.Should().Contain($"\"{field}\"");
        e.Topic.Should().Be(GatewayTopics.TrainMovements);
        e.Raw.Should().StartWith("{");
    }

    // ---- train_delay, clock_msg ----

    [Fact]
    public void Parses_train_delay()
    {
        Single<TrainDelayEvent>("""{"train_delay":{"headcode":"5M01","uid":"","delay":-240}}""")
            .Should().Be(new TrainDelayEvent(At, new Headcode("5M01"), string.Empty, -240));
        Single<TrainDelayEvent>("""{"train_delay":{"headcode":"5M01","uid":"7","delay":"60"}}""")
            .DelaySeconds.Should().Be(60);
    }

    [Fact]
    public void Parses_clock_msg()
    {
        var e = Single<ClockEvent>("""{"clock_msg":{"area_id":"waterloo","clock":15720,"interval":500,"paused":false}}""");

        e.Should().Be(new ClockEvent(At, "waterloo", new SimTime(15720), 500, false));
        e.SpeedRatio.Should().Be(1);
    }

    [Theory]
    [InlineData("\"True\"", true)]
    [InlineData("true", true)]
    [InlineData("\"False\"", false)]
    [InlineData("\"junk\"", false)]
    public void Clock_paused_is_lenient(string paused, bool expected) =>
        Single<ClockEvent>($$$"""{"clock_msg":{"area_id":"a","clock":"100","interval":"125","paused":{{{paused}}}}}""")
            .Should().Match<ClockEvent>(e => e.Paused == expected && e.SpeedRatio == 4 && e.Clock == new SimTime(100));

    [Fact]
    public void Clock_past_midnight_and_zero_interval()
    {
        var e = Single<ClockEvent>("""{"clock_msg":{"clock":90000,"interval":0}}""");

        e.Clock.Day.Should().Be(1);
        e.AreaId.Should().BeEmpty();
        e.SpeedRatio.Should().Be(0);
    }

    // ---- TD ----

    [Fact]
    public void Parses_TD_step_cancel_and_interpose()
    {
        var events = Parse(
            """{"CA_MSG":{"area_id":"waterloo","from":"0243","to":"0235","descr":"2R02","msg_type":"CA","time":"16250"}}""",
            GatewayTopics.TrainDescriber);
        events.Should().Equal(new TdStepEvent(At, TdStepKind.Step, "waterloo", "0243", "0235", "2R02", new SimTime(16250)));

        Single<TdStepEvent>("""{"CB_MSG":{"area_id":"waterloo","from":"0130","descr":"","msg_type":"CB","time":"16249"}}""")
            .Should().Be(new TdStepEvent(At, TdStepKind.Cancel, "waterloo", "0130", string.Empty, string.Empty, new SimTime(16249)));

        Single<TdStepEvent>("""{"CC_MSG":{"area_id":"waterloo","to":"0149","descr":"1A30","msg_type":"CC"}}""")
            .Should().Be(new TdStepEvent(At, TdStepKind.Interpose, "waterloo", string.Empty, "0149", "1A30", null));
    }

    // ---- SG_MSG and snapshots ----

    [Fact]
    public void Parses_SG_MSG_with_object_fields()
    {
        var e = Single<SignallingStateEvent>(
            """{"SG_MSG":{"area_id":"waterloo","obj_id":"S123","obj_type":"signal","aspect":"6","rset":"True","bpull":false,"msg_type":"SG","time":"16250"}}""");

        e.ObjectType.Should().Be(SignallingObjectType.Signal);
        e.ObjectTypeText.Should().Be("signal");
        e.ObjectId.Should().Be("S123");
        e.AreaId.Should().Be("waterloo");
        e.Time.Should().Be(new SimTime(16250));
        e.Fields.Keys.Should().BeEquivalentTo("aspect", "rset", "bpull");
        e.GetInt32("aspect").Should().Be(6);
        e.GetBoolean("rset").Should().BeTrue();
        e.GetBoolean("bpull").Should().BeFalse();
        e.GetBoolean("aspect").Should().BeNull();
        e.GetInt32("rset").Should().BeNull();
        e.GetString("missing").Should().BeNull();
        e.GetInt32("missing").Should().BeNull();
        e.GetBoolean("missing").Should().BeNull();
        e.ToString().Should().Contain("S123").And.Contain("aspect=6");
    }

    [Theory]
    [InlineData("track", SignallingObjectType.Track)]
    [InlineData("point", SignallingObjectType.Point)]
    [InlineData("route", SignallingObjectType.Route)]
    [InlineData("ulc", SignallingObjectType.Subroute)]
    [InlineData("flag", SignallingObjectType.Flag)]
    [InlineData("frame", SignallingObjectType.Frame)]
    [InlineData("crossing", SignallingObjectType.Crossing)]
    [InlineData("AHB", SignallingObjectType.Ahb)]
    [InlineData("gizmo", SignallingObjectType.Unknown)]
    public void Maps_SG_object_types(string type, SignallingObjectType expected) =>
        Single<SignallingStateEvent>($$$"""{"SG_MSG":{"obj_id":"X1","obj_type":"{{{type}}}"}}""").ObjectType.Should().Be(expected);

    [Theory]
    [InlineData("T1", SignallingObjectType.Track)]
    [InlineData("P1", SignallingObjectType.Point)]
    [InlineData("S1", SignallingObjectType.Signal)]
    [InlineData("RS1AM", SignallingObjectType.Route)]
    [InlineData("U1", SignallingObjectType.Subroute)]
    [InlineData("L1", SignallingObjectType.Flag)]
    [InlineData("F1", SignallingObjectType.Frame)]
    [InlineData("G1", SignallingObjectType.Crossing)]
    [InlineData("h1", SignallingObjectType.Ahb)]
    [InlineData("Z1", SignallingObjectType.Unknown)]
    public void Infers_SG_object_type_from_the_ID_prefix_when_missing(string id, SignallingObjectType expected) =>
        Single<SignallingStateEvent>($$$"""{"SG_MSG":{"obj_id":"{{{id}}}"}}""").ObjectType.Should().Be(expected);

    [Fact]
    public void Snapshot_with_duplicate_keys_gives_one_event_per_property()
    {
        var body = """
            {"SG_MSG":{"obj_id":"T1","obj_type":"track","clear":"True"},
             "SG_MSG":{"obj_id":"T2","obj_type":"track","clear":"False"},
             "SG_MSG":{"obj_id":"P1","obj_type":"point","dn":"True","locked":"False"},
             "CA_MSG":{"from":"A","to":"B","descr":"1A01"}}
            """;

        var events = Parse(body, GatewayTopics.TrainDescriber);

        events.Should().HaveCount(4);
        events.OfType<SignallingStateEvent>().Select(e => e.ObjectId).Should().Equal("T1", "T2", "P1");
        events.OfType<SignallingStateEvent>().Select(e => e.GetBoolean("clear")).Should().Equal(true, false, null);
        events[3].Should().BeOfType<TdStepEvent>();
    }

    [Fact]
    public void Duplicate_fields_inside_a_message_keep_the_last_value_and_nested_values_are_skipped()
    {
        var e = Single<SignallingStateEvent>("""{"SG_MSG":{"obj_id":"T1","clear":"False","extra":{"deep":[1,2,{"x":1}]},"list":[1],"clear":"True","none":null}}""");

        e.GetBoolean("clear").Should().BeTrue();
        e.Fields.Keys.Should().BeEquivalentTo("clear");
    }

    [Fact]
    public void Several_message_types_in_one_body()
    {
        var events = Parse("""{"clock_msg":{"clock":1,"interval":500},"train_delay":{"headcode":"1A01","delay":0}}""");

        events.Select(e => e.GetType()).Should().Equal(typeof(ClockEvent), typeof(TrainDelayEvent));
    }

    [Fact]
    public void Message_type_names_are_case_insensitive()
    {
        Single<ClockEvent>("""{"CLOCK_MSG":{"clock":1,"interval":500}}""");
        Single<SignallingStateEvent>("""{"sg_msg":{"obj_id":"T1"}}""");
    }

    // ---- ttsummary reply ----

    [Fact]
    public void Parses_a_ttsummary_array()
    {
        var e = Single<TimetableSummaryEvent>("""
            [{"headcode":"1W51","uid":"","status":"active","delay":"0","locationId":"WLOO","location":"Waterloo","platformId":"","platform":"11"},
             {"headcode":"2C05","uid":"","status":"ok","delay":"-120"},
             {"uid":"x","status":"ok"},
             {"headcode":"2X99","status":"done","delay":"","locationId":" "},
             {"headcode":"9Z99","status":"queued","nested":{"a":1}}]
            """);

        e.Topic.Should().Be(GatewayTopics.TrainMovements);
        e.Trains.Should().HaveCount(4);
        e.Trains[0].Should().Be(new TrainSummary(new Headcode("1W51"), "", TrainSummaryStatus.Active, "active", 0, new Tiploc("WLOO"), "Waterloo", "11"));
        e.Trains[1].Should().Be(new TrainSummary(new Headcode("2C05"), "", TrainSummaryStatus.Ok, "ok", -120, null, "", ""));
        e.Trains[2].Should().BeEquivalentTo(new { Status = TrainSummaryStatus.Done, DelaySeconds = (int?)null, Location = (Tiploc?)null });
        e.Trains[3].Should().BeEquivalentTo(new { Status = TrainSummaryStatus.Unknown, StatusText = "queued" });
    }

    [Fact]
    public void Flat_out_clock_is_flagged()
    {
        Single<ClockEvent>("""{"clock_msg":{"area_id":"waterloo","clock":21360,"interval":1,"paused":false}}""").IsMaximumSpeed.Should().BeTrue();
        Single<ClockEvent>("""{"clock_msg":{"area_id":"waterloo","clock":21360,"interval":31,"paused":false}}""").IsMaximumSpeed.Should().BeFalse();
    }

    [Fact]
    public void Payware_style_headcodes_and_delay_uids_are_kept()
    {
        // Shapes seen in an Australian payware sim: non-standard headcodes, and train_delay carrying the WTT UID.
        var move = Single<TrainLocationEvent>("""{"train_location":{"headcode":"178H","uid":"6","action":"pass","location":"SM269","platform":"","time":50405,"aspPass":0,"aspAppr":0}}""");
        var delay = Single<TrainDelayEvent>("""{"train_delay":{"headcode":"1127","uid":"1127S","delay":-60}}""");

        move.Headcode.Should().Be(new Headcode("178H"));
        move.Headcode.IsStandard.Should().BeFalse();
        move.AspectPassed.Should().Be(0);
        delay.Uid.Should().Be("1127S");
    }

    // ---- Bad input never throws ----

    [Fact]
    public void Unknown_message_types_are_kept_raw()
    {
        var events = Parse("""{"ttsummary":{"trains":[{"hc":"1A01"}]},"ttsummary":{}}""", GatewayTopics.TrainMovementSummary);

        events.Should().HaveCount(2);
        var first = events[0].Should().BeOfType<UnknownEvent>().Subject;
        first.MessageType.Should().Be("ttsummary");
        first.Topic.Should().Be(GatewayTopics.TrainMovementSummary);
        first.Raw.Should().Be("""{"trains":[{"hc":"1A01"}]}""");
        first.Reason.Should().Be("Unknown message type.");
    }

    [Theory]
    [InlineData("""{"train_location":"oops"}""", "not an object")]
    [InlineData("""{"train_location":[1,2]}""", "not an object")]
    [InlineData("[1,2,3]", "Array element is not an object")]
    [InlineData("[]", "no train summaries")]
    [InlineData("""[{"status":"ok"}]""", "no train summaries")]
    [InlineData("\"text\"", "not a JSON object")]
    [InlineData("{\"train_location\":{\"uid\":", "Malformed JSON")]
    [InlineData("{not json}", "Malformed JSON")]
    [InlineData("", "Empty body")]
    [InlineData(" \r\n", "Empty body")]
    public void Bad_bodies_give_unknown_events(string body, string reason)
    {
        var e = Single<UnknownEvent>(body);

        e.Reason.Should().Contain(reason);
    }

    [Fact]
    public void Events_before_a_syntax_error_are_kept()
    {
        var events = Parse("""{"clock_msg":{"clock":1,"interval":500},"train_delay":{"delay":""");

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ClockEvent>();
        events[1].Should().BeOfType<UnknownEvent>().Which.Reason.Should().StartWith("Malformed JSON");
    }

    [Fact]
    public void Invalid_UTF8_does_not_throw()
    {
        var body = Encoding.UTF8.GetBytes("""{"train_location":{"headcode":"1A01","uid":"1","action":"pass","location":"X""").Concat(new byte[] { 0xC3, 0x28 }).Concat("\",\"time\":1}}"u8.ToArray()).ToArray();

        var events = _parser.Parse(GatewayTopics.TrainMovements, body, At);

        events.Should().ContainSingle().Which.Should().BeOfType<UnknownEvent>();
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        Single<ClockEvent>("""{/* hi */ "clock_msg":{"clock":1,"interval":500,},}""");
    }
}
