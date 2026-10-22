using System.Xml.Linq;
using Stringline.Core.Timetables;

namespace Stringline.Timetable.Editing;

/// <summary>
/// A document's import (<see cref="WttDocument.Import"/>), with the way back from each imported train, timing point,
/// activity and rule to its element. Valid for the document's <see cref="WttDocument.Revision"/> it was made at.
/// </summary>
public sealed class WttDocumentImport
{
    private readonly WttDocument _document;
    private readonly long _revision;
    private readonly ImportSources _sources;
    private readonly List<XElement> _trainElements;
    private readonly List<XElement> _ruleElements;
    private readonly Dictionary<Train, int> _trainIndex = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XElement, int> _trainByElement = [];

    internal WttDocumentImport(WttDocument document, WttImportResult result, long revision)
    {
        _document = document;
        _revision = revision;
        Result = result;
        _sources = result.Sources ?? throw new InvalidOperationException("The import has no element places.");
        _trainElements = document.Root.Elements("Timetables").SelectMany(c => c.Elements()).ToList();
        _ruleElements = document.Root.Elements("TimetableRules").SelectMany(c => c.Elements()).ToList();
        for (var i = 0; i < result.Timetable.Trains.Count; i++)
        {
            _trainIndex[result.Timetable.Trains[i]] = i;
            _trainByElement[_trainElements[_sources.Trains[i].Element]] = i;
        }
    }

    public WttImportResult Result { get; }

    public WttDocument Document => _document;

    /// <summary>The document's <see cref="WttDocument.Revision"/> the import was made at.</summary>
    public long Revision => _revision;

    /// <summary>Whether the document is unchanged since the import.</summary>
    public bool IsCurrent => _document.Revision == _revision;

    public WorkingTimetable Timetable => Result.Timetable;

    /// <summary>The imported train for this element, or null if the importer skipped it (no headcode or no usable points).</summary>
    public Train? TrainOf(TrainNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        CheckCurrent();
        return _trainByElement.TryGetValue(node.Element, out var i) ? Timetable.Trains[i] : null;
    }

    public TrainNode NodeOf(Train train)
    {
        CheckCurrent();
        return new TrainNode(_document, _trainElements[_sources.Trains[IndexOf(train)].Element]);
    }

    /// <summary>The <c>Trip</c> of a train's timing point (by <see cref="TimingPoint.Index"/>).</summary>
    public TripNode TripOf(Train train, int pointIndex)
    {
        var source = _sources.Trains[IndexOf(train)];
        var trips = NodeOf(train).TripsElement!.Elements().ToList();
        return new TripNode(_document, trips[source.Trips[pointIndex]]);
    }

    /// <summary>The timing point (its <see cref="TimingPoint.Index"/>) a train's <c>Trip</c> became, or null if the importer left it out.</summary>
    public int? PointOf(Train train, XElement trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        var source = _sources.Trains[IndexOf(train)];
        var place = trip.Parent == NodeOf(train).TripsElement ? trip.ElementsBeforeSelf().Count() : -1;
        for (var k = 0; k < source.Trips.Count; k++)
        {
            if (source.Trips[k] == place)
            {
                return k;
            }
        }

        return null;
    }

    /// <summary>The <c>Activity</c> of a timing point's activity (its place in <see cref="TimingPoint.Activities"/>).</summary>
    public ActivityNode ActivityOf(Train train, int pointIndex, int activityIndex)
    {
        var source = _sources.Trains[IndexOf(train)];
        var activities = TripOf(train, pointIndex).ActivitiesElement!.Elements().ToList();
        return new ActivityNode(_document, activities[source.Activities[pointIndex][activityIndex]]);
    }

    /// <summary>The <c>TimetableRule</c> of a rule (its place in <see cref="WorkingTimetable.Rules"/>).</summary>
    public RuleNode RuleOf(int ruleIndex)
    {
        CheckCurrent();
        return new RuleNode(_document, _ruleElements[_sources.Rules[ruleIndex]]);
    }

    private int IndexOf(Train train) =>
        _trainIndex.TryGetValue(train, out var i) ? i : throw new ArgumentException($"Train {train.Id} is not from this import.", nameof(train));

    private void CheckCurrent()
    {
        if (_document.Revision != _revision)
        {
            throw new InvalidOperationException("The document has changed since this import; import it again.");
        }
    }
}
