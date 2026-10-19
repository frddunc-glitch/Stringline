using System.Text.RegularExpressions;

namespace Stringline.Architecture.Tests;

/// <summary>
/// Punctuality wording (#11): trains are early, on time or late, as the railway says it, never "right time". Checks
/// the app's source and resources (code, XAML), the README and the design docs. The progress log and the plan keep
/// the old words where they record the change.
/// </summary>
public sealed partial class WordingTests
{
    private static readonly string[] History = ["progress.md", "07-implementation-plan.md"];

    [Fact]
    public void No_text_says_right_time()
    {
        var root = LayeringTests.FindRepoRoot();
        var files = Directory.GetFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
            .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Concat(Directory.GetFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories).Where(f => !History.Contains(Path.GetFileName(f))))
            .Append(Path.Combine(root, "README.md"))
            .ToList();
        files.Should().Contain(f => f.EndsWith("GraphHover.cs", StringComparison.Ordinal), "the search must reach the hover's wording");

        var found = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f), Line: i + 1, Text: line)))
            .Where(l => RightTime().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line}: {l.Text.Trim()}")
            .ToList();

        found.Should().BeEmpty("say \"on time\" (or early/late) instead");
    }

    [GeneratedRegex(@"right[\s_-]?time", RegexOptions.IgnoreCase)]
    private static partial Regex RightTime();
}
