using System.Xml.Linq;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Gateway.Tests;
using Stringline.Persistence;
using Stringline.Timetable.Editing;
using static Stringline.Editing.Tests.Draft;

namespace Stringline.Editing.Tests;

/// <summary>
/// After an edit, only the trains it touches and the trains linked to them are checked again; the result must be the
/// same as a full check. And the free sims' timetables have no Errors.
/// </summary>
public sealed class IncrementalValidationTests
{
    private static readonly Lazy<SimKnowledge> WaterlooKnowledge = new(() =>
    {
        var folder = Path.GetDirectoryName(Fixtures.Path("waterloo 2010.WTT"))!;
        var request = new SimKnowledgeRequest { AreaId = "waterloo", TimetableFolders = [folder], PlayFiles = [Fixtures.Path(Fixtures.Waterloo1x)], ReadPlay = path => PlayFiles.Read(path) };
        return SimKnowledgeStore.Build(request, SimKnowledgeStore.FindSources(request), out _);
    });

    /// <summary>What a check says, quick fixes by title (each check makes its own edits).</summary>
    private static List<string> Said(ValidationResult result) =>
        [.. result.Issues.Select(i => $"{i} | {i.Train} | {i.QuickFix?.Title} | {i.TrainElement?.Element("ID")?.Value}")];

    /// <summary>Applies an edit, then checks incrementally and in full, and compares.</summary>
    private static ValidationResult Step(TimetableValidator validator, WttDocument document, ValidationResult previous, TimetableEdit edit)
    {
        document.Apply(edit);
        var import = document.Import();
        var incremental = validator.Revalidate(previous, import, [edit]);
        var full = validator.Validate(import);
        Said(incremental).Should().Equal(Said(full), edit.Description);
        return incremental;
    }

    [Fact]
    public void Incremental_checks_agree_with_full_checks_through_edits_to_links()
    {
        var document = Doc(
            Train("1A01", Trips(Stop("A", null, "10:00"), Stop("B", "10:10", "10:10", Activities(Activity(0, "1B01"))))),
            Train("1B01", NoEntry, Trips(Stop("B", null, "10:20"), Stop("A", "10:30", "10:31"))),
            Train("1A02", Trips(Stop("A", null, "11:00"), Stop("B", "11:10", "11:10", Activities(Activity(0, "1B02"))))),
            Train("1B02", NoEntry, Trips(Stop("B", null, "11:20"), Stop("A", "11:30", "11:31"))),
            Train("1C01", Seeded("S1"), Trips(Stop("A", null, "09:00"), Pass("B", "09:10"))),
            Train("1C02", Seeded("S2"), Trips(Stop("A", null, "09:00"), Pass("B", "09:10"))),
            Simple("2D01"));
        var validator = new TimetableValidator();
        var result = validator.Validate(document);
        Said(result).Should().BeEmpty();

        TrainNode Node(string headcode) => document.Trains.First(t => t.Headcode == headcode);
        XElement NextOf(string headcode) => Node(headcode).Trips.Last().Activities.Single().Element;

        // 1A02 now also forms 1B01: 1B02 is never used, and 1A02 forms a used train.
        result = Step(validator, document, result, TimetableEdit.SetField(NextOf("1A02"), "AssociatedTrain", "1B01"));
        result.Codes().Should().BeEquivalentTo([IssueCodes.NextAlreadyUsed, IssueCodes.NeverUsed]);
        result.TrainsChecked.Should().BeLessThan(7, "2D01 and the seeded trains are not linked");

        // A seed signal shared, then a renumber that names the next working again.
        result = Step(validator, document, result, TimetableEdit.Change(Node("1C02"), t => t.SeedPoint = "S1"));
        result.Codes().Should().Contain(IssueCodes.SeedSignalShared);
        result = Step(validator, document, result, TimetableEdit.Renumber(document, Node("1B02"), "1B03"));
        result = Step(validator, document, result, TimetableEdit.SetField(NextOf("1A02"), "AssociatedTrain", "1B03"));
        result.Codes().Should().BeEquivalentTo([IssueCodes.SeedSignalShared]);

        // A train removed, and one added that takes a removed train's name.
        var removed = Node("1B01").Element;
        result = Step(validator, document, result, TimetableEdit.Remove(removed));
        result.Codes().Should().Contain(IssueCodes.AssociatedNotDefined);
        result = Step(validator, document, result, TimetableEdit.Insert(document.Root.Element("Timetables")!, new XElement(removed)));
        result.Codes().Should().NotContain(IssueCodes.AssociatedNotDefined);

        // A trip retimed out of order, and a category edit (which checks everything).
        result = Step(validator, document, result, TimetableEdit.Change(Node("2D01").Trips.Last(), t => t.DepPassTime = new Core.SimTime(T("10:05"))));
        result.Codes().Should().Contain(IssueCodes.DepartureBeforeArrival);
        result = Step(validator, document, result, TimetableEdit.SetField(document.Root, "Name", "Renamed"));
        result.TrainsChecked.Should().Be(7);
    }

    [Fact]
    public void Incremental_checks_agree_with_full_checks_on_Waterloo()
    {
        var document = WttDocument.Load(Fixtures.Path("waterloo 2010.WTT"));
        var validator = new TimetableValidator(WaterlooKnowledge.Value);
        var result = validator.Validate(document);
        var trains = document.Trains.ToList();

        result = Step(validator, document, result, TimetableEdit.Change(trains[10].Trips.ElementAt(1), t => t.Platform = "99"));
        result.TrainsChecked.Should().BeLessThan(100, "a platform touches one train and its links");
        result = Step(validator, document, result, TimetableEdit.Renumber(document, trains[20], "2Z99"));
        result = Step(validator, document, result, TimetableEdit.Remove(trains[30].Element));
        result = Step(validator, document, result, TimetableEdit.Change(trains.Skip(40).First(t => t.EntryPoint is not null), t => t.EntryPoint = "ENOWHERE"));

        result.Codes().Should().Contain([IssueCodes.InvalidPlatform, IssueCodes.UnknownEntryPoint]);
    }

    [Theory]
    [InlineData("waterloo 2010.WTT", "waterloo")]
    [InlineData("llangollen 2012 B.WTT", "llangollen")]
    public void The_free_sims_have_no_errors(string file, string area)
    {
        var folder = Path.GetDirectoryName(Fixtures.Path(file))!;
        var request = new SimKnowledgeRequest { AreaId = area, TimetableFolders = [folder] };
        var knowledge = SimKnowledgeStore.Build(request, SimKnowledgeStore.FindSources(request), out _);

        var result = Check(WttDocument.Load(Path.Combine(folder, file)), knowledge);

        result.Issues.Where(i => i.Severity == IssueSeverity.Error).Should().BeEmpty();
        result.Issues.Where(i => i.Severity == IssueSeverity.Warning).Should().BeEmpty();
        result.BlocksSave.Should().BeFalse();
    }

    [Fact]
    public void Edits_name_the_elements_they_touch()
    {
        var document = Doc(Simple("1A01"));
        var train = document.Trains.Single();
        var trip = train.Trips.First();

        TimetableEdit.SetField(trip.Element, "Platform", "1").Targets.Should().Equal(trip.Element);
        TimetableEdit.Remove(trip.Element).Targets.Should().Equal(train.TripsElement!, trip.Element);
        TimetableEdit.Composite("Both", [TimetableEdit.Move(trip.Element, 1), TimetableEdit.Insert(train.TripsElement!, new XElement("Trip"))])
            .Targets.Should().HaveCount(3).And.Contain(train.TripsElement!);
    }
}
