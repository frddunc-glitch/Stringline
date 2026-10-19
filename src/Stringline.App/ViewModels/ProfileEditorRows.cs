using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.App.ViewModels;

/// <summary>
/// A row in one of the profile editor's grids. Setting an editable property asks the editor to make the edit; if it is
/// refused, the row shows its old value again and the editor shows why. Each row's <c>Load</c> updates it from the
/// profile without editing anything.
/// </summary>
internal abstract class ProfileEditorRow(ProfileEditorViewModel editor) : ObservableObject
{
    protected ProfileEditorViewModel Editor { get; } = editor;

    /// <summary>Set a field from the profile and tell the grid, without making an edit.</summary>
    protected void Show<T>(ref T field, T value, string property)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(property);
        }
    }

    /// <summary>Make an edit from a cell; if it is refused, the cell goes back to <paramref name="current"/>.</summary>
    protected void Edit<T>(T typed, T current, string property, Func<StringlineProfile, StringlineProfile> edit)
    {
        if (EqualityComparer<T>.Default.Equals(typed, current))
        {
            return;
        }

        if (!Editor.TryEdit(edit))
        {
            // The grid has the typed text; tell it to read the old value back.
            OnPropertyChanged(property);
        }
    }
}

/// <summary>A location's display name, on the General tab.</summary>
internal sealed class LocationNameRow(ProfileEditorViewModel editor, Tiploc tiploc) : ProfileEditorRow(editor)
{
    private string _name = string.Empty;
    private string _corridors = string.Empty;

    public Tiploc Tiploc { get; } = tiploc;

    public string Code => Tiploc.Code;

    public string Name
    {
        get => _name;
        set => Edit(value?.Trim() ?? string.Empty, _name, nameof(Name), p => p.WithLocationName(Tiploc, value));
    }

    /// <summary>The corridors the location is on, e.g. "Main, Windsor".</summary>
    public string Corridors => _corridors;

    public void Load(StringlineProfile profile)
    {
        Show(ref _name, profile.LocationNames.TryGetValue(Tiploc, out var name) ? name : string.Empty, nameof(Name));
        Show(ref _corridors, string.Join(", ", profile.Corridors.Where(c => c.Contains(Tiploc)).Select(c => c.Name)), nameof(Corridors));
    }
}

/// <summary>A corridor in the Corridors tab's list: its tab name is edited here.</summary>
internal sealed class CorridorRow(ProfileEditorViewModel editor, string id) : ProfileEditorRow(editor)
{
    private string _name = string.Empty;
    private string _summary = string.Empty;

    public string Id { get; } = id;

    public string Name
    {
        get => _name;
        set => Edit(value?.Trim() ?? string.Empty, _name, nameof(Name), p => p.RenameCorridor(Id, value ?? string.Empty));
    }

    /// <summary>"WLOO – WIMB, 8 locations".</summary>
    public string Summary => _summary;

    public void Load(Corridor corridor, StringlineProfile profile)
    {
        Show(ref _name, corridor.Name, nameof(Name));
        var ends = corridor.Locations.Count == 0 ? "no locations"
            : $"{profile.DisplayName(corridor.Locations[0].Tiploc)} – {profile.DisplayName(corridor.Locations[^1].Tiploc)}, {corridor.Locations.Count} locations";
        Show(ref _summary, ends, nameof(Summary));
    }
}

/// <summary>A location on the selected corridor.</summary>
internal sealed class CorridorLocationRow(ProfileEditorViewModel editor, string corridorId, int index) : ProfileEditorRow(editor)
{
    private Tiploc _tiploc;
    private string _name = string.Empty;
    private string _distance = string.Empty;
    private string _platforms = string.Empty;
    private string _position = string.Empty;
    private bool _timingPointOnly;

    public int Index { get; } = index;

    public string Code => _tiploc.Code;

    public Tiploc Tiploc => _tiploc;

    /// <summary>The display name, shared by every corridor the location is on.</summary>
    public string Name
    {
        get => _name;
        set => Edit(value?.Trim() ?? string.Empty, _name, nameof(Name), p => p.WithLocationName(_tiploc, value));
    }

