using Stringline.Core;

namespace Stringline.Timetable.Tests;

public sealed class DescriptionTemplateTests
{
    private static readonly DescriptionTemplate.Fields Full = new("175A", new SimTime(13380), "Penrith Down Yard", "Emu Plains", new SimTime(13620), "SYT", "EMU A Set [8] 'Waratah'");

    [Theory]
    [InlineData("$originTime $originName-$destName $operator ($stock)", "03:43 Penrith Down Yard-Emu Plains SYT (EMU A Set [8] 'Waratah')")]
    [InlineData("$originTime $originName-$destName $destTime $operator ($stock)", "03:43 Penrith Down Yard-Emu Plains 03:47 SYT (EMU A Set [8] 'Waratah')")]
    [InlineData("$operator $stock", "SYT EMU A Set [8] 'Waratah'")]
    [InlineData("$HEADCODE $id $destinationName $destinationTime", "175A 175A Emu Plains 03:47")]
    [InlineData("$mystery $originName", "$mystery Penrith Down Yard")]
    public void Fills_in_the_header_template(string template, string expected)
    {
        DescriptionTemplate.Expand("$template", template, Full).Should().Be(expected);
    }

    [Fact]
    public void A_plain_description_is_kept()
    {
        DescriptionTemplate.Expand(" 05:30 London Waterloo - Weymouth (5 444) ", "$operator", Full).Should().Be("05:30 London Waterloo - Weymouth (5 444)");
    }

    [Fact]
    public void Fields_in_a_train_description_are_filled_in_too()
    {
        DescriptionTemplate.Expand("$originTime to $destName", null, Full).Should().Be("03:43 to Emu Plains");
    }

    [Fact]
    public void Empty_fields_leave_no_stray_spaces_or_brackets()
    {
        var empty = new DescriptionTemplate.Fields("1A01", null, null, "Central", null, null, null);

        DescriptionTemplate.Expand("$TEMPLATE", "$originTime $originName-$destName $operator ($stock)", empty).Should().Be("-Central");
    }

    [Fact]
    public void Times_on_later_days_show_the_clock_time()
    {
        var late = Full with { OriginTime = SimTime.FromClock(1, 5, day: 1) };

        DescriptionTemplate.Expand("$originTime", null, late).Should().Be("01:05");
    }

    [Fact]
    public void Template_without_a_header_template_is_empty()
    {
        DescriptionTemplate.Expand("$template", null, Full).Should().BeEmpty();
    }
}
