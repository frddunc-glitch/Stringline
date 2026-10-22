using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Editing;

/// <summary>How a file writes yes/no: SimSig's older <c>True</c>/<c>False</c>, or Delphi's <c>-1</c>/<c>0</c> in newer files.</summary>
public enum WttBooleanStyle
{
    Delphi,
    TrueFalse,
}

/// <summary>
/// A timetable as an editable, lossless document (<c>docs/08-…md</c> §3.1): the parsed <c>SavedTimetable.xml</c> (no
/// whitespace or comments, as <see cref="WttReader"/> reads it) plus the zip's other entries, kept as bytes. Typed
/// views (<see cref="Header"/>, <see cref="Trains"/>…) read and write through to the elements, so whatever Stringline
/// does not model is written back untouched by <see cref="WttWriter"/>.
/// </summary>
/// <remarks>Not thread-safe: one writer (the draft's editor) changes it, through <see cref="TimetableEdit"/>s.</remarks>
public sealed class WttDocument
{
    /// <summary>The header extract, regenerated on every save rather than kept.</summary>
    internal const string HeaderEntry = "TimetableHeader.xml";

    private WttDocument(XDocument xml, IReadOnlyList<KeyValuePair<string, byte[]>> otherEntries, string? sourcePath, bool readAsLatin1)
    {
        if (xml.Root is not { } root || root.Name.LocalName != WttSchema.RootName)
        {
            throw new WttFormatException($"The root element is <{xml.Root?.Name.LocalName}>, not <{WttSchema.RootName}>, so this is not a SimSig timetable.");
        }

        Xml = xml;
        OtherEntries = otherEntries;
        SourcePath = sourcePath;
        ReadAsLatin1 = readAsLatin1;
        BooleanStyle = DetectBooleanStyle(root);
        Xml.Changed += (_, _) => Revision++;
    }

    public XDocument Xml { get; }

    public XElement Root => Xml.Root!;

    /// <summary>The zip's entries other than <c>SavedTimetable.xml</c> and <c>TimetableHeader.xml</c>, written back as they are.</summary>
    public IReadOnlyList<KeyValuePair<string, byte[]>> OtherEntries { get; }

    /// <summary>The file it was opened from, if any.</summary>
    public string? SourcePath { get; set; }

    /// <summary>The XML was not UTF-8 and was read as Latin-1; it is saved as UTF-8.</summary>
    public bool ReadAsLatin1 { get; }

    /// <summary>The file's own style for yes/no, used for every value written (Delphi for a file with none).</summary>
    public WttBooleanStyle BooleanStyle { get; }

    /// <summary>Goes up on every change to the tree, so views of an import can tell they are out of date.</summary>
    public long Revision { get; private set; }

    public HeaderNode Header => new(this, Root);

    public IEnumerable<CategoryNode> Categories => Items("TrainCategories", "TrainCategory").Select(e => new CategoryNode(this, e));

    /// <summary>Every train (<c>Timetable</c> element), in file order.</summary>
    public IEnumerable<TrainNode> Trains => Items("Timetables", "Timetable").Select(e => new TrainNode(this, e));

    public IEnumerable<RuleNode> Rules => Items("TimetableRules", "TimetableRule").Select(e => new RuleNode(this, e));

    public IEnumerable<SeedGroupNode> SeedGroups => Items("SeedGroups", "SeedGroup").Select(e => new SeedGroupNode(this, e));

    public ListNode? Decisions => List("Decisions");

    public ListNode? ClassOfServiceRules => List("TTClassOfServiceRules");

    /// <summary>The driving options' caution speed sets.</summary>
    public ListNode? CautionSpeedSets => List("CautionSpeedSets");

    public static WttDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var stream = File.OpenRead(path);
        var document = Load(stream);
        document.SourcePath = Path.GetFullPath(path);
        return document;
    }

    /// <summary>Reads a <c>.wtt</c> zip, or a <c>SavedTimetable.xml</c> on its own.</summary>
    /// <exception cref="WttFormatException">Not a timetable, or XML that cannot be parsed (an editor needs the whole tree).</exception>
    public static WttDocument Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var xml = WttReader.ExtractXml(WttReader.ReadAll(stream), out var others);
        var latin1 = false;
        try
        {
            using var reader = WttReader.CreateXmlReader(xml, _ => latin1 = true);
            var document = XDocument.Load(reader, LoadOptions.None);
            others.RemoveAll(e => string.Equals(Path.GetFileName(e.Key), HeaderEntry, StringComparison.OrdinalIgnoreCase));
            return new WttDocument(document, others, null, latin1);
        }
        catch (XmlException ex)
        {
            throw new WttFormatException($"The timetable XML cannot be read at line {ex.LineNumber}: {ex.Message}", ex);
        }
    }

    /// <summary>A document over this XML (for tests and new timetables).</summary>
    public static WttDocument Parse(string xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        using var reader = XmlReader.Create(new StringReader(xml), WttReader.XmlSettings);
        return new WttDocument(XDocument.Load(reader, LoadOptions.None), [], null, false);
    }

    /// <summary>A deep copy, with its own elements: a draft that leaves this document (a session's timetable) untouched.</summary>
    public WttDocument Clone() => new(new XDocument(Xml), OtherEntries, SourcePath, ReadAsLatin1);

    /// <summary>Applies an edit and returns the edit that undoes it.</summary>
    public TimetableEdit Apply(TimetableEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return edit.Apply(this);
    }

    /// <summary>Imports the document as it is now with <see cref="WttReader"/>, exactly as the saved file would import.</summary>
    public WttDocumentImport Import(ILogger<WttReader>? logger = null) =>
        new(this, new WttReader(logger).Read(Xml, SourcePath is null ? null : Path.GetFileName(SourcePath)), Revision);

    public WorkingTimetable ToWorkingTimetable() => Import().Timetable;

    /// <summary>A root list by name (<c>Decisions</c>…), if the file has it.</summary>
    public ListNode? List(string name) => Root.Element(name) is { } element ? new ListNode(this, element) : null;

    /// <summary>The items of every root container of this name (a file may, oddly, have two).</summary>
    internal IEnumerable<XElement> Items(string container, string item) => Root.Elements(container).SelectMany(c => c.Elements(item));

    private static WttBooleanStyle DetectBooleanStyle(XElement root)
    {
        foreach (var element in root.Descendants())
        {
            if (!element.HasElements && WttSchema.Booleans.Contains(element.Name.LocalName))
            {
                switch (element.Value.Trim().ToUpperInvariant())
                {
                    case "TRUE" or "FALSE": return WttBooleanStyle.TrueFalse;
                    case "-1" or "0": return WttBooleanStyle.Delphi;
                }
            }
        }

        return WttBooleanStyle.Delphi;
    }
}
