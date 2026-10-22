using Stringline.Core;
using Stringline.Editing.Knowledge;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;
using Stringline.Persistence;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Tests;

/// <summary>The knowledge cache: kept while its sources are unchanged, rebuilt when one changes, and never fed the draft.</summary>
public sealed class SimKnowledgeStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-simdata-{Guid.NewGuid():N}");

    public SimKnowledgeStoreTests()
    {
        Directory.CreateDirectory(Timetables);
        File.Copy(Fixtures.Path("llangollen 2012 B.WTT"), Llangollen);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Timetables => Path.Combine(_dir, "timetables");

    private string Llangollen => Path.Combine(Timetables, "llangollen 2012 B.wtt");

    private SimKnowledgeStore Store => new(Path.Combine(_dir, "profiles"));

    private SimKnowledgeRequest Request(params string[] exclude) => new() { AreaId = "llangollen", TimetableFolders = [Timetables], Exclude = exclude };

    /// <summary>A copy of Llangollen with one train booked through a location no other timetable has.</summary>
    private string SaveDraft(string name)
    {
        var document = WttDocument.Load(Llangollen);
        document.Trains.First().Element.Descendants("Location").First().Value = "DRAFTLOC";
        var path = Path.Combine(Timetables, name);
        File.WriteAllBytes(path, WttWriter.ToBytes(document));
        return path;
    }

    [Fact]
    public void The_cache_is_named_as_the_profile_and_sits_beside_it()
    {
        Store.PathFor("Waterloo").Should().Be(Path.Combine(_dir, "profiles", "waterloo.simdata.json"));
    }

    [Fact]
    public void Knowledge_is_built_once_then_read_from_the_cache()
    {
        var first = Store.Get(Request());
        var second = Store.Get(Request());

        first.FromCache.Should().BeFalse();
        File.Exists(Store.PathFor("llangollen")).Should().BeTrue();
        second.FromCache.Should().BeTrue();
        SimKnowledgeJson.Serialize(second.Knowledge).Should().Be(SimKnowledgeJson.Serialize(first.Knowledge));
        Store.Get(Request(), rebuild: true).FromCache.Should().BeFalse();
    }

    [Fact]
    public void A_changed_added_or_removed_source_rebuilds_it()
    {
        Store.Get(Request());

        File.SetLastWriteTimeUtc(Llangollen, File.GetLastWriteTimeUtc(Llangollen).AddMinutes(1));
        Store.Get(Request()).FromCache.Should().BeFalse("the timetable changed");
        Store.Get(Request()).FromCache.Should().BeTrue();

        var other = SaveDraft("llangollen other.wtt");
        var added = Store.Get(Request());
        added.FromCache.Should().BeFalse("a timetable was added");
        added.Knowledge.Timetables.Should().Be(2);

        File.Delete(other);
        var removed = Store.Get(Request());
        removed.FromCache.Should().BeFalse("a timetable was removed");
        removed.Knowledge.Timetables.Should().Be(1);
    }

    [Fact]
    public void A_profile_is_a_source_too()
    {
        var timetable = new WttReader().Read(Llangollen).Timetable;
        var profilePath = new ProfileStore(Path.Combine(_dir, "profiles")).Save(
            StringlineProfile.Create(timetable) with { LocationNames = new Dictionary<Tiploc, string> { [new Tiploc("CARROG")] = "Carrog" } });
        var request = Request() with { ProfilePath = profilePath };

        Store.Get(request).Knowledge.Location(new Tiploc("CARROG"))!.Name.Should().Be("Carrog");
        Store.Get(request).FromCache.Should().BeTrue();
        Store.Get(Request()).FromCache.Should().BeFalse("the profile is no longer a source");
    }

    [Fact]
    public void The_draft_is_left_out()
    {
        var draft = SaveDraft("llangollen draft.wtt");

        var withDraft = Store.Get(Request()).Knowledge;
        var without = Store.Get(Request(draft)).Knowledge;

        withDraft.Location(new Tiploc("DRAFTLOC")).Should().NotBeNull();
        without.Location(new Tiploc("DRAFTLOC")).Should().BeNull("a draft cannot vouch for itself");
        without.Timetables.Should().Be(1);
        without.Sources.Should().NotContain(s => s.Path.EndsWith("draft.wtt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_damaged_source_is_left_out_and_named()
    {
        var recording = Path.Combine(_dir, "broken.sslog");
        File.WriteAllText(recording, "not a recording");
        var request = Request() with { PlayFiles = [recording], ReadPlay = path => PlayFiles.Read(path) };

        var result = Store.Get(request);

        result.Problems.Should().ContainSingle().Which.Should().StartWith("broken.sslog: ");
        result.Knowledge.Plays.Should().Be(0);
        result.Knowledge.Timetables.Should().Be(1);
        Store.Get(request).FromCache.Should().BeTrue("it stays damaged until it changes");
    }

    [Fact]
    public void A_damaged_or_old_cache_is_rebuilt()
    {
        Store.Get(Request());
        File.WriteAllText(Store.PathFor("llangollen"), "{ \"format\": 0 }");

        Store.Get(Request()).FromCache.Should().BeFalse();

        File.WriteAllText(Store.PathFor("llangollen"), "not json");
        Store.Get(Request()).FromCache.Should().BeFalse();
    }
}
