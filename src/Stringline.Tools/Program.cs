using System.CommandLine;

namespace Stringline.Tools;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("Stringline developer tools: record, replay and inspect SimSig gateway data.");
        root.Subcommands.Add(RecordCommand.Create());

        // Subcommands still to come: replay-server (S3), wtt-dump (S4), corridors (S6), render-png (S7).
        return root.Parse(args).InvokeAsync();
    }
}
