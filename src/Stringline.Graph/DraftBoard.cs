using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.Graph;

/// <summary>What a draft board draws (S44): the draft's plan on one corridor, and the ghosts asked for.</summary>
/// <param name="Plan">The draft's plan (<see cref="DraftBoard.Plan"/>): its booking, forecast with no live data, and conflicts.</param>
/// <param name="Title">The board's title, such as "Draft – Waterloo 2010 1.0.3" (the corridor's name is added).</param>
public sealed record DraftBoardRequest(StringlineProfile Profile, Corridor Corridor, PlanSnapshot Plan, string Title)
{
    /// <summary>The trains to draw a ghost of the original timetable for (<see cref="DraftBoard.Changed"/>), by the draft train they became.</summary>
    public IReadOnlyList<DraftGhost> Originals { get; init; } = [];

    /// <summary>A recording's trains (matched against the draft), drawn as faint actual lines.</summary>
    public IEnumerable<LiveTrain> Actuals { get; init; } = [];

    public bool ShowConflicts { get; init; } = true;

    /// <summary>Draws the WTT lines of trains SimSig will not run by its start rules faintly; false hides them.</summary>
    public bool ShowNotRunning { get; init; } = true;

    /// <summary>How the profile's placed signals show on the axis.</summary>
    public SignalDisplay Signals { get; init; } = SignalDisplay.Ticks;
}

/// <summary>
/// A train of the original timetable whose ghost a draft board draws: <paramref name="Original"/> as it was booked,
/// and <paramref name="Draft"/>, the draft train it became, or null if the draft no longer has it.
/// </summary>
public sealed record DraftGhost(Train Original, TrainId? Draft);

/// <summary>
/// The draft board (S44, <c>08-…md</c> §7): a train graph of a timetable draft over the profile's corridors. It shows
/// the booking only, drawn at full strength (as the plan layer: the draft is the plan being made), with the booked
/// conflicts the forecaster and detector find with no live data, rules included (S32), so the draft's headway,
/// platform and single-line conflicts show as they will in play. Ghosts show the original timetable's lines of the
/// trains the draft changed (faint, as the WTT layer under a re-planned train) and a recording's actuals (faint). Pure.
/// </summary>
public static class DraftBoard
{
    /// <summary>A recording's actual lines are drawn at this share of their colour's alpha, as ghosts.</summary>
    public const double ActualGhostAlpha = 0.35;

