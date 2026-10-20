using Stringline.Timetable;

namespace Stringline.Network.Profiles;

/// <summary>
/// Settles a sim name saved before S31 (<see cref="StringlineProfile.HasUnsettledName"/>). Profiles made before S22 have
/// the title of the timetable they were made from as their name, and nothing records which timetable that was; since
/// S22 a name could only be typed. So the titles of the sim's other timetables in the loaded one's folder are read
/// (Fred, S31): a name that is one of them counts as no name from then on, and any other name is kept as typed.
/// </summary>
public static class SimNames
{
    /// <summary>
    /// The profile with its name settled against the timetables for its sim in <paramref name="timetablePath"/>'s folder
    /// and below (only their headers are read, <see cref="WttCatalogue.FindForArea"/>). Unchanged, and nothing read, when
    /// there is nothing to settle, so a profile is searched for at most once.
    /// </summary>
    public static StringlineProfile Settle(StringlineProfile profile, string timetablePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrEmpty(timetablePath);
        if (!profile.HasUnsettledName || string.IsNullOrWhiteSpace(profile.AreaId))
        {
            return profile;
        }

        var folder = Path.GetDirectoryName(Path.GetFullPath(timetablePath)) ?? string.Empty;
        return profile.SettleName(WttCatalogue.FindForArea(folder, profile.AreaId, cancellationToken).Select(f => f.Name));
    }
}
