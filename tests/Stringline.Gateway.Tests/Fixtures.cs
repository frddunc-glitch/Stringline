namespace Stringline.Gateway.Tests;

/// <summary>
/// Paths to recorded fixtures. Free-sim recordings are in <c>tests/fixtures</c> (in git); payware ones are in the
/// git-ignored <c>local-fixtures</c> and may be absent, so tests using them pass trivially without them.
/// </summary>
internal static class Fixtures
{
    /// <summary>Waterloo at 1×, all four topics including TD and the ttsummary reply. 04:30–05:52 sim.</summary>
    public const string Waterloo1x = "waterloo-1x.sslog";

    /// <summary>Waterloo at 4× then 16×, movements and clock only. 05:52–08:05 sim.</summary>
    public const string Waterloo4x = "waterloo-4x.sslog";

    /// <summary>Waterloo with a track circuit failure on T1620 (05:22:11–05:54:35 sim), with TD. Mostly flat-out speed.</summary>
    public const string WaterlooFailure = "waterloo-failure.sslog";

    private static readonly Lazy<string> Root = new(FindRoot);

    public static string Path(string name) => System.IO.Path.Combine(Root.Value, "tests", "fixtures", name);

    /// <summary>A payware fixture's path, or null if it is not on this machine.</summary>
    public static string? LocalPath(string name)
    {
        var path = System.IO.Path.Combine(Root.Value, "local-fixtures", name);
        return File.Exists(path) ? path : null;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "Stringline.sln")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not find the repository root (Stringline.sln) above " + AppContext.BaseDirectory);
    }
}
