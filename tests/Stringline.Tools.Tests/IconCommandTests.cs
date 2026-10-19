using System.CommandLine;

namespace Stringline.Tools.Tests;

/// <summary>Runs <c>stringline icon</c> in-process (S18).</summary>
public sealed class IconCommandTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("stringline-icon-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public async Task Writes_the_ico_and_a_png_per_size()
    {
        var ico = Path.Combine(_dir.FullName, "out", "app.ico");
        var pngs = Path.Combine(_dir.FullName, "png");
        var output = new StringWriter();
        var root = new RootCommand { IconCommand.Create() };

        var exit = await root.Parse(["icon", ico, "--png", pngs]).InvokeAsync(new InvocationConfiguration { Output = output, Error = new StringWriter() });

        exit.Should().Be(0);
        output.ToString().Should().Contain("16, 20, 24, 32, 40, 48, 64, 128, 256 px");
        new FileInfo(ico).Length.Should().BeGreaterThan(1000);
        Directory.GetFiles(pngs).Select(Path.GetFileName).Should().HaveCount(9).And.Contain("icon-16.png").And.Contain("icon-256.png");
    }
}
