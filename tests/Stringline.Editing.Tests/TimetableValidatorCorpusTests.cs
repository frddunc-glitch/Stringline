using System.Diagnostics;
using Xunit.Abstractions;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Tests;

/// <summary>
/// The checks over the released timetables of the corpus (<c>STRINGLINE_WTT_CORPUS</c>), each sim's knowledge built
/// from all its timetables (Fred, S42). The counts by check go in <c>progress.md</c>: a check that fires on many released
/// timetables is a false alarm.
/// </summary>
public sealed class TimetableValidatorCorpusTests(ITestOutputHelper output)
{
    private const string DefaultCorpus = @"C:\Users\Public\Documents\SimSig\Timetables";

    [Fact]
    public void The_corpus_baseline()
    {
        var folder = Environment.GetEnvironmentVariable("STRINGLINE_WTT_CORPUS") is { Length: > 0 } configured ? configured : DefaultCorpus;
        if (!Directory.Exists(folder))
        {
            output.WriteLine($"No timetable corpus at {folder}; set STRINGLINE_WTT_CORPUS to run this test.");
            return;
        }

        var areas = Directory.EnumerateFiles(folder, "*.wtt", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive })
            .Select(WttCatalogue.ReadInfo).OfType<WttFileInfo>()
            .GroupBy(i => i.AreaId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var issues = new Dictionary<string, int>();
        var files = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        var most = new Dictionary<string, (string File, int Count)>();
        var count = 0;
        var trains = 0;
        var clock = Stopwatch.StartNew();
        var slowest = (Name: string.Empty, Seconds: 0.0);
        foreach (var area in areas)
        {
            var request = new SimKnowledgeRequest { AreaId = area.Key, TimetableFolders = [folder] };
            var validator = new TimetableValidator(SimKnowledgeStore.Build(request, SimKnowledgeStore.FindSources(request), out _));
            foreach (var info in area)
            {
                WttDocument document;
                try
                {
                    document = WttDocument.Load(info.Path);
                }
                catch (WttFormatException)
                {
                    continue; // damaged XML cannot be edited (S40)
                }

                var one = Stopwatch.StartNew();
                var import = document.Import();
                var result = validator.Validate(import);
                if (one.Elapsed.TotalSeconds > slowest.Seconds)
                {
                    slowest = (Path.GetFileName(info.Path), one.Elapsed.TotalSeconds);
                }

                count++;
                trains += import.Timetable.Trains.Count;
                foreach (var group in result.Issues.GroupBy(i => i.Code))
                {
                    issues[group.Key] = issues.GetValueOrDefault(group.Key) + group.Count();
                    files[group.Key] = files.GetValueOrDefault(group.Key) + 1;
                    examples.TryAdd(group.Key, $"{Path.GetFileName(info.Path)}: {group.First()}");
                    if (group.Count() > most.GetValueOrDefault(group.Key).Count)
                    {
                        most[group.Key] = (Path.GetFileName(info.Path), group.Count());
                    }
                }
            }
        }

        output.WriteLine($"{count} timetables, {trains} trains, in {clock.Elapsed.TotalSeconds:0.0} s (slowest {slowest.Name} {slowest.Seconds:0.00} s with its import).");
        output.WriteLine($"{"Check",-32}{"Severity",-9}{"Issues",8}{"Files",7}  Example");
        foreach (var code in IssueCatalogue.Codes.OrderByDescending(c => IssueCatalogue.Of(c).Severity).ThenByDescending(c => files.GetValueOrDefault(c)).ThenBy(c => c, StringComparer.Ordinal))
        {
            output.WriteLine($"{code,-32}{IssueCatalogue.Of(code).Severity,-9}{issues.GetValueOrDefault(code),8}{files.GetValueOrDefault(code),7}  {examples.GetValueOrDefault(code)}{(most.TryGetValue(code, out var m) ? $" (most: {m.File} {m.Count})" : string.Empty)}");
        }

        count.Should().BeGreaterThan(0);
    }
}
