using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.Core;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>A section offered on the Signals tab: two locations next to each other on a corridor, or that places were learned between.</summary>
internal sealed record PlaceSectionChoice(Tiploc A, Tiploc B, string Text)
{
    public bool Is(Tiploc a, Tiploc b) => (A == a && B == b) || (A == b && B == a);

    public override string ToString() => Text;
}

/// <summary>
/// The profile editor's Signals tab (S37, #19): per section, the learned signals, track circuits and berths in order,
/// with their place, samples, spread and state. A place can be moved (it is then hand-set), deleted or added by hand,
/// and the learned places of a section or of the whole sim can be reset.
/// </summary>
internal sealed partial class ProfileEditorViewModel
{
    /// <summary>The kinds offered when adding a place, in <see cref="SignallingKind"/> order.</summary>
    public static IReadOnlyList<string> PlaceKindNames { get; } = [.. Enum.GetValues<SignallingKind>().Select(k => Capital(SignallingPlaces.Name(k)))];

    /// <summary>The kinds of run offered when adding a place, in <see cref="RunKind"/> order.</summary>
    public static IReadOnlyList<string> RunKindNames { get; } = [.. Enum.GetValues<RunKind>().Select(r => Capital(SignallingPlaces.Name(r)))];

    /// <summary>Samples a place needs to be used (Tools ▸ Options ▸ Profile).</summary>
    public int MinSamples { get; private init; } = SignallingPlaces.DefaultMinSamples;

