namespace Stringline.Planning;

/// <summary>One train's time in one section, as forecast (or planned) and as booked in the WTT.</summary>
/// <param name="Down">The train runs through the section From → To.</param>
/// <param name="BookedEnter">When the WTT books it into the section, if it is a WTT train booked over the run.</param>
internal readonly record struct SectionWindow(bool Down, int Enter, int Exit, int? BookedEnter, int? BookedExit)
{
    /// <summary>The window of a run leaving at <paramref name="depart"/> and taking <paramref name="run"/> seconds, in one step of its route.</summary>
    public static SectionWindow Of(RouteStep step, int depart, int run, (int Depart, int Arrive)? booked)
    {
        int? bookedEnter = null, bookedExit = null;
        if (booked is { } b)
        {
            var bookedRun = b.Arrive - b.Depart;
            bookedEnter = b.Depart + (int)Math.Round(bookedRun * step.EnterShare);
            bookedExit = b.Depart + (int)Math.Round(bookedRun * step.ExitShare);
        }

        return new SectionWindow(step.Down, depart + (int)Math.Round(run * step.EnterShare), depart + (int)Math.Round(run * step.ExitShare), bookedEnter, bookedExit);
    }

    public SectionWindow Shifted(int seconds) => this with { Enter = Enter + seconds, Exit = Exit + seconds };
}

/// <summary>A train's window in a section, on one of its tracks that way.</summary>
internal sealed record SectionReservation<T>(T Owner, SectionWindow Window, int Track);

/// <summary>What stops a window going through a section where it is: the train in the way, and how.</summary>
internal enum SectionClash
{
    /// <summary>Following a train the same way on the same track too closely.</summary>
    Headway,

    /// <summary>Catching up a train the same way on the same track: getting to the far end before it does.</summary>
    Overtake,

    /// <summary>Entering a single line an opposing train has not cleared.</summary>
    Opposing,
}

