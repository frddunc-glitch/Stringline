using System.Diagnostics;
using Stringline.Gateway.Tests;

namespace Stringline.App.Tests;

/// <summary>
/// <c>build/publish-wiki.ps1</c> (S28, #4) with <c>-Out</c>: the guide's pages become wiki pages (README is Home), links
/// between them become wiki links with their headings kept, and the images, sidebar and footer are there.
/// </summary>
public sealed class WikiScriptTests : IDisposable
{
    private static readonly string Script = Path.GetFullPath(Fixtures.Path(Path.Combine("..", "..", "build", "publish-wiki.ps1")));

    private static readonly string Guide = Path.GetFullPath(Fixtures.Path(Path.Combine("..", "..", "docs", "user-guide")));

    private readonly string _out = Path.Combine(Path.GetTempPath(), $"stringline-wiki-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_out))
        {
            Directory.Delete(_out, recursive: true);
        }
    }

    [Fact]
    public async Task Writes_the_guide_as_wiki_pages()
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Out", _out })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.Should().Be(0, output);

        var pages = Directory.GetFiles(Guide, "*.md").Select(Path.GetFileName).Select(p => p == "README.md" ? "Home.md" : p);
        Directory.GetFiles(_out, "*.md").Select(Path.GetFileName).Should().BeEquivalentTo(pages.Append("_Sidebar.md").Append("_Footer.md"));
        Directory.GetFiles(Path.Combine(_out, "images")).Select(Path.GetFileName)
            .Should().BeEquivalentTo(Directory.GetFiles(Path.Combine(Guide, "images")).Select(Path.GetFileName));

        var conflicts = File.ReadAllText(Path.Combine(_out, "conflicts-and-suggestions.md"));
        conflicts.Should().Contain("](timetables-and-profiles#headways)").And.Contain("](re-planning#grips)");
        Directory.GetFiles(_out, "*.md").Select(File.ReadAllText)
            .Should().NotContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, @"\]\([^)\s:]+\.md"), "no link to a .md page is left");
        File.ReadAllText(Path.Combine(_out, "Home.md")).Should().Contain("](images/main-window.png)").And.Contain("](installing)");
        File.ReadAllText(Path.Combine(_out, "_Sidebar.md")).Should().Contain("- [Re-planning](re-planning)").And.Contain("(Home)");
        File.ReadAllText(Path.Combine(_out, "_Footer.md")).Should().Contain("docs/user-guide");
    }
}
