using System.Globalization;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Live;

/// <summary>Tuning for <see cref="TrainMatcher"/>. The defaults follow <c>04-…md</c> §3.</summary>
public sealed record MatcherOptions
{
    /// <summary>A candidate is accepted only if its booked times are within this many seconds of the actuals (60 min).</summary>
    public int WindowSeconds { get; init; } = 3600;

    /// <summary>With several candidates, the best must beat the next by this many seconds to be chosen straight away.</summary>
    public int MarginSeconds { get; init; } = 60;

    /// <summary>After this many timing points with no clear winner, the best candidate is taken anyway (ties go to file order).</summary>
    public int DecideAfterTimingPoints { get; init; } = 3;

    /// <summary>
    /// Added to a candidate's score for each reported timing point that is not on its path. Small, because SimSig reports
    /// every timing location a train passes, including ones its own schedule leaves out: it only tells apart alternative
    /// paths when one of them has the point and another does not.
    /// </summary>
    public int OffPathPenaltySeconds { get; init; } = 120;

    /// <summary>
    /// Added to a seeded candidate for a train first seen later in the session, and to an unseeded one for a train
    /// that was there from the start when a seeded alternative exists. Timetables such as Penrith have a seeded and an
    /// unseeded version of the same train, booked seconds apart, for different start times.
    /// </summary>
    public int SeedMismatchPenaltySeconds { get; init; } = 600;
}

/// <summary>What the matcher knows about one live working.</summary>
/// <param name="Actuals">The working's actuals so far, in order (timing points and signals).</param>
/// <param name="Taken">WTT trains already matched to other workings; they are not candidates.</param>
/// <param name="PresentAtStart">True if the live train was first seen near the session start.</param>
/// <param name="PreviousTrain">The WTT train the same uid was matched to under its previous headcode, if any.</param>
public sealed record MatchRequest(
    Headcode Headcode,
    IReadOnlyList<ActualEvent> Actuals,
    IReadOnlySet<TrainId> Taken,
    bool PresentAtStart = false,
    TrainId? PreviousTrain = null);

/// <summary>How one candidate scored.</summary>
/// <param name="DeviationSeconds">Mean |actual − booked| over the timing points it shares with the actuals, or the
/// distance of the first actual from the train's booked span if it shares none.</param>
/// <param name="TimingPoints">How many reported timing points were on its path and timed.</param>
/// <param name="OffPath">How many reported timing points were not on its path.</param>
/// <param name="Score">Deviation plus penalties; lower is better.</param>
/// <param name="Untimed">The train has no booked times at all (#21): its deviation means nothing, and it is ranked by penalties alone.</param>
public sealed record CandidateScore(TrainId Train, int DeviationSeconds, int TimingPoints, int OffPath, int Score, bool Untimed = false);

/// <summary>The matcher's answer for one working.</summary>
public sealed record MatchDecision(
    MatchStatus Status,
    TrainId? Train,
    MatchMethod Method,
    int? DeviationSeconds,
    string? Reason,
    IReadOnlyList<CandidateScore> Candidates)
{
    public static MatchDecision Unmatched(string reason, IReadOnlyList<CandidateScore>? candidates = null) =>
        new(MatchStatus.Unmatched, null, MatchMethod.None, null, reason, candidates ?? []);

    public static MatchDecision Pending(string reason, IReadOnlyList<CandidateScore> candidates) =>
        new(MatchStatus.Pending, null, MatchMethod.None, null, reason, candidates);
}

/// <summary>
/// Matches a live working (sim uid + headcode) to a WTT train, per <c>04-…md</c> §3. Pure: the answer depends only on
/// the timetable, the options and the request.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Same uid, new headcode: if the train it was matched to forms a train with that headcode (next working first,
/// then divide/detach/drop), take it.</item>
/// <item>Otherwise the candidates are the WTT trains with exactly this headcode (suffixes such as <c>-1</c> included)
/// that are not taken. Each is scored by the mean |actual − booked| at the timing points reported so far, plus
/// penalties for points off its path and for seeded/unseeded mismatches. Before any timing point is reported (the
/// train has passed only signals), the score is how far the first actual lies outside the train's booked span.</item>
/// <item>Candidates whose deviation exceeds the window are dropped. One left: matched (even on signals alone). Several:
/// matched once the best leads by the margin, or after a few timing points; until then pending.</item>
/// <item>None within the window, but a WTT train with the headcode has no booked times at all (#21, a train the
/// signaller brings on when wanted): the untimed one with the fewest penalties is taken, as with timed candidates
/// (ties wait for a timing point, then a few, then go to file order).</item>
/// </list>
/// </remarks>
public sealed class TrainMatcher
{
    private readonly WorkingTimetable _timetable;
    private readonly HashSet<Tiploc> _timingPoints;

