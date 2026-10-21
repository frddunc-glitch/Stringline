using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;

namespace Stringline.Architecture.Tests;

/// <summary>Guards the rule that only Stringline.App may use WPF or target net10.0-windows.</summary>
public class LayeringTests
{
    public static TheoryData<string> PortableAssemblies => new()
    {
        "Stringline.Core",
        "Stringline.Gateway",
        "Stringline.Timetable",
        "Stringline.Network",
        "Stringline.Planning",
        "Stringline.Optimiser",
        "Stringline.Persistence",
        "Stringline.Graph",
        "stringline", // Stringline.Tools
    };

    private static readonly string[] WindowsOnlyAssemblies =
        ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Windows.Forms", "System.Xaml", "SkiaSharp.Views.WPF"];

    [Theory]
    [MemberData(nameof(PortableAssemblies))]
    public void Portable_assembly_targets_plain_net10(string assemblyName)
    {
        var assembly = Assembly.Load(new AssemblyName(assemblyName));

        assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName.Should().Be(".NETCoreApp,Version=v10.0");
        assembly.GetReferencedAssemblies().Select(a => a.Name).Should().NotIntersectWith(WindowsOnlyAssemblies);
    }

    [Fact]
    public void Only_the_app_project_is_windows_specific()
    {
        var src = Path.Combine(FindRepoRoot(), "src");
        // WPF markup compilation briefly writes Stringline.App_<random>_wpftmp.csproj beside the real
        // project, so a parallel build of the app must not be mistaken for a new project.
        var projects = Directory.GetFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileNameWithoutExtension(p).EndsWith("_wpftmp", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(d => d is "obj" or "bin"))
            .ToArray();
        projects.Should().HaveCountGreaterThanOrEqualTo(9);

        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            var xml = XDocument.Load(project);
            var tfm = xml.Descendants("TargetFramework").Select(e => e.Value).SingleOrDefault() ?? "net10.0";
            var usesWpf = xml.Descendants("UseWPF").Any(e => e.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
            var usesWinForms = xml.Descendants("UseWindowsForms").Any(e => e.Value.Equals("true", StringComparison.OrdinalIgnoreCase));

            if (name == "Stringline.App")
            {
                tfm.Should().StartWith("net10.0-windows");
                usesWpf.Should().BeTrue();
            }
            else
            {
                tfm.Should().Be("net10.0", "{0} must stay portable", name);
                usesWpf.Should().BeFalse("{0} must not use WPF", name);
                usesWinForms.Should().BeFalse("{0} must not use Windows Forms", name);
            }
        }
    }

    internal static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Stringline.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (Stringline.sln).");
    }
}
