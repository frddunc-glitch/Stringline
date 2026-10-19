using System.IO;
using Microsoft.Extensions.Logging;
using Stringline.Network.Profiles;
using Stringline.Timetable;

namespace Stringline.App.Services;

/// <summary>A timetable read from disk with the profile to draw it.</summary>
/// <param name="ProfileNote">Why the profile is not the saved one (e.g. the file was damaged), if so.</param>
/// <param name="NeedsSimId">
/// The timetable has no sim ID and the user has not named its sim yet, so the profile (<see cref="TimetableLoader.UnnamedSim"/>)
/// is not saved until they do (S14a).
/// </param>
internal sealed record LoadedTimetable(string Path, WttImportResult Import, StringlineProfile Profile, string? ProfileNote, bool NeedsSimId = false)
{
    public Core.Timetables.WorkingTimetable Timetable => Import.Timetable;
}

/// <summary>
/// Reads a WTT and brings its sim's profile up to date (<c>progress.md</c> S6 notes): load the saved profile and
/// refresh it against the timetable, or create one; then save it. A damaged profile file is left alone and a
/// generated profile is used for this session. A timetable with no sim ID uses the name the user gave it, if any.
/// </summary>
internal sealed partial class TimetableLoader(ProfileStore profiles, ILoggerFactory loggerFactory)
{
    /// <summary>The area ID of a profile for a timetable with no sim ID, until the user names the sim.</summary>
    public const string UnnamedSim = "unknown";

    private readonly ILogger _logger = loggerFactory.CreateLogger<TimetableLoader>();

    public ProfileStore Profiles => profiles;

    /// <param name="simId">The sim name the user gave this timetable, used when it has no sim ID of its own.</param>
    /// <exception cref="WttFormatException">The file is not a SimSig timetable.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public LoadedTimetable Load(string path, string? simId = null)
    {
        var import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(path);
        var tt = import.Timetable;
        var area = tt.AreaId is { Length: > 0 } own && !string.IsNullOrWhiteSpace(own) ? own : simId?.Trim();
        if (string.IsNullOrWhiteSpace(area))
        {
            return new LoadedTimetable(path, import, StringlineProfile.Create(tt) with { AreaId = UnnamedSim },
                "The timetable has no sim ID, so its profile is not saved until you name the sim (Profile → Edit profile…).", NeedsSimId: true);
        }

        StringlineProfile profile;
        string? note = null;
        try
        {
            profile = profiles.Load(area) is { } saved ? saved.Refresh(tt) : StringlineProfile.Create(tt) with { AreaId = area };
        }
        catch (ProfileFormatException ex)
        {
            LogProfileDamaged(ex, profiles.PathFor(area));
            return new LoadedTimetable(path, import, StringlineProfile.Create(tt) with { AreaId = area },
                $"The saved profile could not be read ({ex.Message}); using generated corridors without saving.");
        }

        try
        {
            profiles.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogProfileNotSaved(ex, profiles.PathFor(area));
            note = $"The profile could not be saved: {ex.Message}";
        }

        return new LoadedTimetable(path, import, profile, note);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The profile {Path} is damaged; using a generated one")]
    private partial void LogProfileDamaged(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the profile {Path}")]
    private partial void LogProfileNotSaved(Exception ex, string path);
}