    /// <summary>
    /// The draft as a plan with no live data: its booking, the forecast from no reports (rules and the interaction
    /// pass as in play) and the conflicts the detector finds in it. With <paramref name="original"/>, a pair is allowed
    /// as close as the original books it (so what the draft leaves alone is accepted, as a released timetable is), and a
    /// run as fast as the original books it; trains the draft retimes or adds are held to the headways, platform
    /// margins and minimum runs.
    /// </summary>
    /// <param name="original">The timetable the draft was made from; null checks the draft against its own bookings.</param>
    /// <param name="minRuns">The minimum runs (the original timetable's); null for the draft's own.</param>
    /// <param name="sequence">The snapshot's sequence, such as the draft snapshot's; it is also the plan version.</param>
    public static PlanSnapshot Plan(WorkingTimetable draft, ForecastNetwork network, WorkingTimetable? original = null, MinRunTable? minRuns = null, ForecastOptions? forecast = null, ConflictOptions? conflicts = null, long sequence = 1)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(network);
        var plan = WorkingPlan.FromTimetable(draft, minRuns);
        var forecastOptions = forecast ?? new ForecastOptions();
        var conflictOptions = conflicts ?? new ConflictOptions();
        var run = Forecaster.Run(new ForecastRequest(plan) { Network = network, Options = forecastOptions });
        var found = ConflictDetector.Detect(new ConflictRequest(plan, run) { Network = network, Options = conflictOptions, Booked = original });
        return new PlanSnapshot(
            sequence,
            draft,
            [],
            SimClockReading.None,
            PlanSnapshot.Empty.Matches,
            0,
            null,
            plan,
            PlanHistory.Empty,
            sequence,
            Forecast: run,
            Conflicts: found,
            Settings: new ForecastSettings(network, forecastOptions, conflictOptions));
    }

    /// <summary>One corridor's scene of the draft.</summary>
    public static GraphScene Build(DraftBoardRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (profile, corridor, snapshot) = (request.Profile, request.Corridor, request.Plan);
        var actuals = request.Actuals.ToList();
        var scene = GraphSceneBuilder.Build(new GraphSceneRequest(profile, corridor)
        {
            Timetable = snapshot.Timetable,
            ShowWtt = false,
            ShowPlan = false,
            ShowForecast = false,
            ShowLiveHead = false,
            ShowDisruptions = false,
            LiveTrains = actuals,
            ShowActual = actuals.Count > 0,
            Conflicts = snapshot.Conflicts,
            ShowConflicts = request.ShowConflicts,
            Signals = request.Signals,
        });

        var paths = new List<GraphPath>();

        // The original timetable's lines under the draft's (the WTT layer), for the trains the draft changed.
        foreach (var ghost in request.Originals)
        {
            var style = profile.StyleFor(ghost.Original.CategoryId);
            foreach (var path in GraphSceneBuilder.PlanPaths(corridor, TrainPlan.FromWtt(ghost.Original), style.Colour, "orig", style.Style))
            {
                paths.Add(path with { Layer = GraphLayer.Wtt, Train = ghost.Draft, Points = null });
            }
        }

        // The draft's booking, at full strength.
        foreach (var train in snapshot.Plan.Trains)
        {
            var notRunning = snapshot.Forecast.Find(train.Id)?.Status == TrainForecastStatus.NotRunning;
            if (notRunning && !request.ShowNotRunning)
            {
                continue;
            }

            var style = profile.StyleFor(train.CategoryId);
            var colour = notRunning ? GraphSceneBuilder.NotRunningColour(style.Colour) : style.Colour;
            paths.AddRange(GraphSceneBuilder.PlanPaths(corridor, train, colour, "draft", style.Style));
        }

        // A recording's actuals, faint.
        foreach (var path in scene.Paths.Where(p => p.Layer == GraphLayer.Actual))
        {
            paths.Add(path with
            {
                Colour = Faint(path.Colour),
                SegmentColours = path.SegmentColours?.Select(Faint).ToList(),
            });
        }

        var title = $"{request.Title} — {corridor.Name}";
        return scene with { Title = title, Paths = paths };

        static Colour Faint(Colour c) => c with { A = (byte)Math.Round(c.A * ActualGhostAlpha) };
    }

    /// <summary>
    /// The trains of <paramref name="original"/> that the draft books differently (other points or times) or no longer
    /// has, each with the draft train it became. Trains are paired by <see cref="TrainId"/>, which the import makes from
    /// the UID, else the headcode, in file order; a train renumbered without a UID is taken as a new one, and its
    /// original as gone.
    /// </summary>
    public static IReadOnlyList<DraftGhost> Changed(WorkingTimetable original, WorkingTimetable draft)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(draft);
        var changed = new List<DraftGhost>();
        foreach (var train in original.Trains)
        {
            if (draft.Find(train.Id) is not { } now)
            {
                changed.Add(new DraftGhost(train, null));
            }
            else if (!SameBooking(train, now))
            {
                changed.Add(new DraftGhost(train, now.Id));
            }
        }

        return changed;
    }

    /// <summary>
    /// The trains no corridor of <paramref name="corridors"/> draws (none has two of their timing points), in the
    /// timetable's order: the board's status line lists them.
    /// </summary>
    public static IReadOnlyList<Train> OffCorridors(IEnumerable<Corridor> corridors, WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(corridors);
        ArgumentNullException.ThrowIfNull(timetable);
        var sets = corridors.Select(c => c.Locations.Select(l => l.Tiploc).ToHashSet()).ToList();
        return [.. timetable.Trains.Where(t => !sets.Any(set => t.Points.Count(p => set.Contains(p.Location)) >= 2))];
    }

    private static bool SameBooking(Train a, Train b) =>
        a.EntryTime == b.EntryTime && a.Points.Count == b.Points.Count &&
        a.Points.Zip(b.Points).All(p => p.First.Location == p.Second.Location && p.First.Arrive == p.Second.Arrive && p.First.Depart == p.Second.Depart && p.First.IsPass == p.Second.IsPass);
}
