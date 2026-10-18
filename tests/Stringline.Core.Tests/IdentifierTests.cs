namespace Stringline.Core.Tests;

public class IdentifierTests
{
    [Theory]
    [InlineData("WATRLMN", "WATRLMN")]
    [InlineData(" clphmj ", "CLPHMJ")]
    [InlineData("SVC92", "SVC92")]
    public void Tiploc_normalises(string input, string expected)
    {
        new Tiploc(input).Code.Should().Be(expected);
        new Tiploc(input).ToString().Should().Be(expected);
        Tiploc.TryCreate(input, out var t).Should().BeTrue();
        t.Code.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("WAT RLMN")]
    public void Tiploc_rejects_invalid(string? input)
    {
        Tiploc.TryCreate(input, out var t).Should().BeFalse();
        t.IsEmpty.Should().BeTrue();
        FluentActions.Invoking(() => new Tiploc(input!)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tiploc_equality_is_case_insensitive_via_normalisation()
    {
        new Tiploc("clphmj").Should().Be(new Tiploc("CLPHMJ"));
        (new Tiploc("A") == new Tiploc("a")).Should().BeTrue();
        new Tiploc("A").GetHashCode().Should().Be(new Tiploc("a").GetHashCode());
        new Tiploc("A").Should().NotBe(new Tiploc("B"));
    }

    [Fact]
    public void Default_identifiers_are_empty()
    {
        default(Tiploc).IsEmpty.Should().BeTrue();
        default(Tiploc).Code.Should().BeEmpty();
        default(Tiploc).ToString().Should().BeEmpty();
        default(Headcode).IsEmpty.Should().BeTrue();
        default(Headcode).Value.Should().BeEmpty();
        default(Headcode).IsStandard.Should().BeFalse();
        default(Headcode).ClassDigit.Should().BeNull();
        default(TrainId).IsEmpty.Should().BeTrue();
        default(TrainId).Value.Should().BeEmpty();
        new Tiploc("X").IsEmpty.Should().BeFalse();
    }

    [Theory]
    [InlineData("1A23", "1A23", true, 1)]
    [InlineData(" 2r02 ", "2R02", true, 2)]
    [InlineData("0B00", "0B00", true, 0)]
    [InlineData("5M01", "5M01", true, 5)]
    [InlineData("9Z99", "9Z99", true, 9)]
    [InlineData("1A2", "1A2", false, 1)]
    [InlineData("1A234", "1A234", false, 1)]
    [InlineData("11A2", "11A2", false, 1)]
    [InlineData("A123", "A123", false, null)]
    [InlineData("SHUNT", "SHUNT", false, null)]
    public void Headcode_parts(string input, string value, bool standard, int? classDigit)
    {
        var h = new Headcode(input);
        h.Value.Should().Be(value);
        h.ToString().Should().Be(value);
        h.IsStandard.Should().Be(standard);
        h.ClassDigit.Should().Be(classDigit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1A 23")]
    public void Headcode_rejects_invalid(string? input)
    {
        Headcode.TryCreate(input, out var h).Should().BeFalse();
        h.IsEmpty.Should().BeTrue();
        FluentActions.Invoking(() => new Headcode(input!)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Headcode_equality() =>
        new Headcode("1a23").Should().Be(new Headcode("1A23"));

    [Fact]
    public void TrainId_is_opaque_and_case_sensitive()
    {
        var id = new TrainId("1A23#2");
        id.Value.Should().Be("1A23#2");
        id.ToString().Should().Be("1A23#2");
        id.Should().Be(new TrainId("1A23#2"));
        id.Should().NotBe(new TrainId("1a23#2"));
        TrainId.TryCreate("x", out var x).Should().BeTrue();
        x.Value.Should().Be("x");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void TrainId_rejects_blank(string? input)
    {
        TrainId.TryCreate(input, out var id).Should().BeFalse();
        id.IsEmpty.Should().BeTrue();
        FluentActions.Invoking(() => new TrainId(input!)).Should().Throw<ArgumentException>();
    }
}
