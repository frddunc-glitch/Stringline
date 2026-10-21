using Stringline.Core;
using Stringline.Graph;
using Stringline.Network.Profiles;

namespace Stringline.Benchmarks;

/// <summary>
/// A busy made-up corridor for the GR-9 budget: <c>trains</c> trains spread over a 6 hour window, half Down and half
/// Up, each with a WTT path and, if it has started by now, an actual path coloured by delay band segment by segment
/// (the most runs per path), and a live head for each train running at now. Seeded, so every run draws the same scene.
/// </summary>
public static class SyntheticGraph
{
    public const int Locations = 30;

    /// <summary>The window: 06:00 to 12:00, with now at 08:00 (a third of the way, as the app follows).</summary>
    public static readonly SimTime Start = SimTime.FromClock(6, 0);

    public static readonly SimTime End = SimTime.FromClock(12, 0);

    public static readonly SimTime Now = SimTime.FromClock(8, 0);

    private static readonly Colour[] Categories =
    [
        new(0xD3, 0x2F, 0x2F), new(0x19, 0x76, 0xD2), new(0x38, 0x8E, 0x3C), new(0x7B, 0x1F, 0xA2), new(0xF5, 0x7C, 0x00), new(0x45, 0x5A, 0x64),
    ];

    public static GraphScene Create(int trains = 500, int seed = 42)
    {
        var random = new Random(seed);
        var locations = Enumerable.Range(0, Locations)
            .Select(i => new GraphLocation(new Tiploc($"LOC{i:00}"), $"Location {i}", i / (double)(Locations - 1), i % 3 == 0, i % 3 == 0 ? 2 : 0))
            .ToList();

        var paths = new List<GraphPath>();
        var heads = new List<GraphLiveHead>();
        var span = End.Seconds - Start.Seconds;
        for (var t = 0; t < trains; t++)
        {
            var down = t % 2 == 0;
            var headcode = new Headcode($"{1 + (t % 9)}{(char)('A' + (t % 26))}{t % 100:00}");
            var id = new TrainId($"T{t:0000}");
            var colour = Categories[t % Categories.Length];

            // Departures spread over the window, starting an hour early so the left edge is busy too.
            var time = (double)(Start.Seconds - 3600 + (t * (span + 3600) / trains));
            var booked = new List<GraphVertex>();
            var actual = new List<GraphVertex>();
            var delays = new List<int>();
            var delay = random.Next(-60, 120);
            for (var i = 0; i < Locations; i++)
            {
                var axis = locations[down ? i : Locations - 1 - i].Axis;
                var stops = i % 3 == 0 && i > 0 && i < Locations - 1;
                booked.Add(new GraphVertex(time, axis));
                actual.Add(new GraphVertex(time + delay, axis));
                delays.Add(delay);
                if (stops)
                {
                    time += 60;
                    booked.Add(new GraphVertex(time, axis));
                    delay = Math.Max(-60, delay + random.Next(-30, 90));
                    actual.Add(new GraphVertex(time + delay, axis));
                    delays.Add(delay);
                }

                time += random.Next(90, 150);
                delay = Math.Max(-60, delay + random.Next(-20, 60));
            }

            paths.Add(new GraphPath($"wtt:{id}", GraphLayer.Wtt, id, headcode, colour, booked, Direction: down ? Network.TrackDirection.Down : Network.TrackDirection.Up));

            var seen = actual.TakeWhile(v => v.Time <= Now.Seconds).Count();
            if (seen >= 2)
            {
                var segments = Enumerable.Range(1, seen - 1).Select(i => DelayBands.ColourOfSeconds(delays[i])).ToList();
                paths.Add(new GraphPath($"act:{t}/0", GraphLayer.Actual, id, headcode, segments[^1], actual.Take(seen).ToList(),
                    Direction: down ? Network.TrackDirection.Down : Network.TrackDirection.Up, LiveUid: t.ToString(System.Globalization.CultureInfo.InvariantCulture), Working: 0,
                    SegmentColours: segments));
                if (seen < actual.Count)
                {
                    // A live head (S36) from its last report towards the next point, drawn every frame from the clock.
                    heads.Add(new GraphLiveHead($"head:{id}", id, headcode, segments[^1], LineStyle.Solid, [actual[seen - 1], actual[seen]]));
                }
            }
        }

        return new GraphScene("main", "Synthetic — 500 trains", locations, paths, Now) { LiveHeads = heads };
    }

    /// <summary>The window above on a full-HD surface.</summary>
    public static GraphViewport Viewport(GraphOrientation orientation = GraphOrientation.Horizontal) =>
        GraphViewport.Create(Start.Seconds, End.Seconds, 1920, 1080, orientation: orientation);
}
