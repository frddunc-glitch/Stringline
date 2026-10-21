namespace Stringline.Network.Profiles;

/// <summary>
/// The rules every profile must keep, whether read from a file (<see cref="ProfileJson"/>) or changed in the editor
/// (<see cref="ProfileEdits"/>): a known version, an area ID, and consistent corridors (no duplicate IDs or locations,
/// one section per adjacent pair of locations in order, every section with a track).
/// </summary>
public static class ProfileValidator
{
    /// <summary>The first rule the profile breaks, worded for the user, or null if it keeps them all.</summary>
    public static string? FindProblem(StringlineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.Version > StringlineProfile.CurrentVersion)
        {
            return $"The profile is version {profile.Version}, from a newer Stringline (this one reads up to {StringlineProfile.CurrentVersion}).";
        }

        if (string.IsNullOrWhiteSpace(profile.AreaId))
        {
            return "The profile has no areaId.";
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var corridor in profile.Corridors)
        {
            if (string.IsNullOrWhiteSpace(corridor.Id) || !ids.Add(corridor.Id))
            {
                return $"Corridor ID '{corridor.Id}' is empty or used twice.";
            }

            if (FindProblem(corridor) is { } problem)
            {
                return problem;
            }
        }

        return profile.Signalling?.FindProblem();
    }

    /// <summary>The first rule this corridor breaks, or null.</summary>
    public static string? FindProblem(Corridor corridor)
    {
        ArgumentNullException.ThrowIfNull(corridor);
        if (corridor.Locations.Select(l => l.Tiploc).Distinct().Count() != corridor.Locations.Count)
        {
            return $"Corridor {corridor.Id} has a location twice.";
        }

        var expected = Math.Max(0, corridor.Locations.Count - 1);
        if (corridor.Sections.Count != expected)
        {
            return $"Corridor {corridor.Id} has {corridor.Locations.Count} locations, so needs {expected} sections, but has {corridor.Sections.Count}.";
        }

        for (var i = 0; i < corridor.Sections.Count; i++)
        {
            var section = corridor.Sections[i];
            if (section.From != corridor.Locations[i].Tiploc || section.To != corridor.Locations[i + 1].Tiploc)
            {
                return $"Corridor {corridor.Id} section {i + 1} is {section.From}–{section.To}, but the locations there are {corridor.Locations[i].Tiploc}–{corridor.Locations[i + 1].Tiploc}.";
            }

            if (section.Tracks.Count == 0)
            {
                return $"Corridor {corridor.Id} section {section.From}–{section.To} has no tracks.";
            }
        }

        return null;
    }
}
