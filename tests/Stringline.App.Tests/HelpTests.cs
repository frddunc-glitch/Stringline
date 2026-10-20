using System.Windows.Input;
using Stringline.App.Services;
using Stringline.Gateway.Tests;

namespace Stringline.App.Tests;

/// <summary>
/// The bundled user guide (S28, #4): every page and image of <c>docs/user-guide</c> is in the build, it is written out
/// as HTML with its links pointed at the HTML, every F1 topic's page and heading is there, and F1 opens the page for
/// the panel with the focus.
/// </summary>
public sealed class HelpTests : IDisposable
{
    private static readonly string Guide = Path.GetFullPath(Fixtures.Path(Path.Combine("..", "..", "docs", "user-guide")));

    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("stringline-help-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void Every_page_and_image_of_the_guide_is_in_the_build()
    {
        var files = Directory.GetFiles(Guide, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal) || f.EndsWith(".png", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(Guide, f).Replace('\\', '/'))
            .ToList();
        files.Should().Contain("README.md").And.Contain(f => f.StartsWith("images/", StringComparison.Ordinal));

        HelpGuide.Files.Should().BeEquivalentTo(files);
        HelpGuide.ReadText("re-planning.md").Should().Be(File.ReadAllText(Path.Combine(Guide, "re-planning.md")));
    }

    [Fact]
    public void Every_F1_topic_has_its_page_and_heading()
    {
        var guide = new HelpGuide(_folder.FullName);
        guide.WriteAll();
        foreach (var topic in HelpTopics.All)
        {
            var (page, heading) = topic.Split('#', 2) is [var p, var h] ? (p, h) : (topic, null);
            HelpGuide.Pages.Should().Contain(page, topic);
            var html = File.ReadAllText(Path.Combine(_folder.FullName, HelpGuide.HtmlName(page)));
            if (heading is not null)
            {
                html.Should().Contain($"id=\"{heading}\"", $"{topic} names a heading on its page");
            }
        }
    }

    [Fact]
    public void The_guide_is_written_as_html_with_its_links_and_images()
    {
        var guide = new HelpGuide(_folder.FullName);
        var index = guide.PathFor(null);

        index.Should().Be(Path.Combine(_folder.FullName, "index.html"));
        HelpGuide.Pages.Should().OnlyContain(p => File.Exists(Path.Combine(_folder.FullName, HelpGuide.HtmlName(p))));
        HelpGuide.Files.Where(f => f.EndsWith(".png", StringComparison.Ordinal))
            .Should().OnlyContain(f => File.Exists(Path.Combine(_folder.FullName, f)));

        var contents = File.ReadAllText(index);
        contents.Should().Contain("<title>Stringline user guide · Stringline user guide</title>")
            .And.Contain("href=\"re-planning.html\"").And.NotContain("href=\"re-planning.md\"")
            .And.Contain("src=\"images/main-window.png\"")
            .And.Contain("href=\"https://www.simsig.co.uk/\"");
        File.ReadAllText(Path.Combine(_folder.FullName, "conflicts-and-suggestions.html"))
            .Should().Contain("href=\"timetables-and-profiles.html#headways\"", "a link to a heading keeps it")
            .And.Contain("aria-current=\"page\">Conflicts and suggestions</a>", "the contents mark the page shown");

        HelpGuide.LocalUrl("gateway.md#firewalls").Should().Be("gateway.html#firewalls");
        HelpGuide.LocalUrl("README.md").Should().Be("index.html");
        HelpGuide.LocalUrl("#kinds-of-conflict").Should().Be("#kinds-of-conflict");
        HelpGuide.LocalUrl("https://example.org/a.md").Should().Be("https://example.org/a.md");
    }

    [Fact]
    public void A_topic_with_a_heading_opens_through_a_page_that_moves_to_it()
    {
        var guide = new HelpGuide(_folder.FullName);

        guide.PathFor(HelpTopics.Conflicts).Should().Be(Path.Combine(_folder.FullName, "conflicts-and-suggestions.html"));
        var go = guide.PathFor(HelpTopics.TrainList);
        go.Should().Be(Path.Combine(_folder.FullName, "open.html"));
        File.ReadAllText(go).Should().Contain("url=reading-the-graph.html#the-train-list");
        guide.PathFor("no-such-page.md#x").Should().Be(Path.Combine(_folder.FullName, "index.html"), "an unknown page opens the contents");
    }

    [Fact]
    public async Task Help_user_guide_and_F1_open_the_page_for_the_panel_with_the_focus()
    {
        await using var app = new TestApp(wpf: true);
        var vm = app.ViewModel;
        var help = app.Paths.Help;

        await app.Ui.Run(() =>
        {
            var window = MainWindowTests.Show(vm, 1280, 720);
            try
            {
                string Opened(System.Windows.IInputElement on, object? parameter = null)
                {
                    ApplicationCommands.Help.Execute(parameter, on);
                    var path = app.Dialogs.Opened[^1];
                    return Path.GetFileName(path) == "open.html" ? File.ReadAllText(path) : Path.GetFileName(path);
                }

                Opened(window.Graph).Should().Be("reading-the-graph.html");
                Opened(window.ConflictsList).Should().Be("conflicts-and-suggestions.html");
                Opened(window.RegulationList).Should().Be("regulation.html");
                Opened(window.TrainGrid).Should().Contain("url=reading-the-graph.html#the-train-list");
                Opened(window.ScheduleGrid).Should().Contain("url=re-planning.html#the-train-detail-panel");
                Opened(window.HistoryList).Should().Contain("url=re-planning.html#undo-and-the-edit-history");
                Opened(window.Graph, Views.Help.ContentsParameter).Should().Be("index.html", "Help ▸ User guide opens the contents");
                app.Dialogs.Opened.Should().OnlyContain(p => p.StartsWith(help, StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task A_dialog_opens_its_own_page()
    {
        await using var app = new TestApp(wpf: true);
        var vm = app.ViewModel;

        await app.Ui.Run(() =>
        {
            var window = MainWindowTests.Show(vm, 1280, 720);
            var dialog = new Views.ConnectDialog(new ViewModels.ConnectViewModel(app.Dialogs)) { Owner = window, ShowActivated = false };
            try
            {
                dialog.Show();
                ApplicationCommands.Help.Execute(null, dialog);
                Path.GetFileName(app.Dialogs.Opened[^1]).Should().Be("connecting.html");
            }
            finally
            {
                dialog.Close();
                window.Close();
            }
        });
    }
}
