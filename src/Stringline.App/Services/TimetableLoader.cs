using System.IO;
using Microsoft.Extensions.Logging;
using Stringline.Network.Profiles;
using Stringline.Timetable;

namespace Stringline.App.Services;

/// <summary>A timetable read from disk with the profile to draw it.</summary>
/// <param name="ProfileNote">Why the profile is not the saved one (e.g. the file was damaged), if so.</param>
internal sealed record LoadedTimetable(string Path, WttImportResult Import, StringlineProfile Profile, string? ProfileNote)
{
    public Core.Timetables.WorkingTimetable Timetable => Import.Timetable;
}

/// <summary>
/// Reads a WTT and brings its sim's profile up to date (<c>progress.md</c> S6 notes): load the saved profile and
/// refresh it against the timetable, or create one; then save it. A damaged profile file is left alone and a
/// generated profile is used for this session.
/// </summary>
internal sealed partial class TimetableLoader(ProfileStore profiles, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<TimetableLoader>();

    /// <exception cref="WttFormatException">The file is not a SimSig timetable.</exception>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public LoadedTimetable Load(string path)
    {
        var import = new WttReader(loggerFactory.CreateLogger<WttReader>()).Read(path);
        var tt = import.Timetable;
        if (string.IsNullOrWhiteSpace(tt.AreaId))
        {
            return new LoadedTimetable(path, import, StringlineProfile.Create(tt) with { AreaId = "unknown" }, "The timetable has no sim ID, so its profile is not saved.");
        }

        StringlineProfile profile;
        string? note = null;
        try
        {
            profile = profiles.Load(tt.AreaId) is { } saved ? saved.Refresh(tt) : StringlineProfile.Create(tt);
        }
        catch (ProfileFormatException ex)
        {
            LogProfileDamaged(ex, profiles.PathFor(tt.AreaId));
            return new LoadedTimetable(path, import, StringlineProfile.Create(tt), $"The saved profile could not be read ({ex.Message}); using generated corridors without saving.");
        }

        try
        {
            profiles.Save(profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogProfileNotSaved(ex, profiles.PathFor(tt.AreaId));
            note = $"The profile could not be saved: {ex.Message}";
        }

        return new LoadedTimetable(path, import, profile, note);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The profile {Path} is damaged; using a generated one")]
    private partial void LogProfileDamaged(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the profile {Path}")]
    private partial void LogProfileNotSaved(Exception ex, string path);
}
