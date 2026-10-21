using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Network;
using Stringline.Network.Profiles;

namespace Stringline.App.ViewModels;

/// <summary>What applying the editor's profile did.</summary>
/// <param name="Profile">The profile now in use (the saved one, if the user chose it when naming the sim), or null if nothing was applied.</param>
/// <param name="Message">Where it was saved, or why it was not.</param>
internal sealed record ProfileApplyResult(StringlineProfile? Profile, string? Message);

/// <summary>A generated corridor offered for adding.</summary>
internal sealed record CorridorChoice(Corridor Corridor, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// The profile editor (S14a, COR-2, COR-5): the sim's name and location names (with CSV import), the corridors with
/// their locations and sections, the category styles, and the learned signal places (S37, the Signals tab). Every change is a <see cref="ProfileEdits"/> edit on a draft,
/// so a change that would make the profile invalid is refused with its reason and the draft never goes wrong. Apply
/// (or OK) hands the draft to the main window, which saves it and redraws. Touches no WPF types.
/// </summary>
internal sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly WorkingTimetable _timetable;
    private readonly IDialogService _dialogs;
    private readonly Func<StringlineProfile, ProfileApplyResult> _apply;
    private readonly IReadOnlyList<CorridorCandidate> _generated;
    private readonly IReadOnlySet<Tiploc> _known;
    private StringlineProfile _applied;
    private bool _syncing;

    /// <param name="apply">Saves and uses a profile; called by Apply and OK.</param>
    /// <param name="needsSimId">The timetable has no sim ID: the user must name the sim before the profile can be saved.</param>
    public ProfileEditorViewModel(
        StringlineProfile profile,
        WorkingTimetable timetable,
        IDialogService dialogs,
        Func<StringlineProfile, ProfileApplyResult> apply,
        bool needsSimId = false,
        string? suggestedSimId = null,
        int minSamples = SignallingPlaces.DefaultMinSamples)
    {
        ArgumentNullException.ThrowIfNull(profile);
        MinSamples = minSamples;
        _timetable = timetable;
        _dialogs = dialogs;
        _apply = apply;
        _applied = profile;
        Draft = profile.WithBlockHeadways(minSamples);
        NeedsSimId = needsSimId;
        SimId = needsSimId ? suggestedSimId ?? string.Empty : profile.AreaId;
        _generated = CorridorGenerator.Generate(timetable);
        _known = timetable.Trains.SelectMany(t => t.Points).Select(p => p.Location).Where(l => !l.IsEmpty).ToHashSet();
        Sync();
        SelectedCorridor = Corridors.FirstOrDefault();
    }

    /// <summary>Raised when the window should close (after OK, or Cancel).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The profile as edited so far.</summary>
    public StringlineProfile Draft { get; private set; }

    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>The sim must be named before the profile is saved (its timetable has no sim ID).</summary>
    [ObservableProperty]
    public partial bool NeedsSimId { get; private set; }

    /// <summary>The sim's ID: the timetable's own, or the name the user gives it when it has none.</summary>
    [ObservableProperty]
    public partial string SimId { get; set; }

    /// <summary>Why the last change was refused, if it was.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The outcome of the last import or apply.</summary>
    [ObservableProperty]
    public partial string? Status { get; private set; }

    public string Title => $"Profile — {Draft.Name ?? Draft.AreaId}";

    /// <summary>The sim's display name.</summary>
    public string SimName
    {
        get => Draft.Name ?? string.Empty;
        set
        {
            if (value?.Trim() != (Draft.Name ?? string.Empty) && !TryEdit(p => p.WithName(value)))
            {
                OnPropertyChanged();
            }
        }
    }

    /// <summary>The profile's default headway as m:ss; blank uses the global one (#7).</summary>
    public string DefaultHeadway
    {
        get => Draft.DefaultHeadwaySeconds is { } h ? ProfileEditorText.FormatDuration(h) : string.Empty;
        set
        {
            if (value?.Trim() != DefaultHeadway && !TryEdit(p => p.WithDefaultHeadway(string.IsNullOrWhiteSpace(value) ? null : ProfileEditorText.ParseDuration(value, "default headway"))))
            {
                OnPropertyChanged();
            }
        }
    }

    public string DefaultHeadwayHint =>
        $"For sections where the WTT books trains no closer. Blank uses the global default in Tools ▸ Options ({ProfileEditorText.FormatDuration(Draft.GlobalHeadwaySeconds)}). A headway set on a section wins.";

    public ObservableCollection<LocationNameRow> LocationNames { get; } = [];

    public ObservableCollection<CorridorRow> Corridors { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCorridorCommand), nameof(MoveCorridorUpCommand), nameof(MoveCorridorDownCommand), nameof(AddLocationCommand))]
    public partial CorridorRow? SelectedCorridor { get; set; }

    /// <summary>Generated corridors not yet in the profile.</summary>
    public ObservableCollection<CorridorChoice> Candidates { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddCandidateCommand))]
    public partial CorridorChoice? SelectedCandidate { get; set; }

    public ObservableCollection<CorridorLocationRow> Locations { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveLocationCommand), nameof(MoveLocationUpCommand), nameof(MoveLocationDownCommand))]
    public partial CorridorLocationRow? SelectedLocation { get; set; }

    public ObservableCollection<SectionRow> Sections { get; } = [];

    /// <summary>The selected corridor's locations on its axis, for the preview.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AxisMark> AxisMarks { get; private set; } = [];

    /// <summary>Space the selected corridor by the entered distances rather than by run time.</summary>
    public bool UseDistances
    {
        get => CurrentCorridor?.Axis == AxisMode.Distance;
        set
        {
            if (_syncing || CurrentCorridor is not { } corridor || value == (corridor.Axis == AxisMode.Distance))
            {
                return;
            }

            if (!TryEdit(p => p.WithAxis(corridor.Id, value ? AxisMode.Distance : AxisMode.Derived)))
            {
                OnPropertyChanged();
            }
        }
    }

    public ObservableCollection<CategoryRow> Categories { get; } = [];

    public IReadOnlyList<LineStyle> LineStyles { get; } = Enum.GetValues<LineStyle>();

    /// <summary>The track presets' names, then <see cref="TrackLayouts.Custom"/>.</summary>
    public IReadOnlyList<string> LayoutNames { get; } = [.. TrackLayouts.Presets.Select(p => p.Name), TrackLayouts.Custom];

    private Corridor? CurrentCorridor => SelectedCorridor is { } row ? Draft.FindCorridor(row.Id) : null;

    /// <summary>Make an edit on the draft. If it is refused, the draft stays as it was and <see cref="Error"/> says why.</summary>
    public bool TryEdit(Func<StringlineProfile, StringlineProfile> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        StringlineProfile next;
        try
        {
            next = edit(Draft);
        }
        catch (ProfileEditException ex)
        {
            Error = ex.Message;
            return false;
        }

        Error = null;
        Status = null;
        SetDraft(next);
        return true;
    }

    /// <summary>Asks before discarding unapplied changes. True if the window may close.</summary>
    public bool CanClose() =>
        !IsDirty || _dialogs.Choose("Profile", "Discard your changes to the profile?", ["Discard", "Keep editing"]) == 0;

    partial void OnSelectedCorridorChanged(CorridorRow? value)
    {
        Locations.Clear();
        Sections.Clear();
        SyncCorridor();
    }

    [RelayCommand]
    private void ImportNames()
    {
        if (_dialogs.PickFile("Import location names", FileFilters.Csv, null) is not { } path)
        {
            return;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Cannot read {Path.GetFileName(path)}: {ex.Message}";
            return;
        }

        var import = LocationNamesCsv.Parse(text, _known);
        if (!TryEdit(p => p.WithLocationNames(import.Names)))
        {
            return;
        }

        var status = $"Imported {import.Names.Count} names from {Path.GetFileName(path)}.";
        if (import.Unknown.Count > 0)
        {
            status += $" {import.Unknown.Count} are for locations not in this timetable ({string.Join(", ", import.Unknown.Take(5))}{(import.Unknown.Count > 5 ? ", …" : string.Empty)}).";
        }

        if (import.Problems.Count > 0)
        {
            status += $" {import.Problems.Count} rows were skipped.";
            _dialogs.ShowList("Location names import", $"{Path.GetFileName(path)}: {import.Problems.Count} rows skipped", import.Problems);
        }

        Status = status;
    }

    [RelayCommand(CanExecute = nameof(HasCandidate))]
    private void AddCandidate()
    {
        if (SelectedCandidate is { } choice && TryEdit(p => p.AddCorridor(choice.Corridor)))
        {
            Select(choice.Corridor.Id);
        }
    }

    [RelayCommand]
    private void NewCorridor()
    {
        var options = LocationOptions(null);
        var prompt = new PromptViewModel(
            "New corridor",
            "A corridor from its Up end to its other end. Add the locations between them afterwards.",
            new PromptField("Name", string.Empty),
            new PromptField("Up end", string.Empty, options),
            new PromptField("Other end", string.Empty, options))
        {
            Validate = p => string.IsNullOrWhiteSpace(p[0]) ? "The corridor needs a name."
                : ParseLocation(p[1]) is null ? "Choose the Up end."
                : ParseLocation(p[2]) is null ? "Choose the other end."
                : null,
        };
        if (!_dialogs.Prompt(prompt))
        {
            return;
        }

        var id = ProfileEdits.NewCorridorId(Draft, prompt[0]);
        if (TryEdit(p => p.AddCorridor(prompt[0], ParseLocation(prompt[1])!.Value, ParseLocation(prompt[2])!.Value, _timetable)))
        {
            Select(id);
        }
    }

    [RelayCommand(CanExecute = nameof(HasCorridor))]
    private void RemoveCorridor()
    {
        if (SelectedCorridor is not { } row)
        {
            return;
        }

        var index = Corridors.IndexOf(row);
        if (TryEdit(p => p.RemoveCorridor(row.Id)))
        {
            SelectedCorridor = Corridors.Count == 0 ? null : Corridors[Math.Min(index, Corridors.Count - 1)];
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveCorridorUp))]
    private void MoveCorridorUp() => MoveCorridor(-1);

    [RelayCommand(CanExecute = nameof(CanMoveCorridorDown))]
    private void MoveCorridorDown() => MoveCorridor(1);

    /// <summary>Puts a location on the selected corridor after the selected location (or at the end).</summary>
    [RelayCommand(CanExecute = nameof(HasCorridor))]
    private void AddLocation()
    {
        if (CurrentCorridor is not { } corridor)
        {
            return;
        }

        var after = SelectedLocation?.Index ?? corridor.Locations.Count - 1;
        var where = after >= 0 && after < corridor.Locations.Count ? $"after {Draft.DisplayName(corridor.Locations[after].Tiploc)}" : "at the end";
        var prompt = new PromptViewModel(
            "Add location",
            $"Add a location to {corridor.Name}, {where}.",
            new PromptField("Location", string.Empty, LocationOptions(corridor), "A TIPLOC, or pick one the timetable uses."))
        {
            Validate = p => ParseLocation(p[0]) is not { } t ? "Choose a location."
                : corridor.Contains(t) ? $"{t} is already on the corridor." : null,
        };
        if (!_dialogs.Prompt(prompt))
        {
            return;
        }

        var index = after + 1;
        if (TryEdit(p => p.InsertLocation(corridor.Id, index, ParseLocation(prompt[0])!.Value, _timetable)))
        {
            SelectedLocation = index < Locations.Count ? Locations[index] : null;
        }
    }

    [RelayCommand(CanExecute = nameof(HasLocation))]
    private void RemoveLocation()
    {
        if (CurrentCorridor is not { } corridor || SelectedLocation is not { } row)
        {
            return;
        }

        if (TryEdit(p => p.RemoveLocation(corridor.Id, row.Index, _timetable)))
        {
            SelectedLocation = Locations.Count == 0 ? null : Locations[Math.Min(row.Index, Locations.Count - 1)];
        }
    }

    [RelayCommand(CanExecute = nameof(CanMoveLocationUp))]
    private void MoveLocationUp() => MoveLocation(-1);

    [RelayCommand(CanExecute = nameof(CanMoveLocationDown))]
    private void MoveLocationDown() => MoveLocation(1);

    [RelayCommand]
    private void Apply() => TryApply();

    /// <summary>Saves and uses the draft. Returns true if it was applied.</summary>
    private bool TryApply()
    {
        var draft = Draft;
        if (NeedsSimId)
        {
            try
            {
                draft = draft.WithAreaId(SimId);
            }
            catch (ProfileEditException ex)
            {
                Error = "Name the sim on the General tab before saving: " + ex.Message;
                return false;
            }

            if (string.Equals(draft.AreaId, TimetableLoader.UnnamedSim, StringComparison.OrdinalIgnoreCase))
            {
                Error = $"Give the sim a name other than '{TimetableLoader.UnnamedSim}'.";
                return false;
            }
        }

        var result = _apply(draft);
        if (result.Profile is not { } applied)
        {
            Error = result.Message;
            return false;
        }

        _applied = applied;
        NeedsSimId = false;
        SimId = applied.AreaId;
        Error = null;
        SetDraft(applied);
        IsDirty = false;
        Status = result.Message;
        return true;
    }

    [RelayCommand]
    private void Ok()
    {
        if ((!IsDirty && !NeedsSimId) || TryApply())
        {
            IsDirty = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (CanClose())
        {
            IsDirty = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool HasCandidate() => SelectedCandidate is not null;

    private bool HasCorridor() => SelectedCorridor is not null;

    private bool HasLocation() => SelectedLocation is not null;

    private bool CanMoveCorridorUp() => SelectedCorridor is { } row && Corridors.IndexOf(row) > 0;

    private bool CanMoveCorridorDown() => SelectedCorridor is { } row && Corridors.IndexOf(row) is var i && i >= 0 && i < Corridors.Count - 1;

    private bool CanMoveLocationUp() => SelectedLocation is { Index: > 0 };

    private bool CanMoveLocationDown() => SelectedLocation is { } row && row.Index < Locations.Count - 1;

    private void MoveCorridor(int by)
    {
        if (SelectedCorridor is { } row && TryEdit(p => p.MoveCorridor(row.Id, Corridors.IndexOf(row) + by)))
        {
            Select(row.Id);
        }
    }

    private void MoveLocation(int by)
    {
        if (CurrentCorridor is not { } corridor || SelectedLocation is not { } row)
        {
            return;
        }

        var to = row.Index + by;
        if (TryEdit(p => p.MoveLocation(corridor.Id, row.Index, to, _timetable)))
        {
            SelectedLocation = to >= 0 && to < Locations.Count ? Locations[to] : null;
        }
    }

    private void Select(string corridorId) => SelectedCorridor = Corridors.FirstOrDefault(c => c.Id == corridorId);

    private void SetDraft(StringlineProfile next)
    {
        var titleChanged = next.Name != Draft.Name || next.AreaId != Draft.AreaId;
        var headwayChanged = next.DefaultHeadwaySeconds != Draft.DefaultHeadwaySeconds;
        Draft = next.WithBlockHeadways(MinSamples); // places, corridors or aspects may have changed (S39)
        IsDirty = true;
        Sync();
        if (titleChanged)
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(SimName));
        }

        if (headwayChanged)
        {
            OnPropertyChanged(nameof(DefaultHeadway));
        }
    }

    /// <summary>Brings every list up to date with the draft, updating rows in place where they are still the same rows.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            SyncCorridors();
            SyncLocationNames();
            SyncCategories();
            SyncCandidates();
            SyncCorridor();
            SyncPlaces();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncCorridors()
    {
        var corridors = Draft.Corridors;
        if (!Corridors.Select(r => r.Id).SequenceEqual(corridors.Select(c => c.Id)))
        {
            var selected = SelectedCorridor?.Id;
            Corridors.Clear();
            foreach (var corridor in corridors)
            {
                Corridors.Add(new CorridorRow(this, corridor.Id));
            }

            SelectedCorridor = Corridors.FirstOrDefault(r => r.Id == selected) ?? (selected is null ? null : Corridors.FirstOrDefault());
        }

        for (var i = 0; i < corridors.Count; i++)
        {
            Corridors[i].Load(corridors[i], Draft);
        }

        MoveCorridorUpCommand.NotifyCanExecuteChanged();
        MoveCorridorDownCommand.NotifyCanExecuteChanged();
    }

    private void SyncLocationNames()
    {
        var tiplocs = _known
            .Concat(Draft.LocationNames.Keys)
            .Concat(Draft.Corridors.SelectMany(c => c.Locations).Select(l => l.Tiploc))
            .Distinct()
            .OrderBy(t => t.Code, StringComparer.Ordinal)
            .ToList();
        if (!LocationNames.Select(r => r.Tiploc).SequenceEqual(tiplocs))
        {
            LocationNames.Clear();
            foreach (var tiploc in tiplocs)
            {
                LocationNames.Add(new LocationNameRow(this, tiploc));
            }
        }

        foreach (var row in LocationNames)
        {
            row.Load(Draft);
        }
    }

    private void SyncCategories()
    {
        var styles = Draft.Categories;
        if (!Categories.Select(r => r.Id).SequenceEqual(styles.Select(s => s.CategoryId)))
        {
            Categories.Clear();
            foreach (var style in styles)
            {
                Categories.Add(new CategoryRow(this, style.CategoryId));
            }
        }

        var trains = _timetable.Trains.Where(t => t.CategoryId is not null).GroupBy(t => t.CategoryId!).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < styles.Count; i++)
        {
            Categories[i].Load(styles[i], trains.GetValueOrDefault(styles[i].CategoryId));
        }
    }

    private void SyncCandidates()
    {
        var offered = _generated
            .Select(c => c.Corridor)
            .Where(g => Draft.FindCorridor(g.Id) is null
                && !Draft.Corridors.Any(c => c.Locations.Select(l => l.Tiploc).SequenceEqual(g.Locations.Select(l => l.Tiploc))))
            .ToList();
        if (Candidates.Select(c => c.Corridor.Id).SequenceEqual(offered.Select(c => c.Id)))
        {
            return;
        }

        Candidates.Clear();
        foreach (var corridor in offered)
        {
            Candidates.Add(new CorridorChoice(corridor,
                $"{corridor.Name}: {Draft.DisplayName(corridor.Locations[0].Tiploc)} – {Draft.DisplayName(corridor.Locations[^1].Tiploc)}, {corridor.Locations.Count} locations"));
        }

        SelectedCandidate = Candidates.FirstOrDefault();
    }

    /// <summary>The selected corridor's locations, sections, axis mode and preview.</summary>
    private void SyncCorridor()
    {
        var corridor = CurrentCorridor;
        var wasSyncing = _syncing;
        _syncing = true;
        try
        {
            if (corridor is null)
            {
                Locations.Clear();
                Sections.Clear();
                AxisMarks = [];
            }
            else
            {
                if (Locations.Count != corridor.Locations.Count)
                {
                    var selected = SelectedLocation?.Index;
                    Locations.Clear();
                    for (var i = 0; i < corridor.Locations.Count; i++)
                    {
                        Locations.Add(new CorridorLocationRow(this, corridor.Id, i));
                    }

                    SelectedLocation = selected is { } s && s < Locations.Count ? Locations[s] : null;
                }

                for (var i = 0; i < corridor.Locations.Count; i++)
                {
                    Locations[i].Load(corridor.Locations[i], Draft);
                }

                if (Sections.Count != corridor.Sections.Count)
                {
                    Sections.Clear();
                    for (var i = 0; i < corridor.Sections.Count; i++)
                    {
                        Sections.Add(new SectionRow(this, corridor.Id, i));
                    }
                }

                for (var i = 0; i < corridor.Sections.Count; i++)
                {
                    Sections[i].Load(corridor.Sections[i], Draft);
                }

                AxisMarks = corridor.Locations
                    .Select(l => new AxisMark(Draft.DisplayName(l.Tiploc), l.AxisPosition, !l.Minor && (l.Platforms > 0 || !l.IsTimingPointOnly), l.Minor))
                    .ToList();
            }

            OnPropertyChanged(nameof(UseDistances));
            MoveLocationUpCommand.NotifyCanExecuteChanged();
            MoveLocationDownCommand.NotifyCanExecuteChanged();
        }
        finally
        {
            _syncing = wasSyncing;
        }
    }

    /// <summary>Locations to offer: the timetable's, less those already on <paramref name="corridor"/>, as "Name (TIPLOC)".</summary>
    private List<string> LocationOptions(Corridor? corridor) =>
        _known
            .Where(t => corridor is null || !corridor.Contains(t))
            .Select(t => Draft.DisplayName(t) is var name && name != t.Code ? $"{name} ({t.Code})" : t.Code)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>A location typed or picked: a TIPLOC, "Name (TIPLOC)", or a display name.</summary>
    private Tiploc? ParseLocation(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
        {
            return null;
        }

        if (t.EndsWith(')') && t.LastIndexOf('(') is var open and >= 0 && Tiploc.TryCreate(t[(open + 1)..^1], out var inner))
        {
            return inner;
        }

        foreach (var (tiploc, name) in Draft.LocationNames)
        {
            if (string.Equals(name, t, StringComparison.OrdinalIgnoreCase))
            {
                return tiploc;
            }
        }

        return Tiploc.TryCreate(t, out var code) ? code : null;
    }
}
