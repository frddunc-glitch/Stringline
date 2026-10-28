using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Stringline.Core;
using Stringline.Editing.Bulk;
using Stringline.Network.Profiles;

namespace Stringline.Editing.Knowledge;

/// <summary>The platform, line and path codes typed at one location.</summary>
public sealed record TypedLocationCodes
{
    public ImmutableSortedSet<string> Platforms { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    public ImmutableSortedSet<string> Lines { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    public ImmutableSortedSet<string> Paths { get; init; } = ImmutableSortedSet.Create<string>(StringComparer.Ordinal);

    public ImmutableSortedSet<string> Of(TripCode code) => code switch
    {
        TripCode.Platform => Platforms,
        TripCode.Line => Lines,
        _ => Paths,
    };

    public TypedLocationCodes With(TripCode code, string value) => code switch
    {
        TripCode.Platform => this with { Platforms = Platforms.Add(value) },
        TripCode.Line => this with { Lines = Lines.Add(value) },
        _ => this with { Paths = Paths.Add(value) },
    };
}

/// <summary>
/// The platform, line and path codes typed in the timetable editor for one sim (#33), offered in its drop-downs from
/// then on. SimSig's <c>.sim</c> files cannot be read, so where no timetable or recording of the sim has used a code,
/// what the user has typed is the only source. They are offered only: the checks go on using the timetables and
/// recordings alone, so a mistyped code is not taken as proof that it exists.
/// </summary>
public sealed record TypedCodes
{
    public required string AreaId { get; init; }

    /// <summary>By TIPLOC.</summary>
    public ImmutableSortedDictionary<string, TypedLocationCodes> Locations { get; init; } = ImmutableSortedDictionary.Create<string, TypedLocationCodes>(StringComparer.Ordinal);

    public TypedLocationCodes? At(Tiploc location) => Locations.GetValueOrDefault(location.Code);

    /// <summary>These codes with one more; the same codes if it is there already.</summary>
    public TypedCodes With(Tiploc location, TripCode code, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var at = At(location) ?? new TypedLocationCodes();
        return at.Of(code).Contains(value.Trim()) ? this : this with { Locations = Locations.SetItem(location.Code, at.With(code, value.Trim())) };
    }
}

/// <summary>Typed codes on disk beside the profiles, one file per sim: <c>{directory}/{area}.codes.json</c>.</summary>
public sealed class TypedCodesStore(string directory)
{
    public const string Extension = ".codes.json";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public TypedCodesStore()
        : this(ProfileStore.DefaultDirectory)
    {
    }

    public string Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>The file for this sim, named as its profile (<see cref="ProfileStore.PathFor"/>).</summary>
    public string PathFor(string areaId) => Path.ChangeExtension(new ProfileStore(Directory).PathFor(areaId), null) + Extension;

    /// <summary>The codes typed for this sim; none if there is no file or it cannot be read (it is only a convenience).</summary>
    public TypedCodes Load(string areaId)
    {
        var empty = new TypedCodes { AreaId = areaId };
        var path = PathFor(areaId);
        try
        {
            return File.Exists(path) && JsonSerializer.Deserialize<TypedCodes>(File.ReadAllText(path, Encoding.UTF8), Options) is { } codes
                && string.Equals(codes.AreaId, areaId, StringComparison.OrdinalIgnoreCase)
                ? codes with { Locations = codes.Locations.WithComparers(StringComparer.Ordinal) }
                : empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return empty;
        }
    }

    /// <summary>Saves the codes, replacing the old file atomically; false if it cannot be written.</summary>
    public bool Save(TypedCodes codes)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var path = PathFor(codes.AreaId);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(codes, Options) + "\n", new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
