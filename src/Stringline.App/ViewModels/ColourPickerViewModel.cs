using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.Network.Profiles;

namespace Stringline.App.ViewModels;

/// <summary>A colour to pick with one click, with its name for the tooltip.</summary>
internal sealed record ColourSwatch(Colour Colour, string Name)
{
    /// <summary><c>#RRGGBB</c>, for the swatch's brush.</summary>
    public string Hex => Colour.ToString();

    public string ToolTip => $"{Name} ({Hex})";
}

/// <summary>
/// Picks a colour for a train category (#24): a palette of swatches, red, green and blue sliders, and the
/// <c>#RRGGBB</c> code, which stay in step. The colour's alpha is kept unless a <c>#RRGGBBAA</c> code is typed.
/// </summary>
internal sealed partial class ColourPickerViewModel : ObservableObject
{
    private bool _syncing;

    public ColourPickerViewModel(string title, string heading, Colour original, IEnumerable<ColourSwatch>? inUse = null)
    {
        Title = title;
        Heading = heading;
        Original = original;
        InUse = [.. (inUse ?? []).DistinctBy(s => s.Colour)];
        Show(original);
    }

    public string Title { get; }

    public string Heading { get; }

    /// <summary>The colour before the picker opened, shown beside the new one.</summary>
    public Colour Original { get; }

    public string OriginalHex => Original.ToString();

    /// <summary>The headcode classes' default colours (<see cref="CategoryStyle.ForClass"/>), so a category can go back to one.</summary>
    public IReadOnlyList<ColourSwatch> ClassColours { get; } = ClassNames
        .Select(c => new ColourSwatch(CategoryStyle.ForClass(c.Digit).Colour, c.Name))
        .ToList();

    /// <summary>A wider palette, in hue order, darker shades second.</summary>
    public IReadOnlyList<ColourSwatch> Palette { get; } = PaletteColours
        .Select(c => new ColourSwatch(Colour.Parse(c.Hex), c.Name))
        .ToList();

    /// <summary>The colours the profile's other categories use, so two can be told apart (or matched).</summary>
    public IReadOnlyList<ColourSwatch> InUse { get; }

    public bool HasInUse => InUse.Count > 0;

    /// <summary>The colour picked so far.</summary>
    [ObservableProperty]
    public partial Colour Colour { get; private set; }

    public string ColourHex => Colour.ToString();

    [ObservableProperty]
    public partial int Red { get; set; }

    [ObservableProperty]
    public partial int Green { get; set; }

    [ObservableProperty]
    public partial int Blue { get; set; }

    /// <summary>The colour as typed, <c>#RRGGBB</c> (the <c>#</c> may be left out).</summary>
    [ObservableProperty]
    public partial string Hex { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>Takes a swatch's colour.</summary>
    [RelayCommand]
    private void Choose(ColourSwatch? swatch)
    {
        if (swatch is not null)
        {
            Show(swatch.Colour);
        }
    }

    /// <summary>Goes back to the colour the picker opened with.</summary>
    [RelayCommand]
    private void Reset() => Show(Original);

    /// <summary>Called by the dialog's OK button: true (and no error) if the typed code is a colour.</summary>
    public bool TryAccept()
    {
        if (!TryParseHex(Hex, out _))
        {
            Error = $"'{Hex.Trim()}' is not a colour: use #RRGGBB, e.g. #D32F2F.";
            return false;
        }

        Error = null;
        return true;
    }

    partial void OnColourChanged(Colour value) => OnPropertyChanged(nameof(ColourHex));

    partial void OnRedChanged(int value) => FromSliders();

    partial void OnGreenChanged(int value) => FromSliders();

    partial void OnBlueChanged(int value) => FromSliders();

    partial void OnHexChanged(string value)
    {
        if (_syncing)
        {
            return;
        }

        if (TryParseHex(value, out var colour))
        {
            Error = null;
            Show(colour, keepHex: true);
        }
    }

    private void FromSliders()
    {
        if (!_syncing)
        {
            Show(new Colour(Clamp(Red), Clamp(Green), Clamp(Blue), Colour.A));
        }
    }

    private void Show(Colour colour, bool keepHex = false)
    {
        _syncing = true;
        try
        {
            Colour = colour;
            Red = colour.R;
            Green = colour.G;
            Blue = colour.B;
            if (!keepHex)
            {
                Hex = colour.ToString();
                Error = null;
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);

    private static bool TryParseHex(string? text, out Colour colour)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        return Colour.TryParse(trimmed.StartsWith('#') ? trimmed : "#" + trimmed, out colour);
    }

    private static readonly (int Digit, string Name)[] ClassNames =
    [
        (1, "Class 1, express passenger"),
        (9, "Class 9"),
        (2, "Class 2, ordinary passenger"),
        (3, "Class 3"),
        (4, "Class 4, freight"),
        (6, "Class 6, freight"),
        (7, "Class 7, freight"),
        (8, "Class 8, freight"),
        (5, "Class 5, empty stock"),
        (0, "Class 0, light engine"),
    ];

    private static readonly (string Hex, string Name)[] PaletteColours =
    [
        ("#E53935", "Red"),
        ("#D81B60", "Pink"),
        ("#8E24AA", "Purple"),
        ("#5E35B1", "Deep purple"),
        ("#3949AB", "Indigo"),
        ("#1E88E5", "Blue"),
        ("#039BE5", "Light blue"),
        ("#00ACC1", "Cyan"),
        ("#00897B", "Teal"),
        ("#43A047", "Green"),
        ("#7CB342", "Light green"),
        ("#C0CA33", "Lime"),
        ("#FDD835", "Yellow"),
        ("#FFB300", "Amber"),
        ("#FB8C00", "Orange"),
        ("#F4511E", "Deep orange"),
        ("#6D4C41", "Brown"),
        ("#757575", "Grey"),
        ("#546E7A", "Blue grey"),
        ("#000000", "Black"),
        ("#B71C1C", "Dark red"),
        ("#880E4F", "Dark pink"),
        ("#4A148C", "Dark purple"),
        ("#311B92", "Dark deep purple"),
        ("#1A237E", "Dark indigo"),
        ("#0D47A1", "Dark blue"),
        ("#01579B", "Dark light blue"),
        ("#006064", "Dark cyan"),
        ("#004D40", "Dark teal"),
        ("#1B5E20", "Dark green"),
        ("#33691E", "Dark light green"),
        ("#827717", "Dark lime"),
        ("#F57F17", "Dark yellow"),
        ("#FF6F00", "Dark amber"),
        ("#E65100", "Dark orange"),
        ("#BF360C", "Dark deep orange"),
        ("#3E2723", "Dark brown"),
        ("#424242", "Dark grey"),
        ("#263238", "Dark blue grey"),
        ("#BDBDBD", "Light grey"),
    ];
}
