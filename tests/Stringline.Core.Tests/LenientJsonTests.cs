using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stringline.Core.Json;

namespace Stringline.Core.Tests;

public class LenientJsonTests
{
    private static readonly JsonSerializerOptions Gateway = LenientJsonOptions.CreateForGateway();

    // ---- Low-level reader helpers ----

    private delegate bool TryRead<T>(ref Utf8JsonReader reader, out T value);

    private static (bool Ok, T Value) ReadValue<T>(string json, TryRead<T> read)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read().Should().BeTrue();
        var ok = read(ref reader, out var value);
        return (ok, value);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    [InlineData("\"True\"", true)]
    [InlineData("\"FALSE\"", false)]
    [InlineData("\" true \"", true)]
    [InlineData("\"1\"", true)]
    [InlineData("\"0\"", false)]
    public void TryGetBoolean_accepts(string json, bool expected) =>
        ReadValue<bool>(json, LenientJson.TryGetBoolean).Should().Be((true, expected));

    [Theory]
    [InlineData("null")]
    [InlineData("2")]
    [InlineData("-1")]
    [InlineData("0.5")]
    [InlineData("\"\"")]
    [InlineData("\"yes\"")]
    [InlineData("\"2\"")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void TryGetBoolean_rejects(string json) =>
        ReadValue<bool>(json, LenientJson.TryGetBoolean).Ok.Should().BeFalse();

    [Theory]
    [InlineData("15445", 15_445)]
    [InlineData("-240", -240)]
    [InlineData("\"16250\"", 16_250)]
    [InlineData("\" 16250 \"", 16_250)]
    [InlineData("\"-240\"", -240)]
    [InlineData("12.0", 12)]
    [InlineData("\"12.0\"", 12)]
    [InlineData("1e3", 1_000)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    public void TryGetInt32_accepts(string json, int expected) =>
        ReadValue<int>(json, LenientJson.TryGetInt32).Should().Be((true, expected));

    [Theory]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("12.5")]
    [InlineData("\"12.5\"")]
    [InlineData("\"\"")]
    [InlineData("\"abc\"")]
    [InlineData("\"1,000\"")]
    [InlineData("2147483648")]
    [InlineData("\"-2147483649\"")]
    [InlineData("[1]")]
    public void TryGetInt32_rejects(string json) =>
        ReadValue<int>(json, LenientJson.TryGetInt32).Ok.Should().BeFalse();

    [Theory]
    [InlineData("9007199254740993", 9_007_199_254_740_993L)]
    [InlineData("\"9223372036854775807\"", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void TryGetInt64_accepts(string json, long expected) =>
        ReadValue<long>(json, LenientJson.TryGetInt64).Should().Be((true, expected));

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("1e30")]
    [InlineData("\"1e30\"")]
    public void TryGetInt64_rejects_out_of_range(string json) =>
        ReadValue<long>(json, LenientJson.TryGetInt64).Ok.Should().BeFalse();

    [Theory]
    [InlineData("\"5M01\"", "5M01")]
    [InlineData("\"\"", "")]
    [InlineData("4", "4")]
    [InlineData("-1.50", "-1.50")]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("\"caf\\u00e9\"", "café")]
    public void TryGetString_accepts(string json, string expected) =>
        ReadValue<string>(json, LenientJson.TryGetString).Should().Be((true, expected));

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void TryGetString_rejects(string json) =>
        ReadValue<string>(json, LenientJson.TryGetString).Ok.Should().BeFalse();

    [Fact]
    public void Get_methods_throw_JsonException_with_context()
    {
        static void Throws(string json, Action<string> read, string expectedFragment) =>
            FluentActions.Invoking(() => read(json)).Should().Throw<JsonException>().WithMessage($"*{expectedFragment}*");

        static Utf8JsonReader At(string json)
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
            reader.Read();
            return reader;
        }

        Throws("\"maybe\"", j => { var r = At(j); LenientJson.GetBoolean(ref r); }, "\"maybe\"");
        Throws("null", j => { var r = At(j); LenientJson.GetInt32(ref r); }, "Null");
        Throws("\"x\"", j => { var r = At(j); LenientJson.GetInt64(ref r); }, "64-bit");
        Throws("{}", j => { var r = At(j); LenientJson.GetString(ref r); }, "StartObject");
    }

