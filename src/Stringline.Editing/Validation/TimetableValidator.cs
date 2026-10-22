using System.Xml.Linq;
using Stringline.Editing.Knowledge;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Validation;

/// <summary>
/// Checks a timetable before SimSig sees it (<c>08-…md</c> §5): the Analyser's checks that can be made offline (in
/// SimSig's wording), checks against what the sim's other timetables and play show (<see cref="SimKnowledge"/>; never
/// proof, so Warnings or Info), and checks of the rules. Each issue may carry a <see cref="QuickFix"/>, an ordinary
/// <see cref="TimetableEdit"/>.
/// </summary>
/// <remarks>
/// <see cref="Validate(WttDocument)"/> checks everything (on open and save). After an edit, <see cref="Revalidate"/>
/// checks again only the trains the edit touched and the trains linked to them (activities, formations, a shared seed
/// signal), reusing the rest; the document's and the rules' checks, which are cheap, always run in full. Thread-safe:
/// it holds only the knowledge.
/// </remarks>
public sealed class TimetableValidator
{
    /// <param name="knowledge">The sim's knowledge for the sim-knowledge checks; with none (or one for another sim, or built from no timetables) they are left out.</param>
    public TimetableValidator(SimKnowledge? knowledge = null)
    {
        Knowledge = knowledge;
    }

    public SimKnowledge? Knowledge { get; }

    public ValidationResult Validate(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Validate(document.Import());
    }

    /// <summary>Checks the document of a current import (the editor imports each revision once and shares it).</summary>
    public ValidationResult Validate(WttDocumentImport import)
    {
        var context = Prepare(import);
        var issues = new Dictionary<XElement, IReadOnlyList<ValidationIssue>>();
        foreach (var train in context.TrainElements)
        {
            issues[train] = TrainChecks.Check(context, train);
        }

        return Finish(context, issues, context.TrainElements.Count);
    }

    /// <summary>
    /// Checks the document again after <paramref name="edits"/> (applied since <paramref name="previous"/>): the trains
    /// they touched and every train linked to those, before or after, are checked again; the others keep their issues.
    /// An edit outside the trains and rules (the header, a category, a seed group) checks everything.
    /// </summary>
    public ValidationResult Revalidate(ValidationResult previous, WttDocumentImport import, IEnumerable<TimetableEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(edits);
        if (!ReferenceEquals(previous.Knowledge, UsableKnowledge(import)) || TouchedTrains(edits) is not { } touched)
        {
            return Validate(import);
        }

        var context = Prepare(import);
        if (!context.AreaEdges.SetEquals(previous.AreaEdges))
        {
            return Validate(import);
        }

        var affected = new HashSet<XElement>();
        var queue = new Queue<XElement>(touched);
        while (queue.TryDequeue(out var train))
        {
            if (!affected.Add(train))
            {
                continue;
            }

            foreach (var links in new[] { previous.Links, context.Links })
            {
                if (links.TryGetValue(train, out var linked))
                {
                    foreach (var other in linked)
                    {
                        queue.Enqueue(other);
                    }
                }
            }
        }

        var issues = new Dictionary<XElement, IReadOnlyList<ValidationIssue>>();
        var checkedCount = 0;
        foreach (var train in context.TrainElements)
        {
            if (affected.Contains(train) || !previous.TrainIssues.TryGetValue(train, out var kept))
            {
                issues[train] = TrainChecks.Check(context, train);
                checkedCount++;
            }
            else
            {
                // The train's ID can change when another train with its name is added or removed before it.
                issues[train] = context.TrainOf(train) is { } current
                    ? [.. kept.Select(i => i with { Train = current.Id, Headcode = current.Headcode.Value })]
                    : kept;
            }
        }

        return Finish(context, issues, checkedCount);
    }

    /// <summary>The train elements the edits touch (removed trains included); null if one touches something else that trains depend on.</summary>
    private static HashSet<XElement>? TouchedTrains(IEnumerable<TimetableEdit> edits)
    {
        var touched = new HashSet<XElement>();
        foreach (var target in edits.SelectMany(e => e.Targets))
        {
            if (target.AncestorsAndSelf("Timetable").FirstOrDefault() is { } train)
            {
                touched.Add(train);
            }
            else if (target.Name.LocalName != "Timetables")
            {
                return null; // the header, categories, seed groups or rules (which can keep trains apart): check all
            }
        }

        return touched;
    }

    private SimKnowledge? UsableKnowledge(WttDocumentImport import) =>
        Knowledge is { Timetables: > 0 } k && string.Equals(k.AreaId, import.Timetable.AreaId, StringComparison.OrdinalIgnoreCase) ? k : null;

    private ValidationContext Prepare(WttDocumentImport import)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (!import.IsCurrent)
        {
            throw new InvalidOperationException("The document has changed since this import; import it again.");
        }

        return new ValidationContext(import, UsableKnowledge(import));
    }

    private static ValidationResult Finish(ValidationContext context, Dictionary<XElement, IReadOnlyList<ValidationIssue>> issues, int trainsChecked) =>
        new(
            context.Import.Revision,
            context.Knowledge,
            DocumentChecks.Check(context),
            context.TrainElements,
            issues,
            RuleChecks.Check(context),
            context.Links,
            context.AreaEdges,
            trainsChecked);
}
