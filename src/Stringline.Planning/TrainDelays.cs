using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;

namespace Stringline.Planning;

/// <summary>A train's delay at one timing point: actual minus booked, in seconds (negative = early).</summary>
public sealed record TrainDelay(int Seconds, Tiploc Location, SimTime Actual, SimTime Booked, TrainLocationAction Action)
{
    /// <summary>Whole minutes late, rounded towards zero as railway delay minutes are (59 s late is on time).</summary>
    public int Minutes => Seconds / SimTime.SecondsPerMinute;
}

/// <summary>Delays of live trains against their WTT train (the forecaster refines this in S11).</summary>
public static class TrainDelays
{
    /// <summary>
    /// The booked time an actual event compares with: the arrival for an arrive, otherwise the departure (a pass is
    /// booked as a departure or as its only time). A train that calls at a location twice uses the call nearest in time.
    /// </summary>
    public static SimTime? BookedTime(ActualEvent actual, Train train)
    {
        ArgumentNullException.ThrowIfNull(actual);
        ArgumentNullException.ThrowIfNull(train);
        SimTime? best = null;
        foreach (var point in train.Points)
        {
            if (point.Location != actual.Location)
            {
                continue;
            }

            var booked = actual.Action == TrainLocationAction.Arrive ? point.Arrive ?? point.Depart : point.Depart ?? point.Arrive;
            if (booked is { } time && (best is null || Math.Abs(time - actual.Time) < Math.Abs(best.Value - actual.Time)))
            {
                best = time;
            }
        }

        return best;
    }

    /// <summary>The delay at one actual event, or null if the train is not booked there.</summary>
    public static TrainDelay? At(ActualEvent actual, Train train)
    {
        ArgumentNullException.ThrowIfNull(actual);
        return BookedTime(actual, train) is { } booked
            ? new TrainDelay(actual.Time - booked, actual.Location, actual.Time, booked, actual.Action)
            : null;
    }

    /// <summary>
    /// The delay at the latest timing point of a live train's current working that its WTT train is booked at, or
    /// null if the working is not matched or has not reached a booked point yet.
    /// </summary>
    public static TrainDelay? Current(LiveTrain live, WorkingTimetable? timetable)
    {
        ArgumentNullException.ThrowIfNull(live);
        return Latest(live, live.CurrentIndex, timetable);
    }

    /// <summary>The delay at the latest booked timing point of one working, or null.</summary>
    public static TrainDelay? Latest(LiveTrain live, int working, WorkingTimetable? timetable)
    {
        ArgumentNullException.ThrowIfNull(live);
        if (live.Workings[working] is not { IsMatched: true, Train: { } id } || timetable?.Find(id) is not { } train)
        {
            return null;
        }

        TrainDelay? latest = null;
        foreach (var actual in live.ActualsOf(working))
        {
            if (actual.AtTimingPoint && At(actual, train) is { } delay)
            {
                latest = delay;
            }
        }

        return latest;
    }
}
