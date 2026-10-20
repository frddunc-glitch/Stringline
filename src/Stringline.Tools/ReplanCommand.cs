using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Stringline.Core;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Optimiser;
using Stringline.Planning;
using Stringline.Timetable;

namespace Stringline.Tools;

/// <summary>
/// <c>stringline replan</c>: re-plans the hour ahead of a point in a recording (S20, FC-6) with the optimiser or the
/// suggestion rules, and lists the conflicts before and after and the changes, as Plan ▸ Re-plan does in the app.
/// </summary>
internal static class ReplanCommand
{
    public static Command Create()
    {
        var wtt = new Option<FileInfo>("--wtt") { Description = "The SimSig timetable (.wtt).", Required = true };
        var log = new Option<FileInfo>("--log") { Description = "A recording (.sslog) to re-plan part way through.", Required = true };
        var profile = new Option<FileInfo?>("--profile") { Description = "A profile JSON (sections, headways, priorities). Default: generated from the WTT." };
        var at = new Option<string?>("--at") { Description = "Re-plan as at this time (HH:mm or HH:mm:ss). Default: the end of the recording." };
        var minutes = new Option<int>("--minutes") { Description = "How far ahead to re-plan, in minutes.", DefaultValueFactory = _ => 60 };
        var seconds = new Option<double>("--seconds") { Description = "How long the re-planner may think.", DefaultValueFactory = _ => 5 };
        var rules = new Option<bool>("--rules") { Description = "Use the suggestion rules, one conflict at a time, instead of the optimiser." };
        var noPlatforms = new Option<bool>("--no-platforms") { Description = "Do not move trains to other platforms." };
        var noKnockOn = new Option<bool>("--no-knock-on") { Description = "Forecast each train on its own, without knock-on delay from the others." };
        var verbose = new Option<bool>("--verbose") { Description = "Debug logging (each round of the optimiser)." };

        var command = new Command("replan", "Re-plan the conflicts ahead of a point in a recording, and list what changes.")
        {
            wtt, log, profile, at, minutes, seconds, rules, noPlatforms, noKnockOn, verbose,
        };

        command.SetAction((parse, cancellationToken) => RunAsync(
            new Settings(parse.GetValue(wtt)!, parse.GetValue(log)!, parse.GetValue(profile), parse.GetValue(at))
            {
                Minutes = parse.GetValue(minutes),
                Seconds = parse.GetValue(seconds),
                Rules = parse.GetValue(rules),
                NoPlatforms = parse.GetValue(noPlatforms),
                NoKnockOn = parse.GetValue(noKnockOn),
                Verbose = parse.GetValue(verbose),
            },
            parse.InvocationConfiguration.Output,
            parse.InvocationConfiguration.Error,
            cancellationToken));
        return command;
    }

    internal sealed record Settings(FileInfo Wtt, FileInfo Log, FileInfo? Profile, string? At)
    {
        public int Minutes { get; init; } = 60;

        public double Seconds { get; init; } = 5;

        public bool Rules { get; init; }

        public bool NoPlatforms { get; init; }

        public bool NoKnockOn { get; init; }

        public bool Verbose { get; init; }
    }

