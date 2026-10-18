using System.Globalization;
using System.Text.RegularExpressions;
using Stringline.Core;

namespace Stringline.Timetable;

/// <summary>
/// Newer WTTs store <c>$template</c> as a train's description and put a template such as
/// <c>$originTime $originName-$destName $operator ($stock)</c> in the header; SimSig fills it in from the train.
/// The fields are not documented; these are the ones seen in Fred's 284 timetables. Unknown <c>$fields</c> are kept as written.
/// </summary>
internal static partial class DescriptionTemplate
{
    public const string UseHeaderTemplate = "$template";

    public sealed record Fields(
        string Headcode,
        SimTime? OriginTime,
        string? OriginName,
        string? DestinationName,
        SimTime? DestinationTime,
        string? OperatorCode,
        string? Stock);

    /// <summary>The train's description with template fields filled in.</summary>
    /// <param name="description">The train's <c>Description</c> element.</param>
    /// <param name="headerTemplate">The header's <c>TrainDescriptionTemplate</c>, used when the description is <c>$template</c>.</param>
    public static string Expand(string description, string? headerTemplate, Fields fields)
    {
        var text = description.Trim();
        if (string.Equals(text, UseHeaderTemplate, StringComparison.OrdinalIgnoreCase))
        {
            text = headerTemplate ?? string.Empty;
        }

        if (!text.Contains('$', StringComparison.Ordinal))
        {
            return text;
        }

        var expanded = FieldPattern().Replace(text, m => Lookup(m.Groups[1].Value, fields) ?? m.Value);

        // Empty fields leave stray spaces and empty brackets, e.g. "$operator ($stock)" with no operator.
        expanded = expanded.Replace("()", string.Empty, StringComparison.Ordinal);
        return SpacePattern().Replace(expanded, " ").Trim();
    }

    private static string? Lookup(string field, Fields f) => field.ToUpperInvariant() switch
    {
        "ORIGINTIME" => Clock(f.OriginTime),
        "ORIGINNAME" => f.OriginName ?? string.Empty,
        "DESTNAME" or "DESTINATIONNAME" => f.DestinationName ?? string.Empty,
        "DESTTIME" or "DESTINATIONTIME" => Clock(f.DestinationTime),
        "OPERATOR" => f.OperatorCode ?? string.Empty,
        "STOCK" => f.Stock ?? string.Empty,
        "HEADCODE" or "ID" => f.Headcode,
        _ => null,
    };

    /// <summary><c>HH:mm</c> on the 24-hour clock without a day marker, as a passenger timetable would show it.</summary>
    private static string Clock(SimTime? time) => time is { } t
        ? string.Create(CultureInfo.InvariantCulture, $"{t.Hour:00}:{t.Minute:00}")
        : string.Empty;

    [GeneratedRegex(@"\$([A-Za-z]+)", RegexOptions.CultureInvariant)]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex SpacePattern();
}
