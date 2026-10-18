using System.Globalization;
using Stringline.Core;

namespace Stringline.App.ViewModels;

/// <summary>Reading times and durations typed into the plan's cells and prompts.</summary>
internal static class PlanInput
{
    /// <summary>
    /// A time typed as <c>HH:mm</c>, <c>HH:mm:ss</c> or <c>HH:mm½</c>, on the day nearest <paramref name="near"/> (so
    /// <c>00:10</c> typed for a train due at <c>23:55</c> means the next day), unless a day is given (<c>00:10+1</c>).
    /// </summary>
    public static bool TryParseTime(string? text, SimTime near, out SimTime time)
    {
        time = default;
        text = text?.Trim().Replace(" pass", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!SimTime.TryParse(text, out var parsed))
        {
            return false;
        }

        if (text!.AsSpan(1).IndexOfAny('+', '-') >= 0)
        {
            time = parsed;
            return true;
        }

        var best = parsed.SecondOfDay + (near.Day * SimTime.SecondsPerDay);
        foreach (var candidate in (ReadOnlySpan<int>)[best - SimTime.SecondsPerDay, best + SimTime.SecondsPerDay])
        {
            if (Math.Abs(candidate - near.Seconds) < Math.Abs(best - near.Seconds))
            {
                best = candidate;
            }
        }

        time = new SimTime(best);
        return true;
    }

    /// <summary>
    /// A time as short as it can be without losing anything, for the detail panel: <c>07:30</c>, <c>07:30½</c> (as
    /// SimSig writes half minutes) or <c>07:30:15</c>; empty for none. <see cref="TryParseTime"/> reads all three.
    /// </summary>
    public static string Format(SimTime? time) => time switch
    {
        null => string.Empty,
        { Second: 0 } t => t.ToString("t", CultureInfo.InvariantCulture),
        { Second: 30 } t => t.ToString("H", CultureInfo.InvariantCulture),
        { } t => t.ToString("T", CultureInfo.InvariantCulture),
    };

    /// <summary>A number of minutes (<c>2</c>, <c>1.5</c>, <c>0,5</c>), as whole seconds; false unless it is a number.</summary>
    public static bool TryParseMinutes(string? text, out int seconds)
    {
        seconds = 0;
        text = text?.Trim().Replace(',', '.');
        if (string.IsNullOrEmpty(text) || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
            double.IsNaN(minutes) || double.IsInfinity(minutes) || Math.Abs(minutes) > 7 * 24 * 60)
        {
            return false;
        }

        seconds = (int)Math.Round(minutes * SimTime.SecondsPerMinute);
        return true;
    }
}
