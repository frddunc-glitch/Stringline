using System.Collections.Immutable;
using System.Globalization;
using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>
/// The mutable side of a <see cref="WorkingPlan"/>: commands change trains, disruptions and links through it, and it
/// validates every train change (<c>06-…md</c> §1). A change that makes a run faster than the minimum run time
/// (lengthened by any speed restriction) or a departure before the arrival is clamped, with a warning, rather than
/// refused. Only runs and dwells the change shortens are checked, so a WTT that is already tight elsewhere is left as
/// booked; a clamp that moves a point later is checked again at the next point, so it can push the rest of the train on.
/// </summary>
public sealed class WorkingPlanBuilder
{
    private readonly WorkingPlan _start;
    private ImmutableDictionary<TrainId, TrainPlan> _trains;
    private ImmutableList<TrainId> _order;
    private ImmutableList<Disruption> _disruptions;
    private ImmutableList<PlanNote> _notes;
    private ImmutableList<LiveLink> _links;
    private List<PlanWarning> _warnings = [];

    internal WorkingPlanBuilder(WorkingPlan plan)
    {
        _start = plan;
        _trains = plan.TrainMap;
        _order = plan.Order;
        _disruptions = plan.DisruptionList;
        _notes = plan.NoteList;
        _links = plan.LinkList;
    }

    public WorkingTimetable Timetable => _start.Timetable;

    public MinRunTable MinRuns => _start.MinRuns;

    public IReadOnlyList<Disruption> Disruptions => _disruptions;

    public IReadOnlyList<PlanNote> Notes => _notes;

    public IReadOnlyList<LiveLink> Links => _links;

    public TrainPlan? Find(TrainId id) => _trains.GetValueOrDefault(id);

    public Disruption? FindDisruption(string id) => _disruptions.Find(d => d.Id == id);

    public PlanNote? FindNote(string id) => _notes.Find(n => n.Id == id);

    /// <summary>
    /// Replaces a train's plan, clamping it to the minimum run times and dwells. Returns an error, and changes
    /// nothing, if the train is not in the plan or the change makes no difference.
    /// </summary>
    public string? Update(TrainPlan updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (!_trains.TryGetValue(updated.Id, out var before))
        {
            return $"No train {updated.Id} in the plan.";
        }

        if (updated.Points.Count != before.Points.Count)
        {
            throw new ArgumentException("A re-plan must keep every point of the train.", nameof(updated));
        }

        if (updated.SameAs(before))
        {
            return "This makes no change to the plan.";
        }

        var clamped = Clamp(before, updated);
        var modified = clamped.Origin == PlanOrigin.Clone
            || Timetable.Find(clamped.Id) is not { } wtt
            || !clamped.SameAs(TrainPlan.FromWtt(wtt));
        _trains = _trains.SetItem(clamped.Id, clamped with { IsModified = modified });
        return null;
    }

    /// <summary>Adds a new train (a clone) after all the others. Returns an error if its ID is taken.</summary>
    public string? Add(TrainPlan train)
    {
        ArgumentNullException.ThrowIfNull(train);
        if (_trains.ContainsKey(train.Id))
        {
            return $"The train ID {train.Id} is already used.";
        }

        _trains = _trains.Add(train.Id, train with { IsModified = true });
        _order = _order.Add(train.Id);
        return null;
    }

    public string? AddDisruption(Disruption disruption)
    {
        ArgumentNullException.ThrowIfNull(disruption);
        if (disruption.Problem() is { } problem)
        {
            return problem;
        }

        if (FindDisruption(disruption.Id) is not null)
        {
            return $"There is already a disruption {disruption.Id}.";
        }

        _disruptions = _disruptions.Add(disruption);
        return null;
    }

    public string? ReplaceDisruption(Disruption disruption)
    {
        ArgumentNullException.ThrowIfNull(disruption);
        if (disruption.Problem() is { } problem)
        {
            return problem;
        }

        var index = _disruptions.FindIndex(d => d.Id == disruption.Id);
        if (index < 0)
        {
            return $"No disruption {disruption.Id}.";
        }

        if (_disruptions[index] == disruption)
        {
            return "This makes no change to the disruption.";
        }

        _disruptions = _disruptions.SetItem(index, disruption);
        return null;
    }

    public string? RemoveDisruption(string id)
    {
        var index = _disruptions.FindIndex(d => d.Id == id);
        if (index < 0)
        {
            return $"No disruption {id}.";
        }

        _disruptions = _disruptions.RemoveAt(index);
        return null;
    }

    public string? AddNote(PlanNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Problem() is { } problem)
        {
            return problem;
        }

        if (FindNote(note.Id) is not null)
        {
            return $"There is already a note {note.Id}.";
        }

