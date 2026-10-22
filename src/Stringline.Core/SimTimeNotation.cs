using System.Buffers;
using System.Globalization;

namespace Stringline.Core;

/// <summary>What a SimSig timing entry says besides its time, by the character between hours and minutes.</summary>
public enum TimeMark
{
    /// <summary><c>12:30</c>: an arrival or departure.</summary>
    None,

    /// <summary><c>12/30</c>: a pass.</summary>
    Pass,

    /// <summary><c>12d30</c>: set down only.</summary>
    SetDown,

    /// <summary><c>12t30</c>: may stop on the through line.</summary>
    ThruLine,

    /// <summary><c>12w30</c>: wait for the booked time before the dwell starts.</summary>
    DwellWait,

    /// <summary><c>12r30</c>: a request stop.</summary>
    Request,
}

/// <summary>A time as typed or shown in SimSig's notation, with its mark.</summary>
public readonly record struct NotatedTime(SimTime Time, TimeMark Mark = TimeMark.None)
{
    public override string ToString() => SimTimeNotation.Format(Time, Mark);
}

/// <summary>
/// SimSig's timing notation (wiki, <i>Writing Timetables</i>), for the timetable editor's grid and spreadsheet import.
/// </summary>
/// <remarks>
/// <para>Parsed: <c>12:30</c>, <c>9:30</c>, <c>1230</c>, <c>12/34</c>, <c>12d34</c>, <c>12t34</c>, <c>12w34</c>, <c>12r34</c>
/// (the mark in either case), with seconds (<c>12:30:15</c>) or a half minute (<c>1234H</c>, <c>13:45H</c>, <c>13:45½</c>);
/// hours past 23 for the next day (<c>24:01</c>, <c>25:10</c>); and <c>+3</c> or <c>+2H</c>, minutes after the previous
/// time. A time given as a clock time of day 0 that is more than 12 hours before the previous time is taken as the next
/// day's, so 23:50 then 00:10 runs past midnight.</para>
/// <para>Formatted as SimSig shows times: hours counted from midnight of day 0 (<c>24:01</c>, not <c>00:01+1</c>), a
/// trailing <c>H</c> for a half minute, and seconds only when they are neither 0 nor 30.</para>
/// </remarks>
public static class SimTimeNotation
{
    /// <summary>How far before the previous time a time may be before it is taken as the next day's.</summary>
    private const int RolloverSeconds = 12 * SimTime.SecondsPerHour;

    private static readonly SearchValues<char> Separators = SearchValues.Create(":/dtwrDTWR");

    public static NotatedTime Parse(string text, SimTime? previous = null) =>
        TryParse(text, previous, out var result) ? result : throw new FormatException($"'{text}' is not a SimSig time.");

    /// <param name="previous">The time before this one in the train's path, for <c>+N</c> and the day rollover.</param>
    public static bool TryParse(ReadOnlySpan<char> text, SimTime? previous, out NotatedTime result)
    {
        result = default;
        var s = text.Trim();
        if (s.IsEmpty)
        {
            return false;
        }

        var half = false;
        if (s[^1] is 'H' or 'h' or '½')
        {
            half = true;
            s = s[..^1].TrimEnd();
        }

        if (s[0] == '+')
        {
            if (previous is not { } before || !TryDigits(s[1..], 1, 4, out var minutes))
            {
                return false;
            }

            result = new NotatedTime(before + (minutes * SimTime.SecondsPerMinute) + (half ? 30 : 0));
            return true;
        }

        var separator = s.IndexOfAny(Separators);
        int hours, mins, seconds = 0;
        var mark = TimeMark.None;
        if (separator >= 0)
        {
            mark = char.ToLowerInvariant(s[separator]) switch
            {
                '/' => TimeMark.Pass,
                'd' => TimeMark.SetDown,
                't' => TimeMark.ThruLine,
                'w' => TimeMark.DwellWait,
                'r' => TimeMark.Request,
                _ => TimeMark.None,
            };
            var rest = s[(separator + 1)..];
            var colon = rest.IndexOf(':');
            if (!TryDigits(s[..separator], 1, 2, out hours)
                || !TryDigits(colon < 0 ? rest : rest[..colon], 2, 2, out mins)
                || (colon >= 0 && (half || !TryDigits(rest[(colon + 1)..], 2, 2, out seconds))))
            {
                return false;
            }
        }
        else if (s.Length is 3 or 4 && TryDigits(s[..^2], 1, 2, out hours) && TryDigits(s[^2..], 2, 2, out mins))
        {
            // 1234 or 930
        }
        else
        {
            return false;
        }

        if (mins > 59 || seconds > 59)
        {
            return false;
        }

        var time = new SimTime((hours * SimTime.SecondsPerHour) + (mins * SimTime.SecondsPerMinute) + seconds + (half ? 30 : 0));
        if (previous is { } prior && hours < 24)
        {
            while (time.Seconds + RolloverSeconds < prior.Seconds)
            {
                time += SimTime.SecondsPerDay;
            }
        }

        result = new NotatedTime(time, mark);
        return true;
    }

    /// <summary><c>12:34</c>, <c>12/34H</c>, <c>24:01</c>; <paramref name="compact"/> gives <c>1234</c> and <c>1234H</c> (no mark).</summary>
    public static string Format(SimTime time, TimeMark mark = TimeMark.None, bool compact = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(time.Seconds, nameof(time));
        var hours = time.Seconds / SimTime.SecondsPerHour;
        var minutes = time.Seconds % SimTime.SecondsPerHour / SimTime.SecondsPerMinute;
        var seconds = time.Seconds % SimTime.SecondsPerMinute;
        var separator = compact ? string.Empty : mark switch
        {
            TimeMark.Pass => "/",
            TimeMark.SetDown => "d",
            TimeMark.ThruLine => "t",
            TimeMark.DwellWait => "w",
            TimeMark.Request => "r",
            _ => ":",
        };
        var text = string.Create(CultureInfo.InvariantCulture, $"{hours:00}{separator}{minutes:00}");
        return seconds switch
        {
            0 => text,
            30 => text + "H",
            _ => string.Create(CultureInfo.InvariantCulture, $"{text}:{seconds:00}"),
        };
    }

    private static bool TryDigits(ReadOnlySpan<char> s, int minLength, int maxLength, out int value)
    {
        value = 0;
        if (s.Length < minLength || s.Length > maxLength)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }

            value = (value * 10) + (c - '0');
        }

        return true;
    }
}
