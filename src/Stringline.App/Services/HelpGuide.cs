using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Stringline.App.Services;

/// <summary>
/// The user guide bundled with the app (S28, #4): the Markdown of <c>docs/user-guide</c> and its images, embedded in
/// the exe, written out as HTML pages (Markdig, with GitHub's heading IDs, so a link to a heading works here as it
/// does on GitHub) the first time help is opened in a run, and opened in the browser. It works without a network.
/// </summary>
internal sealed class HelpGuide(string folder)
{
    /// <summary>The page with the guide's contents, which becomes <c>index.html</c>.</summary>
    public const string ContentsPage = "README.md";

    private const string Prefix = "Stringline.Guide/";

    private static readonly Assembly App = typeof(HelpGuide).Assembly;

    /// <summary>The embedded files by their path under <c>docs/user-guide</c> ("re-planning.md", "images/graph.png").</summary>
    private static readonly Dictionary<string, string> Resources = App.GetManifestResourceNames()
        .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
        .ToDictionary(n => n[Prefix.Length..].Replace('\\', '/'), n => n, StringComparer.OrdinalIgnoreCase);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .UseEmphasisExtras()
        .Build();

    private bool _written;

    /// <summary>Where the HTML is written: <c>%LOCALAPPDATA%\Stringline\help</c>.</summary>
    public string Folder { get; } = folder;

    /// <summary>Every file of the guide in the build, by its path under <c>docs/user-guide</c>.</summary>
    public static IReadOnlyCollection<string> Files => Resources.Keys;

