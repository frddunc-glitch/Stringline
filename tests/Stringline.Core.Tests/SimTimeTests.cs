namespace Stringline.Core.Tests;

public class SimTimeTests
{
    [Fact]
    public void Default_is_midnight_day_zero()
    {
        SimTime.Zero.Should().Be(default(SimTime));
        SimTime.Zero.Seconds.Should().Be(0);
        SimTime.Zero.ToString().Should().Be("00:00:00");
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(9, 5, 30, 0, 32_730)]
    [InlineData(23, 59, 59, 0, 86_399)]
    [InlineData(1, 0, 0, 1, 90_000)]
    [InlineData(23, 0, 0, -1, -3_600)]
    public void FromClock_computes_seconds(int h, int m, int s, int day, int expected) =>
        SimTime.FromClock(h, m, s, day).Seconds.Should().Be(expected);

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(0, 60, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 60)]
    [InlineData(0, 0, -1)]
    public void FromClock_rejects_out_of_range_parts(int h, int m, int s) =>
        FluentActions.Invoking(() => SimTime.FromClock(h, m, s)).Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(32_730, 0, 32_730, 9, 5, 30)]
    [InlineData(86_399, 0, 86_399, 23, 59, 59)]
    [InlineData(86_400, 1, 0, 0, 0, 0)]
    [InlineData(91_815, 1, 5_415, 1, 30, 15)]
    [InlineData(-1, -1, 86_399, 23, 59, 59)]
    [InlineData(-86_400, -1, 0, 0, 0, 0)]
    [InlineData(-86_401, -2, 86_399, 23, 59, 59)]
    public void Components_use_floor_day(int seconds, int day, int secondOfDay, int hour, int minute, int second)
    {
        var t = new SimTime(seconds);
        t.Day.Should().Be(day);
        t.SecondOfDay.Should().Be(secondOfDay);
        t.Hour.Should().Be(hour);
        t.Minute.Should().Be(minute);
        t.Second.Should().Be(second);
    }

    [Fact]
    public void Arithmetic_and_comparison()
    {
        var nine = SimTime.FromClock(9, 0);
        var later = nine + 90;

        later.Should().Be(SimTime.FromClock(9, 1, 30));
        (later - 90).Should().Be(nine);
        (later - nine).Should().Be(90);
        (nine - later).Should().Be(-90);
        nine.AddMinutes(61).Should().Be(SimTime.FromClock(10, 1));
        nine.AddSeconds(-1).Should().Be(SimTime.FromClock(8, 59, 59));

        (nine < later).Should().BeTrue();
        (nine <= SimTime.FromClock(9, 0)).Should().BeTrue();
        (later > nine).Should().BeTrue();
        (later >= nine).Should().BeTrue();
        (nine > later).Should().BeFalse();
        SimTime.Min(nine, later).Should().Be(nine);
        SimTime.Max(nine, later).Should().Be(later);
        nine.CompareTo(later).Should().BeNegative();
        nine.CompareTo((object)later).Should().BeNegative();
        nine.CompareTo(null).Should().BePositive();
        FluentActions.Invoking(() => nine.CompareTo("09:00")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Sorts_by_seconds()
    {
        var times = new[] { new SimTime(90_000), new SimTime(-5), new SimTime(3_600) };
        times.Order().Should().Equal(new SimTime(-5), new SimTime(3_600), new SimTime(90_000));
    }

    [Fact]
    public void Arithmetic_overflow_throws() =>
        FluentActions.Invoking(() => new SimTime(int.MaxValue) + 1).Should().Throw<OverflowException>();

    // ---- Formatting ----

    [Theory]
    [InlineData(0, null, "00:00:00")]
    [InlineData(32_730, null, "09:05:30")]
    [InlineData(32_730, "", "09:05:30")]
    [InlineData(32_730, "T", "09:05:30")]
    [InlineData(32_730, "G", "09:05:30")]
    [InlineData(32_730, "t", "09:05")]
    [InlineData(32_759, "t", "09:05")]
    [InlineData(32_700, "H", "09:05")]
    [InlineData(32_729, "H", "09:05")]
    [InlineData(32_730, "H", "09:05½")]
    [InlineData(32_759, "H", "09:05½")]
    [InlineData(86_399, null, "23:59:59")]
    [InlineData(86_400, null, "00:00:00+1")]
    [InlineData(91_830, "H", "01:30½+1")]
    [InlineData(91_830, "t", "01:30+1")]
    [InlineData(2 * 86_400 + 60, null, "00:01:00+2")]
    [InlineData(-60, null, "23:59:00-1")]
    [InlineData(-86_401, "t", "23:59-2")]
    public void Formats(int seconds, string? format, string expected) =>
        new SimTime(seconds).ToString(format).Should().Be(expected);

    [Fact]
    public void Extreme_values_format()
    {
        new SimTime(int.MaxValue).ToString().Should().Be("03:14:07+24855");
        new SimTime(int.MinValue).ToString().Should().Be("20:45:52-24856");
    }

    [Theory]
    [InlineData("X")]
    [InlineData("TT")]
    [InlineData("HH:mm")]
    public void Unknown_format_throws(string format) =>
        FluentActions.Invoking(() => new SimTime(0).ToString(format)).Should().Throw<FormatException>();

    [Fact]
    public void Works_in_interpolated_strings_with_format() =>
        $"{new SimTime(32_730):H} / {new SimTime(32_730)}".Should().Be("09:05½ / 09:05:30");

    [Fact]
    public void TryFormat_reports_short_buffer()
    {
        var t = new SimTime(91_830);
        Span<char> small = stackalloc char[8];
        t.TryFormat(small, out var written, "T", null).Should().BeFalse();
        written.Should().Be(0);

        Span<char> exact = stackalloc char[10];
        t.TryFormat(exact, out written, "T", null).Should().BeTrue();
        exact[..written].ToString().Should().Be("01:30:30+1");

        Span<char> tiny = stackalloc char[1];
        t.TryFormat(tiny, out _, "t", null).Should().BeFalse();
        Span<char> noDay = stackalloc char[6];
        t.TryFormat(noDay, out _, "H", null).Should().BeFalse();
        Span<char> noDayDigits = stackalloc char[7];
        t.TryFormat(noDayDigits, out _, "H", null).Should().BeFalse();
        Span<char> hoursAndMinutesOnly = stackalloc char[5];
        t.TryFormat(hoursAndMinutesOnly, out _, "T", null).Should().BeFalse();
        t.TryFormat(hoursAndMinutesOnly, out _, "H", null).Should().BeFalse();
        Span<char> noSecondDigits = stackalloc char[6];
        t.TryFormat(noSecondDigits, out _, "T", null).Should().BeFalse();
    }

    // ---- Parsing ----

    [Theory]
    [InlineData("00:00", 0)]
    [InlineData("9:05", 32_700)]
    [InlineData("09:05", 32_700)]
    [InlineData("09:05:30", 32_730)]
    [InlineData("  09:05:30 ", 32_730)]
    [InlineData("0905", 32_700)]
    [InlineData("090530", 32_730)]
    [InlineData("09:05½", 32_730)]
    [InlineData("09:05H", 32_730)]
    [InlineData("09:05h", 32_730)]
    [InlineData("0905H", 32_730)]
    [InlineData("23:59:59", 86_399)]
    [InlineData("24:00", 86_400)]
    [InlineData("25:10", 90_600)]
    [InlineData("00:00:00+1", 86_400)]
    [InlineData("01:30½+1", 91_830)]
    [InlineData("01:30H+1", 91_830)]
    [InlineData("23:59:00-1", -60)]
    [InlineData("12:00+10", 10 * 86_400 + 43_200)]
    public void Parses(string text, int expectedSeconds)
    {
        SimTime.Parse(text).Seconds.Should().Be(expectedSeconds);
        SimTime.TryParse(text, out var t).Should().BeTrue();
        t.Seconds.Should().Be(expectedSeconds);
        SimTime.Parse(text.AsSpan()).Seconds.Should().Be(expectedSeconds);
        SimTime.TryParse(text.AsSpan(), out var fromSpan).Should().BeTrue();
        fromSpan.Seconds.Should().Be(expectedSeconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("9")]
    [InlineData("905")]
    [InlineData("09:5")]
    [InlineData("09:60")]
    [InlineData("09:05:60")]
    [InlineData("09:05:3")]
    [InlineData("123:00")]
    [InlineData("09:05:30½")]
    [InlineData("090530H")]
    [InlineData("-09:00")]
    [InlineData("+09:00")]
    [InlineData("09:00+")]
    [InlineData("09:00+1234")]
    [InlineData("09:00+x")]
    [InlineData("09-00")]
    [InlineData("ab:cd")]
    [InlineData("09:00:00:00")]
    [InlineData("09.00")]
    [InlineData("½")]
    [InlineData("16250")]
    public void Rejects_invalid(string text)
    {
        SimTime.TryParse(text, out var t).Should().BeFalse();
        t.Should().Be(default(SimTime));
        FluentActions.Invoking(() => SimTime.Parse(text)).Should().Throw<FormatException>();
    }

    [Fact]
    public void Null_input()
    {
        SimTime.TryParse((string?)null, out _).Should().BeFalse();
        FluentActions.Invoking(() => SimTime.Parse(null!)).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Generic_parsable_interface_works()
    {
        static T ParseVia<T>(string s) where T : IParsable<T> => T.Parse(s, null);
        ParseVia<SimTime>("10:00").Should().Be(SimTime.FromClock(10, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("t")]
    [InlineData("H")]
    public void Round_trips_through_format_and_parse(string? format)
    {
        // Every second across a day boundary on either side; short formats lose precision as documented.
        for (var s = -2 * SimTime.SecondsPerDay; s <= 3 * SimTime.SecondsPerDay; s += 7)
        {
            var t = new SimTime(s);
            var expected = format switch
            {
                "t" => s - t.Second,
                "H" => s - (t.Second % 30),
                _ => s,
            };

            SimTime.Parse(t.ToString(format)).Seconds.Should().Be(expected, "{0} formatted as {1}", s, t.ToString(format));
        }
    }
}
