using System.Text;
using Microsoft.Extensions.Logging;
using Stringline.Core.Events;
using Stringline.Editing.Testing;
using Stringline.Editing.Validation;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Persistence;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline report --test --wtt t.wtt --log x.sslog [out.csv]</c> (S48): the timetable test report, printed, and
/// written as CSV when a file is given.
/// </summary>
internal static class TestReport
{
    /// <returns>0 if the run showed nothing, 1 if it reported issues, 2 if a file cannot be read or written.</returns>
    public static async Task<int> RunAsync(ReportCommand.Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        var parser = new MessageParser(loggerFactory.CreateLogger<MessageParser>());
        WttDocumentImport import;
        IEnumerable<GatewayEvent> events;
        try
        {
            var saved = settings.Session is null ? null : SavedSession.Load(settings.Session.FullName);
            var wttPath = settings.Wtt?.FullName ?? saved?.Info.TimetablePath;
            var recordingPath = settings.Log?.FullName ?? saved?.Info.RecordingPath;
            if (wttPath is null || recordingPath is null)
            {
                await error.WriteLineAsync("Give --wtt and --log (a live session has no recording to test from).").ConfigureAwait(false);
                return 2;
            }

            import = WttDocument.Load(wttPath).Import();
            events = SslogRecording.Load(recordingPath).Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage())).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or FormatException or InvalidDataException)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        var issues = TimetableTestReport.Build(import, events);
        foreach (var group in issues.GroupBy(i => i.Code))
        {
            await output.WriteLineAsync($"{IssueCatalogue.Of(group.Key).Title} ({group.Count()}):").ConfigureAwait(false);
            foreach (var issue in group)
            {
                await output.WriteLineAsync($"  {issue.Headcode ?? "-",-8} {issue.Message}").ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync(issues.Count == 0 ? "The test run showed nothing to fix." : $"{issues.Count} issues.").ConfigureAwait(false);
        if (settings.Output is { } file)
        {
            try
            {
                var csv = new StringBuilder("Check,Train,Message,Quick fix\r\n");
                foreach (var issue in issues)
                {
                    csv.Append(string.Join(",", new[] { IssueCatalogue.Of(issue.Code).Title, issue.Headcode ?? string.Empty, issue.Message, issue.QuickFix?.Title ?? string.Empty }.Select(Quote))).Append("\r\n");
                }

                await File.WriteAllTextAsync(file.FullName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Wrote {file.FullName}.").ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await error.WriteLineAsync($"Cannot write {file.FullName}: {ex.Message}").ConfigureAwait(false);
                return 2;
            }
        }

        return issues.Count == 0 ? 0 : 1;
    }

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
}
