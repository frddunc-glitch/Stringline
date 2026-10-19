using System.Text;

namespace Stringline.Network.Profiles;

/// <summary>A named set of tracks offered when editing a section.</summary>
public sealed record TrackLayout(string Name, IReadOnlyList<TrackDef> Tracks)
{
    public override string ToString() => Name;
}

/// <summary>
/// The usual track layouts, and tracks as text for a custom layout: <c>Up Fast:Up, Up Slow:Up, Down:Down, Relief:Bi</c>.
/// Each entry is a name and, after a colon, a direction (<c>Up</c>, <c>Down</c> or <c>Bi</c>). Without a direction, a
/// name starting with Up or Down runs that way and any other is bi-directional.
/// </summary>
public static class TrackLayouts
{
    /// <summary>The name shown for tracks that match no preset.</summary>
    public const string Custom = "Custom";

    public static TrackLayout DoubleLine { get; } = new("Double line", Section.DoubleLine);

    public static TrackLayout SingleLine { get; } = new("Single line", Section.SingleLine);

    public static IReadOnlyList<TrackLayout> Presets { get; } =
    [
        DoubleLine,
        SingleLine,
        new("Two bi-directional", [new("Up", TrackDirection.BiDirectional), new("Down", TrackDirection.BiDirectional)]),
        new("Four tracks (fast and slow)",
        [
            new("Up Fast", TrackDirection.Up), new("Up Slow", TrackDirection.Up),
            new("Down Fast", TrackDirection.Down), new("Down Slow", TrackDirection.Down),
        ]),
        new("Up only", [new("Up", TrackDirection.Up)]),
        new("Down only", [new("Down", TrackDirection.Down)]),
    ];

    /// <summary>The preset these tracks are (same names, directions and order), or null.</summary>
    public static TrackLayout? PresetOf(IReadOnlyList<TrackDef> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return Presets.FirstOrDefault(p => p.Tracks.SequenceEqual(tracks));
    }

    /// <summary>The preset's name, or <see cref="Custom"/>.</summary>
    public static string Describe(IReadOnlyList<TrackDef> tracks) => PresetOf(tracks)?.Name ?? Custom;

    /// <summary>The tracks as text, e.g. <c>Up:Up, Down:Down</c>.</summary>
    public static string Format(IReadOnlyList<TrackDef> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var text = new StringBuilder();
        foreach (var track in tracks)
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(track.Name).Append(':').Append(track.Direction switch
            {
                TrackDirection.Up => "Up",
                TrackDirection.Down => "Down",
                _ => "Bi",
            });
        }

        return text.ToString();
    }

    /// <summary>Reads tracks written as <see cref="Format"/> writes them. Returns false with a reason if the text is not a list of tracks.</summary>
    public static bool TryParse(string? text, out IReadOnlyList<TrackDef> tracks, out string? error)
    {
        tracks = [];
        error = null;
        var list = new List<TrackDef>();
        foreach (var entry in (text ?? string.Empty).Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = entry.LastIndexOf(':');
            var name = (colon < 0 ? entry : entry[..colon]).Trim();
            if (name.Length == 0)
            {
                error = $"'{entry}' has no track name.";
                return false;
            }

            TrackDirection direction;
            if (colon < 0)
            {
                direction = name.StartsWith("Up", StringComparison.OrdinalIgnoreCase) ? TrackDirection.Up
                    : name.StartsWith("Down", StringComparison.OrdinalIgnoreCase) ? TrackDirection.Down
                    : TrackDirection.BiDirectional;
            }
            else if (ParseDirection(entry[(colon + 1)..].Trim()) is { } d)
            {
                direction = d;
            }
            else
            {
                error = $"'{entry[(colon + 1)..].Trim()}' is not a direction (Up, Down or Bi).";
                return false;
            }

            if (list.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"Track '{name}' is named twice.";
                return false;
            }

            list.Add(new TrackDef(name, direction));
        }

        if (list.Count == 0)
        {
            error = "A section needs at least one track.";
            return false;
        }

        tracks = list;
        return true;
    }

    private static TrackDirection? ParseDirection(string text) => text.ToUpperInvariant() switch
    {
        "UP" or "U" => TrackDirection.Up,
        "DOWN" or "DN" or "D" => TrackDirection.Down,
        "BI" or "B" or "BOTH" or "BIDIRECTIONAL" or "BI-DIRECTIONAL" or "BIDI" or "BI-DI" => TrackDirection.BiDirectional,
        _ => null,
    };
}
