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
        "Stringline.Editing",
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

    /// <summary>
    /// The timetable editor's core (S41, <c>08-…md</c> §10) sits on the timetable, network and planning layers. It learns
    /// from play through gateway events (Core), so it never reads recordings or sessions itself, and nothing below it
    /// may use it.
    /// </summary>
    [Fact]
    public void The_editing_layer_uses_only_the_layers_below_it()
    {
        var allowed = new[] { "Stringline.Core", "Stringline.Timetable", "Stringline.Network", "Stringline.Planning" };
        var editing = XDocument.Load(Path.Combine(FindRepoRoot(), "src", "Stringline.Editing", "Stringline.Editing.csproj"));

        editing.Descendants("ProjectReference").Select(r => Path.GetFileNameWithoutExtension(r.Attribute("Include")!.Value)).Should().BeSubsetOf(allowed);
        Assembly.Load(new AssemblyName("Stringline.Editing")).GetReferencedAssemblies().Select(a => a.Name).Where(n => n!.StartsWith("Stringline", StringComparison.Ordinal))
            .Should().BeSubsetOf(allowed);
        foreach (var below in allowed.Append("Stringline.Gateway").Append("Stringline.Persistence").Append("Stringline.Graph").Append("Stringline.Optimiser"))
        {
            Assembly.Load(new AssemblyName(below)).GetReferencedAssemblies().Select(a => a.Name).Should().NotContain("Stringline.Editing", "{0} is below the editing layer", below);
        }
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
