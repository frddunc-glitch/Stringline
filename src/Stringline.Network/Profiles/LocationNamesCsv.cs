using System.Text;
using Stringline.Core;

namespace Stringline.Network.Profiles;

/// <summary>What a location names CSV gave.</summary>
/// <param name="Names">Every name read, by TIPLOC (a later row for the same TIPLOC wins).</param>
/// <param name="Unknown">TIPLOCs named in the file but not in the timetable, in file order. Their names are still imported.</param>
/// <param name="Problems">Rows that could not be read, with their line numbers.</param>
public sealed record LocationNamesImport(IReadOnlyDictionary<Tiploc, string> Names, IReadOnlyList<Tiploc> Unknown, IReadOnlyList<string> Problems);

/// <summary>
/// Location display names from a CSV (<c>04-…md</c> §1): <c>TIPLOC,name</c> per row. A header row is skipped, fields
/// may be quoted (<c>"Clapham Junction, Platform 1"</c>, with <c>""</c> for a quote), further columns are ignored, and
/// blank rows and rows starting with <c>#</c> are skipped. A row that cannot be read is reported and skipped.
/// </summary>
public static class LocationNamesCsv
{
    private static readonly string[] HeaderFirst = ["tiploc", "location", "code", "loc"];
    private static readonly string[] HeaderSecond = ["name", "display name", "displayname", "description"];

    /// <param name="known">The timetable's locations; a TIPLOC not among them is listed in <see cref="LocationNamesImport.Unknown"/>. Null checks none.</param>
    public static LocationNamesImport Parse(string text, IReadOnlySet<Tiploc>? known = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var names = new Dictionary<Tiploc, string>();
        var unknown = new List<Tiploc>();
        var problems = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var first = true;
        for (var n = 0; n < lines.Length; n++)
        {
            var line = lines[n].TrimStart('﻿');
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var lineNumber = n + 1;
            if (!TrySplit(line, out var fields))
            {
                problems.Add($"Line {lineNumber}: a quote is not closed.");
                first = false;
                continue;
            }

            if (first)
            {
                first = false;
                if (IsHeader(fields))
                {
                    continue;
                }
            }

            if (fields.Count < 2)
            {
                problems.Add($"Line {lineNumber}: expected TIPLOC,name.");
                continue;
            }

            if (!Tiploc.TryCreate(fields[0], out var tiploc))
            {
                problems.Add($"Line {lineNumber}: '{fields[0].Trim()}' is not a TIPLOC.");
                continue;
            }

            var name = fields[1].Trim();
            if (name.Length == 0)
            {
                problems.Add($"Line {lineNumber}: {tiploc} has no name.");
                continue;
            }

            if (known is not null && !known.Contains(tiploc) && !names.ContainsKey(tiploc))
            {
                unknown.Add(tiploc);
            }

            names[tiploc] = name;
        }

        return new LocationNamesImport(names, unknown, problems);
    }

    private static bool IsHeader(List<string> fields) =>
        HeaderFirst.Contains(fields[0].Trim(), StringComparer.OrdinalIgnoreCase)
        || (fields.Count > 1 && HeaderSecond.Contains(fields[1].Trim(), StringComparer.OrdinalIgnoreCase));

    /// <summary>Splits one row on commas outside quotes. False if a quote is left open.</summary>
    private static bool TrySplit(string line, out List<string> fields)
    {
        fields = [];
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }
            }
            else if (c == '"' && field.ToString().Trim().Length == 0)
            {
                field.Clear();
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        fields.Add(field.ToString());
        return !quoted;
    }
}
