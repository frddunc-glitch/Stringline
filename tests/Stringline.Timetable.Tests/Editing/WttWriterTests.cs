using Stringline.Gateway.Tests;
using Stringline.Timetable.Editing;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>Saving: versions, atomic writes and backups (<c>08-…md</c> §6).</summary>
public sealed class WttWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"stringline-writer-{Guid.NewGuid():N}");

    public WttWriterTests() => Directory.CreateDirectory(_dir);

    private string Backups => Path.Combine(_dir, "backups");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData(VersionBump.None, "1.2.3")]
    [InlineData(VersionBump.Build, "1.2.4")]
    [InlineData(VersionBump.Minor, "1.3.0")]
    [InlineData(VersionBump.Major, "2.0.0")]
    public void A_version_goes_up_by_its_bump(VersionBump bump, string expected) =>
        new WttVersion(1, 2, 3).Bump(bump).ToString().Should().Be(expected);

    [Fact]
    public void A_version_cannot_pass_65535()
    {
        var act = () => new WttVersion(1, 0, WttVersion.Largest).Bump(VersionBump.Build);

        act.Should().Throw<InvalidOperationException>();
        new WttVersion(1, 0, WttVersion.Largest).Bump(VersionBump.Minor).Should().Be(new WttVersion(1, 1, 0));
        (new WttVersion(1, 0, 9) < new WttVersion(1, 1, 0)).Should().BeTrue();
    }

    [Fact]
    public void A_save_raises_the_build_in_the_file_and_the_document()
    {
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01")));
        var path = Path.Combine(_dir, "test.wtt");

        var result = WttWriter.Save(document, path, new WttSaveOptions(BackupFolder: Backups));

        result.Version.Should().Be(new WttVersion(1, 2, 4));
        document.Header.Version.Should().Be(new WttVersion(1, 2, 4));
        WttDocument.Load(path).Header.Version.Should().Be(new WttVersion(1, 2, 4));
        new WttReader().Read(path).Timetable.Header.TimetableVersion.Should().Be("1.2.4");
        RoundTrip.EntryXml(File.ReadAllBytes(path), "TimetableHeader.xml").Element("VBuild")!.Value.Should().Be("4");
        result.BackupPath.Should().BeNull("there was no file to replace");
        Directory.GetFiles(_dir).Should().ContainSingle("no temporary file is left");
    }

    [Fact]
    public void An_unversioned_timetable_is_saved_as_1_0_1()
    {
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01")).Replace("<VMajor>1</VMajor><VMinor>2</VMinor><VBuild>3</VBuild>", "", StringComparison.Ordinal));
        document.Header.Version.Should().BeNull();

        WttWriter.BumpVersion(document, VersionBump.None).Should().BeNull();
        WttWriter.BumpVersion(document, VersionBump.Build).Should().Be(new WttVersion(1, 0, 1));

        string.Join(" ", document.Root.Elements().Select(e => e.Name.LocalName)).Should().StartWith("Name Description StartTime FinishTime VMajor VMinor VBuild");
    }

    [Fact]
    public void A_replaced_file_is_backed_up_by_sim_name_and_version_and_old_backups_go()
    {
        var path = Path.Combine(_dir, "Waterloo 2010-05-01.wtt");
        File.Copy(Fixtures.Path(WttFixtureTests.Waterloo), path);
        var document = WttDocument.Load(path);
        var options = new WttSaveOptions(BackupFolder: Backups, BackupsKept: 3);

        var first = WttWriter.Save(document, path, options);

        first.BackupPath.Should().NotBeNull();
        Path.GetDirectoryName(first.BackupPath).Should().Be(Path.Combine(Backups, "waterloo"));
        Path.GetFileName(first.BackupPath).Should().MatchRegex(@"^Waterloo 2010-05-01 1\.0\.2 \d{8}-\d{6}\.wtt$");
        File.ReadAllBytes(first.BackupPath!).Should().Equal(File.ReadAllBytes(Fixtures.Path(WttFixtureTests.Waterloo)));

        for (var i = 0; i < 4; i++)
        {
            WttWriter.Save(document, path, options);
        }

        var backups = Directory.GetFiles(Path.Combine(Backups, "waterloo")).Select(Path.GetFileName).ToList();
        backups.Should().HaveCount(3);
        backups.Should().Contain(b => b!.StartsWith("Waterloo 2010-05-01 1.0.6 ", StringComparison.Ordinal), "the newest backup is the file before the last save");
        WttDocument.Load(path).Header.Version.Should().Be(new WttVersion(1, 0, 7));
    }

    [Fact]
    public void Backups_can_be_turned_off()
    {
        var path = Path.Combine(_dir, "a.wtt");
        var document = WttDocument.Parse(Wtt.Document(Wtt.SimpleTrain("1A01")));
        WttWriter.Save(document, path, new WttSaveOptions(BackupFolder: Backups));

        var result = WttWriter.Save(document, path, new WttSaveOptions(BackupFolder: Backups, Backup: false));

        result.BackupPath.Should().BeNull();
        Directory.Exists(Backups).Should().BeFalse();
    }
}
