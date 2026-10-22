using System.Xml.Linq;
using Stringline.Editing.Knowledge;

namespace Stringline.Editing.Validation;

/// <summary>
/// The issues found in one revision of a document: the document's own, each train's (by its element) and the rules'.
/// Kept by the editor so the next check after an edit can reuse the issues of trains the edit did not touch
/// (<see cref="TimetableValidator.Revalidate"/>).
/// </summary>
public sealed class ValidationResult
{
    internal ValidationResult(
        long revision,
        SimKnowledge? knowledge,
        IReadOnlyList<ValidationIssue> documentIssues,
        IReadOnlyList<XElement> trains,
        IReadOnlyDictionary<XElement, IReadOnlyList<ValidationIssue>> trainIssues,
        IReadOnlyList<ValidationIssue> ruleIssues,
        IReadOnlyDictionary<XElement, IReadOnlySet<XElement>> links,
        IReadOnlySet<Core.Tiploc> areaEdges,
        int trainsChecked)
    {
        AreaEdges = areaEdges;
        Revision = revision;
        Knowledge = knowledge;
        DocumentIssues = documentIssues;
        Trains = trains;
        TrainIssues = trainIssues;
        RuleIssues = ruleIssues;
        Links = links;
        TrainsChecked = trainsChecked;
        Issues = [.. documentIssues, .. trains.SelectMany(t => trainIssues.TryGetValue(t, out var issues) ? issues : []), .. ruleIssues];
    }

    /// <summary>The document's revision the issues are for.</summary>
    public long Revision { get; }

    /// <summary>The knowledge the sim-knowledge checks used, if any.</summary>
    public SimKnowledge? Knowledge { get; }

    /// <summary>Every issue: the document's, then each train's in file order, then the rules'.</summary>
    public IReadOnlyList<ValidationIssue> Issues { get; }

    public IReadOnlyList<ValidationIssue> DocumentIssues { get; }

    public IReadOnlyList<ValidationIssue> RuleIssues { get; }

    /// <summary>How many trains were checked to make this result (all of them, or those an edit touched and their links).</summary>
    public int TrainsChecked { get; }

    /// <summary>Errors that would make the saved file unreadable (no sim ID, a category defined twice): a save is refused.</summary>
    public bool BlocksSave => Issues.Any(i => IssueCatalogue.Of(i.Code).BlocksSave);

    /// <summary>The trains' elements in file order.</summary>
    internal IReadOnlyList<XElement> Trains { get; }

    internal IReadOnlyDictionary<XElement, IReadOnlyList<ValidationIssue>> TrainIssues { get; }

    /// <summary>Trains whose checks depend on each other (activities, formations, a shared seed signal), both ways.</summary>
    internal IReadOnlyDictionary<XElement, IReadOnlySet<XElement>> Links { get; }

    /// <summary>Where the trains showed the area ends (<see cref="ValidationContext.AreaEdges"/>): a change checks every train.</summary>
    internal IReadOnlySet<Core.Tiploc> AreaEdges { get; }

    public int Count(IssueSeverity severity) => Issues.Count(i => i.Severity == severity);

    /// <summary>The issues of one train (by its element), rules about it included.</summary>
    public IEnumerable<ValidationIssue> For(XElement train) =>
        (TrainIssues.TryGetValue(train, out var issues) ? issues : []).Concat(RuleIssues.Where(i => i.TrainElement == train));
}
