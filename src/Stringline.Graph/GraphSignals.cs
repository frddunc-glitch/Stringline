using System.Globalization;
using Stringline.Core;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.Graph;

/// <summary>
/// Placed signals on the location axis (S38, View ▸ Signals): where their ticks and rows are, which one is under the
/// mouse, and what its tooltip says.
/// </summary>
public static class GraphSignals
{
    /// <summary>A tick's length into the plot, in pixels.</summary>
    public const float TickLength = 6;

    /// <summary>How near a tick or row, in pixels, the mouse must be to be over it.</summary>
    public const double HoverReach = 3;

    /// <summary>
    /// The signal under a pixel: on a tick (within its length of the plot's edge, Down on the labels' side and Up on the
    /// other), or anywhere along a row. The nearest along the axis wins. Null if none, or the signals are off.
    /// </summary>
    public static GraphSignal? SignalAt(GraphScene scene, GraphViewport viewport, ScreenPoint at)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(viewport);
        if (scene.SignalDisplay == SignalDisplay.Off || scene.Signals.Count == 0 || !viewport.IsInPlot(at))
        {
            return null;
        }

        var across = viewport.TimePixelOf(at);
        var (near, far) = (viewport.TimeOrigin, viewport.TimeOrigin + viewport.TimeLength);
        var along = viewport.AxisPixelOf(at);
        GraphSignal? best = null;
        var bestDistance = HoverReach;
        foreach (var signal in scene.Signals)
        {
            if (scene.SignalDisplay == SignalDisplay.Ticks)
            {
                var edge = signal.Direction == TrackDirection.Down ? near : far;
                if (Math.Abs(across - edge) > TickLength + HoverReach)
                {
                    continue;
                }
            }

            var distance = Math.Abs(along - viewport.AxisToPixel(signal.Axis));
            if (distance <= bestDistance)
            {
                (best, bestDistance) = (signal, distance);
            }
        }

        return best;
    }

    /// <summary>The tooltip over a signal: its direction, between which timing locations, how far, and how it was learned.</summary>
    public static HoverInfo Describe(GraphSignal signal, GraphScene scene)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(scene);
        string Name(Tiploc t) => scene.Locations.FirstOrDefault(l => l.Tiploc == t)?.Name ?? t.Code;
        var lines = new List<HoverLine>
        {
            new("Signal", $"{signal.Direction}, {Name(signal.From)} → {Name(signal.To)}"),
            new("Place", string.Create(CultureInfo.InvariantCulture, $"{signal.Fraction * 100:0}% of the run ({SignallingPlaces.Name(signal.Run)})")),
            new("Learned", signal.HandSet ? "set by hand" : $"from {signal.Samples} {(signal.Samples == 1 ? "train" : "trains")}"),
        };
        return new HoverInfo(signal.Id, lines);
    }
}
