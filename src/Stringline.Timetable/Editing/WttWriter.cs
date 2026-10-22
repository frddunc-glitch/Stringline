using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Stringline.Timetable.Editing;

/// <summary>A timetable's version, <c>VMajor.VMinor.VBuild</c>, each 0–65 535 (SimSig wiki: it must always go up).</summary>
public readonly record struct WttVersion(int Major, int Minor, int Build) : IComparable<WttVersion>
{
    public const int Largest = 65_535;

    /// <summary>What a file with no version is taken as before its first bump, so its first save is 1.0.1 (Fred, S40).</summary>
    public static WttVersion Unversioned => new(1, 0, 0);

    /// <summary>The version after a save: <see cref="VersionBump.Build"/> adds one to the build; a release raises the minor or major number and sets the build (and minor) to 0.</summary>
    public WttVersion Bump(VersionBump bump)
    {
        var next = bump switch
        {
            VersionBump.None => this,
            VersionBump.Build => this with { Build = Build + 1 },
            VersionBump.Minor => new WttVersion(Major, Minor + 1, 0),
            VersionBump.Major => new WttVersion(Major + 1, 0, 0),
            _ => throw new ArgumentOutOfRangeException(nameof(bump), bump, null),
        };
        return next.Major > Largest || next.Minor > Largest || next.Build > Largest
            ? throw new InvalidOperationException($"Version {this} cannot go up any further by {bump}; release a new minor or major version.")
            : next;
    }

    public static bool operator <(WttVersion left, WttVersion right) => left.CompareTo(right) < 0;

    public static bool operator <=(WttVersion left, WttVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >(WttVersion left, WttVersion right) => left.CompareTo(right) > 0;

    public static bool operator >=(WttVersion left, WttVersion right) => left.CompareTo(right) >= 0;

    public int CompareTo(WttVersion other) => (Major, Minor, Build).CompareTo((other.Major, other.Minor, other.Build));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Build}");
}

public enum VersionBump
{
    /// <summary>Keep the version (a round trip).</summary>
    None,

    /// <summary>A test version: <c>VBuild</c> + 1, as every save does.</summary>
    Build,

    /// <summary>A release: <c>VMinor</c> + 1, build 0.</summary>
    Minor,

    /// <summary>A release: <c>VMajor</c> + 1, minor and build 0.</summary>
    Major,
}

/// <param name="Bump">How the version goes up (SimSig: on every save).</param>
/// <param name="BackupFolder">Where a replaced file is copied first, per sim; null for <see cref="WttWriter.DefaultBackupFolder"/>.</param>
/// <param name="Backup">Copy a file before replacing it.</param>
/// <param name="BackupsKept">Backups kept per timetable; older ones are deleted.</param>
public sealed record WttSaveOptions(VersionBump Bump = VersionBump.Build, string? BackupFolder = null, bool Backup = true, int BackupsKept = 20);

/// <param name="Version">The version saved, or null if the file has none.</param>
/// <param name="BackupPath">Where the file replaced was copied, if one was.</param>
public sealed record WttSaveResult(string Path, WttVersion? Version, string? BackupPath);

/// <summary>
/// Saves a <see cref="WttDocument"/> as a <c>.wtt</c> (<c>docs/08-…md</c> §3.3, §6): a zip of <c>SavedTimetable.xml</c>,
/// a regenerated <c>TimetableHeader.xml</c> and the zip's other entries as read. The XML is written as SimSig writes it:
/// UTF-8 with no byte order mark and no declaration, indented two spaces with CRLF line ends (one after the root too),
/// <c>&lt;DwellTimes/&gt;</c> with no space, and SimSig's escaping (<see cref="Escape"/>). Most files come back byte for byte.
/// </summary>
public static partial class WttWriter
{
    public static string DefaultBackupFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stringline", "timetable-backups");

