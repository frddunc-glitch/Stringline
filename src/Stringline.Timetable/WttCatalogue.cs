using System.IO.Compression;
using System.Xml;

namespace Stringline.Timetable;

/// <summary>What a <c>.wtt</c> file is, from its root element and name alone.</summary>
/// <param name="AreaId">The sim's area ID (root <c>ID</c>), matching the gateway's <c>area_id</c>.</param>
/// <param name="Name">The timetable's name, or empty if it has none.</param>
public sealed record WttFileInfo(string Path, string AreaId, string Name, string SimSigVersion, DateTime LastWriteUtc)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    public override string ToString() => Name.Length > 0 ? $"{Name} ({FileName})" : FileName;
}

/// <summary>
/// Finds timetables for a sim without importing them: reads only the start of each file's XML (the root's sim ID and
/// the name), so a folder of hundreds of timetables is searched in a second or two.
/// </summary>
public static class WttCatalogue
{
    private static readonly XmlReaderSettings XmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
        CloseInput = false,
    };

    /// <summary>
    /// Where SimSig keeps timetables: <c>C:\Users\Public\Documents\SimSig\Timetables</c> on Windows. Empty where there
    /// is no public documents folder.
    /// </summary>
    public static string DefaultFolder
    {
        get
        {
            var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolderOption.DoNotVerify);
            return common.Length == 0 ? string.Empty : Path.Combine(common, "SimSig", "Timetables");
        }
    }

    /// <summary>The sim ID and name of a timetable file, or null if it cannot be read or is not a SimSig timetable.</summary>
    public static WttFileInfo? ReadInfo(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[4];
            var isZip = file.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 && head.AsSpan().SequenceEqual("PK\u0003\u0004"u8);
            file.Position = 0;
            if (!isZip)
            {
                return ReadXml(file, path);
            }

            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, "SavedTimetable.xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            using var xml = entry.Open();
            return ReadXml(xml, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every timetable in <paramref name="folder"/> and its subfolders for this sim (area IDs compare without case),
    /// newest first. A missing folder gives none.
    /// </summary>
    public static IReadOnlyList<WttFileInfo> FindForArea(string folder, string areaId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return [];
        }

        var found = new List<WttFileInfo>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.wtt", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true });
            foreach (var path in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ReadInfo(path) is { } info && string.Equals(info.AreaId, areaId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(info);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that vanishes or locks part way: keep what was found.
        }

        return found.OrderByDescending(f => f.LastWriteUtc).ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static WttFileInfo? ReadXml(Stream stream, string path)
    {
        using var reader = XmlReader.Create(stream, XmlSettings);
        reader.MoveToContent();
        if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "SimSigTimetable")
        {
            return null;
        }

        var areaId = reader.GetAttribute("ID")?.Trim() ?? string.Empty;
        var version = reader.GetAttribute("Version")?.Trim() ?? string.Empty;
        var name = string.Empty;
        if (!reader.IsEmptyElement)
        {
            reader.Read();
            while (!reader.EOF && reader.NodeType != XmlNodeType.EndElement)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    reader.Read();
                }
                else if (reader.LocalName == "Name")
                {
                    name = reader.ReadElementContentAsString().Trim();
                    break;
                }
                else if (reader.LocalName is "Timetables" or "TrainCategories")
                {
                    break; // the name comes before these; there is none
                }
                else
                {
                    reader.Skip();
                }
            }
        }

        return new WttFileInfo(path, areaId, name, version, File.GetLastWriteTimeUtc(path));
    }
}
