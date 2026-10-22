using System.Globalization;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>How many places of one kind are used, still learning, or set by hand.</summary>
public sealed record PlaceCounts(int Used, int Learning, int HandSet)
{
    public int Total => Used + Learning + HandSet;

    public static PlaceCounts Of(IEnumerable<SignallingPlace> places, int minSamples)
    {
        ArgumentNullException.ThrowIfNull(places);
        int used = 0, learning = 0, hand = 0;
        foreach (var place in places)
        {
            switch (place.StateFor(minSamples))
            {
                case PlaceState.Used:
                    used++;
                    break;
                case PlaceState.HandSet:
                    hand++;
                    break;
                default:
                    learning++;
                    break;
            }
        }

        return new PlaceCounts(used, learning, hand);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Used} used, {Learning} learning") + (HandSet > 0 ? string.Create(CultureInfo.InvariantCulture, $", {HandSet} hand-set") : string.Empty);
}

/// <summary>A profile after learning from a session, and what changed.</summary>
/// <param name="Added">New samples pooled in (none for a session already learned from).</param>
public sealed record LearnOutcome(StringlineProfile Profile, int Added, PlaceCounts Signals, PlaceCounts TrackCircuits, PlaceCounts Berths)
{
    /// <summary>One line for the log and the status bar.</summary>
    public string Describe(string sessionId) => string.Create(CultureInfo.InvariantCulture,
        $"Learned {Added} new samples from {sessionId}: signals {Signals}; track circuits {TrackCircuits}; berths {Berths}.");
}

/// <summary>Learning signal places (S37): the IDs a session is known by, and pooling what it taught into a profile.</summary>
public static class SignalLearning
{
    /// <summary>A replayed recording, by when it was made (so a renamed copy is the same recording).</summary>
    public static string RecordingId(DateTimeOffset created) => "recording " + Stamp(created);

    /// <summary>A live session, by when its session file was made (so resuming and reviewing it are the same session).</summary>
    public static string SessionId(DateTimeOffset created) => "session " + Stamp(created);

    /// <summary>
    /// The profile with the session's samples pooled into its places (<see cref="SignallingPlaces.Learn(IEnumerable{PlaceSample}, IEnumerable{RunSample}, string, Core.SimTime, IEnumerable{Network.Corridor}, out int)"/>), and its
    /// block headways measured from them again (S39, <see cref="StringlineProfile.WithBlockHeadways(int)"/>), and its
    /// axis spaced by the learned run times (<see cref="StringlineProfile.WithLearnedRuns"/>).
    /// </summary>
    public static LearnOutcome Apply(StringlineProfile profile, LearnedPlaces learned, string sessionId, int minSamples = SignallingPlaces.DefaultMinSamples)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(learned);
        var places = profile.Places;
        var added = 0;
        if (learned.UpTo is { } upTo)
        {
            places = places.Learn(learned.Samples, learned.RunTimes, sessionId, upTo, profile.Corridors, out added);
        }

        return new LearnOutcome(
            profile.WithPlaces(places).WithBlockHeadways(minSamples).WithLearnedRuns(minSamples),
            added,
            PlaceCounts.Of(places.Signals, minSamples),
            PlaceCounts.Of(places.TrackCircuits, minSamples),
            PlaceCounts.Of(places.Berths, minSamples));
    }

    private static string Stamp(DateTimeOffset created) => created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