    /// <summary>
    /// Raises the version (<see cref="WttSaveOptions.Bump"/>, in the document too), backs up the file being replaced,
    /// and writes atomically: a temporary file in the same folder, then moved over the target.
    /// </summary>
    public static WttSaveResult Save(WttDocument document, string path, WttSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new WttSaveOptions();
        path = Path.GetFullPath(path);
        var version = BumpVersion(document, options.Bump);
        var bytes = ToBytes(document);

        var backup = options.Backup && File.Exists(path)
            ? BackUp(path, options.BackupFolder ?? DefaultBackupFolder, document.Header.AreaId, options.BackupsKept)
            : null;

        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }

        return new WttSaveResult(path, version, backup);
    }

    /// <summary>Sets the document's version to the next one; a file with no version gets one unless the bump is <see cref="VersionBump.None"/>.</summary>
    public static WttVersion? BumpVersion(WttDocument document, VersionBump bump)
    {
        ArgumentNullException.ThrowIfNull(document);
        var current = document.Header.Version;
        if (bump == VersionBump.None)
        {
            return current;
        }

        var next = (current ?? WttVersion.Unversioned).Bump(bump);
        document.Header.Version = next;
        return next;
    }

    /// <summary>The <c>.wtt</c> zip's bytes.</summary>
    public static byte[] ToBytes(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(zip, WttReader.TimetableEntry, stream => WriteXml(document.Root, stream));
            WriteEntry(zip, WttDocument.HeaderEntry, stream => WriteXml(HeaderOf(document), stream));
            foreach (var (name, bytes) in document.OtherEntries)
            {
                WriteEntry(zip, name, stream => stream.Write(bytes));
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// <c>TimetableHeader.xml</c>: the root with its attributes and its <c>[TH]</c> children (name, description, times,
    /// version, template, seed group summary, scenario options), in the document's order.
    /// </summary>
    public static XElement HeaderOf(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root;
        return new XElement(
            root.Name,
            root.Attributes().Select(a => new XAttribute(a)),
            root.Elements().Where(e => WttSchema.HeaderFields.Contains(e.Name.LocalName)).Select(e => new XElement(e)));
    }

    /// <summary>Writes an element as SimSig does (see the class remarks).</summary>
    public static void WriteXml(XElement element, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1 << 16, leaveOpen: true);
        Write(writer, element, 0);
    }

    private static void Write(TextWriter w, XElement element, int depth)
    {
        Indent(w, depth);
        WriteStart(w, element);
        if (element.IsEmpty)
        {
            w.Write("/>\r\n");
            return;
        }

        w.Write('>');
        if (element.Nodes().All(n => n is XElement) && element.HasElements)
        {
            w.Write("\r\n");
            foreach (var child in element.Elements())
            {
                Write(w, child, depth + 1);
            }

            Indent(w, depth);
        }
        else
        {
            foreach (var node in element.Nodes())
            {
                WriteInline(w, node);
            }
        }

        w.Write($"</{element.Name.LocalName}>\r\n");
    }

    /// <summary>Text, or (never seen from SimSig) text mixed with elements, written with no added whitespace.</summary>
    private static void WriteInline(TextWriter w, XNode node)
    {
        switch (node)
        {
            case XText text:
                Escape(w, text.Value);
                break;
            case XElement child:
                WriteStart(w, child);
                if (child.IsEmpty)
                {
                    w.Write("/>");
                    break;
                }

                w.Write('>');
                foreach (var grandchild in child.Nodes())
                {
                    WriteInline(w, grandchild);
                }

                w.Write($"</{child.Name.LocalName}>");
                break;
        }
    }

    private static void WriteStart(TextWriter w, XElement element)
    {
        w.Write('<');
        w.Write(element.Name.LocalName);
        foreach (var attribute in element.Attributes())
        {
            w.Write($" {attribute.Name.LocalName}=\"");
            Escape(w, attribute.Value);
            w.Write('"');
        }
    }

    private static void Indent(TextWriter w, int depth)
    {
        for (var i = 0; i < depth; i++)
        {
            w.Write("  ");
        }
    }

    /// <summary>
    /// SimSig's escaping, from the corpus: the five XML entities, and a character reference with four or more upper-case
    /// hex digits (<c>&amp;#x00BD;</c>, <c>&amp;#x000A;</c>) for anything outside printable ASCII but the tab.
    /// </summary>
    private static void Escape(TextWriter w, string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '&': w.Write("&amp;"); break;
                case '<': w.Write("&lt;"); break;
                case '>': w.Write("&gt;"); break;
                case '\'': w.Write("&apos;"); break;
                case '"': w.Write("&quot;"); break;
                case '\t': w.Write(c); break;
                case < ' ' or > '~':
                    var code = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])
                        ? char.ConvertToUtf32(c, text[++i])
                        : c;
                    w.Write(string.Create(CultureInfo.InvariantCulture, $"&#x{code:X4};"));
                    break;
                default: w.Write(c); break;
            }
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, Action<Stream> write)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        write(stream);
    }

    /// <summary>
    /// Copies <paramref name="path"/> to <c>{folder}\{area}\{name} {version} {time}.wtt</c>, outside SimSig's folders so
    /// the Loader never lists it, and keeps the newest <paramref name="kept"/> of that timetable's backups.
    /// </summary>
    private static string BackUp(string path, string folder, string? area, int kept)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var version = ReadVersion(path)?.ToString() ?? "unversioned";
        var directory = Path.Combine(folder, SafeName(string.IsNullOrWhiteSpace(area) ? "unknown" : area));
        Directory.CreateDirectory(directory);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(directory, $"{SafeName(name)} {version} {stamp}.wtt");
        for (var n = 2; File.Exists(target); n++)
        {
            target = Path.Combine(directory, $"{SafeName(name)} {version} {stamp} ({n}).wtt");
        }

        File.Copy(path, target);
        Prune(directory, SafeName(name), kept);
        return target;
    }

    private static void Prune(string directory, string name, int kept)
    {
        var pattern = new Regex($"^{Regex.Escape(name)} (?:(?<major>\\d+)\\.(?<minor>\\d+)\\.(?<build>\\d+)|unversioned) (?<stamp>\\d{{8}}-\\d{{6}})(?: \\((?<n>\\d+)\\))?\\.wtt$", RegexOptions.IgnoreCase);
        var backups = Directory.EnumerateFiles(directory, "*.wtt")
            .Select(f => (File: f, Match: pattern.Match(Path.GetFileName(f))))
            .Where(b => b.Match.Success)
            .OrderByDescending(b => b.Match.Groups["stamp"].Value, StringComparer.Ordinal)
            .ThenByDescending(b => b.Match.Groups["major"].Success
                ? new WttVersion(Number(b.Match, "major"), Number(b.Match, "minor"), Number(b.Match, "build"))
                : new WttVersion(0, 0, 0))
            .ThenByDescending(b => b.Match.Groups["n"].Success ? Number(b.Match, "n") : 1)
            .Skip(Math.Max(kept, 1));
        foreach (var (file, _) in backups)
        {
            File.Delete(file);
        }

        static int Number(Match match, string group) => int.TryParse(match.Groups[group].Value, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>The version of a file on disk, from its header (or its timetable); null if it has none or cannot be read.</summary>
    private static WttVersion? ReadVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.Name, WttDocument.HeaderEntry, StringComparison.OrdinalIgnoreCase))
                ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, WttReader.TimetableEntry, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                return null;
            }

            using var entryStream = entry.Open();
            using var reader = XmlReader.Create(entryStream, WttReader.XmlSettings);
            var root = XElement.Load(reader);
            return int.TryParse((string?)root.Element("VMajor"), CultureInfo.InvariantCulture, out var major)
                ? new WttVersion(major, ParseOrZero(root.Element("VMinor")), ParseOrZero(root.Element("VBuild")))
                : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or UnauthorizedAccessException)
        {
            return null;
        }

        static int ParseOrZero(XElement? e) => int.TryParse((string?)e, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string SafeName(string name) => InvalidChars().Replace(name, "_");

    [GeneratedRegex("[\\\\/:*?\"<>|]")]
    private static partial Regex InvalidChars();
}
