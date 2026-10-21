using System.Collections.Immutable;
using Stringline.Core.Events;

namespace Stringline.Core.Live;

/// <summary>A signal's aspect, and since when it has shown it (<c>SG_MSG</c> <c>aspect</c>, S38).</summary>
public readonly record struct SignalState(int Aspect, SimTime Since)
{
    public bool IsRed => Aspect == SignalAspects.Red;
}

/// <summary>
/// A track circuit's state (S38): clear or occupied since <see cref="Since"/>, and when it was last occupied, which
/// stays after it clears so a train that has gone through it can still be placed.
/// </summary>
public readonly record struct TrackState(bool Clear, SimTime Since, SimTime? LastOccupied);

/// <summary>A TD berth's description and since when it has held it (S38). Empty when cancelled or stepped out.</summary>
public readonly record struct BerthState(string Description, SimTime Since);

/// <summary>
/// The signals, track circuits and TD berths as the sim last reported them (S38, #17/#19): what the forecaster needs
/// to tell where a train is between timing locations, and whether it is standing at a red signal. Filled from
/// <c>SG_MSG</c>s (signal and track only) and TD steps, so it stays empty without the TD topic. Immutable;
/// <see cref="With"/> returns this same instance when an event changes nothing.
/// </summary>
public sealed class LineSideState
{
    private LineSideState(ImmutableDictionary<string, SignalState> signals, ImmutableDictionary<string, TrackState> tracks, ImmutableDictionary<string, BerthState> berths)
    {
        Signals = signals;
        Tracks = tracks;
        Berths = berths;
    }

    public static LineSideState Empty { get; } = new(
        ImmutableDictionary.Create<string, SignalState>(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary.Create<string, TrackState>(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary.Create<string, BerthState>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Signal ID → its aspect now.</summary>
    public ImmutableDictionary<string, SignalState> Signals { get; }

    /// <summary>Track circuit ID → clear or occupied.</summary>
    public ImmutableDictionary<string, TrackState> Tracks { get; }

    /// <summary>Berth ID → the description stepped into it, and when.</summary>
    public ImmutableDictionary<string, BerthState> Berths { get; }

    public bool IsEmpty => Signals.IsEmpty && Tracks.IsEmpty && Berths.IsEmpty;

    public SignalState? Signal(string id) => Signals.TryGetValue(id, out var s) ? s : null;

    public TrackState? Track(string id) => Tracks.TryGetValue(id, out var t) ? t : null;

    public BerthState? Berth(string id) => Berths.TryGetValue(id, out var b) ? b : null;

    /// <summary>
    /// The state with what <paramref name="e"/> says; this same instance if nothing changed. An event without a time
    /// (some snapshot entries) takes <paramref name="fallback"/>, and is skipped if there is none.
    /// </summary>
    public LineSideState With(GatewayEvent e, SimTime? fallback = null)
    {
        switch (e)
        {
            case SignallingStateEvent { ObjectType: SignallingObjectType.Signal } s when s.ObjectId.Trim().Length > 0 && s.GetInt32("aspect") is { } aspect && (s.Time ?? fallback) is { } at:
            {
                var id = s.ObjectId.Trim();
                return Signals.TryGetValue(id, out var old) && old.Aspect == aspect ? this : new(Signals.SetItem(id, new SignalState(aspect, at)), Tracks, Berths);
            }

            case SignallingStateEvent { ObjectType: SignallingObjectType.Track } t when t.ObjectId.Trim().Length > 0 && t.GetBoolean("clear") is { } clear && (t.Time ?? fallback) is { } at:
            {
                var id = t.ObjectId.Trim();
                var had = Tracks.TryGetValue(id, out var old);
                if (had && old.Clear == clear)
                {
                    return this;
                }

                var state = new TrackState(clear, at, clear ? (had ? old.LastOccupied : null) : at);
                return new(Signals, Tracks.SetItem(id, state), Berths);
            }

            case TdStepEvent step when (step.Time ?? fallback) is { } at:
                return WithStep(step, at);

            default:
                return this;
        }
    }

    private LineSideState WithStep(TdStepEvent step, SimTime at)
    {
        var berths = Berths;
        var from = step.From.Trim();
        var to = step.To.Trim();
        var description = step.Description.Trim();
        switch (step.Kind)
        {
            case TdStepKind.Step:
                if (from.Length > 0)
                {
                    berths = berths.Remove(from);
                }

                if (to.Length > 0)
                {
                    berths = description.Length == 0 ? berths.Remove(to) : berths.SetItem(to, new BerthState(description, at));
                }

                break;
            case TdStepKind.Cancel when from.Length > 0:
                berths = berths.Remove(from);
                break;
            case TdStepKind.Interpose when to.Length > 0:
                berths = description.Length == 0 ? berths.Remove(to) : berths.SetItem(to, new BerthState(description, at));
                break;
            default:
                return this;
        }

        return ReferenceEquals(berths, Berths) ? this : new(Signals, Tracks, berths);
    }
}

/// <summary>
/// SimSig's signal aspects as the gateway numbers them (<c>aspPass</c>, <c>aspAppr</c>, an <c>SG_MSG</c>'s
/// <c>aspect</c>; <c>03-…md</c> §3.5): 0 red to 6 green. Waterloo's recordings show 0, 1, 2, 4 and 6.
/// </summary>
public static class SignalAspects
{
    public const int Red = 0;

    public const int Shunt = 1;

    public const int SingleYellow = 2;

    public const int FlashingYellow = 3;

    public const int DoubleYellow = 4;

    public const int FlashingDoubleYellow = 5;

    public const int Green = 6;

    /// <summary>The aspect's name as SimSig gives it, e.g. "single yellow"; the number for one not known.</summary>
    public static string Name(int aspect) => aspect switch
    {
        0 => "red",
        1 => "shunt",
        2 => "single yellow",
        3 => "flashing single yellow",
        4 => "double yellow",
        5 => "flashing double yellow",
        6 => "green",
        _ => $"aspect {aspect}",
    };
}
