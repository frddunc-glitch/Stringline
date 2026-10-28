using Stringline.Core;
using Stringline.Editing.Bulk;
using Stringline.Editing.Knowledge;

namespace Stringline.Editing.Tests;

/// <summary>#33: the platform, line and path codes typed for a sim, kept beside its profile and offered from then on.</summary>
public sealed class TypedCodesTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("stringline-codes-");

    public void Dispose() => _folder.Delete(recursive: true);

    [Fact]
    public void Typed_codes_are_kept_per_location_and_read_back_beside_the_profile()
    {
        var store = new TypedCodesStore(_folder.FullName);
        var stkbgl = new Tiploc("STKBGL");

        var codes = store.Load("moss_vale_nsw").With(stkbgl, TripCode.Platform, " 1 ").With(stkbgl, TripCode.Line, "UM");
        codes.With(stkbgl, TripCode.Platform, "1").Should().BeSameAs(codes, "a code already there changes nothing");
        store.Save(codes).Should().BeTrue();

        store.PathFor("moss_vale_nsw").Should().Be(Path.Combine(_folder.FullName, "moss_vale_nsw.codes.json"));
        var back = store.Load("moss_vale_nsw").At(stkbgl)!;
        back.Platforms.Should().Equal("1");
        back.Lines.Should().Equal("UM");
        back.Paths.Should().BeEmpty();
        store.Load("waterloo").Locations.Should().BeEmpty("each sim has its own");
    }

    [Fact]
    public void A_file_that_cannot_be_read_is_no_codes()
    {
        var store = new TypedCodesStore(_folder.FullName);
        File.WriteAllText(store.PathFor("test"), "{ not json");

        store.Load("test").Locations.Should().BeEmpty();
    }
}
