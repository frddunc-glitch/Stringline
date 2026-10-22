using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Gateway;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline wtt-check</c> (S42): the checks before SimSig (<c>08-…md</c> §5) on a <c>.wtt</c>: the Analyser's that
/// can be made offline, those against what the sim's other timetables, recordings and profile show, and the rules'.
/// </summary>
internal static class WttCheckCommand
{
    public static Command Create()
    {
        var input = new Argument<FileInfo>("timetable") { Description = "SimSig timetable to check (.wtt, or its SavedTimetable.xml)." };
        var folders = new Option<DirectoryInfo[]>("--knowledge-folder") { Description = "A folder of timetables (with its subfolders) to learn the sim from; give it more than once for several. Default: SimSig's Timetables folder.", AllowMultipleArgumentsPerToken = true };
        var logs = new Option<FileInfo[]>("--log") { Description = "A recording (.sslog) or session (.stringline) to learn from too; give it more than once for several.", AllowMultipleArgumentsPerToken = true };
        var profile = new Option<FileInfo?>("--profile") { Description = "The sim's profile. Default: its saved profile, if any." };
        var noProfile = new Option<bool>("--no-profile") { Description = "Use no profile." };
        var includeSelf = new Option<bool>("--include-self") { Description = "Learn from the checked timetable too (by default it is left out, as a draft is, so it cannot vouch for itself)." };
        var noKnowledge = new Option<bool>("--no-knowledge") { Description = "Only the Analyser's and the rules' checks; no sim knowledge." };
        var severity = new Option<IssueSeverity>("--severity") { Description = "The least severity listed: info (default), warning or error.", DefaultValueFactory = _ => IssueSeverity.Info };
        var train = new Option<string?>("--train") { Description = "List only this train's issues (headcode, UID or ID)." };
        var summary = new Option<bool>("--summary") { Description = "Only the counts by check." };

        var command = new Command("wtt-check", "Check a timetable before SimSig sees it: the Analyser's offline checks, the sim's knowledge and the rules.")
        {
            input, folders, logs, profile, noProfile, includeSelf, noKnowledge, severity, train, summary,
        };
        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(
                parse.GetValue(input)!,
                parse.GetValue(folders) ?? [],
                parse.GetValue(logs) ?? [],
                parse.GetValue(profile),
                parse.GetValue(noProfile),
                parse.GetValue(includeSelf),
                parse.GetValue(noKnowledge),
                parse.GetValue(severity),
                parse.GetValue(train),
                parse.GetValue(summary)),
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(
        FileInfo Input,
        IReadOnlyList<DirectoryInfo> Folders,
        IReadOnlyList<FileInfo>? Logs = null,
        FileInfo? Profile = null,
        bool NoProfile = false,
        bool IncludeSelf = false,
        bool NoKnowledge = false,
        IssueSeverity Severity = IssueSeverity.Info,
        string? Train = null,
        bool Summary = false);

    /// <returns>0 if no Errors, 1 if there are Errors, 2 if the file cannot be read.</returns>
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

        if (settings.Profile is { Exists: false } missing)
        {
            await error.WriteLineAsync($"No profile {missing.FullName}.").ConfigureAwait(false);
            return 2;
        }

        var text = new StringWriter(CultureInfo.InvariantCulture);
        var import = document.Import();
        var area = import.Timetable.AreaId;
        SimKnowledge? knowledge = null;
        if (!settings.NoKnowledge && area.Length > 0)
        {
            using var loggerFactory = ConsoleLogging.Create(false);
            var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
            var profilePath = settings.NoProfile ? null : settings.Profile?.FullName ?? new ProfileStore().PathFor(area);
            var request = new SimKnowledgeRequest
            {
                AreaId = area,
                TimetableFolders = settings.Folders.Count > 0 ? [.. settings.Folders.Select(f => f.FullName)] : [WttCatalogue.DefaultFolder],
                PlayFiles = [.. (settings.Logs ?? []).Select(f => f.FullName)],
                ReadPlay = path => PlayFiles.Read(path, parser),
                ProfilePath = profilePath,
                Exclude = settings.IncludeSelf ? [] : [settings.Input.FullName],
            };
            var clock = Stopwatch.StartNew();
            knowledge = SimKnowledgeStore.Build(request, SimKnowledgeStore.FindSources(request, cancellationToken), out var problems, cancellationToken);
            text.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Knowledge of {area}: {knowledge.Timetables} timetables ({knowledge.Trains} trains), {knowledge.Plays} recordings or sessions, {(knowledge.Sources.Any(s => s.Kind == KnowledgeSourceKind.Profile) ? "the profile" : "no profile")}; built in {clock.Elapsed.TotalSeconds:0.00} s."));
            foreach (var problem in problems)
            {
                text.WriteLine("  Left out: " + problem);
            }

            if (knowledge.Timetables == 0)
            {
                text.WriteLine("  No other timetable of the sim was found, so the sim-knowledge checks are left out.");
            }
        }

        var validation = new TimetableValidator(knowledge).Validate(import);
        var issues = validation.Issues.Where(i => i.Severity >= settings.Severity).ToList();
        if (settings.Train is { } wanted)
        {
            issues = [.. issues.Where(i => string.Equals(i.Headcode, wanted, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Train?.Value, wanted, StringComparison.OrdinalIgnoreCase)
                || (i.TrainElement is { } e && string.Equals(new TrainNode(document, e).Uid, wanted, StringComparison.OrdinalIgnoreCase)))];
        }

        var name = import.Timetable.Header.Name.Length > 0 ? import.Timetable.Header.Name : settings.Input.Name;
        text.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name} ({(area.Length > 0 ? area : "no sim ID")}), {import.Timetable.Trains.Count} trains, {import.Timetable.Rules.Count} rules."));
        if (!settings.Summary)
        {
            foreach (var issue in issues)
            {
                var where = issue.PointIndex is { } p && issue.Train is { } id && import.Timetable.Find(id) is { } t ? $"{t.Points[p].Location}" : string.Empty;
                text.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{issue.Severity,-8}{issue.Headcode ?? "-",-10}{where,-12}{issue.Message}"));
                if (issue.QuickFix is { } fix)
                {
                    text.WriteLine($"{string.Empty,30}Fix: {fix.Title}");
                }
            }
        }

        text.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{issues.Count(i => i.Severity == IssueSeverity.Error)} errors, {issues.Count(i => i.Severity == IssueSeverity.Warning)} warnings, {issues.Count(i => i.Severity == IssueSeverity.Info)} info."));
        foreach (var group in issues.GroupBy(i => i.Code).OrderByDescending(g => IssueCatalogue.Of(g.Key).Severity).ThenByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            var entry = IssueCatalogue.Of(group.Key);
            text.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {group.Key,-32}{group.Count(),6}  {entry.Severity}: {entry.Title}"));
        }

        if (validation.BlocksSave)
        {
            text.WriteLine("Stringline will not save this timetable until the errors that make it unreadable are fixed.");
        }

        await output.WriteAsync(text.ToString().AsMemory(), cancellationToken).ConfigureAwait(false);
        return validation.Issues.Any(i => i.Severity == IssueSeverity.Error) ? 1 : 0;
    }
}
