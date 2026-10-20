using System.IO;
using System.Reflection;

namespace Stringline.App.Services;

/// <summary>A component Stringline is built with, credited in the About window and <c>THIRD-PARTY-NOTICES.md</c>.</summary>
/// <param name="Packages">The NuGet package IDs (or ID prefixes ending in <c>*</c>) it covers, so a test can check every package is credited.</param>
internal sealed record Credit(string Name, string Licence, string Url, string What, params string[] Packages);

/// <summary>
/// What the About window shows (S18): the version, the commit it was built from, and the notices GPLv3 §5(d) asks an
/// interactive program to show — the copyright, that there is no warranty, the licence and where the source is.
/// </summary>
internal static class AboutInfo
{
    public const string Product = "Stringline";

    public const string Copyright = "Copyright (C) 2026 Frederick Duncan and Stringline contributors";

    public const string SourceUrl = "https://github.com/frddunc-glitch/Stringline";

    public const string LicenceName = "GNU General Public Licence, version 3 or (at your option) any later version";

    public const string LicenceUrl = "https://www.gnu.org/licenses/gpl-3.0.html";

    public const string Warranty =
        "This program comes with ABSOLUTELY NO WARRANTY. It is free software: you can redistribute it and/or modify it " +
        "under the terms of the GNU General Public Licence as published by the Free Software Foundation, either version 3 " +
        "of the Licence, or (at your option) any later version.";

    public const string Disclaimer =
        "Stringline is an independent project. It is not affiliated with, endorsed by or supported by SimSig or its " +
        "developers. SimSig is a trademark of its respective owners.";

    private const string LicenceResource = "Stringline.LICENSE.txt";

    private static readonly Assembly App = typeof(AboutInfo).Assembly;

    private static readonly string Informational =
        App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The version, e.g. <c>0.1.0</c>.</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>The first 7 characters of the commit built from, if the build knew it.</summary>
    public static string? Commit { get; } = Informational.Split('+') is [_, { Length: >= 7 } sha, ..] ? sha[..7] : null;

    /// <summary>"0.1.0 (abc1234)", or just the version.</summary>
    public static string VersionText => Commit is null ? Version : $"{Version} ({Commit})";

    /// <summary>True when running as a published single-file app (no assembly file on disk).</summary>
#pragma warning disable IL3000 // An empty location is exactly what this looks for.
    public static bool IsSingleFile => string.IsNullOrEmpty(App.Location);
#pragma warning restore IL3000

    public static IReadOnlyList<Credit> Credits { get; } =
    [
        new(".NET, WPF and Microsoft.Extensions", "MIT", "https://github.com/dotnet", "runtime, windowing, hosting and logging", "Microsoft.Extensions.*"),
        new("SkiaSharp", "MIT (Skia: BSD-3-Clause)", "https://github.com/mono/SkiaSharp", "drawing the graph, PNG and PDF export", "SkiaSharp*"),
        new("CommunityToolkit.Mvvm", "MIT", "https://github.com/CommunityToolkit/dotnet", "view models", "CommunityToolkit.Mvvm"),
        new("Serilog", "Apache-2.0", "https://serilog.net", "log files", "Serilog*"),
        new("Microsoft.Data.Sqlite and SQLitePCLRaw", "MIT and Apache-2.0", "https://github.com/dotnet/efcore", "saved sessions", "Microsoft.Data.Sqlite"),
        new("SQLite", "Public domain", "https://sqlite.org", "saved sessions"),
        new("Markdig", "BSD-2-Clause", "https://github.com/xoofx/markdig", "showing the user guide", "Markdig"),
        new("Google OR-Tools", "Apache-2.0", "https://github.com/google/or-tools", "the re-planning optimiser", "Google.OrTools"),
        new("Protocol Buffers, Abseil, RE2, SCIP, HiGHS, zlib and bzip2", "BSD-3-Clause, Apache-2.0, MIT, zlib and bzip2", "https://github.com/google/or-tools/blob/stable/LICENSE", "inside OR-Tools", "Google.Protobuf"),
        new("Noto Sans", "SIL Open Font Licence 1.1", "https://notofonts.github.io", "the graph's labels"),
    ];

    /// <summary>The full text of the GPL, as shipped (embedded, so it is there in a single-file build).</summary>
    public static string LicenceText()
    {
        using var stream = App.GetManifestResourceStream(LicenceResource)
            ?? throw new InvalidOperationException($"The licence resource {LicenceResource} is missing from the build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
