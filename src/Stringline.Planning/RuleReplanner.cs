namespace Stringline.Planning;

/// <summary>
/// Re-plans a horizon with the suggestion rules (<c>06-…md</c> §4), one conflict at a time: the earliest clash between
/// two trains still in the horizon gets its best suggestion that resolves it, if that leaves the plan better, and so
/// on until none is left, nothing more helps or the time runs out. Pure, deterministic and thread-safe.
/// </summary>
/// <remarks>
/// Blocks and runs planned too fast are left to their own suggestions: they are about one train, not the order of
/// trains. Platform changes are made only if <see cref="ReplanOptions.AllowPlatformChanges"/>; cancelling never is.
/// </remarks>
public sealed class RuleReplanner : IReplanner
{
    /// <summary>Most suggestions applied in one re-plan.</summary>
    public const int MaxSteps = 40;

    public string Name => "rules";

    public ReplanResult Replan(ReplanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evaluator = new ReplanEvaluator(request);
        var options = request.Options;
        var suggestionOptions = new SuggestionOptions
        {
            StepSeconds = options.StepSeconds,
            MaxHoldSeconds = options.MaxHoldSeconds,
            PlatformCandidates = options.AllowPlatformChanges ? options.PlatformCandidates : 0,
            PlatformChangePenalty = options.PlatformChangePenalty,
            LineChangePenalty = options.LineChangePenalty,
            HorizonSeconds = options.HorizonSeconds,
        };

        var limit = TimeSpan.FromSeconds(Math.Max(0, options.TimeLimitSeconds));
        var current = evaluator.Evaluate([])!;
        var commands = new List<IPlanCommand>();
        var steps = new List<ReplanStep>();
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var rounds = 0;
        while (steps.Count < MaxSteps && evaluator.Elapsed < limit && !cancellationToken.IsCancellationRequested)
        {
            if (current.InHorizon.Where(ReplanEvaluator.IsBetweenTrains).FirstOrDefault(c => !tried.Contains(c.Id)) is not { } conflict)
            {
                break;
            }

            tried.Add(conflict.Id);
            var forecastRequest = request.Forecast with { Plan = current.Plan };
            var suggestions = Suggester.Suggest(new SuggestionRequest(forecastRequest, conflict)
            {
                Conflicts = request.Conflicts,
                Options = suggestionOptions,
                Baseline = current.Forecast,
                BaselineConflicts = current.Conflicts,
            });

            ReplanCandidate? next = null;
            Suggestion? chosen = null;
            var penalty = steps.Sum(evaluator.Penalty);
            foreach (var s in suggestions.Where(s => s.Resolves))
            {
                rounds++;
                if (evaluator.Evaluate([.. commands, .. s.Commands]) is { } candidate &&
                    candidate.BetterThan(current, penalty + evaluator.Penalty(ReplanStep.Of(s)), penalty))
                {
                    (next, chosen) = (candidate, s);
                    break;
                }
            }

            if (next is null || chosen is null)
            {
                continue;
            }

            commands.AddRange(chosen.Commands);
            steps.Add(ReplanStep.Of(chosen));
            current = next;
        }

        return evaluator.Result(Name, steps.Count > 0 ? current : null, steps, rounds);
    }
}
