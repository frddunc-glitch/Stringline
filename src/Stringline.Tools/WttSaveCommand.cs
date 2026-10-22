using System.CommandLine;
using System.Globalization;
using Stringline.Core;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline wtt-save</c>: open a <c>.wtt</c> as an editable document and save it again, optionally after shifting
/// trains' times. A round-trip and smoke tool for the lossless writer (S40), not the editor.
/// </summary>
internal static class WttSaveCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("timetable") { Description = "SimSig timetable to open (.wtt, or its SavedTimetable.xml)." };
        var output = new Argument<FileInfo>("out") { Description = "Where to save it (.wtt). A file already there is backed up first." };
        var bump = new Option<VersionBump>("--bump")
        {
            Description = "How the version goes up: build (default, as every save does), minor or major (a release), or none (a pure round trip).",
            DefaultValueFactory = _ => VersionBump.Build,
        };
        var shift = new Option<string[]>("--shift-train")
        {
            Description = "Shift a train's times by whole or half minutes, e.g. --shift-train 1A23 +5 (headcode, UID or ID; repeatable; +2H is 2½ min).",
            Arity = new ArgumentArity(2, 2),
            AllowMultipleArgumentsPerToken = true,
        };
        var backups = new Option<DirectoryInfo?>("--backup-folder") { Description = @"Where a replaced file is backed up (default %LOCALAPPDATA%\Stringline\timetable-backups)." };

        var command = new Command("wtt-save", "Open a timetable losslessly and save it again (a new version), optionally shifting trains' times.")
        {
            input, output, bump, shift, backups,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(input)!, parse.GetValue(output)!, parse.GetValue(bump), ParseShifts(parse.GetResult(shift)), parse.GetValue(backups)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo Input, FileInfo Output, VersionBump Bump, IReadOnlyList<(string Train, string By)> Shifts, DirectoryInfo? BackupFolder);

    private static List<(string, string)> ParseShifts(System.CommandLine.Parsing.OptionResult? result)
    {
        var shifts = new List<(string, string)>();
        foreach (var token in result?.Tokens.Select(t => t.Value).Chunk(2) ?? [])
        {
            shifts.Add((token[0], token.Length > 1 ? token[1] : string.Empty));
        }

        return shifts;
    }

    /// <returns>0 if saved, 1 if a shift names no train or a bad amount, 2 if the file cannot be read or written.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        WttDocument document;
        try
        {
            document = WttDocument.Load(settings.Input.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            await error.WriteLineAsync($"Cannot read {settings.Input.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var text = new StringWriter(CultureInfo.InvariantCulture);
        foreach (var (name, by) in settings.Shifts)
        {
            if (!TryParseMinutes(by, out var seconds))
            {
                await error.WriteLineAsync($"'{by}' is not a shift in minutes (e.g. +5, -3 or +2H).").ConfigureAwait(false);
                return 1;
            }

            var import = document.Import();
            var trains = WttDumpCommand.FindTrains(import.Timetable, name);
            if (trains.Count == 0)
            {
                // A UID is matched too, though imported IDs use it only when the train has one.
                trains = import.Timetable.Trains.Where(t => string.Equals(t.Uid, name, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (trains.Count == 0)
            {
                await error.WriteLineAsync($"No train {name} in the timetable.").ConfigureAwait(false);
                return 1;
            }

            var nodes = trains.Select(import.NodeOf).ToList();
            var times = 0;
            foreach (var node in nodes)
            {
                var edit = Shift(node, seconds);
                times += Count(edit);
                document.Apply(edit);
            }

            text.WriteLine($"Shifted {string.Join(", ", trains.Select(t => t.Id.Value))} by {Minutes(seconds)} ({times} times).");
        }

        WttSaveResult saved;
        try
        {
            saved = WttWriter.Save(document, settings.Output.FullName, new WttSaveOptions(settings.Bump, settings.BackupFolder?.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Cannot save {settings.Output.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var trainCount = document.Trains.Count();
        text.WriteLine($"Saved {saved.Path} ({(saved.Version is { } v ? "version " + v : "no version")}, {trainCount} trains).");
        if (saved.BackupPath is not null)
        {
            text.WriteLine($"Backed up the file it replaced to {saved.BackupPath}.");
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    /// <summary>Moves every time of a train (entry, origin and destination, each timing point's) by the same amount; 00:00 (no time) is left alone, as SimSig's Adjust times does.</summary>
    internal static TimetableEdit Shift(TrainNode train, int seconds) =>
        TimetableEdit.Change(
            train,
            t =>
            {
                t.DepartTime = Moved(t.DepartTime, seconds);
                t.OriginTime = Moved(t.OriginTime, seconds);
                t.DestinationTime = Moved(t.DestinationTime, seconds);
                foreach (var trip in t.Trips)
                {
                    trip.ArrTime = Moved(trip.ArrTime, seconds);
                    trip.DepPassTime = Moved(trip.DepPassTime, seconds);
                }
            },
            $"Shift {train.Headcode} by {Minutes(seconds)}");

    private static SimTime? Moved(SimTime? time, int seconds) => time is { Seconds: > 0 } t ? SimTime.Max(new SimTime(1), t + seconds) : time;

    private static int Count(TimetableEdit edit) => edit is CompositeEdit composite ? composite.Edits.Sum(Count) : 1;

    /// <summary><c>+5</c>, <c>-3</c>, <c>5</c>, <c>+2H</c> (half a minute more).</summary>
    internal static bool TryParseMinutes(string text, out int seconds)
    {
        seconds = 0;
        var s = text.Trim();
        var half = s.EndsWith('H') || s.EndsWith('h');
        if (half)
        {
            s = s[..^1];
        }

        var negative = s.StartsWith('-');
        if (!int.TryParse(s.TrimStart('+', '-'), NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) || s.Count(c => c is '+' or '-') > 1)
        {
            return false;
        }

        seconds = ((minutes * SimTime.SecondsPerMinute) + (half ? 30 : 0)) * (negative ? -1 : 1);
        return true;
    }

    private static string Minutes(int seconds)
    {
        var sign = seconds < 0 ? "-" : "+";
        var abs = Math.Abs(seconds);
        return $"{sign}{abs / 60}{(abs % 60 == 30 ? "½" : "")} min";
    }
}
