using System.Text.Json.Serialization;
using Stringline.Core;
using Stringline.Network;

namespace Stringline.Planning;

/// <summary>The kinds of conflict (<c>06-…md</c> §3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ConflictKind>))]
public enum ConflictKind
{
    /// <summary>Two trains the same way on the same track closer than the headway (or than the WTT books them).</summary>
    Headway,

    /// <summary>A train catches up the one ahead on the same track: it would reach the far end first, with no track to pass on.</summary>
    Overtake,

    /// <summary>Opposing trains in a single-line section at once.</summary>
    SingleLine,

    /// <summary>Two trains at one platform at once, or closer than the re-occupation margin.</summary>
    Platform,

    /// <summary>A train runs into a block in force on its track, and is held until it ends.</summary>
    Disruption,

    /// <summary>A run planned faster than the train can run it (the minimum run, lengthened by speed restrictions).</summary>
    Unrealistic,
}

/// <summary>
/// A clash in the plan the controller must resolve (<c>04-…md</c> §2, <c>06-…md</c> §3), found by
/// <see cref="ConflictDetector"/>. <see cref="A"/> is the train that must take the delay (the follower, the second into
/// the platform or section, or the one held); <see cref="B"/> is the other, if any.
/// </summary>
/// <param name="Id">Stable while the same clash stays (kind, trains and place), so a list can keep its selection.</param>
/// <param name="Where">Where <see cref="A"/> meets it: the section end it enters by, the platform's location, or the point it is held at.</param>
/// <param name="When">When <see cref="A"/> gets there (as forecast).</param>
/// <param name="SeveritySeconds">The delay <see cref="A"/> must take to resolve it.</param>
public sealed record Conflict(string Id, ConflictKind Kind, TrainId A, TrainId? B, Tiploc Where, SimTime When, int SeveritySeconds)
{
    public Headcode HeadcodeA { get; init; }

    public Headcode? HeadcodeB { get; init; }

    /// <summary>The far end of the section(s) or run, for a section, disruption or run-time conflict; null at a platform.</summary>
    public Tiploc? To { get; init; }

    /// <summary>
    /// The start of the time the clash covers, for the graph's highlight: both trains' times in the section, the wait
    /// for the platform, the block's hold, or the run as it must be.
    /// </summary>
    public SimTime Start { get; init; }

    /// <summary>The end of the time the clash covers.</summary>
    public SimTime End { get; init; }

    /// <summary>The plan point of <see cref="A"/> at <see cref="Where"/> (or the one it runs from into it).</summary>
    public int PointIndex { get; init; }

    /// <summary>The platform, for a platform conflict.</summary>
    public string? Platform { get; init; }

    /// <summary>
    /// The headway of the section where a headway, overtake or single-line conflict is found (the worst one, if it
    /// runs through several); null for other kinds. A pair the WTT books closer than this may run as close as booked.
    /// </summary>
    public int? HeadwaySeconds { get; init; }

    /// <summary>Where <see cref="HeadwaySeconds"/> comes from, when it is set.</summary>
    public HeadwaySource? HeadwaySource { get; init; }

    /// <summary>The block's ID, for a disruption conflict.</summary>
    public string? DisruptionId { get; init; }

    /// <summary>
    /// How much later <see cref="A"/> gets to its last point because of other trains, by the full forecast (with the
    /// interaction pass); 0 without it. Summarises what the clashes cost if nothing is re-planned.
    /// </summary>
    public int KnockOnSeconds { get; init; }

    /// <summary>True if <paramref name="train"/> is one of the trains in it.</summary>
    public bool Involves(TrainId train) => A == train || B == train;

    /// <summary>A one-line description, e.g. "2S01 too close behind 1F01".</summary>
    public string Summary => Kind switch
    {
        ConflictKind.Headway => $"{HeadcodeA} too close behind {Other}",
        ConflictKind.Overtake => $"{HeadcodeA} catches up {Other}",
        ConflictKind.SingleLine => $"{HeadcodeA} meets {Other} on the single line",
        ConflictKind.Platform => $"{HeadcodeA} due in platform {Platform} before {Other} clears it",
        ConflictKind.Disruption => $"{HeadcodeA} runs into block {DisruptionId}",
        ConflictKind.Unrealistic => $"{HeadcodeA} planned faster than it can run",
        _ => HeadcodeA.Value,
    };

    private string Other => HeadcodeB?.Value ?? B?.Value ?? "another train";

    public override string ToString() => $"{Kind} {HeadcodeA}{(HeadcodeB is { } b ? "/" + b : string.Empty)} {Where}{(To is { } to ? "–" + to : string.Empty)} {When} {SeveritySeconds}s";
}

/// <summary>The conflicts in a plan at one moment, by time (<see cref="Conflict.When"/>). Immutable.</summary>
public sealed class ConflictSet
{
    private Dictionary<string, Conflict>? _byId;

    public ConflictSet(IEnumerable<Conflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        All = conflicts.OrderBy(c => c.When).ThenBy(c => c.Kind).ThenBy(c => c.Id, StringComparer.Ordinal).ToArray();
    }

    public static ConflictSet Empty { get; } = new([]);

    public IReadOnlyList<Conflict> All { get; }

    public int Count => All.Count;

    public Conflict? Find(string id)
    {
        _byId ??= All.ToDictionary(c => c.Id, StringComparer.Ordinal);
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>The conflicts a train is in.</summary>
    public IEnumerable<Conflict> Involving(TrainId train) => All.Where(c => c.Involves(train));
}
