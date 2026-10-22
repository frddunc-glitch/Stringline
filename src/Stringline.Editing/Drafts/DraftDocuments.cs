using System.Globalization;
using System.Xml.Linq;
using Stringline.Core;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Drafts;

/// <summary>New documents and new trains for drafts (Timetable ▸ New draft ▸ Blank; the editor's New and Copy train).</summary>
public static class DraftDocuments
{
    /// <summary>
    /// A blank timetable for a sim: its ID, a name, start and finish, version 1.0.0 (so the first save is 1.0.1), and
    /// the categories (train types) of <paramref name="categoriesFrom"/> (a timetable of the same sim), so trains can
    /// be given one at once.
    /// </summary>
    public static WttDocument Blank(string areaId, string name, SimTime start, WttDocument? categoriesFrom = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        var finish = start + 24 * 3600;
        var root = new XElement(
            "SimSigTimetable",
            new XAttribute("ID", areaId.Trim()),
            categoriesFrom?.Header.SimSigVersion is { } version ? new XAttribute("Version", version) : null,
            new XElement("Name", name),
            new XElement("StartTime", start.Seconds.ToString(CultureInfo.InvariantCulture)),
            new XElement("FinishTime", finish.Seconds.ToString(CultureInfo.InvariantCulture)),
            new XElement("VMajor", "1"),
            new XElement("VMinor", "0"),
            new XElement("VBuild", "0"),
            new XElement("TrainCategories", categoriesFrom?.Categories.Select(c => new XElement(c.Element))),
            new XElement("Timetables"));
        return WttDocument.Parse(root.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>A new train with this headcode, a category (if any), and one timing point to start from.</summary>
    public static XElement NewTrain(string headcode, string? category, string? firstLocation = null) =>
        new(
            "Timetable",
            new XElement("ID", headcode),
            category is null ? null : new XElement("Category", category),
            new XElement("Trips", new XElement("Trip", new XElement("Location", firstLocation ?? string.Empty))));

    /// <summary>A copy of a train with a new headcode and no UID (UIDs must be unique), its activities kept.</summary>
    public static XElement CopyTrain(TrainNode train, string headcode)
    {
        ArgumentNullException.ThrowIfNull(train);
        var copy = new XElement(train.Element);
        copy.Element("ID")?.SetValue(headcode);
        copy.Element("UID")?.Remove();
        return copy;
    }

    /// <summary>The <c>Timetables</c> element trains go in, made (with its insert) if the document has none.</summary>
    public static (XElement Container, TimetableEdit? Insert) TrainsContainer(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Root.Element("Timetables") is { } existing)
        {
            return (existing, null);
        }

        var made = new XElement("Timetables");
        return (made, TimetableEdit.Insert(document.Root, made));
    }
}
