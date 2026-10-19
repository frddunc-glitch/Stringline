using System.Text.RegularExpressions;
using System.Xml.Linq;
using Stringline.App.Services;
using Stringline.Gateway.Tests;

namespace Stringline.App.Tests;

/// <summary>S18: what the About window shows, and that every component shipped is credited.</summary>
public sealed partial class AboutTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Fixtures.Path("."), "..", ".."));

    /// <summary>Packages that are not shipped in the app: test, benchmark and command-line-only ones.</summary>
    private static readonly string[] NotShipped = ["System.CommandLine"];

    [Fact]
    public void The_version_and_commit_come_from_the_build()
    {
        AboutInfo.Version.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        AboutInfo.Version.Should().NotContain("+");
        if (AboutInfo.Commit is { } commit)
        {
            commit.Should().MatchRegex("^[0-9a-f]{7}$");
            AboutInfo.VersionText.Should().Be($"{AboutInfo.Version} ({commit})");
        }

        AboutInfo.IsSingleFile.Should().BeFalse("tests run from the build output");
        AppLogging.Environment().Should().StartWith($"Stringline {AboutInfo.Version}").And.Contain("from build output");
    }

    [Fact]
    public void The_legal_notices_are_there_and_the_licence_is_embedded()
    {
        AboutInfo.Warranty.Should().Contain("ABSOLUTELY NO WARRANTY");
        AboutInfo.Copyright.Should().Contain("Frederick Duncan");
        AboutInfo.SourceUrl.Should().StartWith("https://github.com/");
        var licence = AboutInfo.LicenceText();
        licence.Should().Contain("GNU GENERAL PUBLIC LICENSE").And.Contain("Version 3, 29 June 2007");
        licence.Should().Be(File.ReadAllText(Path.Combine(Root, "LICENSE")));
    }

    [Fact]
    public void Every_shipped_package_is_credited_in_the_app_and_the_notices()
    {
        var props = XDocument.Load(Path.Combine(Root, "Directory.Packages.props"));
        var shipped = props.Descendants("ItemGroup")
            .Where(g => (string?)g.Attribute("Label") is "App" or "Libraries")
            .Descendants("PackageVersion")
            .Select(p => (string)p.Attribute("Include")!)
            .Except(NotShipped)
            .ToList();
        shipped.Should().NotBeEmpty();

        bool Covers(Credit credit, string package) => credit.Packages.Any(p => p.EndsWith('*') ? package.StartsWith(p[..^1], StringComparison.Ordinal) : package == p);
        shipped.Where(package => !AboutInfo.Credits.Any(c => Covers(c, package))).Should().BeEmpty("every package the app ships is credited");

        var notices = File.ReadAllText(Path.Combine(Root, "THIRD-PARTY-NOTICES.md"));
        foreach (var credit in AboutInfo.Credits)
        {
            notices.Should().Contain($"| {credit.Name} | {credit.Licence} | {credit.What} | {credit.Url} |", "the notices file matches the About window");
        }

        TableRow().Count(notices).Should().Be(AboutInfo.Credits.Count, "no credit is in the notices only");
    }

    [GeneratedRegex(@"^\| (?!Component|---).+ \| https?://\S+ \|$", RegexOptions.Multiline)]
    private static partial Regex TableRow();
}
