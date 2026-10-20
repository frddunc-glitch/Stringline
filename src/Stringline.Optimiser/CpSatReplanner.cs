using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Planning;

namespace Stringline.Optimiser;

/// <summary>
/// Re-plans a horizon with Google OR-Tools CP-SAT (<c>06-…md</c> §5, FC-6): every clash between trains in the next
/// hour (by default) at once, by choosing their order and where trains are held, and (if allowed) moving trains to
/// other platforms. Thread-safe: each call builds its own model.
/// </summary>
/// <remarks>
/// <para>The model (<see cref="ReplanModel"/>) starts with the plan's conflicts and their trains' neighbours. Each
/// round solves it, turns the solution into plan commands (holds to the next half minute, platform changes), and
/// checks them with the forecaster and conflict detector, which have the last word. Clashes still found are added to
/// the model (or given more spacing) and it is solved again, until none is left in the horizon, nothing changes, the
/// rounds run out or the time limit is reached. The best plan checked is the result.</para>
/// <para>Blocks and runs planned too fast are left to their own suggestions.</para>
/// </remarks>
public sealed partial class CpSatReplanner(ILogger<CpSatReplanner>? logger = null) : IReplanner
{
    private readonly ILogger _logger = logger ?? NullLogger<CpSatReplanner>.Instance;

    /// <summary>Time kept back from the solver for checking its plans.</summary>
    private static readonly TimeSpan CheckReserve = TimeSpan.FromMilliseconds(250);

    /// <summary>The part of the time limit for solving; the rest is for dropping steps that do not help.</summary>
    private const double SolveShare = 0.8;

    public string Name => "optimiser";

    public ReplanResult Replan(ReplanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var evaluator = new ReplanEvaluator(request);
        if (!evaluator.Before.Any(ReplanEvaluator.IsBetweenTrains))
        {
            return evaluator.Result(Name, null, [], 0);
        }

        var options = request.Options;
        var limit = TimeSpan.FromSeconds(Math.Max(0.1, options.TimeLimitSeconds));
        var model = new ReplanModel(evaluator);
        model.Add(evaluator.Before, evaluator.Plan, evaluator.Baseline.Independent);

        ReplanCandidate? best = null;
        List<ReplanStep> bestSteps = [];
        List<IPlanCommand[]> bestByStep = [];
        var bestPenalty = 0.0;
        var optimal = false;
        var rounds = 0;
        while (rounds < Math.Max(1, options.MaxRounds) && !cancellationToken.IsCancellationRequested)
        {
            var left = (limit * SolveShare) - evaluator.Elapsed - CheckReserve;
            if (left <= TimeSpan.Zero && rounds > 0)
            {
                break;
            }

            // Later rounds share what is left, so the first is not starved of time.
            var share = rounds == 0 ? left / 2 : left / Math.Max(1, Math.Min(2, options.MaxRounds - rounds));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (model.Solve(share > TimeSpan.Zero ? share : TimeSpan.FromMilliseconds(50), cancellationToken) is not { } solution)
            {
                LogNoSolution(rounds + 1, model.TrainCount, model.DisjunctionCount, watch.ElapsedMilliseconds, model.LastStatus);
                break;
            }

            rounds++;
            var (commands, steps, byStep) = model.Commands(solution);
            if (evaluator.Evaluate(commands) is not { } candidate)
            {
                LogNotApplied(rounds, commands.Count);
                break;
            }

            var penalty = steps.Sum(evaluator.Penalty);
            if (commands.Count > 0 && candidate.BetterThan(best, penalty, bestPenalty))
            {
                (best, bestSteps, bestByStep, bestPenalty, optimal) = (candidate, steps, byStep, penalty, solution.Optimal);
            }

            var remaining = candidate.InHorizon.Where(ReplanEvaluator.IsBetweenTrains).ToList();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var delay = Math.Round(candidate.WeightedDelay);
                LogRound(rounds, model.TrainCount, model.DisjunctionCount, solution.Optimal ? "optimal" : "feasible", watch.ElapsedMilliseconds, steps.Count, solution.Clashes.Count, candidate.InHorizon.Count, delay);
            }

            if (remaining.Count == 0 || !model.Add(remaining, candidate.Plan, candidate.Forecast.Independent))
            {
                break;
            }
        }

        if (best is not null)
        {
            (best, bestSteps) = Prune(evaluator, best, bestSteps, bestByStep, limit, cancellationToken);
        }

        return evaluator.Result(Name, best, bestSteps, rounds, optimal);
    }

    /// <summary>
    /// Drops the steps that do not help, smallest first, while there is time: a solution found within the time
    /// limit, or rounded to the half minute, can hold trains for nothing. A step goes if the plan without it is no
    /// worse by the forecast (as many conflicts in the horizon, no more severe, no more delay).
    /// </summary>
    private static (ReplanCandidate, List<ReplanStep>) Prune(ReplanEvaluator evaluator, ReplanCandidate best, List<ReplanStep> steps, List<IPlanCommand[]> byStep, TimeSpan limit, CancellationToken cancellationToken)
    {
        var keep = Enumerable.Range(0, steps.Count).ToList();
        var order = keep.OrderBy(i => steps[i].Kind == SuggestionKind.Platform ? 1 : 0).ThenBy(i => steps[i].HoldSeconds).ToList();
        foreach (var i in order)
        {
            if (evaluator.Elapsed >= limit || cancellationToken.IsCancellationRequested || keep.Count <= 1)
            {
                break;
            }

            var trial = keep.Where(j => j != i).ToList();
            if (evaluator.Evaluate([.. trial.SelectMany(j => byStep[j])]) is { } candidate &&
                !best.BetterThan(candidate, keep.Sum(j => evaluator.Penalty(steps[j])), trial.Sum(j => evaluator.Penalty(steps[j]))))
            {
                (best, keep) = (candidate, trial);
            }
        }

        return (best, keep.Select(j => steps[j]).ToList());
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Re-plan round {Round}: {Trains} trains, {Pairs} pairs, {Status} in {Milliseconds} ms; {Steps} steps, {Clashes} unavoidable in the model, {Conflicts} conflicts left, weighted delay {Delay} s")]
    private partial void LogRound(int round, int trains, int pairs, string status, long milliseconds, int steps, int clashes, int conflicts, double delay);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Re-plan round {Round}: no solution ({Trains} trains, {Pairs} pairs) in {Milliseconds} ms: {Status}")]
    private partial void LogNoSolution(int round, int trains, int pairs, long milliseconds, string status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Re-plan round {Round}: its {Commands} commands did not apply to the plan")]
    private partial void LogNotApplied(int round, int commands);
}