    public ObservableCollection<PlaceSectionChoice> PlaceSections { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddPlaceCommand), nameof(ResetSectionPlacesCommand))]
    public partial PlaceSectionChoice? SelectedPlaceSection { get; set; }

    /// <summary>The selected section's places, the Up-to-Down direction first (as the section is named), each in order along the run.</summary>
    public ObservableCollection<SignallingPlaceRow> Places { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeletePlaceCommand))]
    public partial SignallingPlaceRow? SelectedPlace { get; set; }

    /// <summary>What has been learned, e.g. "Signals: 115 used, 17 learning. … From 3 sessions."</summary>
    [ObservableProperty]
    public partial string PlacesSummary { get; private set; } = string.Empty;

    partial void OnSelectedPlaceSectionChanged(PlaceSectionChoice? value)
    {
        Places.Clear();
        SyncPlaceRows();
    }

    /// <summary>Adds a place to the selected section by hand.</summary>
    [RelayCommand(CanExecute = nameof(HasPlaceSection))]
    private void AddPlace()
    {
        if (SelectedPlaceSection is not { } section)
        {
            return;
        }

        var directions = new[] { Direction(section.A, section.B), Direction(section.B, section.A) };
        var prompt = new PromptViewModel(
            "Add a place",
            $"A signal, track circuit or berth between {Draft.DisplayName(section.A)} and {Draft.DisplayName(section.B)}, set by hand: learning never moves it.",
            new PromptField("Kind", PlaceKindNames[0], PlaceKindNames),
            new PromptField("ID", string.Empty, null, "As SimSig names it: S457, T810 or 0457."),
            new PromptField("Running", directions[0], directions),
            new PromptField("Trains", RunKindNames[0], RunKindNames, "Trains running through, or starting or stopping at either end."),
            new PromptField("Place", "0.5", null, "0 at the first location, 1 at the second, in run time."))
        {
            Validate = p => IndexIn(PlaceKindNames, p[0]) < 0 ? "Choose a kind."
                : p[1].Length == 0 ? "Give the ID."
                : Array.IndexOf(directions, p[2]) < 0 ? "Choose the direction."
                : IndexIn(RunKindNames, p[3]) < 0 ? "Choose which trains."
                : TryFraction(p[4], out _) ? null : "The place must be from 0 to 1.",
        };
        if (!_dialogs.Prompt(prompt))
        {
            return;
        }

        var kind = (SignallingKind)IndexIn(PlaceKindNames, prompt[0]);
        var (from, to) = Array.IndexOf(directions, prompt[2]) == 0 ? (section.A, section.B) : (section.B, section.A);
        var run = (RunKind)IndexIn(RunKindNames, prompt[3]);
        TryFraction(prompt[4], out var fraction);
        var id = prompt[1];
        if (TryEdit(p => p.WithPlaces(p.Places.SetByHand(kind, id, from, to, fraction, run, p.Corridors))))
        {
            SelectedPlace = Places.FirstOrDefault(r => r.Kind == kind && r.Place.Is(id, from, to, run));
        }
    }

    [RelayCommand(CanExecute = nameof(HasPlace))]
    private void DeletePlace()
    {
        if (SelectedPlace is { } row)
        {
            TryEdit(p => p.WithPlaces(p.Places.Remove(row.Kind, row.Place.Id, row.Place.From, row.Place.To, row.Place.Run)));
        }
    }

    /// <summary>Forgets the learned places of the selected section (both directions); hand-set ones stay.</summary>
    [RelayCommand(CanExecute = nameof(HasPlaceSection))]
    private void ResetSectionPlaces()
    {
        if (SelectedPlaceSection is { } section
            && _dialogs.Choose("Reset learned places", $"Forget the places learned between {Draft.DisplayName(section.A)} and {Draft.DisplayName(section.B)}? Places set by hand stay, and learning starts again from the next trains.", ["Reset", "Cancel"]) == 0)
        {
            TryEdit(p => p.WithPlaces(p.Places.Reset(section.A, section.B)));
        }
    }

    /// <summary>Forgets every learned place of the sim; hand-set ones stay.</summary>
    [RelayCommand]
    private void ResetAllPlaces()
    {
        if (_dialogs.Choose("Reset learned places", $"Forget every place learned for {Draft.Name ?? Draft.AreaId}? Places set by hand stay, and learning starts again from the next trains.", ["Reset all", "Cancel"]) == 0)
        {
            TryEdit(p => p.WithPlaces(p.Places.ResetAll()));
        }
    }

    private bool HasPlaceSection() => SelectedPlaceSection is not null;

    private bool HasPlace() => SelectedPlace is not null;

    /// <summary>The section list, the summary and the selected section's rows, from the draft.</summary>
    private void SyncPlaces()
    {
        var places = Draft.Places;
        var choices = new List<PlaceSectionChoice>();
        foreach (var corridor in Draft.Corridors)
        {
            foreach (var section in corridor.Sections)
            {
                if (!choices.Any(c => c.Is(section.From, section.To)))
                {
                    choices.Add(Choice(corridor.Name, section.From, section.To));
                }
            }
        }

        foreach (var (_, place) in places.All.OrderBy(p => p.Place.From.Code, StringComparer.Ordinal).ThenBy(p => p.Place.To.Code, StringComparer.Ordinal))
        {
            if (!choices.Any(c => c.Is(place.From, place.To)))
            {
                choices.Add(Choice("Off the corridors", place.From, place.To));
            }
        }

        if (!PlaceSections.SequenceEqual(choices))
        {
            var selected = SelectedPlaceSection;
            PlaceSections.Clear();
            foreach (var choice in choices)
            {
                PlaceSections.Add(choice);
            }

            SelectedPlaceSection = (selected is null ? null : PlaceSections.FirstOrDefault(c => c.Is(selected.A, selected.B)))
                ?? PlaceSections.FirstOrDefault(c => Count(c) > 0) ?? PlaceSections.FirstOrDefault();
        }

        var sessions = places.Sessions.Count == 1 ? "1 session" : $"{places.Sessions.Count} sessions";
        PlacesSummary = places.Count == 0
            ? "Nothing learned yet. Places are learned from live and replayed sessions (Tools ▸ Options ▸ Profile)."
            : $"Signals: {PlaceCounts.Of(places.Signals, MinSamples)}. Track circuits: {PlaceCounts.Of(places.TrackCircuits, MinSamples)}. Berths: {PlaceCounts.Of(places.Berths, MinSamples)}. Learned from {sessions}; a place is used after {MinSamples} trains have passed it, close together.";
        SyncPlaceRows();

        PlaceSectionChoice Choice(string corridor, Tiploc a, Tiploc b)
        {
            var count = places.All.Count(p => (p.Place.From == a && p.Place.To == b) || (p.Place.From == b && p.Place.To == a));
            return new PlaceSectionChoice(a, b, $"{corridor}: {Draft.DisplayName(a)} – {Draft.DisplayName(b)}" + (count == 0 ? string.Empty : $" ({count})"));
        }

        int Count(PlaceSectionChoice c) => places.All.Count(p => c.Is(p.Place.From, p.Place.To));
    }

    private void SyncPlaceRows()
    {
        var wanted = SelectedPlaceSection is { } section
            ? Draft.Places.All
                .Where(p => section.Is(p.Place.From, p.Place.To))
                .OrderBy(p => p.Place.From != section.A).ThenBy(p => p.Place.Run).ThenBy(p => p.Place.Fraction).ThenBy(p => p.Kind)
                .ToList()
            : [];
        // The same places in another order (a place just moved): keep the rows where they are, so the grid does not
        // jump under the user; they are put in order when the section is picked again.
        var keys = Places.Select(r => (r.Kind, Id: r.Place.Id.ToUpperInvariant(), r.Place.From, r.Place.To, r.Place.Run)).ToList();
        var wantedKeys = wanted.Select(p => (p.Kind, Id: p.Place.Id.ToUpperInvariant(), p.Place.From, p.Place.To, p.Place.Run)).ToList();
        if (keys.Count == wantedKeys.Count && keys.ToHashSet().SetEquals(wantedKeys))
        {
            wanted = [.. keys.Select(k => wanted[wantedKeys.IndexOf(k)])];
        }
        else
        {
            var selected = SelectedPlace;
            Places.Clear();
            foreach (var (kind, place) in wanted)
            {
                Places.Add(new SignallingPlaceRow(this, kind, place));
            }

            SelectedPlace = selected is null ? null : Places.FirstOrDefault(r => r.Kind == selected.Kind && r.Place.Is(selected.Place.Id, selected.Place.From, selected.Place.To, selected.Place.Run));
        }

        for (var i = 0; i < wanted.Count; i++)
        {
            Places[i].Load(wanted[i].Place, Draft, MinSamples);
        }
    }

    private string Direction(Tiploc from, Tiploc to) => $"{Draft.DisplayName(from)} → {Draft.DisplayName(to)}";

    /// <summary>A place typed as a share (0.42) or a percentage (42%).</summary>
    internal static bool TryFraction(string? text, out double fraction)
    {
        var t = text?.Trim() ?? string.Empty;
        var percent = t.EndsWith('%');
        t = t.TrimEnd('%').Trim();
        if ((double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out fraction) || double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction))
            && !double.IsNaN(fraction))
        {
            fraction = percent ? fraction / 100 : fraction;
            return fraction is >= 0 and <= 1;
        }

        return false;
    }

    private static int IndexIn(IReadOnlyList<string> names, string name)
    {
        for (var i = 0; i < names.Count; i++)
        {
            if (string.Equals(names[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];
}

/// <summary>A learned (or hand-set) place, on the Signals tab.</summary>
internal sealed class SignallingPlaceRow(ProfileEditorViewModel editor, SignallingKind kind, SignallingPlace place) : ProfileEditorRow(editor)
{
    private string _fraction = string.Empty;
    private string _end = string.Empty;
    private string _direction = string.Empty;
    private string _samples = string.Empty;
    private string _spread = string.Empty;
    private string _state = string.Empty;
    private string _detail = string.Empty;

    public SignallingKind Kind { get; } = kind;

    /// <summary>The place as last loaded; its key (ID, locations, run) never changes for this row.</summary>
    public SignallingPlace Place { get; private set; } = place;

    public string KindText => ProfileEditorViewModel.PlaceKindNames[(int)Kind];

    public string Id => Place.Id;

    /// <summary>"Vauxhall → Queenstown Road".</summary>
    public string Direction => _direction;

    public string Run => ProfileEditorViewModel.RunKindNames[(int)Place.Run];

    /// <summary>The place, 0–1, to three places. Typing one sets it by hand.</summary>
    public string Fraction
    {
        get => _fraction;
        set => Edit(value?.Trim() ?? string.Empty, _fraction, nameof(Fraction), p => p.WithPlaces(p.Places.SetByHand(
            Kind, Place.Id, Place.From, Place.To,
            ProfileEditorViewModel.TryFraction(value, out var f) ? f : throw new ProfileEditException($"'{value?.Trim()}' is not a place: give a share from 0 to 1, such as 0.42."),
            Place.Run, p.Corridors)));
    }

    /// <summary>A track circuit's end, if known.</summary>
    public string End => _end;

    public string Samples => _samples;

    public string Spread => _spread;

    /// <summary>"Used", "Learning" or "Hand-set".</summary>
    public string State => _state;

    /// <summary>The line, the signal it goes with, and when it was last learned.</summary>
    public string Detail => _detail;

    public void Load(SignallingPlace place, StringlineProfile profile, int minSamples)
    {
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(profile);
        Place = place;
        Show(ref _direction, $"{profile.DisplayName(place.From)} → {profile.DisplayName(place.To)}", nameof(Direction));
        Show(ref _fraction, place.Fraction.ToString("0.000", CultureInfo.CurrentCulture), nameof(Fraction));
        Show(ref _end, place.EndFraction?.ToString("0.000", CultureInfo.CurrentCulture) ?? string.Empty, nameof(End));
        Show(ref _samples, place.SampleCount.ToString(CultureInfo.CurrentCulture), nameof(Samples));
        Show(ref _spread, place.Samples.Count < 2 ? string.Empty : place.Spread.ToString("0.000", CultureInfo.CurrentCulture), nameof(Spread));
        Show(ref _state, place.StateFor(minSamples) switch { PlaceState.Used => "Used", PlaceState.HandSet => "Hand-set", _ => "Learning" }, nameof(State));
        var detail = new List<string>();
        if (place.Line is { } line)
        {
            detail.Add($"line {line}");
        }

        if (place.Signal is { } signal)
        {
            detail.Add(Kind == SignallingKind.TrackCircuit ? $"after {signal}" : $"for {signal}");
        }

        if (place.LastLearned is { } at)
        {
            detail.Add($"last {at}");
        }

        Show(ref _detail, string.Join(", ", detail), nameof(Detail));
    }
}
