using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Stringline.Core.Json;

namespace Stringline.Core;

/// <summary>
/// A point in simulation time: whole seconds after midnight of sim day 0. Overnight sims run past
/// 86 400, so the value is unbounded; it may also be negative after arithmetic.
/// </summary>
/// <remarks>
/// <para>Formats (<see cref="ToString(string?, IFormatProvider?)"/>), each followed by a day marker
/// <c>+N</c>/<c>-N</c> when <see cref="Day"/> is not 0:</para>
/// <list type="bullet">
/// <item><c>T</c> (default, also <c>G</c>): <c>HH:mm:ss</c>, e.g. <c>09:05:30</c>.</item>
/// <item><c>t</c>: <c>HH:mm</c>, seconds truncated.</item>
/// <item><c>H</c>: <c>HH:mm½</c>, WTT style — seconds truncated to the half minute.</item>
/// </list>
/// <para>Parsing accepts <c>H:mm</c>, <c>HH:mm</c>, <c>HH:mm:ss</c>, <c>HHmm</c> and <c>HHmmss</c>; a
/// trailing <c>½</c> or <c>H</c> on a minutes-only time adds 30 s; hours may exceed 23 for overnight
/// running (<c>25:10</c>); a <c>+N</c>/<c>-N</c> suffix adds or subtracts whole days.</para>
/// </remarks>
[JsonConverter(typeof(SimTimeJsonConverter))]
public readonly record struct SimTime(int Seconds) : IComparable<SimTime>, IComparable, ISpanFormattable, ISpanParsable<SimTime>
{
    public const int SecondsPerMinute = 60;
    public const int SecondsPerHour = 3600;
    public const int SecondsPerDay = 86_400;

    private const char HalfMinute = '½';

    /// <summary>Midnight at the start of sim day 0.</summary>
    public static SimTime Zero => default;

    /// <summary>Creates a time from a clock reading on the given sim day.</summary>
    public static SimTime FromClock(int hours, int minutes, int seconds = 0, int day = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(hours);
        ArgumentOutOfRangeException.ThrowIfNegative(minutes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 59);
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(seconds, 59);
        return new SimTime(checked((day * SecondsPerDay) + (hours * SecondsPerHour) + (minutes * SecondsPerMinute) + seconds));
    }

    /// <summary>Sim day, counting from 0 (floor division, so negative times are on day -1 or earlier).</summary>
    public int Day
    {
        get
        {
            var day = Seconds / SecondsPerDay;
            return Seconds % SecondsPerDay < 0 ? day - 1 : day;
        }
    }

    /// <summary>Seconds after midnight of <see cref="Day"/>, always 0–86 399.</summary>
    public int SecondOfDay
    {
        get
        {
            var remainder = Seconds % SecondsPerDay;
            return remainder < 0 ? remainder + SecondsPerDay : remainder;
        }
    }

    public int Hour => SecondOfDay / SecondsPerHour;

    public int Minute => SecondOfDay % SecondsPerHour / SecondsPerMinute;

    public int Second => SecondOfDay % SecondsPerMinute;

    public SimTime AddSeconds(int seconds) => new(checked(Seconds + seconds));

    public SimTime AddMinutes(int minutes) => AddSeconds(checked(minutes * SecondsPerMinute));

    public static SimTime operator +(SimTime time, int seconds) => time.AddSeconds(seconds);

    public static SimTime operator -(SimTime time, int seconds) => time.AddSeconds(checked(-seconds));

    /// <summary>Difference in seconds (<paramref name="later"/> − <paramref name="earlier"/>).</summary>
    public static int operator -(SimTime later, SimTime earlier) => checked(later.Seconds - earlier.Seconds);

    public static bool operator <(SimTime left, SimTime right) => left.Seconds < right.Seconds;

    public static bool operator >(SimTime left, SimTime right) => left.Seconds > right.Seconds;

    public static bool operator <=(SimTime left, SimTime right) => left.Seconds <= right.Seconds;

    public static bool operator >=(SimTime left, SimTime right) => left.Seconds >= right.Seconds;

    public static SimTime Min(SimTime a, SimTime b) => a <= b ? a : b;

    public static SimTime Max(SimTime a, SimTime b) => a >= b ? a : b;

    public int CompareTo(SimTime other) => Seconds.CompareTo(other.Seconds);

    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        SimTime other => CompareTo(other),
        _ => throw new ArgumentException($"Object must be of type {nameof(SimTime)}.", nameof(obj)),
    };

    // ---- Formatting -------------------------------------------------------------------------

    public override string ToString() => ToString(null, null);

    public string ToString(string? format) => ToString(format, null);

    public string ToString(string? format, IFormatProvider? formatProvider)
    {
        Span<char> buffer = stackalloc char[32];
        if (!TryFormat(buffer, out var written, format, formatProvider))
        {
            throw new FormatException($"Unknown {nameof(SimTime)} format '{format}'. Use T, t or H.");
        }

        return new string(buffer[..written]);
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        charsWritten = 0;
        var style = format.IsEmpty ? 'T' : format.Length == 1 ? format[0] : '\0';
        bool showSeconds, showHalf;
        switch (style)
        {
            case 'T' or 'G':
                (showSeconds, showHalf) = (true, false);
                break;
            case 't':
                (showSeconds, showHalf) = (false, false);
                break;
            case 'H':
                (showSeconds, showHalf) = (false, true);
                break;
            default:
                return false;
        }

        var pos = 0;
        if (!TryWrite2(destination, ref pos, Hour) || !TryWriteChar(destination, ref pos, ':') || !TryWrite2(destination, ref pos, Minute))
        {
            return false;
        }

        if (showSeconds && (!TryWriteChar(destination, ref pos, ':') || !TryWrite2(destination, ref pos, Second)))
        {
            return false;
        }

        if (showHalf && Second >= 30 && !TryWriteChar(destination, ref pos, HalfMinute))
        {
            return false;
        }

        var day = Day;
        if (day != 0)
        {
            if (!TryWriteChar(destination, ref pos, day > 0 ? '+' : '-')
                || !Math.Abs((long)day).TryFormat(destination[pos..], out var dayChars, default, System.Globalization.CultureInfo.InvariantCulture))
            {
                return false;
            }

            pos += dayChars;
        }

        charsWritten = pos;
        return true;
    }

    private static bool TryWrite2(Span<char> destination, ref int pos, int value)
    {
        if (pos + 2 > destination.Length)
        {
            return false;
        }

        destination[pos++] = (char)('0' + (value / 10));
        destination[pos++] = (char)('0' + (value % 10));
        return true;
    }

    private static bool TryWriteChar(Span<char> destination, ref int pos, char c)
    {
        if (pos >= destination.Length)
        {
            return false;
        }

        destination[pos++] = c;
        return true;
    }

    // ---- Parsing ----------------------------------------------------------------------------

    public static SimTime Parse(string s) => Parse(s, null);

    public static SimTime Parse(string s, IFormatProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan(), provider);
    }

    public static SimTime Parse(ReadOnlySpan<char> s, IFormatProvider? provider = null) =>
        TryParse(s, provider, out var result) ? result : throw new FormatException($"'{s}' is not a valid sim time.");

    public static bool TryParse([NotNullWhen(true)] string? s, out SimTime result) => TryParse(s, null, out result);

    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out SimTime result)
    {
        result = default;
        return s is not null && TryParse(s.AsSpan(), provider, out result);
    }

    public static bool TryParse(ReadOnlySpan<char> s, out SimTime result) => TryParse(s, null, out result);

    public static bool TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out SimTime result)
    {
        result = default;
        s = s.Trim();

        // Day suffix: +N / -N (a leading sign is not allowed, so search from index 1).
        var days = 0;
        var signAt = s.Length > 1 ? s[1..].IndexOfAny('+', '-') : -1;
        if (signAt >= 0)
        {
            signAt++;
            var dayDigits = s[(signAt + 1)..];
            if (!TryParseDigits(dayDigits, 1, 3, out days))
            {
                return false;
            }

            if (s[signAt] == '-')
            {
                days = -days;
            }

            s = s[..signAt];
        }

        var half = false;
        if (!s.IsEmpty && s[^1] is HalfMinute or 'H' or 'h')
        {
            half = true;
            s = s[..^1];
        }

        int hours, minutes, seconds = 0;
        bool hasSeconds;
        var firstColon = s.IndexOf(':');
        if (firstColon >= 0)
        {
            var rest = s[(firstColon + 1)..];
            var secondColon = rest.IndexOf(':');
            hasSeconds = secondColon >= 0;
            var minutePart = hasSeconds ? rest[..secondColon] : rest;
            if (!TryParseDigits(s[..firstColon], 1, 2, out hours)
                || !TryParseDigits(minutePart, 2, 2, out minutes)
                || (hasSeconds && !TryParseDigits(rest[(secondColon + 1)..], 2, 2, out seconds)))
            {
                return false;
            }
        }
        else
        {
            hasSeconds = s.Length == 6;
            if ((s.Length != 4 && !hasSeconds)
                || !TryParseDigits(s[..2], 2, 2, out hours)
                || !TryParseDigits(s[2..4], 2, 2, out minutes)
                || (hasSeconds && !TryParseDigits(s[4..], 2, 2, out seconds)))
            {
                return false;
            }
        }

        if (minutes > 59 || seconds > 59 || (half && hasSeconds))
        {
            return false;
        }

        result = new SimTime((days * SecondsPerDay) + (hours * SecondsPerHour) + (minutes * SecondsPerMinute) + seconds + (half ? 30 : 0));
        return true;
    }

    private static bool TryParseDigits(ReadOnlySpan<char> s, int minLength, int maxLength, out int value)
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
