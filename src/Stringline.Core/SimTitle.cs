using System.Globalization;
using Stringline.Core.Timetables;

namespace Stringline.Core;

/// <summary>
/// "<i>sim</i> – <i>timetable</i>", the way every place that names the running sim shows it (#6): the window title,
/// the status bar, saved sessions, the PNG and PDF title band and the CSV export.
/// </summary>
/// <remarks>
/// The sim is the profile's name, then the gateway's or the WTT's area ID tidied (<c>waterloo</c> → <c>Waterloo</c>),
/// then <see cref="UnknownSim"/>. A name that is only the timetable's title (as profiles made before S22 have) counts
/// as no name, unless the caller says to keep it (a name typed in the profile editor, S31). The profile also drops a
/// name equal to any other title of the sim's timetables (<c>StringlineProfile.SimName</c>). The timetable is the WTT header's name with its version, e.g. <c>Timetable B 2012 (v1.0.0)</c>.
/// </remarks>
/// <param name="Sim">The sim's display name; never empty.</param>
/// <param name="Timetable">The timetable's name and version, or null when none is loaded.</param>
/// <param name="Review">Reviewing a saved session: adds " (review)".</param>
public sealed record SimTitle(string Sim, string? Timetable = null, bool Review = false)
{
    public const string UnknownSim = "Unknown sim";

    /// <summary>The en dash between the sim and the timetable.</summary>
    public const string Separator = " – ";

    /// <summary>The title from what is known about the sim and its timetable.</summary>
    /// <param name="simName">The profile's name for the sim, if any.</param>
    /// <param name="areaId">The sim's area ID, from the timetable or the gateway's clock.</param>
    /// <param name="timetableName">The WTT header's name.</param>
    /// <param name="timetableVersion">The WTT header's version, e.g. <c>1.0.2</c>.</param>
    /// <param name="keepName">Show <paramref name="simName"/> even when it is the timetable's title.</param>
    public static SimTitle From(string? simName, string? areaId, string? timetableName = null, string? timetableVersion = null, bool review = false, bool keepName = false)
    {
        var name = Clean(timetableName);
        var version = Clean(timetableVersion);
        var sim = Clean(simName) is { } given && (keepName || !SameTitle(given, name)) ? given
            : TidyAreaId(areaId) ?? UnknownSim;
        string? timetable = name is null ? null
            : version is null || name.Contains(version, StringComparison.OrdinalIgnoreCase) ? name
            : $"{name} (v{version})";
        return new SimTitle(sim, timetable, review);
    }

    /// <summary>
    /// The title for a sim running this timetable. Its header gives the name and version, and the area ID when
    /// <paramref name="areaId"/> (from the gateway) is not known.
    /// </summary>
    public static SimTitle From(string? simName, string? areaId, TimetableHeader? header, bool review = false, bool keepName = false) =>
        From(simName, Clean(areaId) ?? header?.AreaId, header?.Name, header?.TimetableVersion, review, keepName);

    /// <summary>
    /// Whether two titles are the same, ignoring case and white space (<c>Timetable B  2012</c> is
    /// <c>timetable b 2012</c>). Blank is never a title.
    /// </summary>
    public static bool SameTitle(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        var (i, j) = (0, 0);
        while (true)
        {
            while (i < a.Length && char.IsWhiteSpace(a[i]))
            {
                i++;
            }

            while (j < b.Length && char.IsWhiteSpace(b[j]))
            {
                j++;
            }

            if (i == a.Length || j == b.Length)
            {
                return i == a.Length && j == b.Length;
            }

            if (char.ToUpperInvariant(a[i++]) != char.ToUpperInvariant(b[j++]))
            {
                return false;
            }
        }
    }

    /// <summary>
    /// An area ID made readable: each word (split at <c>_</c>, <c>-</c> or a space) starts with a capital letter, e.g.
    /// <c>waterloo</c> → <c>Waterloo</c>. An ID with capitals already is left as it is. Null for a blank ID.
    /// </summary>
    public static string? TidyAreaId(string? areaId)
    {
        if (Clean(areaId) is not { } id)
        {
            return null;
        }

        if (id.Any(char.IsUpper))
        {
            return id;
        }

        var words = id.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]));
    }

    /// <summary>"Sim – Timetable (review)", leaving out what is not known.</summary>
    public override string ToString() =>
        Sim + (Timetable is null ? string.Empty : Separator + Timetable) + (Review ? " (review)" : string.Empty);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