    [Fact]
    public void Get_methods_return_values()
    {
        var reader = new Utf8JsonReader("[\"True\",\"7\",\"8\",9]"u8);
        reader.Read();
        reader.Read();
        LenientJson.GetBoolean(ref reader).Should().BeTrue();
        reader.Read();
        LenientJson.GetInt32(ref reader).Should().Be(7);
        reader.Read();
        LenientJson.GetInt64(ref reader).Should().Be(8);
        reader.Read();
        LenientJson.GetString(ref reader).Should().Be("9");
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("\"\"", true)]
    [InlineData("\"  \"", true)]
    [InlineData("\"x\"", false)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    public void IsNullOrBlank(string json, bool expected)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();
        LenientJson.IsNullOrBlank(ref reader).Should().Be(expected);
    }

    [Fact]
    public void Helpers_work_while_streaming_a_body_with_duplicate_keys()
    {
        // Snapshot replies repeat SG_MSG keys in one object; the helpers must work token by token.
        var json = """{"SG_MSG":{"obj_id":"T1","clear":"True"},"SG_MSG":{"obj_id":"T2","clear":false}}"""u8;
        var reader = new Utf8JsonReader(json);
        var clears = new List<bool>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("clear"))
            {
                reader.Read();
                clears.Add(LenientJson.GetBoolean(ref reader));
            }
        }

        clears.Should().Equal(true, false);
    }

    // ---- Serializer converters ----

    private sealed record Sample(bool Flag, bool? MaybeFlag, int Count, int? MaybeCount, long Big);

    [Fact]
    public void Converters_read_strings_and_natives()
    {
        JsonSerializer.Deserialize<Sample>("""{"flag":"True","maybeFlag":"","count":"1523","maybeCount":null,"big":"9000000000"}""", Gateway)
            .Should().Be(new Sample(true, null, 1523, null, 9_000_000_000));

        JsonSerializer.Deserialize<Sample>("""{"flag":false,"maybeFlag":"0","count":7,"maybeCount":" ","big":1}""", Gateway)
            .Should().Be(new Sample(false, false, 7, null, 1));

        JsonSerializer.Deserialize<Sample>("""{"flag":1,"maybeFlag":true,"count":-3,"maybeCount":"42","big":-1}""", Gateway)
            .Should().Be(new Sample(true, true, -3, 42, -1));
    }

    [Fact]
    public void Converters_write_native_json()
    {
        JsonSerializer.Serialize(new Sample(true, null, 5, null, 6), Gateway)
            .Should().Be("""{"Flag":true,"MaybeFlag":null,"Count":5,"MaybeCount":null,"Big":6}""");
        JsonSerializer.Serialize(new Sample(false, false, 5, 9, 6), Gateway)
            .Should().Be("""{"Flag":false,"MaybeFlag":false,"Count":5,"MaybeCount":9,"Big":6}""");
    }

    [Theory]
    [InlineData("""{"flag":"maybe","count":1,"big":1}""")]
    [InlineData("""{"flag":true,"count":"lots","big":1}""")]
    [InlineData("""{"flag":true,"count":null,"big":1}""")]
    [InlineData("""{"flag":true,"count":1,"big":""}""")]
    [InlineData("""{"flag":true,"count":1,"maybeCount":"x","big":1}""")]
    [InlineData("""{"flag":true,"count":1,"maybeFlag":2,"big":1}""")]
    public void Converters_throw_JsonException_on_garbage(string json) =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Sample>(json, Gateway)).Should().Throw<JsonException>();

    private sealed record TrainLocation(
        [property: JsonConverter(typeof(LenientStringConverter))] string? Uid,
        Headcode Headcode,
        Tiploc Location,
        SimTime Time);

    [Fact]
    public void Reads_a_gateway_style_message()
    {
        var msg = JsonSerializer.Deserialize<TrainLocation>(
            """{"headcode":"5M01","uid":4,"action":"pass","location":"INTJN","platform":"5","time":"15445"}""", Gateway)!;

        msg.Uid.Should().Be("4");
        msg.Headcode.Should().Be(new Headcode("5M01"));
        msg.Location.Should().Be(new Tiploc("INTJN"));
        msg.Time.Should().Be(new SimTime(15_445));

        JsonSerializer.Deserialize<TrainLocation>("""{"uid":null,"headcode":"1O28","location":"SVC92","time":15310}""", Gateway)!
            .Uid.Should().BeNull();

        JsonSerializer.Serialize(msg, Gateway)
            .Should().Be("""{"Uid":"4","Headcode":"5M01","Location":"INTJN","Time":15445}""");
    }

    [Theory]
    [InlineData("15445", 15_445)]
    [InlineData("\"15445\"", 15_445)]
    [InlineData("\"04:17:25\"", 15_445)]
    [InlineData("\"01:30½+1\"", 91_830)]
    [InlineData("-60", -60)]
    public void SimTime_reads_seconds_or_clock_text(string json, int expected) =>
        JsonSerializer.Deserialize<SimTime>(json).Seconds.Should().Be(expected);

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"soon\"")]
    [InlineData("true")]
    [InlineData("1.5")]
    public void SimTime_rejects_garbage(string json) =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<SimTime>(json)).Should().Throw<JsonException>();

    [Fact]
    public void SimTime_writes_seconds() =>
        JsonSerializer.Serialize(new SimTime(91_830)).Should().Be("91830");

    [Fact]
    public void Identifiers_round_trip_as_strings_and_dictionary_keys()
    {
        JsonSerializer.Serialize(new Tiploc("wimbldn")).Should().Be("\"WIMBLDN\"");
        JsonSerializer.Serialize(new Headcode("2r02")).Should().Be("\"2R02\"");
        JsonSerializer.Serialize(new TrainId("t-17")).Should().Be("\"t-17\"");

        JsonSerializer.Deserialize<Tiploc>("\"wimbldn\"").Should().Be(new Tiploc("WIMBLDN"));
        JsonSerializer.Deserialize<Headcode>("\"2R02\"").Should().Be(new Headcode("2R02"));
        JsonSerializer.Deserialize<TrainId>("\"t-17\"").Should().Be(new TrainId("t-17"));
        JsonSerializer.Deserialize<TrainId>("17").Should().Be(new TrainId("17"));

        var positions = new Dictionary<Tiploc, double> { [new Tiploc("WATRLMN")] = 0, [new Tiploc("CLPHMJ")] = 6.4 };
        var json = JsonSerializer.Serialize(positions);
        json.Should().Be("""{"WATRLMN":0,"CLPHMJ":6.4}""");
        JsonSerializer.Deserialize<Dictionary<Tiploc, double>>(json).Should().BeEquivalentTo(positions);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"  \"")]
    [InlineData("null")]
    [InlineData("{}")]
    public void Identifiers_reject_blank(string json)
    {
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Tiploc>(json)).Should().Throw<JsonException>();
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Headcode>(json)).Should().Throw<JsonException>();
        FluentActions.Invoking(() => JsonSerializer.Deserialize<TrainId>(json)).Should().Throw<JsonException>();
    }

    [Fact]
    public void Identifier_dictionary_key_must_be_valid() =>
        FluentActions.Invoking(() => JsonSerializer.Deserialize<Dictionary<Tiploc, int>>("""{" ":1}"""))
            .Should().Throw<JsonException>();

    [Fact]
    public void AddLenientConverters_rejects_null() =>
        FluentActions.Invoking(() => LenientJsonOptions.AddLenientConverters(null!)).Should().Throw<ArgumentNullException>();
}
