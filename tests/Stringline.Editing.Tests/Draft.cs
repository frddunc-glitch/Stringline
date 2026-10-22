using System.Xml.Linq;
using Stringline.Core;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Tests;

/// <summary>Builds small timetables as XML for the checks' tests: trains enter at E, run on diesel and are 100 m long unless told otherwise.</summary>
internal static class Draft
{
    public static int T(string clock) => SimTime.Parse(clock).Seconds;

    public static WttDocument Doc(params object[] content) => Doc("test", content);

    /// <param name="content">Trains (<c>Timetable</c>), rules (<c>TimetableRule</c>), categories (<c>TrainCategory</c>) and seed groups (<c>SeedGroup</c>), put in their containers.</param>
    public static WttDocument Doc(string? area, params object[] content)
    {
        var elements = Flatten(content).ToList();
        XElement Container(string name, string item) => new(name, elements.Where(e => e.Name.LocalName == item));
        var root = new XElement(
            "SimSigTimetable",
            area is null ? null : new XAttribute("ID", area),
            new XAttribute("Version", "5.0"),
            new XElement("Name", "Test"),
            new XElement("StartTime", T("04:00")),
            new XElement("FinishTime", T("23:00")),
            Container("TrainCategories", "TrainCategory"),
            Container("Timetables", "Timetable"),
            Container("TimetableRules", "TimetableRule"),
            Container("SeedGroups", "SeedGroup"));
        return WttDocument.Parse(root.ToString());
    }

    /// <summary>A train: its fields and <c>Trips</c>; entering at E at 09:59 by default, diesel, 100 m.</summary>
    public static XElement Train(string id, params object[] content)
    {
        var parts = Flatten(content).ToList();
        var train = new XElement("Timetable", new XElement("ID", id), parts);
        void Default(string name, object value)
        {
            if (train.Element(name) is null)
            {
                train.AddFirst(new XElement(name, value));
            }
        }

        if (train.Element("SeedPoint") is null && train.Element("NoEntry") is null)
        {
            Default("EntryPoint", "E");
            Default("DepartTime", T("09:59"));
        }

        train.Element("NoEntry")?.Remove();
        Default("TrainLength", 100);
        Default("Electrification", "D");
        return train;
    }

    /// <summary>The elements in some content, however nested in arrays.</summary>
    private static IEnumerable<XElement> Flatten(IEnumerable<object?> content)
    {
        foreach (var item in content)
        {
            switch (item)
            {
                case XElement element:
                    yield return element;
                    break;
                case IEnumerable<object?> many:
                    foreach (var inner in Flatten(many))
                    {
                        yield return inner;
                    }

                    break;
            }
        }
    }

    /// <summary>Marks a train as having no entry point (it starts where it is formed).</summary>
    public static XElement NoEntry => new("NoEntry");

    public static XElement[] Seeded(string signal, string? group = null) =>
        group is null ? [new XElement("SeedPoint", signal)] : [new XElement("SeedPoint", signal), new XElement("SeedGroup", group)];

    public static XElement Field(string name, object value) => new(name, value);

    public static XElement Trips(params XElement[] trips) => new("Trips", trips);

    public static XElement Stop(string location, string? arrive, string? depart, params object[] extra) =>
        new("Trip", new XElement("Location", location), arrive is null ? null : new XElement("ArrTime", T(arrive)), depart is null ? null : new XElement("DepPassTime", T(depart)), extra);

    public static XElement Pass(string location, string time, params object[] extra) =>
        new("Trip", new XElement("Location", location), new XElement("DepPassTime", T(time)), new XElement("IsPassTime", -1), extra);

    public static XElement Activities(params XElement[] activities) => new("Activities", activities);

    public static XElement Activity(int code, string? train, params object[] extra) =>
        new("Activity", new XElement("Activity", code), train is null ? null : new XElement("AssociatedTrain", train), extra);

    public static XElement Rule(int code, string x, string y, string? location = null) =>
        new("TimetableRule", new XElement("Train", x), new XElement("Rule", code), location is null ? null : new XElement("Location", location), new XElement("Train2", y));

    public static XElement Category(string id, params object[] content) => new("TrainCategory", new XAttribute("ID", id), new XElement("Description", id), content);

    public static XElement SeedGroup(string id, string start) => new("SeedGroup", new XElement("ID", id), new XElement("StartTime", T(start)), new XElement("Title", start));

    /// <summary>A simple well-formed train: enters, departs A at 10:00 and calls and leaves at B.</summary>
    public static XElement Simple(string id, params object[] content) =>
        Train(id, [.. content, Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:11"))]);

    public static ValidationResult Check(WttDocument document, SimKnowledge? knowledge = null) => new TimetableValidator(knowledge).Validate(document);

    public static IEnumerable<string> Codes(this ValidationResult result) => result.Issues.Select(i => i.Code);

    public static IEnumerable<ValidationIssue> Of(this ValidationResult result, string code) => result.Issues.Where(i => i.Code == code);

    /// <summary>Applies an issue's quick fix and checks the document again.</summary>
    public static ValidationResult Fix(this ValidationIssue issue, WttDocument document, SimKnowledge? knowledge = null)
    {
        issue.QuickFix.Should().NotBeNull(issue.ToString());
        document.Apply(issue.QuickFix!.Edit);
        return Check(document, knowledge);
    }
}
