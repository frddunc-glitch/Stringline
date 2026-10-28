using System.CommandLine;
using System.Text;
using Stringline.Editing.Csv;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline wtt-csv export t.wtt out.csv</c> and <c>stringline wtt-csv import t.wtt in.csv out.wtt</c> (S47): a
/// timetable as a spreadsheet, one row per timing point, and a sheet merged back as a new version.
/// </summary>
internal static class WttCsvCommand
{
    public static Command Create()
    {
        var exportInput = new Argument<FileInfo>("timetable") { Description = "SimSig timetable (.wtt)." };
        var exportOutput = new Argument<FileInfo>("out") { Description = "The sheet to write (.csv)." };
        var export = new Command("export", "Write a timetable's trains as a CSV sheet, one row per timing point.") { exportInput, exportOutput };
        export.SetAction((parse, cancellationToken) => ExportAsync(parse.GetValue(exportInput)!, parse.GetValue(exportOutput)!, parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error, cancellationToken));

        var importInput = new Argument<FileInfo>("timetable") { Description = "SimSig timetable to merge into (.wtt)." };
        var sheet = new Argument<FileInfo>("sheet") { Description = "The CSV sheet." };
        var importOutput = new Argument<FileInfo>("out") { Description = "Where to save the result (.wtt). A file already there is backed up first." };
        var import = new Command("import", "Merge a CSV sheet into a timetable (by UID, else headcode and first time, else as new trains) and save it as a new version.")
        {
            importInput, sheet, importOutput,
        };
        import.SetAction((parse, cancellationToken) => ImportAsync(parse.GetValue(importInput)!, parse.GetValue(sheet)!, parse.GetValue(importOutput)!, parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error, cancellationToken));

        return new Command("wtt-csv", "A timetable as a spreadsheet, and back (S47).") { export, import };
    }

    /// <returns>0 if written, 2 if a file cannot be read or written.</returns>
    internal static async Task<int> ExportAsync(FileInfo input, FileInfo output, TextWriter text, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            var document = WttDocument.Load(input.FullName);
            await File.WriteAllTextAsync(output.FullName, TimetableCsv.Export(document), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), cancellationToken).ConfigureAwait(false);
            await text.WriteLineAsync($"Wrote {output.FullName}: {document.Trains.Count()} trains, {document.Trains.Sum(t => t.Trips.Count())} points.").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }
    }

    /// <returns>0 if saved (rows it could not read are listed), 1 if the sheet changes nothing, 2 if a file cannot be read or written.</returns>
    internal static async Task<int> ImportAsync(FileInfo input, FileInfo sheetFile, FileInfo output, TextWriter text, TextWriter error, CancellationToken cancellationToken)
    {
        WttDocument document;
        string csv;
        try
        {
            document = WttDocument.Load(input.FullName);
            csv = await File.ReadAllTextAsync(sheetFile.FullName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException)
        {
            await error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 2;
        }

        var result = TimetableCsv.Merge(document, TimetableCsv.Read(csv));
        foreach (var change in result.Changes)
        {
            await text.WriteLineAsync(change).ConfigureAwait(false);
        }

        foreach (var problem in result.Problems)
        {
            await error.WriteLineAsync(problem).ConfigureAwait(false);
        }

        if (result.Edit is null)
        {
            await text.WriteLineAsync("The sheet changes nothing; nothing saved.").ConfigureAwait(false);
            return 1;
        }

        document.Apply(result.Edit);
        try
        {
            var saved = WttWriter.Save(document, output.FullName, new WttSaveOptions(VersionBump.Build, null));
            await text.WriteLineAsync($"Saved {saved.Path} ({(saved.Version is { } v ? "version " + v : "no version")}, {result.Changes.Count} changes).").ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await error.WriteLineAsync($"Cannot save {output.FullName}: {ex.Message}").ConfigureAwait(false);
            return 2;
        }
    }
}