/// <summary>
/// The trains through one section so far, in entry order, and the rules for one more (<c>06-…md</c> §2 step 4): the
/// forecaster's interaction pass holds trains with it, and the conflict detector (§3) reports what it would hold.
/// </summary>
/// <remarks>
/// A train following another the same way on the same track must enter and leave at least the headway after it; on a
/// single line, an opposing train must have left. No pair is held further apart than the WTT books them: when both are
/// booked through the section in that order, the spacing is the lesser of the headway and their booked spacing.
/// Opposing trains booked to overlap by up to <see cref="ForecastNetwork.ParallelSeconds"/> (crossing-loop rounding)
/// keep that overlap.
/// </remarks>
internal sealed class SectionOccupancy<T>(ForecastSection section)
    where T : class
{
    /// <summary>Most re-checks of one window before it is accepted as it stands (a guard; real runs settle in two or three).</summary>
    private const int MaxSettle = 64;

    private readonly List<SectionReservation<T>> _reservations = [];
    private int _longest;

    public ForecastSection Section => section;

    public void Add(SectionReservation<T> reservation)
    {
        var at = _reservations.FindLastIndex(r => r.Window.Enter <= reservation.Window.Enter) + 1;
        _reservations.Insert(at, reservation);
        _longest = Math.Max(_longest, reservation.Window.Exit - reservation.Window.Enter);
    }

    /// <summary>The least delay that lets <paramref name="x"/> through, and the track it then takes.</summary>
    public int Shift(SectionWindow x, T owner, out int track)
    {
        var tracks = section.TracksFor(x.Down);
        var best = int.MaxValue;
        track = 0;
        for (var t = 0; t < tracks; t++)
        {
            var shift = ShiftOnTrack(x, owner, t);
            if (shift < best)
            {
                best = shift;
                track = t;
            }

            if (best == 0)
            {
                break;
            }
        }

        return best;
    }

    /// <summary>
    /// The train most in the way of <paramref name="x"/> on <paramref name="track"/> where it is now: the one that
    /// needs the longest delay, how it clashes, and that delay. Null if none is in the way.
    /// </summary>
    public (SectionReservation<T> Other, SectionClash Clash, int Seconds)? Blocker(SectionWindow x, T owner, int track)
    {
        (SectionReservation<T>, SectionClash, int)? worst = null;
        var h = section.HeadwaySeconds;
        foreach (var r in Near(x))
        {
            if (ReferenceEquals(r.Owner, owner))
            {
                continue;
            }

            int shift;
            SectionClash clash;
            if (r.Window.Down == x.Down)
            {
                if (r.Track != track)
                {
                    continue;
                }

                shift = SameWay(r.Window, x, h);
                clash = x.Enter >= r.Window.Enter && x.Exit < r.Window.Exit ? SectionClash.Overtake : SectionClash.Headway;
            }
            else if (section.SingleLine)
            {
                shift = Opposing(r.Window, x, h);
                clash = SectionClash.Opposing;
            }
            else
            {
                continue;
            }

            if (shift > 0 && (worst is null || shift > worst.Value.Item3))
            {
                worst = (r, clash, shift);
            }
        }

        return worst;
    }

    private int ShiftOnTrack(SectionWindow x, T owner, int track)
    {
        var total = 0;
        var h = section.HeadwaySeconds;
        for (var settle = 0; settle < MaxSettle; settle++)
        {
            var w = x.Shifted(total);
            var shift = 0;
            foreach (var r in Near(w))
            {
                if (ReferenceEquals(r.Owner, owner))
                {
                    continue;
                }

                if (r.Window.Down == w.Down)
                {
                    if (r.Track == track)
                    {
                        shift = Math.Max(shift, SameWay(r.Window, w, h));
                    }
                }
                else if (section.SingleLine)
                {
                    shift = Math.Max(shift, Opposing(r.Window, w, h));
                }
            }

            if (shift == 0)
            {
                break;
            }

            total += shift;
        }

        return total;
    }

    /// <summary>Reservations that could clash with <paramref name="w"/>: entering no more than the longest stay plus the headway before it.</summary>
    private IEnumerable<SectionReservation<T>> Near(SectionWindow w)
    {
        var from = w.Enter - _longest - section.HeadwaySeconds - 1;
        var to = w.Exit + section.HeadwaySeconds + 1;
        var lo = 0;
        var hi = _reservations.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_reservations[mid].Window.Enter < from)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        for (var i = lo; i < _reservations.Count && _reservations[i].Window.Enter <= to; i++)
        {
            yield return _reservations[i];
        }
    }

    /// <summary>
    /// Delay needed for <paramref name="x"/> to follow <paramref name="l"/> on the same track: the headway at both
    /// ends, or the booked spacing if that is less. None if it already runs ahead of it by as much.
    /// </summary>
    private static int SameWay(SectionWindow l, SectionWindow x, int h)
    {
        if (x.Enter + Gap(x.BookedEnter, l.BookedEnter, h) <= l.Enter && x.Exit + Gap(x.BookedExit, l.BookedExit, h) <= l.Exit)
        {
            return 0; // x runs ahead of l
        }

        var enter = l.Enter + Gap(l.BookedEnter, x.BookedEnter, h) - x.Enter;
        var exit = l.Exit + Gap(l.BookedExit, x.BookedExit, h) - x.Exit;
        return Math.Max(0, Math.Max(enter, exit));
    }

    /// <summary>Delay needed for <paramref name="x"/> to enter a single line only once opposing <paramref name="l"/> has cleared it.</summary>
    private static int Opposing(SectionWindow l, SectionWindow x, int h)
    {
        const int tolerance = ForecastNetwork.ParallelSeconds;
        if (x.Exit + Gap(x.BookedExit, l.BookedEnter, h, tolerance) <= l.Enter)
        {
            return 0; // x is through before l comes
        }

        return Math.Max(0, l.Exit + Gap(l.BookedExit, x.BookedEnter, h, tolerance) - x.Enter);
    }

    /// <summary>
    /// The spacing needed from a leader's time to a follower's: the headway, or the booked spacing if both are
    /// booked in this order and it is less. Opposing trains on a single line may be booked to overlap by up to
    /// <see cref="ForecastNetwork.ParallelSeconds"/> (booking rounding at a crossing loop), which is kept.
    /// </summary>
    private static int Gap(int? leader, int? follower, int h, int tolerance = 0) =>
        leader is { } a && follower is { } b && b >= a - tolerance ? Math.Min(h, b - a) : h;
}