    /// <summary>The guide's pages (its Markdown files).</summary>
    public static IEnumerable<string> Pages => Files.Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase));

    /// <summary>The text of one of the guide's Markdown files.</summary>
    public static string ReadText(string file)
    {
        using var stream = Open(file);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Stream Open(string file) =>
        Resources.TryGetValue(file, out var name) && App.GetManifestResourceStream(name) is { } stream
            ? stream
            : throw new FileNotFoundException($"The user guide's {file} is missing from the build.", file);

    /// <summary>
    /// Writes the guide out (once a run) and gives the file to open for a topic, "page.md" or "page.md#heading". A page
    /// the guide does not have opens the contents. A heading goes through a small page that moves to it, because
    /// Windows drops the "#heading" when it hands a file to the browser.
    /// </summary>
    public string PathFor(string? topic)
    {
        WriteAll();
        var (page, heading) = topic?.Split('#', 2) is [var p, var h] ? (p, h) : (topic, null);
        if (page is null || !Resources.ContainsKey(page) || !page.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            (page, heading) = (ContentsPage, null);
        }

        var html = HtmlName(page);
        if (string.IsNullOrEmpty(heading))
        {
            return Path.Combine(Folder, html);
        }

        var target = WebUtility.HtmlEncode($"{html}#{heading}");
        var go = Path.Combine(Folder, "open.html");
        File.WriteAllText(
            go,
            $"""
            <!doctype html>
            <meta charset="utf-8"><meta http-equiv="refresh" content="0; url={target}">
            <title>Stringline user guide</title>
            <p><a href="{target}">Open the user guide</a></p>
            """,
            Encoding.UTF8);
        return go;
    }

    /// <summary>Every page as HTML and every image, into <see cref="Folder"/>. Done again in each run, so a new version's guide replaces the last.</summary>
    public void WriteAll()
    {
        if (_written)
        {
            return;
        }

        Directory.CreateDirectory(Folder);
        var contents = Contents();
        foreach (var file in Files)
        {
            var path = Path.Combine(Folder, (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? HtmlName(file) : file).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (file.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(path, RenderPage(file, ReadText(file), contents), Encoding.UTF8);
            }
            else
            {
                using var source = Open(file);
                using var target = File.Create(path);
                source.CopyTo(target);
            }
        }

        _written = true;
    }

    /// <summary>The HTML file a page becomes: <c>README.md</c> is <c>index.html</c>.</summary>
    public static string HtmlName(string page) =>
        string.Equals(page, ContentsPage, StringComparison.OrdinalIgnoreCase) ? "index.html" : Path.ChangeExtension(page, ".html");

    /// <summary>The page's title: its first level-one heading, else its file name.</summary>
    public static string TitleOf(string markdown, string file) =>
        markdown.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.StartsWith("# ", StringComparison.Ordinal)) is { } heading
            ? heading[2..].Trim()
            : Path.GetFileNameWithoutExtension(file);

    /// <summary>The pages in the order the contents page links to them, with their titles; the contents page first.</summary>
    public static IReadOnlyList<(string File, string Title)> Contents()
    {
        var readme = ReadText(ContentsPage);
        var pages = new List<string> { ContentsPage };
        foreach (var link in Markdown.Parse(readme, Pipeline).Descendants<LinkInline>().Where(l => !l.IsImage))
        {
            var page = link.Url?.Split('#')[0];
            if (page is not null && Resources.ContainsKey(page) && page.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !pages.Contains(page, StringComparer.OrdinalIgnoreCase))
            {
                pages.Add(page);
            }
        }

        return pages.Select(p => (p, p == ContentsPage ? "Contents" : TitleOf(ReadText(p), p))).ToList();
    }

    /// <summary>One page as a whole HTML document: its links to other pages pointed at their HTML, with the contents beside it.</summary>
    public static string RenderPage(string file, string markdown, IReadOnlyList<(string File, string Title)> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            link.Url = LocalUrl(link.Url);
        }

        using var body = new StringWriter(CultureInfo.InvariantCulture);
        var renderer = new HtmlRenderer(body);
        Pipeline.Setup(renderer);
        renderer.Render(document);
        body.Flush();

        var nav = new StringBuilder();
        foreach (var (page, title) in contents)
        {
            var current = string.Equals(page, file, StringComparison.OrdinalIgnoreCase) ? " aria-current=\"page\"" : "";
            nav.Append(CultureInfo.InvariantCulture, $"<li><a href=\"{HtmlName(page)}\"{current}>{WebUtility.HtmlEncode(title)}</a></li>");
        }

        var online = $"{AboutInfo.SourceUrl}/blob/main/docs/user-guide/{file}";
        return $$"""
            <!doctype html>
            <html lang="en-AU">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{WebUtility.HtmlEncode(TitleOf(markdown, file))}} · Stringline user guide</title>
            <style>{{Style}}</style>
            </head>
            <body>
            <nav><p class="brand">Stringline user guide</p><ul>{{nav}}</ul></nav>
            <main>
            {{body}}
            <footer>Stringline {{WebUtility.HtmlEncode(AboutInfo.VersionText)}} · <a href="{{online}}">This page online</a></footer>
            </main>
            </body>
            </html>
            """;
    }

    /// <summary>A link as the HTML pages need it: another page's <c>.md</c> becomes its <c>.html</c>; web links, headings and images stay.</summary>
    internal static string? LocalUrl(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Contains("://", StringComparison.Ordinal) || url.StartsWith('#') || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var (path, heading) = url.Split('#', 2) is [var p, var h] ? (p, "#" + h) : (url, "");
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? HtmlName(path) + heading : url;
    }

    private const string Style = """
        :root { --text: #1f2328; --muted: #59636e; --bg: #ffffff; --panel: #f6f8fa; --line: #d1d9e0; --link: #0969da; color-scheme: light dark; }
        @media (prefers-color-scheme: dark) {
          :root { --text: #e6edf3; --muted: #9198a1; --bg: #0d1117; --panel: #151b23; --line: #3d444d; --link: #4493f8; }
        }
        body { margin: 0; display: flex; min-height: 100vh; background: var(--bg); color: var(--text);
               font: 15px/1.6 "Segoe UI", system-ui, sans-serif; }
        nav { flex: 0 0 15rem; background: var(--panel); border-right: 1px solid var(--line); padding: 1rem; box-sizing: border-box; }
        nav .brand { font-weight: 600; margin: 0 0 .5rem; }
        nav ul { list-style: none; margin: 0; padding: 0; }
        nav a { display: block; padding: .15rem .4rem; border-radius: 4px; color: var(--text); text-decoration: none; }
        nav a:hover { background: var(--line); }
        nav a[aria-current] { background: var(--link); color: #fff; }
        main { flex: 1; max-width: 52rem; padding: 1rem 2rem 3rem; min-width: 0; }
        a { color: var(--link); }
        h1, h2, h3 { line-height: 1.25; }
        h2 { border-bottom: 1px solid var(--line); padding-bottom: .3rem; margin-top: 2rem; }
        img { max-width: 100%; border: 1px solid var(--line); }
        table { border-collapse: collapse; margin: 1rem 0; display: block; overflow-x: auto; }
        th, td { border: 1px solid var(--line); padding: .3rem .6rem; text-align: left; vertical-align: top; }
        th { background: var(--panel); }
        code, kbd { font-family: Consolas, monospace; font-size: 90%; background: var(--panel); border: 1px solid var(--line); border-radius: 4px; padding: 0 .3rem; }
        pre { background: var(--panel); border: 1px solid var(--line); border-radius: 6px; padding: .8rem; overflow-x: auto; }
        pre code { border: 0; padding: 0; }
        blockquote { margin: 1rem 0; padding: .2rem 1rem; border-left: 4px solid var(--line); color: var(--muted); }
        footer { margin-top: 3rem; padding-top: 1rem; border-top: 1px solid var(--line); color: var(--muted); font-size: 90%; }
        @media (max-width: 700px) { body { display: block; } nav { border-right: 0; border-bottom: 1px solid var(--line); } main { padding: 1rem; } }
        """;
}
