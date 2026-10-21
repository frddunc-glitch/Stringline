using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>Where a planned train comes from.</summary>
public enum PlanOrigin
{
    /// <summary>A WTT train (possibly re-planned).</summary>
    Wtt,

    /// <summary>A copy of another train made by <see cref="CloneTrain"/>.</summary>
    Clone,
}

/// <summary>One timing point of a <see cref="TrainPlan"/>: what control intends at a location.</summary>
/// <param name="Index">Position in the train's point list. The same as the WTT point's index: edits never add or remove points.</param>
/// <param name="Arrive">Planned arrival; null at an origin or a pass.</param>
/// <param name="Depart">Planned departure, or the pass time when <paramref name="IsPass"/>; null at a destination.</param>
/// <param name="Platform">Planned platform, if any.</param>
/// <param name="Line">Planned line out of this location, if any.</param>
public sealed record PlannedPoint(int Index, Tiploc Location, SimTime? Arrive, SimTime? Depart, bool IsPass, string? Platform, string? Line)
{
    /// <summary>Arrival if planned, else departure or pass.</summary>
    public SimTime? FirstTime => Arrive ?? Depart;

    /// <summary>Departure or pass if planned, else arrival.</summary>
    public SimTime? LastTime => Depart ?? Arrive;

    public bool IsTimed => Arrive is not null || Depart is not null;

    /// <summary>A stop with both times: it has a dwell that can be changed or skipped.</summary>
    public bool IsCall => !IsPass && Arrive is not null && Depart is not null;

    public static PlannedPoint From(TimingPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        return new(point.Index, point.Location, point.Arrive, point.Depart, point.IsPass, point.Platform, point.Line);
    }

    /// <summary>Both times moved by <paramref name="seconds"/>.</summary>
    public PlannedPoint Shift(int seconds) => this with { Arrive = Arrive + seconds, Depart = Depart + seconds };

    public override string ToString() =>
        $"{Location} {(IsPass ? "pass " + Depart : $"{Arrive?.ToString() ?? "-"}/{Depart?.ToString() ?? "-"}")}";
}

/// <summary>
/// One train in the <see cref="WorkingPlan"/>: its WTT booking with every re-plan applied. Immutable; the plan builder
/// replaces it on change. The point list keeps every booked point (a train terminated short keeps the ones it no
/// longer runs to), so a reinstatement brings them back.
/// </summary>
/// <param name="Source">The WTT train this one is booked as (itself) or was cloned from.</param>
/// <param name="TerminatesAt">The index the train is terminated short at, if it is.</param>
/// <param name="IsModified">True if the plan differs from the WTT in any way (always for a clone).</param>
public sealed record TrainPlan(
    TrainId Id,
    Headcode Headcode,
    string? CategoryId,
    PlanOrigin Origin,
    TrainId Source,
    IReadOnlyList<PlannedPoint> Points,
    bool Cancelled,
    int? TerminatesAt,
    bool IsModified)
{
    /// <summary>The index of the last point the train runs to.</summary>
    public int LastIndex => TerminatesAt ?? Points.Count - 1;

    /// <summary>Where the train is held at signals between its points (S33), in the order made; none in the WTT.</summary>
    public IReadOnlyList<SignalHold> SignalHolds { get; init; } = [];

    /// <summary>
    /// The points the train runs to, in order. When terminated short, the last one is a terminating stop: it arrives at
    /// the booked arrival or pass time and does not depart. Includes the points of a cancelled train (see <see cref="Cancelled"/>).
    /// </summary>
    public IReadOnlyList<PlannedPoint> RunningPoints
    {
        get
        {
            if (TerminatesAt is not { } end)
            {
                return Points;
            }

            var running = new PlannedPoint[end + 1];
            for (var i = 0; i < end; i++)
            {
                running[i] = Points[i];
            }

            var last = Points[end];
            running[end] = last with { Arrive = last.FirstTime, Depart = null, IsPass = false };
            return running;
        }
    }

    /// <summary>The plan of a WTT train as booked.</summary>
    public static TrainPlan FromWtt(Train train)
    {
        ArgumentNullException.ThrowIfNull(train);
        var points = new PlannedPoint[train.Points.Count];
        for (var i = 0; i < points.Length; i++)
        {
            points[i] = PlannedPoint.From(train.Points[i]) with { Index = i };
        }

        return new(train.Id, train.Headcode, train.CategoryId, PlanOrigin.Wtt, train.Id, points, false, null, false);
    }

    /// <summary>True if both plans have the same points (by value), cancellation, termination and signal holds.</summary>
    public bool SameAs(TrainPlan other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Cancelled == other.Cancelled && TerminatesAt == other.TerminatesAt && Points.SequenceEqual(other.Points) &&
            SignalHolds.SequenceEqual(other.SignalHolds);
    }

    public override string ToString() => $"{Headcode} {Id}{(Cancelled ? " (cancelled)" : string.Empty)}";
}
