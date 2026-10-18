using System.IO.Compression;
using Stringline.Gateway.Tests;

namespace Stringline.Timetable.Tests;

public sealed class WttCatalogueTests : IDisposable
{
    private readonly DirectoryInfo _folder = Directory.CreateTempSubdirectory("stringline-wtt-");

    public void Dispose() => _folder.Delete(recursive: true);

    private string Write(string name, string xml)
    {
        var path = Path.Combine(_folder.FullName, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, xml);
        return path;
    }

    private string WriteZip(string name, string xml, string entry = "SavedTimetable.xml")
    {
        var path = Path.Combine(_folder.FullName, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
        writer.Write(xml);
        return path;
    }

    [Fact]
    public void Reads_the_sim_and_name_of_a_real_timetable()
    {
        var info = WttCatalogue.ReadInfo(Fixtures.Path(WttFixtureTests.Llangollen))!;

        info.AreaId.Should().Be("llangollen");
        info.Name.Should().NotBeEmpty();
        info.SimSigVersion.Should().NotBeEmpty();
        info.FileName.Should().Be(WttFixtureTests.Llangollen);
        info.ToString().Should().Contain(WttFixtureTests.Llangollen);
        WttCatalogue.ReadInfo(Fixtures.Path(WttFixtureTests.Waterloo))!.AreaId.Should().Be("waterloo");
    }

    [Fact]
    public void Reads_bare_xml_and_a_timetable_without_a_name()
    {
        var named = Write("a.wtt", "<SimSigTimetable ID=\"test\" Version=\"5.2\"><Description>x</Description><Name>Test TT</Name></SimSigTimetable>");
        var unnamed = WriteZip("b.wtt", "<SimSigTimetable ID=\"test\"><Timetables /><Name>after</Name></SimSigTimetable>");
        var empty = Write("c.wtt", "<SimSigTimetable ID=\"test\" />");

        WttCatalogue.ReadInfo(named)!.Name.Should().Be("Test TT");
        WttCatalogue.ReadInfo(named)!.SimSigVersion.Should().Be("5.2");
        WttCatalogue.ReadInfo(unnamed)!.Name.Should().BeEmpty("the name comes before the trains");
        WttCatalogue.ReadInfo(unnamed)!.ToString().Should().Be("b.wtt");
        WttCatalogue.ReadInfo(empty)!.AreaId.Should().Be("test");
    }

    [Fact]
    public void Gives_nothing_for_files_that_are_not_timetables()
    {
        WttCatalogue.ReadInfo(Write("x.wtt", "<Other ID=\"test\" />")).Should().BeNull();
        WttCatalogue.ReadInfo(Write("y.wtt", "not xml")).Should().BeNull();
        WttCatalogue.ReadInfo(WriteZip("z.wtt", "<SimSigTimetable ID=\"test\" />", entry: "Other.xml")).Should().BeNull();
        WttCatalogue.ReadInfo(Path.Combine(_folder.FullName, "missing.wtt")).Should().BeNull();
    }

    [Fact]
    public void Finds_a_sims_timetables_in_a_folder_tree_newest_first()
    {
        var older = Write("Test 2010.wtt", "<SimSigTimetable ID=\"Test\"><Name>Old</Name></SimSigTimetable>");
        var newer = Write(Path.Combine("sub", "test 2020.WTT"), "<SimSigTimetable ID=\"test\"><Name>New</Name></SimSigTimetable>");
        Write("other.wtt", "<SimSigTimetable ID=\"other\" />");
        Write("notes.txt", "<SimSigTimetable ID=\"test\" />");
        File.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newer, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var found = WttCatalogue.FindForArea(_folder.FullName, "TEST ");

        found.Select(f => f.Name).Should().Equal("New", "Old");
    }

    [Fact]
    public void A_missing_folder_has_no_timetables()
    {
        WttCatalogue.FindForArea(Path.Combine(_folder.FullName, "nope"), "test").Should().BeEmpty();
        WttCatalogue.FindForArea(string.Empty, "test").Should().BeEmpty();
    }

    [Fact]
    public void The_default_folder_is_simsigs()
    {
        if (OperatingSystem.IsWindows())
        {
            WttCatalogue.DefaultFolder.Should().EndWith(Path.Combine("SimSig", "Timetables"));
        }
    }
}
