using System.Diagnostics;
using System.Text;
using Stringline.Core.Events;
using Stringline.Network.Profiles;
using Stringline.Timetable;

namespace Stringline.Editing.Knowledge;

/// <summary>Reads a recording's or session's events; throws <see cref="IOException"/> or <see cref="InvalidDataException"/> if it cannot.</summary>
public delegate IEnumerable<GatewayEvent> PlayReader(string path);

/// <summary>What to build a sim's knowledge from.</summary>
public sealed record SimKnowledgeRequest
{
    /// <summary>The sim's area ID, e.g. <c>waterloo</c>.</summary>
    public required string AreaId { get; init; }

    /// <summary>Folders searched (with their subfolders) for the sim's timetables (<see cref="WttCatalogue.FindForArea"/>).</summary>
    public IReadOnlyList<string> TimetableFolders { get; init; } = [];

    /// <summary>Recordings and sessions to learn from (see <see cref="ReadPlay"/>). Missing files are left out.</summary>
    public IReadOnlyList<string> PlayFiles { get; init; } = [];

    /// <summary>Reads <see cref="PlayFiles"/>; with none, play is left out.</summary>
    public PlayReader? ReadPlay { get; init; }

    /// <summary>The sim's profile file, if it has one.</summary>
    public string? ProfilePath { get; init; }

    /// <summary>
    /// Files never to learn from: the draft being edited, so a draft cannot vouch for itself (<c>08-…md</c> §4).
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];
}

/// <summary>Knowledge got from the store.</summary>
/// <param name="FromCache">Read from the cache, every source unchanged.</param>
/// <param name="Problems">Sources that could not be read, and why; they are left out.</param>
public sealed record SimKnowledgeResult(SimKnowledge Knowledge, bool FromCache, IReadOnlyList<string> Problems, TimeSpan Elapsed);

/// <summary>
/// Sim knowledge cached on disk beside the profiles (<c>{directory}/{area}.simdata.json</c>), keyed by its sources'
/// paths, times and sizes, and rebuilt when one is added, removed or changed (<c>08-…md</c> §4).
/// </summary>
public sealed class SimKnowledgeStore(string directory)
{
    public const string Extension = ".simdata.json";

    public SimKnowledgeStore()
        : this(ProfileStore.DefaultDirectory)
    {
    }

    public string Directory { get; } = directory ?? throw new ArgumentNullException(nameof(directory));

    /// <summary>The cache file for this sim, named as its profile (<see cref="ProfileStore.PathFor"/>).</summary>
    public string PathFor(string areaId) => Path.ChangeExtension(new ProfileStore(Directory).PathFor(areaId), null) + Extension;

    /// <summary>The files the knowledge would be built from now, in a stable order, the excluded ones left out.</summary>
    public static IReadOnlyList<KnowledgeSource> FindSources(SimKnowledgeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var excluded = request.Exclude.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = new List<KnowledgeSource>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(KnowledgeSourceKind kind, string path)
        {
            if (!excluded.Contains(Path.GetFullPath(path)) && KnowledgeSource.Of(kind, path) is { } source && seen.Add(source.Path))
            {
                sources.Add(source);
            }
        }

        foreach (var folder in request.TimetableFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var info in WttCatalogue.FindForArea(folder, request.AreaId, cancellationToken).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase))
            {
                Add(KnowledgeSourceKind.Timetable, info.Path);
            }
        }

        if (request.ReadPlay is not null)
        {
            foreach (var path in request.PlayFiles)
            {
                Add(KnowledgeSourceKind.Play, path);
            }
        }

        if (request.ProfilePath is { } profile)
        {
            Add(KnowledgeSourceKind.Profile, profile);
        }

        return sources;
    }

    /// <summary>
    /// The sim's knowledge: from the cache if its sources are the same files, unchanged; otherwise built afresh and
    /// cached. <paramref name="rebuild"/> builds it anyway.
    /// </summary>
    public SimKnowledgeResult Get(SimKnowledgeRequest request, bool rebuild = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clock = Stopwatch.StartNew();
        var sources = FindSources(request, cancellationToken);
        if (!rebuild && Load(request.AreaId) is { } cached && SameSources(cached.Sources, sources))
        {
            return new SimKnowledgeResult(cached, true, [], clock.Elapsed);
        }

        var knowledge = Build(request, sources, out var problems, cancellationToken);
        Save(knowledge);
        return new SimKnowledgeResult(knowledge, false, problems, clock.Elapsed);
    }

    /// <summary>Builds the knowledge from these sources, leaving out any that cannot be read (listed in <paramref name="problems"/>).</summary>
    public static SimKnowledge Build(SimKnowledgeRequest request, IReadOnlyList<KnowledgeSource> sources, out IReadOnlyList<string> problems, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sources);
        var builder = new SimKnowledgeBuilder(request.AreaId);
        var found = new List<string>();
        var used = new List<KnowledgeSource>();
        var reader = new WttReader();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(source.Path);
            try
            {
                switch (source.Kind)
                {
                    case KnowledgeSourceKind.Timetable:
                        builder.AddTimetable(reader.Read(source.Path).Timetable);
                        break;
                    case KnowledgeSourceKind.Play when request.ReadPlay is { } read:
                        if (!builder.AddPlay(read(source.Path)))
                        {
                            found.Add($"{name}: another sim's");
                        }

                        break;
                    case KnowledgeSourceKind.Profile:
                        var profile = ProfileJson.Deserialize(File.ReadAllText(source.Path, Encoding.UTF8));
                        if (!string.Equals(profile.AreaId, request.AreaId.Trim(), StringComparison.OrdinalIgnoreCase))
                        {
                            found.Add($"{name}: the profile for '{profile.AreaId}'");
                        }
                        else
                        {
                            builder.AddProfile(profile);
                        }

                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or WttFormatException or ProfileFormatException or ArgumentException)
            {
                found.Add($"{name}: {ex.Message}");
            }

            // A source that cannot be read is still a source: the cache stays good until it changes.
            used.Add(source);
        }

        problems = found;
        return builder.Build(used);
    }

    /// <summary>The cached knowledge for this sim, or null if there is none readable in the current format.</summary>
    public SimKnowledge? Load(string areaId)
    {
        var path = PathFor(areaId);
        try
        {
            return File.Exists(path) && SimKnowledgeJson.TryDeserialize(File.ReadAllText(path, Encoding.UTF8)) is { } knowledge
                && string.Equals(knowledge.AreaId, areaId.Trim(), StringComparison.OrdinalIgnoreCase) ? knowledge : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Caches the knowledge, replacing the old atomically. A cache that cannot be written is only slower next time.</summary>
    public string? Save(SimKnowledge knowledge)
    {
        ArgumentNullException.ThrowIfNull(knowledge);
        var path = PathFor(knowledge.AreaId);
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, SimKnowledgeJson.Serialize(knowledge), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool SameSources(IReadOnlyList<KnowledgeSource> cached, IReadOnlyList<KnowledgeSource> now) =>
        cached.Count == now.Count && cached.Zip(now).All(p => p.First.SameAs(p.Second));
}
