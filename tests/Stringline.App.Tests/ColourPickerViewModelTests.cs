using Stringline.App.ViewModels;
using Stringline.Network.Profiles;

namespace Stringline.App.Tests;

/// <summary>#24: the category colour picker's swatches, sliders and code stay in step.</summary>
public sealed class ColourPickerViewModelTests
{
    private static readonly Colour Express = Colour.Parse("#D32F2F");

    [Fact]
    public void It_opens_on_the_colour_it_was_given()
    {
        var picker = new ColourPickerViewModel("Category colour", "Colour for Express (XP)", Express);

        picker.Colour.Should().Be(Express);
        picker.Hex.Should().Be("#D32F2F");
        (picker.Red, picker.Green, picker.Blue).Should().Be((0xD3, 0x2F, 0x2F));
        picker.ClassColours.Should().HaveCount(10).And.Contain(s => s.Colour == CategoryStyle.ForClass(2).Colour);
        picker.Palette.Select(s => s.Colour).Should().OnlyHaveUniqueItems();
        picker.HasInUse.Should().BeFalse();
    }

    [Fact]
    public void Swatches_sliders_and_code_follow_each_other()
    {
        var picker = new ColourPickerViewModel("t", "h", Express);

        picker.ChooseCommand.Execute(picker.ClassColours.Single(s => s.Name.StartsWith("Class 2", StringComparison.Ordinal)));
        picker.Colour.Should().Be(Colour.Parse("#1976D2"));
        picker.Hex.Should().Be("#1976D2");
        picker.Green.Should().Be(0x76);

        picker.Red = 255;
        picker.Colour.Should().Be(Colour.Parse("#FF76D2"));
        picker.Hex.Should().Be("#FF76D2");
        picker.ColourHex.Should().Be("#FF76D2");

        picker.Hex = "00ff00";
        picker.Colour.Should().Be(Colour.Parse("#00FF00"));
        picker.Hex.Should().Be("00ff00", "the box keeps what is being typed");
        (picker.Red, picker.Green, picker.Blue).Should().Be((0, 255, 0));

        picker.ResetCommand.Execute(null);
        picker.Colour.Should().Be(Express);
        picker.Hex.Should().Be("#D32F2F");
    }

    [Fact]
    public void A_half_typed_code_waits_and_a_wrong_one_is_refused()
    {
        var picker = new ColourPickerViewModel("t", "h", Express);

        picker.Hex = "#12";
        picker.Colour.Should().Be(Express, "a half-typed code leaves the colour as it was");
        picker.TryAccept().Should().BeFalse();
        picker.Error.Should().Contain("'#12' is not a colour");

        picker.Hex = "#123456";
        picker.Error.Should().BeNull();
        picker.TryAccept().Should().BeTrue();
        picker.Colour.Should().Be(Colour.Parse("#123456"));
    }

    [Fact]
    public void Alpha_is_kept_unless_typed()
    {
        var faint = new Colour(0x10, 0x20, 0x30, 0x80);
        var picker = new ColourPickerViewModel("t", "h", faint);

        picker.Hex.Should().Be("#10203080");
        picker.Blue = 0x40;
        picker.Colour.Should().Be(new Colour(0x10, 0x20, 0x40, 0x80));
        picker.Hex = "#102040FF";
        picker.Colour.A.Should().Be(255);
    }

    [Fact]
    public void Colours_in_use_are_listed_once()
    {
        var blue = Colour.Parse("#1976D2");
        var picker = new ColourPickerViewModel("t", "h", Express, [new(blue, "Stopping (OO)"), new(blue, "Semi-fast (OS)")]);

        picker.HasInUse.Should().BeTrue();
        picker.InUse.Should().ContainSingle().Which.ToolTip.Should().Be("Stopping (OO) (#1976D2)");
    }
}
