using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Stringline.Timetable.Editing;

namespace Stringline.Timetable.Tests.Editing;

/// <summary>Helpers for checking that a save gives back the same timetable.</summary>
internal static class RoundTrip
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>A zip entry's XML as <see cref="WttReader"/> reads it (no whitespace or comments).</summary>
    public static XElement EntryXml(byte[] wtt, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(wtt), ZipArchiveMode.Read);
        var entry = zip.Entries.Single(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        using var stream = entry.Open();
        var bytes = WttReader.ReadAll(stream);
        using var reader = WttReader.CreateXmlReader(bytes);
        return XElement.Load(reader);
    }

    public static byte[] EntryBytes(byte[] wtt, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(wtt), ZipArchiveMode.Read);
        using var stream = zip.Entries.Single(e => e.Name == name).Open();
        return WttReader.ReadAll(stream);
    }

    /// <summary>Where two trees first differ (names, order, attributes, text, empty or not), or null if they are equal element by element.</summary>
    public static string? FirstDifference(XElement a, XElement b, string path = "")
    {
        path = $"{path}/{a.Name.LocalName}";
        if (a.Name != b.Name)
        {
            return $"{path}: element {a.Name} against {b.Name}";
        }

        var attributesA = a.Attributes().Select(x => (x.Name, x.Value)).ToList();
        var attributesB = b.Attributes().Select(x => (x.Name, x.Value)).ToList();
        if (!attributesA.SequenceEqual(attributesB))
        {
            return $"{path}: attributes [{string.Join(", ", attributesA)}] against [{string.Join(", ", attributesB)}]";
        }

        if (!a.HasElements && !b.HasElements)
        {
            return a.IsEmpty != b.IsEmpty || a.Value != b.Value ? $"{path}: '{a.Value}' (empty {a.IsEmpty}) against '{b.Value}' (empty {b.IsEmpty})" : null;
        }

        var childrenA = a.Nodes().ToList();
        var childrenB = b.Nodes().ToList();
        if (childrenA.Count != childrenB.Count)
        {
            return $"{path}: {childrenA.Count} children against {childrenB.Count}";
        }

        for (var i = 0; i < childrenA.Count; i++)
        {
            var difference = (childrenA[i], childrenB[i]) switch
            {
                (XElement x, XElement y) => FirstDifference(x, y, $"{path}[{i}]"),
                (XText x, XText y) => x.Value == y.Value ? null : $"{path}[{i}]: text '{x.Value}' against '{y.Value}'",
                var (x, y) => x.NodeType == y.NodeType && XNode.DeepEquals(x, y) ? null : $"{path}[{i}]: {x.NodeType} against {y.NodeType}",
            };
            if (difference is not null)
            {
                return difference;
            }
        }

        return null;
    }

    /// <summary>The whole import as one string: header, categories, trains, rules, seed groups, warnings.</summary>
    public static string Canonical(WttImportResult result)
    {
        var tt = result.Timetable;
        return JsonSerializer.Serialize(
            new { tt.Header, tt.Categories, tt.Trains, tt.Rules, tt.SeedGroups, result.Warnings, result.UnknownElements, result.SkippedTrains },
            Json);
    }

    /// <summary>A snapshot of the document to compare after undo.</summary>
    public static XElement Snapshot(WttDocument document) => new(document.Root);

    public static void ShouldEqualTree(this XElement actual, XElement expected) => FirstDifference(actual, expected).Should().BeNull();

    public static string XmlText(this WttDocument document)
    {
        using var buffer = new MemoryStream();
        WttWriter.WriteXml(document.Root, buffer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static XmlReaderSettings Settings => WttReader.XmlSettings;
}