    public TrainMatcher(WorkingTimetable timetable, MatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(timetable);
        _timetable = timetable;
        Options = options ?? new MatcherOptions();
        _timingPoints = timetable.Trains.SelectMany(t => t.Points).Select(p => p.Location).ToHashSet();
    }

    public MatcherOptions Options { get; }

    public WorkingTimetable Timetable => _timetable;

    /// <summary>True if any WTT train has a timing point here. Gateway locations that are not are signal IDs.</summary>
    public bool IsTimingPoint(Tiploc location) => _timingPoints.Contains(location);

    public MatchDecision Match(MatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headcode.IsEmpty)
        {
            return MatchDecision.Unmatched("No headcode");
        }

        if (request.PreviousTrain is { } previous && FollowOn(previous, request) is { } formed)
        {
            return formed;
        }

        var all = _timetable.WithHeadcode(request.Headcode).ToList();
        if (all.Count == 0)
        {
            return MatchDecision.Unmatched($"No train {request.Headcode} in the timetable");
        }

        var free = all.Where(t => !request.Taken.Contains(t.Id)).ToList();
        if (free.Count == 0)
        {
            return MatchDecision.Unmatched($"Every {request.Headcode} in the timetable is already matched");
        }

        if (request.Actuals.Count == 0)
        {
            return MatchDecision.Pending("No movements yet", []);
        }

        var anySeeded = free.Any(t => t.Start == TrainStart.Seeded);
        var scores = free
            .Select(t => Score(t, request, anySeeded))
            .OrderBy(s => s.Score)
            .ToList();                                 // OrderBy is stable, so ties stay in file order.
        var inWindow = scores.Where(s => !s.Untimed && s.DeviationSeconds <= Options.WindowSeconds).ToList();
        if (inWindow.Count == 0 && scores.Any(s => s.Untimed))
        {
            return MatchUntimed(scores, request);
        }

        if (inWindow.Count == 0)
        {
            return MatchDecision.Unmatched(
                string.Create(CultureInfo.InvariantCulture, $"No {request.Headcode} booked within {Options.WindowSeconds / 60} min (nearest {scores[0].DeviationSeconds / 60} min off)"),
                scores);
        }

        var best = inWindow[0];
        if (inWindow.Count == 1)
        {
            return Matched(best, request, scores, note: null);
        }

        if (best.TimingPoints == 0)
        {
            return MatchDecision.Pending($"{inWindow.Count} candidates; waiting for a timing point", scores);
        }

        var margin = inWindow[1].Score - best.Score;
        if (margin >= Options.MarginSeconds)
        {
            return Matched(best, request, scores, note: null);
        }

        if (best.TimingPoints >= Options.DecideAfterTimingPoints)
        {
            return Matched(best, request, scores, note: string.Create(CultureInfo.InvariantCulture, $"Close call: {inWindow[1].Train} scored within {margin} s"));
        }

