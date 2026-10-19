using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Stringline.App.ViewModels;

namespace Stringline.App.Views;

/// <summary>Visible when the value is not null (or a non-empty string, or true); <c>ConverterParameter=invert</c> flips it.</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            string s => s.Length > 0,
            bool b => b,
            _ => value is not null,
        };
        if (parameter is "invert")
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>The status bar dot's colour.</summary>
public sealed class StatusLightBrushConverter : IValueConverter
{
    private static readonly Brush Off = Frozen(0x9E, 0x9E, 0x9E);
    private static readonly Brush Busy = Frozen(0x19, 0x76, 0xD2);
    private static readonly Brush Good = Frozen(0x2E, 0x7D, 0x32);
    private static readonly Brush Warning = Frozen(0xEF, 0x6C, 0x00);
    private static readonly Brush Bad = Frozen(0xC6, 0x28, 0x28);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        StatusLight.Busy => Busy,
        StatusLight.Good => Good,
        StatusLight.Warning => Warning,
        StatusLight.Bad => Bad,
        _ => Off,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    internal static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>The info bar's background by severity.</summary>
public sealed class InfoSeverityBrushConverter : IValueConverter
{
    private static readonly Brush Info = StatusLightBrushConverter.Frozen(0xE3, 0xF2, 0xFD);
    private static readonly Brush Warning = StatusLightBrushConverter.Frozen(0xFF, 0xF3, 0xE0);
    private static readonly Brush Error = StatusLightBrushConverter.Frozen(0xFF, 0xEB, 0xEE);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        InfoSeverity.Warning => Warning,
        InfoSeverity.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A swatch for a <c>#RRGGBB</c> colour; transparent if the text is not a colour.</summary>
public sealed class ColourTextBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!Network.Profiles.Colour.TryParse(value as string, out var colour))
        {
            return Brushes.Transparent;
        }

        var brush = new SolidColorBrush(Color.FromArgb(colour.A, colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Not, for binding <c>IsReadOnly</c> to a flag that means editable.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public static InverseBooleanConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>True when the value is not null, for enabling a panel while something is selected.</summary>
public sealed class NotNullToBooleanConverter : IValueConverter
{
    public static NotNullToBooleanConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
