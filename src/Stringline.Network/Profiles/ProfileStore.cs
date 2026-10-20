using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stringline.Network.Profiles;

/// <summary>A profile file that cannot be used: not JSON, missing required values, inconsistent, or from a newer Stringline.</summary>
public sealed class ProfileFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Profile JSON: camelCase, indented with LF line ends on every platform, enums as names, colours as <c>#RRGGBB</c>,
/// nulls left out. Reading allows comments and trailing commas (for hand edits) and ignores unknown properties.
/// <para>
/// A version 1 profile (before S27) has no headway sources. Its sections at the old standard 3 minutes are taken as
/// defaults, so the WTT's headway can now apply; any other headway was set by hand and stays. It is then read as the
/// current version.
/// </para>
/// </summary>
public static class ProfileJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static string Serialize(StringlineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, Options) + "\n";
    }

    /// <exception cref="ProfileFormatException">The text is not a usable profile.</exception>
    public static StringlineProfile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        StringlineProfile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<StringlineProfile>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ProfileFormatException($"Not a valid profile: {ex.Message}", ex);
        }

        if (profile is null)
        {
            throw new ProfileFormatException("Not a valid profile: the file is empty (null).");
        }

        Validate(profile);
        return Upgraded(profile);
    }

    private static StringlineProfile Upgraded(StringlineProfile profile) => profile.Version >= 2 ? profile : profile with
    {
        Version = StringlineProfile.CurrentVersion,
        Corridors = profile.Corridors.Select(c => c with
        {
            Sections = c.Sections.Select(s => s.DefaultHeadwaySeconds == Section.StandardHeadwaySeconds ? s : s with { HeadwaySource = HeadwaySource.Hand }).ToList(),
        }).ToList(),
    };

    private static void Validate(StringlineProfile profile)
    {
        if (ProfileValidator.FindProblem(profile) is { } problem)
        {
            throw new ProfileFormatException(problem);
        }
    }
}

/// <summary>
/// Profiles on disk, one file per sim: <c>{directory}/{area_id}.json</c>. The default directory is
/// <c>%APPDATA%\Stringline\profiles</c> (on Linux, <c>~/.config/Stringline/profiles</c>).
/// </summary>
public sealed class ProfileStore(string directory)
{
    public ProfileStore()
        : this(DefaultDirectory)
    {
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Stringline", "profiles");

    public string Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>The file for this sim. The area ID is lower-cased, and characters a file name cannot hold become <c>_</c>.</summary>
    public string PathFor(string areaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        var invalid = Path.GetInvalidFileNameChars();
        var name = new StringBuilder(areaId.Length);
        foreach (var c in areaId.Trim().ToLowerInvariant())
        {
            name.Append(invalid.Contains(c) || c is '/' or '\\' or ':' or '.' ? '_' : c);
        }

        return Path.Combine(Directory, name + ".json");
    }

    public bool Exists(string areaId) => File.Exists(PathFor(areaId));

    /// <summary>The saved profile for this sim, or null if there is none.</summary>
    /// <exception cref="ProfileFormatException">The file exists but is not a usable profile.</exception>
    public StringlineProfile? Load(string areaId)
    {
        var path = PathFor(areaId);
        if (!File.Exists(path))
        {
            return null;
        }

        var profile = ProfileJson.Deserialize(File.ReadAllText(path, Encoding.UTF8));
        if (!string.Equals(profile.AreaId, areaId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new ProfileFormatException($"{path} is the profile for '{profile.AreaId}', not '{areaId}'.");
        }

        return profile;
    }

    /// <summary>Saves the profile under its area ID, replacing any earlier one atomically. Returns the path.</summary>
    public string Save(StringlineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var path = PathFor(profile.AreaId);
        System.IO.Directory.CreateDirectory(Directory);
        var temp = path + ".tmp";
        File.WriteAllText(temp, ProfileJson.Serialize(profile), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
        return path;
    }
}
