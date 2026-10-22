using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Network;

/// <summary>
/// Finds the off-corridor locations a corridor draws at one of its own (<see cref="Corridor.DrawAt"/>, #22, #23):
/// <list type="bullet">
/// <item>A <b>location within a station</b>: a loop, yard or platform the WTT books by a code of its own, such as Moss
/// Vale's <c>HARDN10</c> (Harden's platform 10, where trains are booked to be overtaken) beside <c>HARDEN</c>. Its code
/// shares a prefix of at least <see cref="MinPrefix"/> characters with the corridor location's (all but two of the
/// shorter code), and trains run between the two in at most <see cref="MaxRunSeconds"/>.</item>
/// <item>An <b>entry point beyond the corridor's end</b>, such as <c>EJUNEE</c>, where trains bound for Picton enter
/// and first book Cootamundra: its code less the leading <c>E</c> names the corridor location the same way, and that
/// location is the train's first booked location or next to it.</item>
/// </list>
/// A guess from codes and booked runs, so the profile editor lists them and can change or turn off each one.
/// </summary>
public static class CorridorDrawAt
{
    /// <summary>The least shared prefix of two codes for one to be part of the other.</summary>
    public const int MinPrefix = 4;

    /// <summary>The longest booked run between a location and the corridor location it is part of.</summary>
    public const int MaxRunSeconds = 300;

    /// <summary>The automatic entries for this corridor, in code order. Locations on the corridor are left out.</summary>
    public static IReadOnlyList<DrawnAt> Find(Corridor corridor, WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        ArgumentNullException.ThrowIfNull(timetable);
        var found = new Dictionary<Tiploc, (Tiploc At, int Prefix, int Run)>();
        var adjacent = new HashSet<LocationPair>();

        void Offer(Tiploc location, Tiploc at, int prefix, int run)
        {
            if (!found.TryGetValue(location, out var best) || prefix > best.Prefix || (prefix == best.Prefix && run < best.Run))
            {
                found[location] = (at, prefix, run);
            }
        }

        // Locations within a station: one step from a corridor location, booked close by.
        foreach (var train in timetable.Trains)
        {
            for (var i = 0; i + 1 < train.Points.Count; i++)
            {
                var (p, q) = (train.Points[i], train.Points[i + 1]);
                if (p.Location == q.Location)
                {
                    continue;
                }

                adjacent.Add(new LocationPair(p.Location, q.Location));
                var (onP, onQ) = (corridor.Contains(p.Location), corridor.Contains(q.Location));
                if (onP == onQ)
                {
                    continue;
                }

                var (off, on) = onP ? (q.Location, p.Location) : (p.Location, q.Location);
                var run = p.LastTime is { } from && q.FirstTime is { } to ? Math.Abs(to - from) : (int?)null;
                if (run > MaxRunSeconds || PrefixOf(off.Code, on.Code) is not { } prefix)
                {
                    continue;
                }

                Offer(off, on, prefix, run ?? MaxRunSeconds);
            }
        }

        // Entry points beyond an end.
        foreach (var train in timetable.Trains)
        {
            if (train.EntryPoint is not { } entry || corridor.Contains(entry) || train.Points.Count == 0)
            {
                continue;
            }

            var first = train.Points[0].Location;
            var name = entry.Code.Length > 1 && char.ToUpperInvariant(entry.Code[0]) == 'E' ? entry.Code[1..] : entry.Code;
            foreach (var location in corridor.Locations)
            {
                var at = location.Tiploc;
                if (PrefixOf(name, at.Code) is { } prefix && (at == first || adjacent.Contains(new LocationPair(at, first))))
                {
                    Offer(entry, at, prefix, 0);
                }
            }
        }

        return [.. found.OrderBy(f => f.Key.Code, StringComparer.Ordinal).Select(f => new DrawnAt(f.Key, f.Value.At))];
    }

    /// <summary>
    /// The corridor with its automatic entries found again: entries set by hand are kept (and win), and those for
    /// locations now on the corridor are dropped.
    /// </summary>
    public static Corridor Apply(Corridor corridor, WorkingTimetable timetable)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        var hand = corridor.DrawAt.Where(d => d.HandSet && !corridor.Contains(d.Location)).ToList();
        var handSet = hand.Select(d => d.Location).ToHashSet();
        var auto = Find(corridor, timetable).Where(d => !handSet.Contains(d.Location));
        return corridor with { DrawAt = [.. hand.Concat(auto).OrderBy(d => d.Location.Code, StringComparer.Ordinal)] };
    }

    /// <summary>
    /// The length of the prefix two codes share (ignoring case) if it makes one part of the other: at least
    /// <see cref="MinPrefix"/> and all but two characters of the shorter code. Null otherwise.
    /// </summary>
    public static int? PrefixOf(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && char.ToUpperInvariant(a[n]) == char.ToUpperInvariant(b[n]))
        {
            n++;
        }

        return n >= MinPrefix && n >= Math.Min(a.Length, b.Length) - 2 ? n : null;
    }
}
