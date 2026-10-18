using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Stringline.Core.Timetables;

namespace Stringline.Network.Profiles;

[JsonConverter(typeof(JsonStringEnumConverter<LineStyle>))]
public enum LineStyle
{
    Solid,
    Dashed,
    Dotted,
}

/// <summary>An sRGB colour with alpha, written as <c>#RRGGBB</c> or <c>#RRGGBBAA</c>.</summary>
[JsonConverter(typeof(ColourJsonConverter))]
public readonly record struct Colour(byte R, byte G, byte B, byte A = 255)
{
    public static Colour Parse(string text) =>
        TryParse(text, out var colour) ? colour : throw new FormatException($"'{text}' is not a colour (#RRGGBB or #RRGGBBAA).");

    public static bool TryParse([NotNullWhen(true)] string? text, out Colour colour)
    {
        colour = default;
        var hex = text?.Trim();
        if (hex is not { Length: 7 or 9 } || hex[0] != '#'
            || !uint.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return false;
        }

        if (hex.Length == 7)
        {
            value = (value << 8) | 0xFF;
        }

        colour = new Colour((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";
}

public sealed class ColourJsonConverter : JsonConverter<Colour>
{
    public override Colour Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Colour.TryParse(reader.GetString(), out var colour)
            ? colour
            : throw new JsonException("Expected a colour string such as \"#D32F2F\".");

    public override void Write(Utf8JsonWriter writer, Colour value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}

/// <summary>How trains of one WTT category are drawn and how much they matter when regulating. Not in the WTT; kept in the profile.</summary>
/// <param name="CategoryId">The <see cref="TrainCategory.Id"/>.</param>
/// <param name="Name">Display name (the category's description by default).</param>
/// <param name="Priority">Higher is more important (0–100); weights delay in suggestions.</param>
public sealed record CategoryStyle(string CategoryId, string Name, int Priority, Colour Colour, LineStyle Style = LineStyle.Solid)
{
    /// <summary>Style for trains with no category, or one the profile does not list.</summary>
    public static CategoryStyle Fallback { get; } = new("", "Other", 50, new Colour(0x60, 0x7D, 0x8B));

    /// <summary>
    /// A style for every category in the timetable, from the headcode class most of its trains use: express
    /// passenger (1) red and highest, ordinary passenger (2) blue, freight (4, 6–8) greens and browns, empty stock
    /// (5) grey, light engines (0) near-black and lowest. A category no train uses goes by its freight flag.
    /// </summary>
    public static IReadOnlyList<CategoryStyle> CreateDefaults(WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        var classes = timetable.Trains
            .Where(t => t.CategoryId is not null && t.Headcode.ClassDigit is not null)
            .GroupBy(t => t.CategoryId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.GroupBy(t => t.Headcode.ClassDigit!.Value).OrderByDescending(c => c.Count()).ThenBy(c => c.Key).First().Key,
                StringComparer.Ordinal);
        return timetable.Categories
            .DistinctBy(c => c.Id, StringComparer.Ordinal)
            .Select(c =>
            {
                var digit = classes.TryGetValue(c.Id, out var d) ? d : c.IsFreight ? 4 : 2;
                var (priority, colour) = ForClass(digit);
                return new CategoryStyle(c.Id, string.IsNullOrWhiteSpace(c.Description) ? c.Id : c.Description, priority, colour);
            })
            .ToList();
    }

    /// <summary>Default priority and colour for a headcode class digit.</summary>
    public static (int Priority, Colour Colour) ForClass(int digit) => digit switch
    {
        1 => (90, new Colour(0xD3, 0x2F, 0x2F)),
        9 => (85, new Colour(0x7B, 0x1F, 0xA2)),
        2 => (70, new Colour(0x19, 0x76, 0xD2)),
        3 => (60, new Colour(0xF5, 0x7C, 0x00)),
        4 => (50, new Colour(0x38, 0x8E, 0x3C)),
        6 => (40, new Colour(0x79, 0x55, 0x48)),
        7 => (35, new Colour(0x82, 0x77, 0x17)),
        8 => (30, new Colour(0x00, 0x79, 0x6B)),
        5 => (20, new Colour(0x75, 0x75, 0x75)),
        _ => (10, new Colour(0x21, 0x21, 0x21)),
    };
}
