using System.CommandLine;

namespace Stringline.Tools;

internal static class Program
{
    public static Task<int> Main(string[] args)
    {
        var root = new RootCommand("Stringline developer tools: record, replay and inspect SimSig gateway data.");
        root.Subcommands.Add(RecordCommand.Create());
        root.Subcommands.Add(ReplayServerCommand.Create());
        root.Subcommands.Add(WttDumpCommand.Create());
        root.Subcommands.Add(MatchCommand.Create());
        root.Subcommands.Add(CorridorsCommand.Create());
        root.Subcommands.Add(RenderPngCommand.Create());
        root.Subcommands.Add(ReportCommand.Create());
        root.Subcommands.Add(ReplanCommand.Create());
        root.Subcommands.Add(GapsCommand.Create());
        root.Subcommands.Add(LearnCommand.Create());
        root.Subcommands.Add(IconCommand.Create());

        return root.Parse(args).InvokeAsync();
    }
}
