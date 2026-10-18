using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Timetable;

namespace Stringline.Tools;

/// <summary><c>stringline wtt-dump</c>: import a <c>.wtt</c> and print a summary, a table of trains and the import warnings.</summary>
internal static class WttDumpCommand
{
    /// <summary>Warnings shown without <c>--warnings</c>.</summary>
    private const int WarningPreview = 20;

    public static Command Create()
    {
        var file = new Argument<FileInfo>("timetable") { Description = "SimSig timetable (.wtt, or its SavedTimetable.xml)." };
        var train = new Option<string[]>("--train")
        {
            Description = "Also print every timing point of this train (headcode or ID). Repeatable.",
            AllowMultipleArgumentsPerToken = true,
        };
        var summaryOnly = new Option<bool>("--summary") { Description = "Print the summary only, without the table of trains." };
        var warnings = new Option<bool>("--warnings") { Description = $"List every import warning, including Info (by default only the first {WarningPreview} warnings and errors)." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging." };

        var command = new Command("wtt-dump", "Import a SimSig timetable and print a summary, a table of trains and any import warnings.")
        {
            file, train, summaryOnly, warnings, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(file)!, parse.GetValue(train) ?? [], parse.GetValue(summaryOnly), parse.GetValue(warnings), parse.GetValue(verbose)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo File, IReadOnlyList<string> Trains, bool SummaryOnly, bool AllWarnings, bool Verbose);

    /// <returns>0 if read, 1 if the file is damaged (an Error warning), 2 if it cannot be read at all.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        WttImportResult result;
        using (var loggerFactory = ConsoleLogging.Create(settings.Verbose))
        {
            try
            {
                result = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(settings.File.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
            {
                await error.WriteLineAsync($"Cannot read {settings.File.FullName}: {ex.Message}").ConfigureAwait(false);
                return 2;
            }
        }

        var text = new StringWriter(CultureInfo.InvariantCulture);
        WriteSummary(text, result, settings.File.Name);
        WriteWarnings(text, result, settings.AllWarnings);
        if (!settings.SummaryOnly)
        {
            text.WriteLine();
            WriteTrainTable(text, result.Timetable);
        }

        foreach (var name in settings.Trains)
        {
            text.WriteLine();
            var trains = FindTrains(result.Timetable, name);
            if (trains.Count == 0)
            {
                text.WriteLine($"No train {name} in the timetable.");
            }

            foreach (var t in trains)
            {
                WriteTrain(text, result.Timetable, t);
            }
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return result.Warnings.Any(w => w.Severity == ImportSeverity.Error) ? 1 : 0;
    }

    /// <summary>The train with this ID and every train with this headcode, in file order.</summary>
    internal static List<Train> FindTrains(WorkingTimetable timetable, string name)
    {
        var byId = TrainId.TryCreate(name, out var id) ? timetable.Find(id) : null;
        var byHeadcode = Headcode.TryCreate(name, out var headcode) ? timetable.WithHeadcode(headcode) : [];
        return timetable.Trains.Where(t => t == byId || byHeadcode.Contains(t)).ToList();
    }

    internal static void WriteSummary(TextWriter w, WttImportResult result, string fileName)
    {
        var tt = result.Timetable;
        var h = tt.Header;
        w.WriteLine($"{fileName}: sim {Or(h.AreaId)}, \"{h.Name}\" (saved by SimSig {Or(h.SimSigVersion)}{(h.TimetableVersion is null ? "" : ", timetable version " + h.TimetableVersion)})");
        var starts = tt.Trains.GroupBy(t => t.Start).ToDictionary(g => g.Key, g => g.Count());
        w.WriteLine($"Runs {h.StartTime} to {h.FinishTime}. {tt.Trains.Count} trains: {starts.GetValueOrDefault(TrainStart.EntryPoint)} enter, {starts.GetValueOrDefault(TrainStart.Seeded)} seeded, {starts.GetValueOrDefault(TrainStart.AtFirstLocation)} start in the sim{(result.SkippedTrains > 0 ? $"; {result.SkippedTrains} skipped" : "")}.");
        var points = tt.Trains.Sum(t => t.Points.Count);
        var locations = tt.Trains.SelectMany(t => t.Points).Select(p => p.Location).Distinct().Count();
        w.WriteLine($"{points} timing points at {locations} locations; {tt.Categories.Count} categories; {tt.Rules.Count} rules.");
        var uids = tt.Trains.Count(t => t.Uid is not null);
        var headcodes = tt.Trains.Select(t => t.Headcode).Distinct().Count();
        w.WriteLine($"{headcodes} distinct headcodes; {uids} trains have a WTT UID.");

        var activities = tt.Trains.SelectMany(t => t.Points).SelectMany(p => p.Activities).ToList();
        if (activities.Count > 0)
        {
            var kinds = activities.GroupBy(a => a.Kind).OrderBy(g => g.Key == ActivityKind.Unknown ? int.MaxValue : (int)g.Key).Select(g => $"{g.Key} {g.Count()}");
            var lost = activities.Count(a => a.Train is null && a.AssociatedText is not null);
            w.WriteLine($"Activities: {string.Join(", ", kinds)}{(lost > 0 ? $" ({lost} name a train that is not in the timetable)" : "")}.");
        }

        if (tt.Rules.Count > 0)
        {
            var kinds = tt.Rules.GroupBy(r => r.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}");
            w.WriteLine($"Rules: {string.Join(", ", kinds)}.");
        }

        if (result.UnknownElements.Count > 0)
        {
            w.WriteLine($"Unknown elements (ignored): {string.Join(", ", result.UnknownElements.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{e.Key} ×{e.Value}"))}.");
        }
    }

    private static void WriteWarnings(TextWriter w, WttImportResult result, bool all)
    {
        var counts = result.Warnings.GroupBy(x => x.Severity).ToDictionary(g => g.Key, g => g.Count());
        w.WriteLine($"Import: {counts.GetValueOrDefault(ImportSeverity.Error)} errors, {counts.GetValueOrDefault(ImportSeverity.Warning)} warnings, {counts.GetValueOrDefault(ImportSeverity.Info)} notes.");
        var shown = all ? result.Warnings : result.WarningsAtLeast(ImportSeverity.Warning).ToList();
        foreach (var warning in all ? shown : shown.Take(WarningPreview))
        {
            w.WriteLine("  " + warning);
        }

        if (!all && (shown.Count > WarningPreview || counts.GetValueOrDefault(ImportSeverity.Info) > 0))
        {
            w.WriteLine("  (--warnings lists them all)");
        }
    }

    internal static void WriteTrainTable(TextWriter w, WorkingTimetable tt)
    {
        string[] header = ["ID", "Headcode", "Start", "First", "Last", "From", "To", "Pts", "Next", "Category"];
        var rows = tt.Trains.Select(t => new[]
        {
            t.Id.Value,
            t.Headcode.Value,
            t.Start switch
            {
                TrainStart.EntryPoint => $"enter {t.EntryPoint}",
                TrainStart.Seeded => $"seed {t.SeedPoint}",
                _ => t.FormedFrom is { } from ? $"from {from}" : "in sim",
            },
            t.FirstTime?.ToString() ?? "-",
            t.LastTime?.ToString() ?? "-",
            t.Origin.Code,
            t.Destination.Code,
            t.Points.Count.ToString(CultureInfo.InvariantCulture),
            t.NextWorking?.Value ?? "",
            tt.FindCategory(t.CategoryId)?.Description ?? t.CategoryId ?? "",
        }).ToList();
        WriteTable(w, header, rows);
    }

    internal static void WriteTrain(TextWriter w, WorkingTimetable tt, Train t)
    {
        w.WriteLine($"{t.Headcode} ({t.Id}) {t.Description}");
        var details = new List<string>();
        if (t.Uid is not null)
        {
            details.Add("UID " + t.Uid);
        }

        details.Add(t.Start switch
        {
            TrainStart.EntryPoint => $"enters at {t.EntryPoint} {t.EntryTime?.ToString() ?? "(no time)"}",
            TrainStart.Seeded => $"seeded at {t.SeedPoint}",
            _ => t.FormedFrom is { } from ? $"formed from {from}" : "starts in the sim",
        });
        if (tt.FindCategory(t.CategoryId) is { } category)
        {
            details.Add(category.Description);
        }

        if (t.MaxSpeedMph is { } speed)
        {
            details.Add($"{speed} mph");
        }

        if (t.LengthMetres is { } length)
        {
            details.Add($"{length} m");
        }

        if (t.IsFreight)
        {
            details.Add("freight");
        }

        if (t.AsRequired)
        {
            details.Add("as required");
        }

        w.WriteLine("  " + string.Join(", ", details));
        string[] header = ["#", "Location", "Arr", "Dep", "Plat", "Line", "Path", "Allow", "Activities"];
        var rows = t.Points.Select(p => new[]
        {
            (p.Index + 1).ToString(CultureInfo.InvariantCulture),
            p.Location.Code,
            p.IsPass ? "" : p.Arrive?.ToString() ?? "",
            p.Depart is { } d ? (p.IsPass ? d + " pass" : d.ToString()) : "",
            p.Platform ?? "",
            p.Line ?? "",
            p.Path ?? "",
            p.AllowanceSeconds > 0 ? FormatAllowance(p) : "",
            string.Join("; ", p.Activities.Select(a => $"{a.Kind} {a.Train?.Value ?? a.AssociatedText + "?"}")),
        }).ToList();
        WriteTable(w, header, rows);
    }

    /// <summary>Allowances in minutes with ½, as a WTT shows them, e.g. <c>E1½ P2</c>.</summary>
    private static string FormatAllowance(TimingPoint p)
    {
        var parts = new List<string>(3);
        Add("E", p.EngineeringAllowanceSeconds);
        Add("P", p.PathingAllowanceSeconds);
        Add("R", p.PerformanceAllowanceSeconds);
        return string.Join(' ', parts);

        void Add(string prefix, int seconds)
        {
            if (seconds > 0)
            {
                var whole = seconds / 60;
                var half = seconds % 60 >= 30 ? "½" : "";
                parts.Add(prefix + (whole > 0 || half.Length == 0 ? whole.ToString(CultureInfo.InvariantCulture) : "") + half);
            }
        }
    }

    internal static void WriteTable(TextWriter w, string[] header, List<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        WriteRow(header);
        foreach (var row in rows)
        {
            WriteRow(row);
        }

        void WriteRow(string[] cells) =>
            w.WriteLine(string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd());
    }

    private static string Or(string text) => text.Length == 0 ? "?" : text;
}