    /// <summary>km, blank for none.</summary>
    public string Distance
    {
        get => _distance;
        set => Edit(value?.Trim() ?? string.Empty, _distance, nameof(Distance), p => p.WithDistance(corridorId, Index, ProfileEditorText.ParseKm(value)));
    }

    public string Platforms
    {
        get => _platforms;
        set => Edit(value?.Trim() ?? string.Empty, _platforms, nameof(Platforms), p => p.WithPlatforms(corridorId, Index, ProfileEditorText.ParseCount(value, "platforms")));
    }

    /// <summary>Where it sits on the axis, as a percentage of the corridor.</summary>
    public string Position => _position;

    public bool IsTimingPointOnly => _timingPointOnly;

    public void Load(CorridorLocation location, StringlineProfile profile)
    {
        if (_tiploc != location.Tiploc)
        {
            _tiploc = location.Tiploc;
            OnPropertyChanged(nameof(Code));
            OnPropertyChanged(nameof(Tiploc));
        }

        Show(ref _name, profile.LocationNames.TryGetValue(location.Tiploc, out var name) ? name : string.Empty, nameof(Name));
        Show(ref _distance, location.DistanceKm is { } km ? km.ToString("0.###", CultureInfo.CurrentCulture) : string.Empty, nameof(Distance));
        Show(ref _platforms, location.Platforms.ToString(CultureInfo.CurrentCulture), nameof(Platforms));
        Show(ref _position, location.AxisPosition.ToString("P0", CultureInfo.CurrentCulture), nameof(Position));
        Show(ref _timingPointOnly, location.IsTimingPointOnly, nameof(IsTimingPointOnly));
    }
}

/// <summary>A section of the selected corridor: its tracks and headway.</summary>
internal sealed class SectionRow(ProfileEditorViewModel editor, string corridorId, int index) : ProfileEditorRow(editor)
{
    private string _label = string.Empty;
    private string _layout = string.Empty;
    private string _tracks = string.Empty;
    private string _headway = string.Empty;
    private string _minRun = string.Empty;

    public int Index { get; } = index;

    /// <summary>"Waterloo – Vauxhall".</summary>
    public string Label => _label;

    /// <summary>The preset's name, or <see cref="TrackLayouts.Custom"/>. Picking a preset sets the tracks.</summary>
    public string Layout
    {
        get => _layout;
        set
        {
            if (TrackLayouts.Presets.FirstOrDefault(p => p.Name == value) is { } preset)
            {
                Edit(value, _layout, nameof(Layout), p => p.WithTracks(corridorId, Index, preset.Tracks));
            }
            else
            {
                // Custom: the tracks text is edited instead.
                OnPropertyChanged(nameof(Layout));
            }
        }
    }

    /// <summary>The tracks as text, <c>Up:Up, Down:Down</c>.</summary>
    public string Tracks
    {
        get => _tracks;
        set => Edit(value?.Trim() ?? string.Empty, _tracks, nameof(Tracks), p => p.WithTracks(corridorId, Index,
            TrackLayouts.TryParse(value, out var tracks, out var error) ? tracks : throw new ProfileEditException(error!)));
    }

    /// <summary>The headway as <c>m:ss</c>.</summary>
    public string Headway
    {
        get => _headway;
        set => Edit(value?.Trim() ?? string.Empty, _headway, nameof(Headway), p => p.WithHeadway(corridorId, Index, ProfileEditorText.ParseDuration(value, "headway")));
    }

    /// <summary>The fastest booked run, from the WTT: "1:30 (12 runs)".</summary>
    public string MinRun => _minRun;

    public bool IsSingleLine { get; private set; }

    public void Load(Section section, StringlineProfile profile)
    {
        Show(ref _label, $"{profile.DisplayName(section.From)} – {profile.DisplayName(section.To)}", nameof(Label));
        Show(ref _layout, TrackLayouts.Describe(section.Tracks), nameof(Layout));
        Show(ref _tracks, TrackLayouts.Format(section.Tracks), nameof(Tracks));
        Show(ref _headway, ProfileEditorText.FormatDuration(section.DefaultHeadwaySeconds), nameof(Headway));
        var runs = section.Samples == 1 ? "1 run" : $"{section.Samples} runs";
        Show(ref _minRun, $"{ProfileEditorText.FormatDuration(section.MinRunSeconds)} ({runs})", nameof(MinRun));
        if (IsSingleLine != section.IsSingleLine)
        {
            IsSingleLine = section.IsSingleLine;
            OnPropertyChanged(nameof(IsSingleLine));
        }
    }
}

