using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Stringline.Core;
using Stringline.Editing.Bulk;
using Stringline.Editing.Drafts;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Csv;

/// <summary>
/// A timetable as a spreadsheet (S47, <c>08-…md</c> §9): one row per timing point, in <see cref="Columns"/>. Export
/// writes every train; import reads a sheet back tolerantly (column names in any order and several spellings, quoted
/// fields, <c>,</c> <c>;</c> or tab, times in SimSig's notation, allowances with <c>H</c>) and merges it into a
/// document as one edit: a train by its UID, else by headcode and first time, else added.
/// </summary>
public static class TimetableCsv
{
    /// <summary>The columns, as export writes them. The train's own columns are read from its first row.</summary>
    public static IReadOnlyList<string> Columns { get; } =
        ["Headcode", "UID", "Category", "Entry", "Entry time", "Point", "Location", "Arr", "Dep", "Plat", "Line", "Path", "Eng", "Pth", "Prf", "Dwell", "Req %", "Set down", "Thru line", "Wait"];

    private static readonly char[] Separators = [',', ';', '\t'];

    /// <summary>Other names a column may have in a sheet (compared ignoring case, spaces and punctuation).</summary>
    private static readonly Dictionary<string, string> Aliases = Build(new()
    {
        ["Headcode"] = ["headcode", "train", "id", "reporting number", "trainid"],
        ["UID"] = ["uid", "wttuid"],
        ["Category"] = ["category", "cat", "traintype", "type"],
        ["Entry"] = ["entry", "entrypoint", "enters"],
        ["Entry time"] = ["entrytime", "departtime", "enterstime"],
        ["Point"] = ["point", "seq", "sequence", "#", "no", "index"],
        ["Location"] = ["location", "tiploc", "loc", "place"],
        ["Arr"] = ["arr", "arrive", "arrival", "arrtime"],
        ["Dep"] = ["dep", "depart", "departure", "deppass", "dep/pass", "pass", "deppasstime"],
        ["Plat"] = ["plat", "platform", "pl"],
        ["Line"] = ["line"],
        ["Path"] = ["path"],
        ["Eng"] = ["eng", "engineering", "engallowance"],
        ["Pth"] = ["pth", "pathing", "pathallowance"],
        ["Prf"] = ["prf", "perf", "performance", "perfallowance"],
        ["Dwell"] = ["dwell", "dwelltime"],
        ["Req %"] = ["req", "req%", "request", "requestpercent", "request%"],
        ["Set down"] = ["setdown", "setdownonly"],
        ["Thru line"] = ["thruline", "throughline", "allowstopsonthroughlines"],
        ["Wait"] = ["wait", "waitforbookedtime", "wait for booked time"],
    });

    // ---- Export ----

    /// <summary>The document's trains as CSV text (UTF-8, CRLF, a header row).</summary>
    public static string Export(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var text = new StringBuilder();
        Row(text, Columns);
        foreach (var train in document.Trains)
        {
            var point = 0;
            foreach (var trip in train.Trips)
            {
                point++;
                var first = point == 1;
                Row(text,
                [
                    train.Headcode ?? string.Empty, train.Uid ?? string.Empty,
                    first ? train.Category ?? string.Empty : string.Empty,
                    first ? train.EntryPoint ?? string.Empty : string.Empty,
                    first ? Time(train.DepartTime) : string.Empty,
                    point.ToString(CultureInfo.InvariantCulture), trip.Location ?? string.Empty,
                    Time(trip.ArrTime),
                    trip.DepPassTime is { Seconds: not 0 } dep ? SimTimeNotation.Format(dep, trip.IsPassTime ? TimeMark.Pass : TimeMark.None) : string.Empty,
                    trip.Platform ?? string.Empty, trip.Line ?? string.Empty, trip.Path ?? string.Empty,
                    Allowance(trip.EngAllowanceSeconds), Allowance(trip.PathAllowanceSeconds), Allowance(trip.PerfAllowanceSeconds),
                    Number(trip.DwellTime), Number(trip.RequestPercent),
                    trip.SetDownOnly ? "Y" : string.Empty, trip.AllowStopsOnThroughLines ? "Y" : string.Empty, trip.WaitForBookedTime ? "Y" : string.Empty,
                ]);
            }
        }

        return text.ToString();
    }

    // ---- Import ----

