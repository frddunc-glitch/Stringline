using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// The Profile menu (S14a): edit the sim's profile, open its file, reload it and regenerate its corridors. A changed
/// profile is saved, then used without reloading the WTT: the forecast network, corridor tabs and graph are rebuilt,
/// and the working plan, disruptions and selection are kept.
/// </summary>
internal sealed partial class MainViewModel
{
    private bool _rebuildingTabs;
    private string? _tabBeforeRebuild;

    /// <summary>A timetable (and so a profile) is loaded.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditProfileCommand), nameof(OpenProfileFileCommand), nameof(ReloadProfileCommand), nameof(RegenerateCorridorsCommand), nameof(ExportProfileCommand), nameof(ImportProfileCommand))]
    public partial bool HasTimetable { get; private set; }

    /// <summary>Opens the profile editor on the loaded timetable's profile.</summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private void EditProfile()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        var suggested = loaded.NeedsSimId ? Snapshot.AreaId : null;
        _dialogs.ShowProfileEditor(new ProfileEditorViewModel(loaded.Profile, loaded.Timetable, _dialogs, ApplyEditedProfile, loaded.NeedsSimId, suggested, _settings.LearnMinSamples));
    }

    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private void OpenProfileFile()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        if (loaded.NeedsSimId)
        {
            ShowInfo(InfoSeverity.Warning, NotSavedYet);
            return;
        }

        var path = _loader.Profiles.PathFor(loaded.Profile.AreaId);
        if (!File.Exists(path))
        {
            ShowInfo(InfoSeverity.Warning, $"There is no profile file at {path} yet.");
            return;
        }

        if (_dialogs.OpenInShell(path) is { } error)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot open {path}: {error}");
        }
        else
        {
            ShowInfo(InfoSeverity.Info, $"Opened {path}. After saving changes there, use Tools ▸ Reload profile.");
        }
    }

    /// <summary>Re-reads the saved profile (after editing its file by hand, say) and redraws, without reloading the WTT.</summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private void ReloadProfile()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        if (loaded.NeedsSimId)
        {
            ShowInfo(InfoSeverity.Warning, NotSavedYet);
            return;
        }

        StringlineProfile? saved;
        try
        {
            saved = _loader.Profiles.Load(loaded.Profile.AreaId);
        }
        catch (Exception ex) when (ex is ProfileFormatException or IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot reload the profile: {ex.Message}");
            return;
        }

        if (saved is null)
        {
            ShowInfo(InfoSeverity.Warning, $"There is no saved profile for {loaded.Profile.AreaId}.");
            return;
        }

        UseProfile(saved.Refresh(loaded.Timetable));
        ShowInfo(InfoSeverity.Info, $"Reloaded the profile from {_loader.Profiles.PathFor(saved.AreaId)}.");
    }

    /// <summary>
    /// Replaces the corridors with freshly generated ones. If any were changed by hand, asks whether to replace those
    /// too or keep them.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private async Task RegenerateCorridorsAsync()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        var profile = loaded.Profile;
        var tuned = await Task.Run(() => profile.HandTuned(loaded.Timetable));
        var keep = false;
        if (tuned.Count > 0)
        {
            var names = string.Join(", ", tuned.Select(c => c.Name));
            var choice = _dialogs.Choose(
                "Regenerate corridors",
                $"{(tuned.Count == 1 ? "This corridor differs" : "These corridors differ")} from the generated ones: {names}. Replace {(tuned.Count == 1 ? "it" : "them")} too, or keep {(tuned.Count == 1 ? "it" : "them")} beside the generated corridors?",
                ["Replace all", "Keep mine", "Cancel"]);
            if (choice is not (0 or 1))
            {
                return;
            }

            keep = choice == 1;
        }

        StringlineProfile regenerated;
        try
        {
            regenerated = await Task.Run(() => profile.RegenerateCorridors(loaded.Timetable, keep));
        }
        catch (ProfileEditException ex)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot regenerate the corridors: {ex.Message}");
            return;
        }

        if (!ReferenceEquals(_timetable, loaded))
        {
            return; // another timetable was loaded meanwhile
        }

        var saved = loaded.NeedsSimId ? null : SaveProfile(regenerated);
        UseProfile(regenerated);
        ShowInfo(saved is null || saved.Error is null ? InfoSeverity.Info : InfoSeverity.Warning,
            $"Regenerated {regenerated.Corridors.Count} corridors{(keep ? $", keeping {tuned.Count} of yours" : string.Empty)}." + (saved?.Error is { } error ? $" {error}" : string.Empty));
    }

    /// <summary>
    /// Tools ▸ Export profile… (S37): the sim's profile, with what the running session has learned so far, to a JSON
    /// file to share.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private async Task ExportProfileAsync()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        if (loaded.NeedsSimId)
        {
            ShowInfo(InfoSeverity.Warning, NotSavedYet);
            return;
        }

        await LearnNowAsync();
        var profile = _timetable?.Profile ?? loaded.Profile;
        SaveExport("Export the profile", FileFilters.Profile, ExportName("profile", null, ".json"), "the profile",
            path => File.WriteAllText(path, ProfileJson.Serialize(profile)));
    }

    /// <summary>
    /// Tools ▸ Import profile… (S37): a shared profile for this sim. Merge keeps this profile and pools the shared one's
    /// learned places into it (places set by hand here stay put); Replace uses the shared profile instead.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasTimetable))]
    private async Task ImportProfileAsync()
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        if (loaded.NeedsSimId)
        {
            ShowInfo(InfoSeverity.Warning, NotSavedYet);
            return;
        }

        var folder = _settings.LastExportFolder is { Length: > 0 } last && Directory.Exists(last) ? last : _paths.Exports;
        if (_dialogs.PickFile("Import a profile", FileFilters.Profile, folder + Path.DirectorySeparatorChar) is not { } path)
        {
            return;
        }

        StringlineProfile imported;
        try
        {
            imported = ProfileJson.Deserialize(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is ProfileFormatException or IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot import {Path.GetFileName(path)}: {ex.Message}");
            return;
        }

        if (!string.Equals(imported.AreaId, loaded.Profile.AreaId, StringComparison.OrdinalIgnoreCase))
        {
            ShowInfo(InfoSeverity.Error, $"{Path.GetFileName(path)} is the profile for '{imported.AreaId}', but this timetable's sim is '{loaded.Profile.AreaId}'.");
            return;
        }

        var places = imported.Places;
        var choice = _dialogs.Choose(
            "Import profile",
            $"{Path.GetFileName(path)} has {places.Signals.Count} signals, {places.TrackCircuits.Count} track circuits and {places.Berths.Count} berths learned. Merge them into this profile (keeping its corridors, names and styles, and places you set by hand), or replace this profile with the shared one?",
            ["Merge", "Replace", "Cancel"]);
        if (choice is not (0 or 1) || _timetable is not { } current || !ReferenceEquals(current.Timetable, loaded.Timetable))
        {
            return;
        }

        await LearnNowAsync();
        current = _timetable ?? current;
        var profile = choice == 0
            ? current.Profile.WithPlaces(current.Profile.Places.Pool(places))
            : Replaced(imported.Refresh(current.Timetable), current.Profile);
        var saved = SaveProfile(profile);
        UseProfile(profile);
        var what = choice == 0 ? "Merged the learned places of" : "Replaced the profile with";
        ShowInfo(saved.Error is null ? InfoSeverity.Info : InfoSeverity.Warning, $"{what} {Path.GetFileName(path)}." + (saved.Error is { } error ? $" {error}" : string.Empty));
    }

    /// <summary>A shared profile in place of this one, still knowing which sessions this one learned from, so they are not learned again.</summary>
    private static StringlineProfile Replaced(StringlineProfile shared, StringlineProfile mine) =>
        shared.WithPlaces(shared.Places.Pool(SignallingPlaces.Empty with { Sessions = mine.Places.Sessions }));

    private const string NotSavedYet = "This timetable has no sim ID, so its profile is not saved yet: name the sim in Tools ▸ Edit profile… first.";

    /// <summary>The editor's Apply: name the sim if it needs it, save, then use the profile.</summary>
    private ProfileApplyResult ApplyEditedProfile(StringlineProfile draft)
    {
        if (_timetable is not { } loaded)
        {
            return new ProfileApplyResult(null, "No timetable is loaded.");
        }

        var profile = draft;
        var naming = loaded.NeedsSimId;
        if (naming && _loader.Profiles.Exists(draft.AreaId))
        {
            var choice = _dialogs.Choose(
                "Name the sim",
                $"A profile for '{draft.AreaId}' is saved already. Use the saved profile for this timetable, or replace it with this one?",
                ["Use the saved profile", "Replace it", "Cancel"]);
            if (choice == 0)
            {
                try
                {
                    profile = _loader.Profiles.Load(draft.AreaId)!.Refresh(loaded.Timetable);
                }
                catch (Exception ex) when (ex is ProfileFormatException or IOException or UnauthorizedAccessException)
                {
                    return new ProfileApplyResult(null, $"Cannot read the saved profile: {ex.Message}");
                }
            }
            else if (choice != 1)
            {
                return new ProfileApplyResult(null, null);
            }
        }

        if (SaveProfile(profile) is { Error: { } error })
        {
            return new ProfileApplyResult(null, error);
        }

        if (naming)
        {
            _settings = _settings.WithSimId(loaded.Path, profile.AreaId);
            _settingsStore.Save(_settings);
        }

        UseProfile(profile);
        _ = LearnQuietlyAsync();
        return new ProfileApplyResult(profile, $"Saved to {_loader.Profiles.PathFor(profile.AreaId)}.");
    }

    private sealed record SaveResult(string? Error);

    private SaveResult SaveProfile(StringlineProfile profile)
    {
        try
        {
            _loader.Profiles.Save(profile);
            return new SaveResult(null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SaveResult($"The profile could not be saved: {ex.Message}");
        }
    }

    /// <summary>
    /// Uses a changed profile with the loaded timetable: the forecast network, the corridor tabs (a removed corridor's
    /// tab closes; the one shown stays, with its view, if it is still there), the train list's colours and the graph.
    /// </summary>
    private void UseProfile(StringlineProfile profile)
    {
        if (_timetable is not { } loaded)
        {
            return;
        }

        profile = WithGlobalHeadway(profile);
        _timetable = loaded with { Profile = profile, NeedsSimId = false, ProfileNote = null };
        _engine.SetForecast(ForecastNetwork.For(profile, loaded.Timetable, _settings.LearnMinSamples));
        RebuildCorridorTabs(profile);
        Trains.Update(Snapshot, profile);
        UpdateTitle();
        UpdatePlaceholder();
        UpdateEditing();
        RequestScene();
    }

    /// <summary>The tabs for the profile's corridors, keeping the selected one if it is still there.</summary>
    private void RebuildCorridorTabs(StringlineProfile profile)
    {
        var previous = SelectedCorridor?.Id;
        _rebuildingTabs = true;
        _tabBeforeRebuild = previous;
        try
        {
            Corridors.Clear();
            foreach (var corridor in profile.Corridors)
            {
                Corridors.Add(new CorridorTab(corridor));
            }

            SelectedCorridor = Corridors.FirstOrDefault(c => c.Id == previous) ?? Corridors.FirstOrDefault();
            RefreshBoardCorridors();
            Regulation.SetLocations(profile);
        }
        finally
        {
            _rebuildingTabs = false;
            _tabBeforeRebuild = null;
        }
    }

    /// <summary>While the tabs are rebuilt, the corridor that stays selected keeps its view.</summary>
    private bool KeepsViewOnTabChange(CorridorTab? value) => _rebuildingTabs && (value is null || value.Id == _tabBeforeRebuild);
}
