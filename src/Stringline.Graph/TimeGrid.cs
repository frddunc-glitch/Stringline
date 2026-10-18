using Stringline.Core;

namespace Stringline.Graph;

public enum TickWeight
{
    Minor,

    /// <summary>Labelled.</summary>
    Major,

    /// <summary>On the hour: labelled and drawn heaviest.</summary>
    Hour,
}

/// <param name="Time">Sim seconds.</param>
/// <param name="Label">Clock text (<c>HH:mm</c>) for labelled ticks, else null.</param>
public readonly record struct TimeTick(int Time, TickWeight Weight, string? Label);

/// <summary>
/// Vertical grid lines for a viewport. At the default zoom (a few hours across) that is a minor line every 5 minutes
/// and a labelled one every 30 (<c>05-…md</c> §2); the spacing coarsens or refines with zoom so lines stay readable.
/// </summary>
public static class TimeGrid
{
    /// <summary>Closest minor lines may be, in pixels.</summary>
    public const double MinMinorPixels = 20;

    /// <summary>Closest labelled lines may be, in pixels (room for an <c>HH:mm</c> label).</summary>
    public const double MinMajorPixels = 64;

    private static readonly int[] MinorSteps = [1, 2, 5, 10, 15, 30, 60, 120, 180, 360];
    private static readonly int[] MajorSteps = [5, 10, 15, 30, 60, 120, 180, 360, 720, 1440];

    /// <summary>Minor and major spacing in minutes for this zoom.</summary>
    public static (int Minor, int Major) Spacing(double pixelsPerMinute)
    {
        var minor = MinorSteps.FirstOrDefault(m => m * pixelsPerMinute >= MinMinorPixels, MinorSteps[^1]);

        // At least six minor lines per major one, so 5 min minor lines get 30 min labels, the usual TCD look.
        var major = MajorSteps.FirstOrDefault(m => m >= 6 * minor && m % minor == 0 && m * pixelsPerMinute >= MinMajorPixels, MajorSteps[^1]);
        return (minor, major);
    }

    public static IReadOnlyList<TimeTick> Ticks(GraphViewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var (minor, major) = Spacing(viewport.PixelsPerMinute);
        var step = minor * SimTime.SecondsPerMinute;
        var majorStep = major * SimTime.SecondsPerMinute;

        var ticks = new List<TimeTick>();
        var first = (long)Math.Ceiling(viewport.TimeStart / step) * step;
        for (var t = first; t <= viewport.TimeEnd; t += step)
        {
            var time = (int)t;
            var weight = Mod(time, majorStep) != 0 ? TickWeight.Minor
                : Mod(time, SimTime.SecondsPerHour) == 0 ? TickWeight.Hour
                : TickWeight.Major;
            ticks.Add(new TimeTick(time, weight, weight == TickWeight.Minor ? null : new SimTime(time).ToString("t")));
        }

        return ticks;
    }

    private static int Mod(int value, int step) => ((value % step) + step) % step;
}
