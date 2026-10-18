using System.CommandLine;

namespace Stringline.Tools;

internal static class Program
{
    public static int Main(string[] args)
    {
        var root = new RootCommand("Stringline developer tools: record, replay and inspect SimSig gateway data.");

        // Subcommands arrive in later sessions: record (S2), replay-server (S3), wtt-dump (S4),
        // corridors (S6), render-png (S7).
        return root.Parse(args).Invoke();
    }
}
