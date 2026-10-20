using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Stringline.Architecture.Tests;

/// <summary>
/// The user guide (S28, #4; <c>docs/user-guide</c>): no broken relative links or images, every heading a link names is
/// there (by GitHub's heading IDs, which the app's help and the wiki use too), every page is in the contents, and every
/// image is used. Web links are not fetched.
/// </summary>
public sealed partial class GuideLinkTests
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseEmphasisExtras()
        .Build();

    private static string Guide => Path.Combine(LayeringTests.FindRepoRoot(), "docs", "user-guide");

    private static IEnumerable<string> Pages => Directory.GetFiles(Guide, "*.md", SearchOption.AllDirectories);

    /// <summary>The links on a page: its target as written, and whether it is an image.</summary>
    private static List<(string Url, bool IsImage)> LinksOn(string page) =>
        Markdown.Parse(File.ReadAllText(page), Pipeline).Descendants<LinkInline>().Select(l => (l.Url ?? "", l.IsImage)).ToList();

    /// <summary>The IDs a page's headings get.</summary>
    private static HashSet<string> HeadingIds(string page) =>
        [.. IdAttribute().Matches(Markdown.ToHtml(File.ReadAllText(page), Pipeline)).Select(m => m.Groups[1].Value)];

    private static bool IsWeb(string url) => url.Contains("://", StringComparison.Ordinal) || url.StartsWith("mailto:", StringComparison.Ordinal);

    [Fact]
    public void The_guide_has_its_pages()
    {
        Pages.Select(Path.GetFileName).Should().Contain(
            ["README.md", "installing.md", "gateway.md", "connecting.md", "timetables-and-profiles.md", "reading-the-graph.md", "re-planning.md",
             "disruptions-and-notes.md", "conflicts-and-suggestions.md", "regulation.md", "sessions-and-review.md", "exports-and-reports.md",
             "settings.md", "troubleshooting.md", "glossary.md", "keyboard-and-mouse.md", "further-reading.md"]);
    }

    [Fact]
    public void Every_relative_link_and_image_is_there()
    {
        var broken = new List<string>();
        foreach (var page in Pages)
        {
            var name = Path.GetRelativePath(Guide, page);
            foreach (var (url, isImage) in LinksOn(page).Where(l => !IsWeb(l.Url)))
            {
                var (path, heading) = url.Split('#', 2) is [var p, var h] ? (p, h) : (url, null);
                var target = path.Length == 0 ? page : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, path));
                if (!target.StartsWith(Guide + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    broken.Add($"{name}: {url} is outside the guide (link to it on GitHub instead)");
                }
                else if (!File.Exists(target))
                {
                    broken.Add($"{name}: {url} does not exist");
                }
                else if (isImage != target.EndsWith(".png", StringComparison.Ordinal))
                {
                    broken.Add($"{name}: {url} should be {(isImage ? "a PNG" : "a link, not an image")}");
                }
                else if (heading is not null && !HeadingIds(target).Contains(heading))
                {
                    broken.Add($"{name}: {url} names a heading {Path.GetFileName(target)} does not have");
                }
            }
        }

        broken.Should().BeEmpty();
    }

    /// <summary>
    /// The README and the developer page (S35, #18) link into the repository freely, so their links need only exist
    /// in it (headings too). The README is shown on GitHub and copied into the zip, so its picture must be there.
    /// </summary>
    [Theory]
    [InlineData("README.md")]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("docs/developer.md")]
    public void Every_relative_link_in_the_readme_and_developer_pages_is_there(string file)
    {
        var root = LayeringTests.FindRepoRoot();
        var page = Path.GetFullPath(Path.Combine(root, file));
        var broken = new List<string>();
        foreach (var (url, isImage) in LinksOn(page).Where(l => !IsWeb(l.Url)))
        {
            var (path, heading) = url.Split('#', 2) is [var p, var h] ? (p, h) : (url, null);
            var target = path.Length == 0 ? page : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(page)!, path));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                broken.Add($"{url} is outside the repository");
            }
            else if (!File.Exists(target) && !(Directory.Exists(target) && !isImage))
            {
                broken.Add($"{url} does not exist");
            }
            else if (heading is not null && target.EndsWith(".md", StringComparison.Ordinal) && !HeadingIds(target).Contains(heading))
            {
                broken.Add($"{url} names a heading {Path.GetFileName(target)} does not have");
            }
        }

        broken.Should().BeEmpty();
    }

    [Fact]
    public void The_readme_is_for_users_and_points_developers_on()
    {
        var readme = File.ReadAllText(Path.Combine(LayeringTests.FindRepoRoot(), "README.md"));
        readme.Should().Contain("(docs/developer.md)", "the README links developers on under Contributing")
            .And.Contain("](docs/user-guide/images/", "it shows the graph")
            .And.Contain("Interface Gateway").And.Contain("SmartScreen").And.Contain("/wiki")
            .And.Contain("GPL").And.Contain("/issues");
        readme.Should().NotContain("dotnet ", "build and CLI commands are in docs/developer.md");
    }

    [Fact]
    public void Every_page_is_in_the_contents_and_every_image_is_used()
    {
        var readme = Path.Combine(Guide, "README.md");
        var listed = LinksOn(readme).Select(l => l.Url.Split('#')[0]).ToHashSet();
        Pages.Select(Path.GetFileName).Where(p => p != "README.md").Should().OnlyContain(p => listed.Contains(p!), "the contents lists every page");

        var used = Pages.SelectMany(p => LinksOn(p).Where(l => l.IsImage).Select(l => Path.GetFileName(l.Url))).ToHashSet();
        var images = Directory.GetFiles(Path.Combine(Guide, "images"), "*.*").Select(Path.GetFileName);
        images.Should().OnlyContain(i => used.Contains(i!), "every image in the guide is shown somewhere");
    }

    [GeneratedRegex(@"\bid=""([^""]+)""")]
    private static partial Regex IdAttribute();
}