    /// <summary>One timing point read from a sheet, with its line number.</summary>
    public sealed record CsvPoint(
        int Line,
        string Location,
        NotatedTime? Arrive,
        NotatedTime? Depart,
        string? Platform,
        string? LineCode,
        string? Path,
        int Eng,
        int Pth,
        int Prf,
        int? Dwell,
        int? Request,
        bool SetDown,
        bool ThruLine,
        bool Wait);

    /// <summary>One train read from a sheet.</summary>
    public sealed record CsvTrain(int Line, string Headcode, string? Uid, string? Category, string? Entry, SimTime? EntryTime, IReadOnlyList<CsvPoint> Points)
    {
        public SimTime? FirstTime => Points.Select(p => p.Arrive?.Time ?? p.Depart?.Time).FirstOrDefault(t => t is not null) ?? EntryTime;
    }

    /// <summary>What a sheet held: its trains and the rows it could not read (with line numbers).</summary>
    public sealed record CsvSheet(IReadOnlyList<CsvTrain> Trains, IReadOnlyList<string> Problems);

    /// <summary>
    /// Reads a sheet. A new train starts where the headcode or UID changes, or the point number goes back to 1. A row
    /// that cannot be read is reported by its line number and skipped.
    /// </summary>
    public static CsvSheet Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = SplitLines(text.TrimStart('﻿'));
        var problems = new List<string>();
        if (lines.Count == 0)
        {
            return new CsvSheet([], ["The sheet is empty."]);
        }