/// <summary>A category's style, on the Categories tab.</summary>
internal sealed class CategoryRow(ProfileEditorViewModel editor, string categoryId) : ProfileEditorRow(editor)
{
    private CategoryStyle _style = CategoryStyle.Fallback;
    private string _name = string.Empty;
    private string _colour = string.Empty;
    private string _priority = string.Empty;
    private int _trains;

    public string Id { get; } = categoryId;

    public string Name
    {
        get => _name;
        set => Edit(value?.Trim() ?? string.Empty, _name, nameof(Name), p => p.WithCategoryStyle(_style with { Name = value?.Trim() ?? string.Empty }));
    }

    /// <summary><c>#RRGGBB</c>.</summary>
    public string Colour
    {
        get => _colour;
        set => Edit(value?.Trim() ?? string.Empty, _colour, nameof(Colour), p => p.WithCategoryStyle(_style with
        {
            Colour = Network.Profiles.Colour.TryParse(value, out var c) ? c : throw new ProfileEditException($"'{value?.Trim()}' is not a colour: use #RRGGBB, e.g. #D32F2F."),
        }));
    }

    public LineStyle Style
    {
        get => _style.Style;
        set => Edit(value, _style.Style, nameof(Style), p => p.WithCategoryStyle(_style with { Style = value }));
    }

    /// <summary>0–100; higher matters more when regulating.</summary>
    public string Priority
    {
        get => _priority;
        set => Edit(value?.Trim() ?? string.Empty, _priority, nameof(Priority), p => p.WithCategoryStyle(_style with { Priority = ProfileEditorText.ParseCount(value, "priority") }));
    }

    /// <summary>How many timetabled trains are in the category.</summary>
    public int Trains => _trains;

    public void Load(CategoryStyle style, int trains)
    {
        var styleChanged = _style.Style != style.Style;
        _style = style;
        Show(ref _name, style.Name, nameof(Name));
        Show(ref _colour, style.Colour.ToString(), nameof(Colour));
        Show(ref _priority, style.Priority.ToString(CultureInfo.CurrentCulture), nameof(Priority));
        Show(ref _trains, trains, nameof(Trains));
        if (styleChanged)
        {
            OnPropertyChanged(nameof(Style));
        }
    }
}

/// <summary>A location mark on the axis preview.</summary>
/// <param name="Position">0 at the corridor's Up end, 1 at the other.</param>
internal sealed record AxisMark(string Label, double Position, bool IsStation);

/// <summary>How the editor reads and writes numbers and durations.</summary>
internal static class ProfileEditorText
{
    /// <summary>A distance in km; blank for none.</summary>
    public static double? ParseKm(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().TrimEnd('k', 'K', 'm', 'M').Trim();
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out var km) || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out km)
            ? km
            : throw new ProfileEditException($"'{text.Trim()}' is not a distance in km.");
    }

    public static int ParseCount(string? text, string what) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var n)
            ? n
            : throw new ProfileEditException($"The {what} must be a whole number.");

    /// <summary>
    /// A duration: <c>m:ss</c>, whole or decimal minutes (<c>3</c>, <c>2.5</c>), or seconds with an <c>s</c>
    /// (<c>90s</c>).
    /// </summary>
    public static int ParseDuration(string? text, string what)
    {
        var t = text?.Trim() ?? string.Empty;
        var colon = t.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && int.TryParse(t[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            && int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s < 60 && t.Length - colon - 1 == 2)
        {
            return (m * 60) + s;
        }

        if (t.EndsWith('s') && int.TryParse(t[..^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds;
        }

        if (double.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes) && minutes < 24 * 60)
        {
            return (int)Math.Round(minutes * 60);
        }

        throw new ProfileEditException($"'{t}' is not a {what}: use m:ss (3:00), minutes (2.5) or seconds (90s).");
    }

    public static string FormatDuration(int seconds) => string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
}
