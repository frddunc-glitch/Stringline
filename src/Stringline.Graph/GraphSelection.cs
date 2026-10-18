using Stringline.Core;

namespace Stringline.Graph;

/// <summary>
/// The selected train, drawn heavier with a halo while everything else dims (<c>05-…md</c> §2). A selection names a
/// WTT train, a live train (sim uid), or both: a matched live train selects its WTT path too.
/// </summary>
public sealed record GraphSelection(TrainId? Train, string? LiveUid)
{
    /// <summary>The selection a path belongs to: its WTT train and, for an actual path, its live train.</summary>
    public static GraphSelection Of(GraphPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new GraphSelection(path.Train, path.LiveUid);
    }

    public bool Matches(GraphPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return (Train is { } train && path.Train == train) || (LiveUid is { } uid && path.LiveUid == uid);
    }
}