        var separator = Separators.OrderByDescending(c => lines[0].Text.Count(ch => ch == c)).First();
        var header = Fields(lines[0].Text, separator);
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < header.Count; i++)
        {
            if (Aliases.TryGetValue(Key(header[i]), out var column))
            {
                index.TryAdd(column, i);
            }
        }

        if (!index.ContainsKey("Headcode") || !index.ContainsKey("Location"))
        {
            return new CsvSheet([], [$"Line {lines[0].Number}: the header needs at least a Headcode and a Location column (it has {string.Join(", ", header)})."]);
        }

        var trains = new List<CsvTrain>();
        List<CsvPoint>? points = null;
        (int Line, string Headcode, string? Uid, string? Category, string? Entry, SimTime? EntryTime)? current = null;
        SimTime? previous = null;

        void Close()
        {
            if (current is { } c && points is { Count: > 0 })
            {
                trains.Add(new CsvTrain(c.Line, c.Headcode, c.Uid, c.Category, c.Entry, c.EntryTime, points));
            }

            points = null;
            current = null;
            previous = null;
        }

        foreach (var (number, line) in lines.Skip(1))
        {
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var f = Fields(line, separator);
            string Get(string column) => index.TryGetValue(column, out var i) && i < f.Count ? f[i].Trim() : string.Empty;
            string? Opt(string column) => Get(column) is { Length: > 0 } v ? v : null;
            try
            {
                var headcode = Get("Headcode");
                if (!Headcode.TryCreate(headcode, out _))
                {
                    throw new FormatException($"'{headcode}' is not a headcode");
                }

                var uid = Opt("UID");
                var pointText = Get("Point");
                var restart = pointText == "1" || current is null || !string.Equals(current.Value.Headcode, headcode, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(current.Value.Uid, uid, StringComparison.OrdinalIgnoreCase);
                if (restart)
                {
                    Close();
                    SimTime? entryTime = Opt("Entry time") is { } et ? ParseTime(et, null, "entry time").Time : null;
                    current = (number, headcode.ToUpperInvariant(), uid, Opt("Category"), Opt("Entry")?.ToUpperInvariant(), entryTime);
                    points = [];
                    previous = entryTime;
                }

                var location = Get("Location").ToUpperInvariant();
                if (!Tiploc.TryCreate(location, out _))
                {
                    throw new FormatException($"'{location}' is not a TIPLOC");
                }

                var arrive = Opt("Arr") is { } a ? ParseTime(a, previous, "arrival") : (NotatedTime?)null;
                previous = arrive?.Time ?? previous;
                var depart = Opt("Dep") is { } d ? ParseTime(d, previous, "departure") : (NotatedTime?)null;
                previous = depart?.Time ?? previous;
                points!.Add(new CsvPoint(
                    number, location, arrive, depart, Opt("Plat"), Opt("Line"), Opt("Path"),
                    Minutes(Get("Eng"), "engineering allowance"), Minutes(Get("Pth"), "pathing allowance"), Minutes(Get("Prf"), "performance allowance"),
                    Whole(Get("Dwell"), "dwell"), Whole(Get("Req %"), "request"),
                    Yes(Get("Set down")) || depart?.Mark == TimeMark.SetDown,
                    Yes(Get("Thru line")) || depart?.Mark == TimeMark.ThruLine,
                    Yes(Get("Wait")) || depart?.Mark == TimeMark.DwellWait));
            }
            catch (FormatException ex)
            {
                problems.Add($"Line {number}: {ex.Message}; skipped.");
            }
        }

        Close();
        return new CsvSheet(trains, problems);
    }

    /// <summary>
    /// The sheet merged into the document, as one edit: each train found by its UID, else by headcode and first time
    /// (or the only train with the headcode, if the sheet gives no time), else added. A train whose locations are
    /// the same has its points' fields set; one whose locations differ has its points replaced, keeping the
    /// activities of points at the same locations.
    /// </summary>
    public static BulkResult Merge(WttDocument document, CsvSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sheet);
        var import = document.Import();
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = sheet.Problems.ToList();
        var used = new HashSet<XElement>();
        var (container, make) = DraftDocuments.TrainsContainer(document);
        if (make is not null)
        {
            edits.Add(make);
        }

        foreach (var row in sheet.Trains)
        {
            var node = Find(document, import, row, used);
            if (node is null)
            {
                var element = DraftDocuments.NewTrain(row.Headcode, row.Category);
                element.Element("Trips")!.RemoveNodes();
                _ = new TrainNode(document, element) { Uid = row.Uid, EntryPoint = row.Entry, DepartTime = row.EntryTime };
                foreach (var point in row.Points)
                {
                    var trip = new TripNode(document, new XElement("Trip"));
                    Fill(trip, point);
                    element.Element("Trips")!.Add(trip.Element);
                }

                edits.Add(TimetableEdit.Insert(container, element, description: $"Add {row.Headcode}"));
                changes.Add($"{row.Headcode}: new train, {row.Points.Count} points (line {row.Line})");
                continue;
            }

            used.Add(node.Element);
            var before = edits.Count;
            edits.Add(TimetableEdit.Change(node, t =>
            {
                t.Category = row.Category ?? t.Category;
                t.EntryPoint = row.Entry ?? t.EntryPoint;
                t.DepartTime = row.EntryTime ?? t.DepartTime;
            }));
            var trips = node.Trips.ToList();
            if (trips.Select(t => t.Location?.ToUpperInvariant()).SequenceEqual(row.Points.Select(p => p.Location)))
            {
                var changed = 0;
                for (var i = 0; i < trips.Count; i++)
                {
                    var edit = TimetableEdit.Change(trips[i], t => Fill(t, row.Points[i]));
                    if (edit is CompositeEdit { Edits.Count: 0 })
                    {
                        continue;
                    }

                    edits.Add(edit);
                    changed++;
                }

                if (changed > 0)
                {
                    changes.Add($"{node.Headcode}: {changed} point{(changed == 1 ? "" : "s")} changed");
                }
            }
            else
            {
                var replacement = new XElement("Trips");
                var spare = trips.ToList();
                foreach (var point in row.Points)
                {
                    var old = spare.FirstOrDefault(t => string.Equals(t.Location, point.Location, StringComparison.OrdinalIgnoreCase));
                    var trip = new TripNode(document, old is null ? new XElement("Trip") : new XElement(old.Element));
                    if (old is not null)
                    {
                        spare.Remove(old);
                    }

                    Fill(trip, point);
                    replacement.Add(trip.Element);
                }

                if (node.TripsElement is { } existing)
                {
                    edits.Add(TimetableEdit.Remove(existing));
                }

                edits.Add(TimetableEdit.Insert(node.Element, replacement));
                changes.Add($"{node.Headcode}: route {string.Join(" ", trips.Select(t => t.Location))} → {string.Join(" ", row.Points.Select(p => p.Location))}");
                if (spare.Any(t => t.Activities.Any()))
                {
                    problems.Add($"{node.Headcode}: the activities at {string.Join(", ", spare.Where(t => t.Activities.Any()).Select(t => t.Location))} went with the points the sheet leaves out.");
                }
            }

            if (edits.Count == before + 1 && edits[^1] is CompositeEdit { Edits.Count: 0 })
            {
                edits.RemoveAt(edits.Count - 1);
            }
        }

        edits.RemoveAll(e => e is CompositeEdit { Edits.Count: 0 });
        return new BulkResult(
            edits.Count == 0 || (edits.Count == 1 && make is not null) ? null : TimetableEdit.Composite($"Import {sheet.Trains.Count} train{(sheet.Trains.Count == 1 ? "" : "s")} from a sheet", edits),
            changes,
            problems);
    }

    private static TrainNode? Find(WttDocument document, WttDocumentImport import, CsvTrain row, HashSet<XElement> used)
    {
        var trains = document.Trains.Where(t => !used.Contains(t.Element)).ToList();
        if (row.Uid is { } uid && trains.FirstOrDefault(t => string.Equals(t.Uid, uid, StringComparison.OrdinalIgnoreCase)) is { } byUid)
        {
            return byUid;
        }

        var same = trains.Where(t => string.Equals(t.Headcode, row.Headcode, StringComparison.OrdinalIgnoreCase)).ToList();
        if (row.FirstTime is not { } first)
        {
            return same.Count == 1 ? same[0] : null;
        }

        return same.FirstOrDefault(t => import.TrainOf(t) is { } train && (train.EntryTime == first || train.FirstTime == first || (train.Points.Count > 0 ? train.Points[0].FirstTime : null) == first));
    }

    /// <summary>Sets a trip's fields from a sheet's point, in the grid's way.</summary>
    private static void Fill(TripNode t, CsvPoint p)
    {
        t.Location = p.Location;
        t.ArrTime = p.Arrive?.Time;
        t.DepPassTime = p.Depart?.Time;
        t.IsPassTime = p.Depart?.Mark == TimeMark.Pass;
        t.Platform = p.Platform;
        t.Line = p.LineCode;
        t.Path = p.Path;
        t.EngAllowanceSeconds = p.Eng;
        t.PathAllowanceSeconds = p.Pth;
        t.PerfAllowanceSeconds = p.Prf;
        t.DwellTime = p.Dwell;
        t.RequestPercent = p.Request ?? (p.Depart?.Mark == TimeMark.Request ? 50 : null);
        t.SetDownOnly = p.SetDown;
        t.AllowStopsOnThroughLines = p.ThruLine;
        t.WaitForBookedTime = p.Wait;
    }

    // ---- Text ----

    private static NotatedTime ParseTime(string text, SimTime? previous, string what) =>
        SimTimeNotation.TryParse(text, previous, out var time) ? time : throw new FormatException($"'{text}' is not a time ({what})");

    private static int Minutes(string text, string what)
    {
        var t = text.Trim().ToUpperInvariant();
        if (t.Length == 0)
        {
            return 0;
        }

        var half = t.EndsWith('H');
        var whole = half ? t[..^1] : t;
        if (whole.Length == 0 && half)
        {
            return 30;
        }

        return int.TryParse(whole, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m < 1000
            ? (m * 60) + (half ? 30 : 0)
            : decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d) && d * 2 == Math.Floor(d * 2)
                ? (int)(d * 60)
                : throw new FormatException($"'{text}' is not an {what} (minutes, H for a half)");
    }

    private static int? Whole(string text, string what) =>
        text.Trim().Length == 0 ? null
        : int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n
        : throw new FormatException($"'{text}' is not a whole number ({what})");

    private static bool Yes(string text) => text.Trim().ToUpperInvariant() is "Y" or "YES" or "TRUE" or "1" or "X" or "-1";

    private static string Time(SimTime? time) => time is { Seconds: not 0 } t ? SimTimeNotation.Format(t) : string.Empty;

    private static string Allowance(int seconds) =>
        seconds <= 0 ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}{(seconds % 60 >= 30 ? "H" : string.Empty)}");

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static void Row(StringBuilder text, IEnumerable<string> fields)
    {
        text.Append(string.Join(",", fields.Select(Quote)));
        text.Append("\r\n");
    }

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n', ';', '\t']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;

    private static List<(int Number, string Text)> SplitLines(string text)
    {
        // Lines, keeping a line break inside quotes as part of its field.
        var lines = new List<(int, string)>();
        var current = new StringBuilder();
        var quoted = false;
        var number = 1;
        var start = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quoted = !quoted;
            }

            if (!quoted && (c == '\n' || c == '\r'))
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                lines.Add((start, current.ToString()));
                current.Clear();
                number++;
                start = number;
                continue;
            }

            if (c == '\n')
            {
                number++;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            lines.Add((start, current.ToString()));
        }

        return lines;
    }

    private static List<string> Fields(string line, char separator)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == separator)
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
        return fields;
    }

    private static string Key(string name) => new([.. name.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is '%' or '#' or '/')]);

    private static Dictionary<string, string> Build(Dictionary<string, string[]> names)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (column, aliases) in names)
        {
            map[Key(column)] = column;
            foreach (var alias in aliases)
            {
                map[Key(alias)] = column;
            }
        }

        return map;
    }
}