    /// <returns>0 on success, 2 on bad arguments or a file that cannot be read.</returns>
    internal static async Task<int> RunAsync(Settings settings, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        SimTime? at = null;
        if (settings.At is not null)
        {
            if (!SimTime.TryParse(settings.At, out var parsed))
            {
                await error.WriteLineAsync($"--at '{settings.At}' is not a time (HH:mm or HH:mm:ss).").ConfigureAwait(false);
                return 2;
            }

            at = parsed;
        }

        if (settings.Minutes is < 1 or > 24 * 60 || settings.Seconds is <= 0 or > 600)
        {
            await error.WriteLineAsync("--minutes must be 1–1440 and --seconds in (0, 600].").ConfigureAwait(false);
            return 2;
        }

        using var loggerFactory = ConsoleLogging.Create(settings.Verbose);
        WttImportResult import;
        StringlineProfile profile;
        SslogRecording recording;
        try
        {
            import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(settings.Wtt.FullName);
            profile = settings.Profile is null
                ? StringlineProfile.Create(import.Timetable)
                : ProfileJson.Deserialize(await File.ReadAllTextAsync(settings.Profile.FullName, cancellationToken).ConfigureAwait(false));
            recording = SslogRecording.Load(settings.Log.FullName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WttFormatException or InvalidDataException or ProfileFormatException)
        {
            await error.WriteLineAsync($"Cannot read input: {ex.Message}").ConfigureAwait(false);
            return 2;
        }

        var tt = import.Timetable;
        var state = RenderPngCommand.Play(recording, tt, at, loggerFactory, cancellationToken);
        var request = new ReplanRequest(new ForecastRequest(WorkingPlan.FromTimetable(tt))
        {
            LiveTrains = state.Trains.ToList(),
            Now = at ?? (state.Clock.HasTime ? state.Clock.LastReported : null),
            SessionStart = state.SessionStart,
            EntryDelays = state.EntryDelays,
            Network = ForecastNetwork.For(profile, tt),
            Options = new ForecastOptions { Interaction = !settings.NoKnockOn },
        })
        {
            Options = new ReplanOptions
            {
                HorizonSeconds = settings.Minutes * SimTime.SecondsPerMinute,
                TimeLimitSeconds = settings.Seconds,
                AllowPlatformChanges = !settings.NoPlatforms,
            },
        };

        IReplanner replanner = settings.Rules ? new RuleReplanner() : new CpSatReplanner(loggerFactory.CreateLogger<CpSatReplanner>());
        var result = replanner.Replan(request, cancellationToken);
        string Name(Tiploc t) => profile.DisplayName(t);
        static string Time(SimTime t) => t.ToString("H", CultureInfo.InvariantCulture);

        await output.WriteLineAsync($"Re-plan by the {result.Method}, {Time(result.From)} to {Time(result.Until)}: {Outcome(result)}").ConfigureAwait(false);
        await output.WriteLineAsync($"Conflicts before ({result.Before.Count}):").ConfigureAwait(false);
        foreach (var c in result.Before)
        {
            await output.WriteLineAsync($"  {Time(c.When)} {Name(c.Where)}: {c.Summary} ({PlanText.Duration(c.SeveritySeconds)})").ConfigureAwait(false);
        }

        if (result.Outcome == ReplanOutcome.Improved)
        {
            await output.WriteLineAsync($"Changes ({result.Steps.Count}):").ConfigureAwait(false);
            foreach (var s in result.Steps)
            {
                await output.WriteLineAsync($"  {s.Describe(Name, Time)}").ConfigureAwait(false);
            }

            await output.WriteLineAsync($"Conflicts after ({result.After.Count}):").ConfigureAwait(false);
            foreach (var c in result.After)
            {
                await output.WriteLineAsync($"  {Time(c.When)} {Name(c.Where)}: {c.Summary} ({PlanText.Duration(c.SeveritySeconds)})").ConfigureAwait(false);
            }
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Worked out in {result.Elapsed.TotalSeconds:0.0} s, {result.Rounds} round{(result.Rounds == 1 ? string.Empty : "s")}{(result.Optimal ? ", optimal for its model" : string.Empty)}.")).ConfigureAwait(false);
        return 0;
    }

    private static string Outcome(ReplanResult r) => r.Outcome switch
    {
        ReplanOutcome.Improved => string.Create(CultureInfo.InvariantCulture,
            $"{r.Holds} hold{(r.Holds == 1 ? string.Empty : "s")}, {r.PlatformChanges} platform change{(r.PlatformChanges == 1 ? string.Empty : "s")}; conflicts {r.Before.Count} → {r.After.Count}; weighted delay {PlanText.WithSign((int)Math.Round(r.WeightedDelaySeconds))}."),
        ReplanOutcome.NoConflicts => "no conflicts to clear.",
        _ => "nothing better found.",
    };
}