        _notes = _notes.Add(note);
        return null;
    }

    public string? ReplaceNote(PlanNote note)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (note.Problem() is { } problem)
        {
            return problem;
        }

        var index = _notes.FindIndex(n => n.Id == note.Id);
        if (index < 0)
        {
            return $"No note {note.Id}.";
        }

        if (_notes[index] == note)
        {
            return "This makes no change to the note.";
        }

        _notes = _notes.SetItem(index, note);
        return null;
    }

    public string? RemoveNote(string id)
    {
        var index = _notes.FindIndex(n => n.Id == id);
        if (index < 0)
        {
            return $"No note {id}.";
        }

        _notes = _notes.RemoveAt(index);
        return null;
    }

    /// <summary>Adds or replaces the manual link of a live working. The train, if any, must be in the WTT.</summary>
    public string? SetLink(LiveLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrEmpty(link.SimUid))
        {
            return "A live link needs a sim uid.";
        }

        if (link.Train is { } train && Timetable.Find(train) is null)
        {
            return $"Only WTT trains can be linked; {train} is not in the timetable.";
        }

        var index = _links.FindIndex(l => l.SimUid == link.SimUid && l.Headcode == link.Headcode);
        if (index >= 0 && _links[index] == link)
        {
            return "That working is already linked so.";
        }

        _links = (index >= 0 ? _links.RemoveAt(index) : _links).Add(link);
        return null;
    }

    public string? RemoveLink(string simUid, Headcode headcode)
    {
        var index = _links.FindIndex(l => l.SimUid == simUid && l.Headcode == headcode);
        if (index < 0)
        {
            return $"{headcode} (uid {simUid}) has no manual link.";
        }

        _links = _links.RemoveAt(index);
        return null;
    }

    /// <summary>Adds a warning to the current command's result.</summary>
    public void Warn(PlanWarning warning)
    {
        ArgumentNullException.ThrowIfNull(warning);
        _warnings.Add(warning);
    }

    public WorkingPlan Build() => new(_start.Timetable, _start.MinRuns, _trains, _order, _disruptions, _notes, _links);

    /// <summary>
    /// Runs a command: on success its warnings are returned with it; on failure (an error, or an exception from a
    /// faulty command) every change it made is undone.
    /// </summary>
    internal PlanCommandResult Run(IPlanCommand command)
    {
        var saved = (_trains, _order, _disruptions, _notes, _links, _warnings);
        _warnings = [];
        PlanCommandResult result;
        try
        {
            result = command.Apply(this);
        }
        catch
        {
            (_trains, _order, _disruptions, _notes, _links, _warnings) = saved;
            throw;
        }

        if (!result.Applied)
        {
            (_trains, _order, _disruptions, _notes, _links, _warnings) = saved;
            return result;
        }

        var warnings = _warnings.Count == 0 ? result.Warnings : [.. _warnings, .. result.Warnings];
        _warnings = saved._warnings;
        return result with { Warnings = warnings };
    }

    private TrainPlan Clamp(TrainPlan before, TrainPlan after)
    {
        PlannedPoint[]? points = null;
        var current = after.Points;
        int? previous = null;
        for (var i = 0; i < current.Count; i++)
        {
            var point = current[i];
            if (!point.IsTimed)
            {
                continue;
            }

            if (previous is { } j && point.FirstTime is { } arrive && current[j].LastTime is { } depart)
            {
                var run = arrive - depart;
                var bookedRun = before.Points[i].FirstTime - before.Points[j].LastTime;
                if ((bookedRun is not { } was || run < was) && MinRun(current, j, i) is { } min && run < min)
                {
                    var shift = min - run;
                    point = point.Shift(shift);
                    Set(ref points, current, i, point);
                    current = points!;
                    _warnings.Add(new PlanWarning(PlanWarningKind.RunTimeClamped, after.Id, i, point.Location, shift,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{after.Headcode} needs at least {PlanText.Duration(min)} from {current[j].Location} to {point.Location}; {point.Location} moved {PlanText.Duration(shift)} later.")));
                }
            }

            if (point is { IsPass: false, Arrive: { } a, Depart: { } d } && d < a)
            {
                var old = before.Points[i];
                var bookedDwell = old.Depart - old.Arrive;
                if (bookedDwell is not { } was || d - a < was)
                {
                    point = point with { Depart = a };
                    Set(ref points, current, i, point);
                    current = points!;
                    _warnings.Add(new PlanWarning(PlanWarningKind.DwellClamped, after.Id, i, point.Location, a - d,
                        $"{after.Headcode} cannot leave {point.Location} before it arrives; departure set to {a}."));
                }
            }

            previous = i;
        }

        return points is null ? after : after with { Points = points };
    }

    /// <summary>The minimum run between two points, lengthened by the speed restriction in force when it departs that lengthens it most.</summary>
    private int? MinRun(IReadOnlyList<PlannedPoint> points, int from, int to) =>
        MinRuns.Along(points, from, to, points[from].LastTime, _disruptions);

    private static void Set(ref PlannedPoint[]? points, IReadOnlyList<PlannedPoint> current, int index, PlannedPoint point)
    {
        points ??= [.. current];
        points[index] = point;
    }
}
