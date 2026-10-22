namespace Stringline.Core.Tests;

public sealed class SimTimeNotationTests
{
    private static SimTime T(int h, int m, int s = 0) => new((h * 3600) + (m * 60) + s);

    [Theory]
    [InlineData("12:30", 12, 30, 0, TimeMark.None)]
    [InlineData("9:30", 9, 30, 0, TimeMark.None)]
    [InlineData("1230", 12, 30, 0, TimeMark.None)]
    [InlineData("930", 9, 30, 0, TimeMark.None)]
    [InlineData("12/34", 12, 34, 0, TimeMark.Pass)]
    [InlineData("12d34", 12, 34, 0, TimeMark.SetDown)]
    [InlineData("12D34", 12, 34, 0, TimeMark.SetDown)]
    [InlineData("12t34", 12, 34, 0, TimeMark.ThruLine)]
    [InlineData("12w34", 12, 34, 0, TimeMark.DwellWait)]
    [InlineData("12r34", 12, 34, 0, TimeMark.Request)]
    [InlineData("1234H", 12, 34, 30, TimeMark.None)]
    [InlineData("1234h", 12, 34, 30, TimeMark.None)]
    [InlineData("13:45H", 13, 45, 30, TimeMark.None)]
    [InlineData("13:45½", 13, 45, 30, TimeMark.None)]
    [InlineData("12/34H", 12, 34, 30, TimeMark.Pass)]
    [InlineData("12:30:15", 12, 30, 15, TimeMark.None)]
    [InlineData(" 24:01 ", 24, 1, 0, TimeMark.None)]
    [InlineData("2510", 25, 10, 0, TimeMark.None)]
    public void Parses_every_code(string text, int h, int m, int s, TimeMark mark)
    {
        SimTimeNotation.Parse(text).Should().Be(new NotatedTime(T(h, m, s), mark));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12")]
    [InlineData("12:3")]
    [InlineData("12:60")]
    [InlineData("12x30")]
    [InlineData("12345")]
    [InlineData("12:30:15H")]
    [InlineData("+3")]
    [InlineData("+")]
    [InlineData("ab:cd")]
    public void Rejects_what_is_not_a_time(string text)
    {
        SimTimeNotation.TryParse(text, null, out _).Should().BeFalse();
    }

    [Fact]
    public void Plus_is_minutes_after_the_previous_time()
    {
        SimTimeNotation.Parse("+3", T(12, 30)).Time.Should().Be(T(12, 33));
        SimTimeNotation.Parse("+2H", T(12, 30)).Time.Should().Be(T(12, 32, 30));
        SimTimeNotation.Parse("+45", T(23, 50)).Time.Should().Be(T(24, 35));
    }

    [Fact]
    public void A_time_well_before_the_previous_is_the_next_day()
    {
        SimTimeNotation.Parse("00:10", T(23, 50)).Time.Should().Be(T(24, 10));
        SimTimeNotation.Parse("0010", T(47, 50)).Time.Should().Be(T(48, 10));
        SimTimeNotation.Parse("23:45", T(23, 50)).Time.Should().Be(T(23, 45), "a few minutes back is a mistake to show, not tomorrow");
        SimTimeNotation.Parse("24:01", T(23, 50)).Time.Should().Be(T(24, 1));
        SimTimeNotation.Parse("00:10").Time.Should().Be(T(0, 10), "with nothing before it, day 0");
    }

    [Theory]
    [InlineData(12, 30, 0, TimeMark.None, "12:30")]
    [InlineData(12, 34, 30, TimeMark.Pass, "12/34H")]
    [InlineData(9, 5, 0, TimeMark.SetDown, "09d05")]
    [InlineData(12, 34, 0, TimeMark.ThruLine, "12t34")]
    [InlineData(12, 34, 0, TimeMark.DwellWait, "12w34")]
    [InlineData(12, 34, 0, TimeMark.Request, "12r34")]
    [InlineData(24, 1, 0, TimeMark.None, "24:01")]
    [InlineData(12, 30, 15, TimeMark.None, "12:30:15")]
    public void Formats_as_SimSig_shows_times(int h, int m, int s, TimeMark mark, string expected)
    {
        var text = SimTimeNotation.Format(T(h, m, s), mark);

        text.Should().Be(expected);
        SimTimeNotation.Parse(text).Should().Be(new NotatedTime(T(h, m, s), mark));
    }

    [Fact]
    public void Formats_compactly()
    {
        SimTimeNotation.Format(T(12, 34, 30), TimeMark.Pass, compact: true).Should().Be("1234H");
        SimTimeNotation.Format(T(25, 10), compact: true).Should().Be("2510");
        new NotatedTime(T(7, 5), TimeMark.Pass).ToString().Should().Be("07/05");
    }
}
