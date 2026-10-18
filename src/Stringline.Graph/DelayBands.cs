using Stringline.Core;
using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>How actual lines are coloured (GR-7).</summary>
public enum GraphColourMode
{
    /// <summary>By the WTT train's category, from the profile.</summary>
    Category,

    /// <summary>By delay band, segment by segment (<see cref="DelayBands"/>).</summary>
    Delay,
}

/// <summary>A band of lateness (<c>05-…md</c> §2, delay bands).</summary>
public enum DelayBand
{
    /// <summary>Early or right time: 0 minutes late or less.</summary>
    RightTime,

    /// <summary>1–5 minutes late.</summary>
    Slight,

    /// <summary>6–15 minutes late.</summary>
    Late,

    /// <summary>More than 15 minutes late.</summary>
    VeryLate,
}

/// <summary>
/// Delay bands and their colours: ≤ 0 min green, 1–5 amber, 6–15 orange, &gt; 15 red. Minutes are whole minutes
/// rounded towards zero, as <c>TrainDelay.Minutes</c> is, so 59 s late is right time.
/// </summary>
public static class DelayBands
{
    public static Colour RightTime { get; } = new(0x2E, 0x7D, 0x32);

    public static Colour Slight { get; } = new(0xE0, 0xA0, 0x00);

    public static Colour Late { get; } = new(0xEF, 0x6C, 0x00);

    public static Colour VeryLate { get; } = new(0xC6, 0x28, 0x28);

    /// <summary>A matched train with no booked time to compare with yet.</summary>
    public static Colour Unknown { get; } = GraphSceneBuilder.UnmatchedColour;

    public static DelayBand BandOf(int minutes) => minutes switch
    {
        <= 0 => DelayBand.RightTime,
        <= 5 => DelayBand.Slight,
        <= 15 => DelayBand.Late,
        _ => DelayBand.VeryLate,
    };

    /// <summary>The band of a delay in seconds, after rounding it to whole minutes towards zero.</summary>
    public static DelayBand BandOfSeconds(int seconds) => BandOf(seconds / SimTime.SecondsPerMinute);

    public static Colour ColourOf(DelayBand band) => band switch
    {
        DelayBand.RightTime => RightTime,
        DelayBand.Slight => Slight,
        DelayBand.Late => Late,
        DelayBand.VeryLate => VeryLate,
        _ => throw new ArgumentOutOfRangeException(nameof(band), band, null),
    };

    /// <summary>The colour of a delay in seconds, or <see cref="Unknown"/> without one.</summary>
    public static Colour ColourOfSeconds(int? seconds) => seconds is { } s ? ColourOf(BandOfSeconds(s)) : Unknown;
}