        return MatchDecision.Pending(string.Create(CultureInfo.InvariantCulture, $"{inWindow.Count} candidates within {Options.MarginSeconds} s of each other"), scores);
    }

    /// <summary>
    /// Step 4: the untimed candidates, by penalties (off-path points, seeding). One, or a clear best: matched. Several
    /// alike: pending until a timing point, and taken in file order after <see cref="MatcherOptions.DecideAfterTimingPoints"/>.
    /// </summary>
    private MatchDecision MatchUntimed(IReadOnlyList<CandidateScore> scores, MatchRequest request)
    {
        var untimed = scores.Where(s => s.Untimed).ToList();          // Already in score order, ties in file order.
        var best = untimed[0];
        var timingPoints = request.Actuals.Count(a => a.AtTimingPoint);
        if (untimed.Count == 1 || untimed[1].Score - best.Score >= Options.MarginSeconds || timingPoints >= Options.DecideAfterTimingPoints)
        {
            var note = untimed.Count > 1 && untimed[1].Score - best.Score < Options.MarginSeconds
                ? string.Create(CultureInfo.InvariantCulture, $"Close call: {untimed[1].Train} is untimed too")
                : null;
            return new MatchDecision(MatchStatus.Matched, best.Train, MatchMethod.Untimed, null, note, scores);
        }

        return MatchDecision.Pending(
            timingPoints == 0
                ? string.Create(CultureInfo.InvariantCulture, $"{untimed.Count} untimed candidates; waiting for a timing point")
                : string.Create(CultureInfo.InvariantCulture, $"{untimed.Count} untimed candidates alike"),
            scores);
    }

    /// <summary>Step 1: the same uid under a new headcode continues as a train its previous train forms.</summary>
    private MatchDecision? FollowOn(TrainId previous, MatchRequest request)
    {
        var train = _timetable.Find(previous);
        if (train is null)
        {
            return null;
        }

        var formed = train.Points
            .SelectMany(p => p.Activities)
            .Where(a => a.FormsAssociatedTrain && a.Train is { } id && !request.Taken.Contains(id) && _timetable.Find(id)?.Headcode == request.Headcode)
            .OrderBy(a => a.Kind == ActivityKind.Next ? 0 : 1)
            .FirstOrDefault();
        if (formed?.Train is not { } next)
        {
            return null;
        }

        var method = formed.Kind == ActivityKind.Next ? MatchMethod.NextWorking : MatchMethod.Formed;
        return new MatchDecision(MatchStatus.Matched, next, method, null, null, []);
    }

    private MatchDecision Matched(CandidateScore best, MatchRequest request, IReadOnlyList<CandidateScore> scores, string? note)
    {
        var train = _timetable.Get(best.Train);
        var method = train.FormedFrom is { } parent && request.Taken.Contains(parent) ? MatchMethod.Formed : MatchMethod.HeadcodeAndTime;
        return new MatchDecision(MatchStatus.Matched, best.Train, method, best.TimingPoints > 0 ? best.DeviationSeconds : null, note, scores);
    }

    private CandidateScore Score(Train train, MatchRequest request, bool anySeeded)
    {
        long total = 0;
        var timed = 0;
        var offPath = 0;
        foreach (var actual in request.Actuals)
        {
            if (!actual.AtTimingPoint)
            {
                continue;
            }

            if (Deviation(train, actual) is { } deviationHere)
            {
                total += deviationHere;
                timed++;
            }
            else if (!train.Points.Any(p => p.Location == actual.Location))
            {
                offPath++;
            }
        }

        var untimed = train.EntryTime is null && train.Points.All(p => p.Arrive is null && p.Depart is null);
        var deviation = untimed ? 0 : timed > 0 ? (int)(total / timed) : SpanDistance(train, request.Actuals[0].Time);
        var penalty = offPath * Options.OffPathPenaltySeconds;
        if (train.Start == TrainStart.Seeded ? !request.PresentAtStart : request.PresentAtStart && anySeeded)
        {
            penalty += Options.SeedMismatchPenaltySeconds;
        }

        return new CandidateScore(train.Id, deviation, timed, offPath, deviation + penalty, untimed);
    }

    /// <summary>
    /// |actual − booked| at this actual's location, for the action, at the call nearest in time (a train may call twice);
    /// null if the location is not a timed point of the train. An arrival where only a departure is booked (an origin)
    /// is waiting, so arriving early there costs nothing: trains already in the sim at the start report <c>arrive</c>
    /// at their origin long before they are due away.
    /// </summary>
    private static int? Deviation(Train train, ActualEvent actual)
    {
        int? best = null;
        foreach (var point in train.Points)
        {
            if (point.Location != actual.Location)
            {
                continue;
            }

            int? deviation;
            if (actual.Action == TrainLocationAction.Arrive && point.Arrive is null)
            {
                deviation = point.Depart is { } departOnly ? Math.Max(0, actual.Time - departOnly) : null;
            }
            else
            {
                var booked = actual.Action == TrainLocationAction.Arrive ? point.Arrive : point.Depart ?? point.Arrive;
                deviation = booked is { } time ? Math.Abs(actual.Time - time) : null;
            }

            if (deviation < (best ?? int.MaxValue))
            {
                best = deviation;
            }
        }

        return best;
    }

    /// <summary>How far <paramref name="time"/> lies outside the train's booked span (entry or first time to last time); 0 inside.</summary>
    private static int SpanDistance(Train train, SimTime time)
    {
        var first = train.EntryTime ?? train.FirstTime;
        var last = train.LastTime ?? first;
        if (first is not { } start || last is not { } end)
        {
            return int.MaxValue / 2;
        }

        return time < start ? start - time : time > end ? time - end : 0;
    }
}
