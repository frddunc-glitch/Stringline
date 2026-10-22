using System.Globalization;
using System.Text.RegularExpressions;

namespace Stringline.Editing.Drafts;

/// <summary>
/// SimSig's naming convention for timetable files (wiki: <i>Timetable filenames</i>; <c>08-…md</c> §6):
/// <c>Sim name YYYY-MM-DD [tags].wtt</c>, with the version never in the name.
/// </summary>
public static partial class TimetableNames
{
    /// <summary>The tag Stringline adds to a timetable it saves as a new file.</summary>
    public const string Tag = "Stringline";

    /// <summary>
    /// A new file's name for a draft of <paramref name="originalFileName"/>: <c>Waterloo 2010-05-01 Stringline.wtt</c>.
    /// The date is the one in the original's name (Fred, S43): a full date, else its year, else today's.
    /// </summary>
    public static string Suggest(string simName, string? originalFileName, DateOnly today)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(simName);
        var stem = originalFileName is null ? string.Empty : Path.GetFileNameWithoutExtension(originalFileName);
        var date = FullDate().Match(stem) is { Success: true } full ? full.Value
            : Year().Match(stem) is { Success: true } year ? year.Value
            : today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return Clean($"{simName.Trim()} {date} {Tag}") + ".wtt";
    }

    /// <summary>The sim's name for file names: its name if known, else its area ID with a capital (<c>waterloo</c> → <c>Waterloo</c>).</summary>
    public static string SimName(string? name, string? areaId) =>
        !string.IsNullOrWhiteSpace(name) ? name.Trim()
        : string.IsNullOrWhiteSpace(areaId) ? "Timetable"
        : char.ToUpperInvariant(areaId.Trim()[0]) + areaId.Trim()[1..];

    /// <summary>The file name in the folder, with " (2)", " (3)"… added if one of that name is there.</summary>
    public static string Unique(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            path = Path.Combine(folder, $"{stem} ({n.ToString(CultureInfo.InvariantCulture)}){extension}");
        }

        return path;
    }

    private static string Clean(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '-');
        }

        return name;
    }

    [GeneratedRegex(@"(?<!\d)(19|20)\d\d-[01]\d-[0-3]\d(?!\d)")]
    private static partial Regex FullDate();

    [GeneratedRegex(@"(?<!\d)(19|20)\d\d(?!\d)")]
    private static partial Regex Year();
}
