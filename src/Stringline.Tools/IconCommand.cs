using System.CommandLine;
using Stringline.Graph;

namespace Stringline.Tools;

/// <summary><c>stringline icon</c>: draw the app's icon (<see cref="AppIcon"/>) as a <c>.ico</c>, and optionally PNGs to look at.</summary>
internal static class IconCommand
{
    public static Command Create()
    {
        var output = new Argument<FileInfo>("output") { Description = "The .ico to write (the app's is src/Stringline.App/Assets/stringline.ico)." };
        var png = new Option<DirectoryInfo?>("--png") { Description = "Also write each size as icon-{size}.png in this folder." };
        var command = new Command("icon", "Draw Stringline's app icon at every size Windows uses, into one .ico.") { output, png };
        command.SetAction((parse, cancellationToken) => RunAsync(parse.GetValue(output)!, parse.GetValue(png), parse.InvocationConfiguration.Output, parse.InvocationConfiguration.Error, cancellationToken));
        return command;
    }

    /// <returns>0 on success, 2 if a file cannot be written.</returns>
    internal static async Task<int> RunAsync(FileInfo output, DirectoryInfo? png, TextWriter stdout, TextWriter error, CancellationToken cancellationToken)
    {
        try
        {
            output.Directory?.Create();
            await using (var stream = output.Create())
            {
                AppIcon.WriteIco(stream);
            }

            if (png is not null)
            {
                png.Create();
                foreach (var size in AppIcon.Sizes)
                {
                    await File.WriteAllBytesAsync(Path.Combine(png.FullName, $"icon-{size}.png"), AppIcon.Png(size), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await error.WriteLineAsync($"Cannot write the icon: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        await stdout.WriteLineAsync($"Wrote {output.FullName} ({string.Join(", ", AppIcon.Sizes)} px).").ConfigureAwait(false);
        return 0;
    }
}
