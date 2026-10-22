using System.Xml.Linq;

namespace Stringline.Timetable.Editing;

/// <summary>What an <see cref="EditStep"/> does: the primitive edits of <see cref="TimetableEdit"/>.</summary>
public enum EditStepKind
{
    /// <summary>Set, make or remove a field (<see cref="TimetableEdit.SetField"/>).</summary>
    SetField,

    /// <summary>Set a leaf element's text, or make it self-closing.</summary>
    SetLeaf,

    /// <summary>Replace an element's attributes.</summary>
    SetAttributes,

    Insert,

    Remove,

    Move,
}

/// <summary>An attribute of a <see cref="EditStepKind.SetAttributes"/> step.</summary>
public sealed record EditAttributeValue(string Name, string Value);

/// <summary>
/// One primitive edit with its elements named by their place in the tree (<see cref="ElementPath"/>), so it can be
/// saved as JSON and applied to the same document read again from disk.
/// </summary>
/// <param name="Path">The element changed: the field's owner, the leaf, the parent of an insert, or the node removed or moved.</param>
/// <param name="Text">The new text (null removes the field, or makes a leaf self-closing).</param>
/// <param name="Index">Where an insert or move puts the node among its parent's child elements; null for SimSig's order.</param>
/// <param name="Xml">The node an insert adds.</param>
public sealed record EditStep(
    EditStepKind Kind,
    string Path,
    string? Field = null,
    string? Text = null,
    int? Index = null,
    string? Xml = null,
    IReadOnlyList<EditAttributeValue>? Attributes = null)
{
    public static EditStep SetField(string owner, string field, string? text) => new(EditStepKind.SetField, owner, Field: field, Text: text);

    public static EditStep SetLeaf(string leaf, string? text) => new(EditStepKind.SetLeaf, leaf, Text: text);

    public static EditStep SetAttributes(string element, IReadOnlyList<EditAttributeValue> attributes) => new(EditStepKind.SetAttributes, element, Attributes: attributes);

    public static EditStep Insert(string parent, int? index, string xml) => new(EditStepKind.Insert, parent, Index: index, Xml: xml);

    public static EditStep Remove(string node) => new(EditStepKind.Remove, node);

    public static EditStep Move(string node, int index) => new(EditStepKind.Move, node, Index: index);

    /// <summary>The edit over this document's elements as they are now.</summary>
    /// <exception cref="InvalidOperationException">The step does not fit the document (an element it names is not there).</exception>
    internal TimetableEdit Resolve(WttDocument document, string description)
    {
        var element = ElementPath.Resolve(document, Path)
            ?? throw new InvalidOperationException($"The edit names {(Path.Length == 0 ? "the root" : Path)}, which is not in the timetable.");
        return Kind switch
        {
            EditStepKind.SetField => new SetFieldEdit(element, Field ?? throw Missing(nameof(Field)), Text, description),
            EditStepKind.SetLeaf => new SetLeafEdit(element, Text, description),
            EditStepKind.SetAttributes => new AttributesEdit(element, (Attributes ?? []).Select(a => (XName.Get(a.Name), a.Value)).ToList(), description),
            EditStepKind.Insert => new InsertNodeEdit(element, XElement.Parse(Xml ?? throw Missing(nameof(Xml)), LoadOptions.PreserveWhitespace), Index, description),
            EditStepKind.Remove => new RemoveNodeEdit(element, description),
            EditStepKind.Move => new MoveNodeEdit(element, Index ?? throw Missing(nameof(Index)), description),
            _ => throw new InvalidOperationException($"Unknown edit step {Kind}."),
        };

        InvalidOperationException Missing(string what) => new($"A {Kind} step has no {what}.");
    }
}

/// <summary>
/// An edit as saved steps (<see cref="TimetableEdit.ApplyRecorded"/>). Each step names its elements in the tree as it is
/// when that step is applied, so a record fits the document only in the state it was recorded for: a forward record
/// before the edit, a backward record after it.
/// </summary>
public sealed record EditRecord(string Description, IReadOnlyList<EditStep> Steps)
{
    /// <summary>An edit that finds the record's elements when it is applied, step by step.</summary>
    public TimetableEdit ToEdit() => new RecordedEdit(this);
}

/// <summary>An edit made by <see cref="TimetableEdit.ApplyRecorded"/>: its undo, and both as records.</summary>
public sealed record RecordedChange(TimetableEdit Undo, EditRecord Forward, EditRecord Backward);

/// <summary>
/// Names an element by its place in the tree: each level's name and its index among the siblings of that name, such as
/// <c>Timetables[0]/Timetable[12]/Trips[0]/Trip[3]</c> (the root is the empty path). Fields added or removed beside a
/// path's elements (a version written into the header, say) leave it pointing at the same element.
/// </summary>
public static class ElementPath
{
    /// <exception cref="InvalidOperationException">The element is not in the document.</exception>
    public static string Of(WttDocument document, XElement element)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(element);
        if (element.Document != document.Xml)
        {
            throw new InvalidOperationException($"The {element.Name.LocalName} element is not in this document.");
        }

        var parts = new List<string>();
        for (var e = element; e.Parent is not null; e = e.Parent)
        {
            parts.Add($"{e.Name}[{e.ElementsBeforeSelf(e.Name).Count()}]");
        }

        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>The element at the path, or null if there is none.</summary>
    public static XElement? Resolve(WttDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(path);
        var element = document.Root;
        if (path.Length == 0)
        {
            return element;
        }

        foreach (var part in path.Split('/'))
        {
            var open = part.LastIndexOf('[');
            if (open <= 0 || !part.EndsWith(']') || !int.TryParse(part.AsSpan(open + 1, part.Length - open - 2), out var index) || index < 0)
            {
                return null;
            }

            element = element.Elements(XName.Get(part[..open])).ElementAtOrDefault(index);
            if (element is null)
            {
                return null;
            }
        }

        return element;
    }
}

/// <summary>A saved record applied step by step, each step's elements found in the tree as the steps before it left it.</summary>
internal sealed class RecordedEdit(EditRecord record) : TimetableEdit(record.Description)
{
    /// <summary>Not known until applied: the edit's undo has them.</summary>
    public override IEnumerable<XElement> Targets => [];

    private protected override TimetableEdit ApplyCore(WttDocument document) =>
        ApplyRecording(document, [], []);

    internal override EditStep ToStep(WttDocument document) => throw new InvalidOperationException("A recorded edit is recorded step by step.");

    internal override TimetableEdit ApplyRecording(WttDocument document, List<EditStep> forward, List<EditStep> backward)
    {
        var (forwardCount, backwardCount) = (forward.Count, backward.Count);
        var undos = new List<TimetableEdit>(record.Steps.Count);
        try
        {
            foreach (var step in record.Steps)
            {
                undos.Add(step.Resolve(document, Description).ApplyRecording(document, forward, backward));
            }
        }
        catch
        {
            for (var i = undos.Count - 1; i >= 0; i--)
            {
                undos[i].Apply(document);
            }

            forward.RemoveRange(forwardCount, forward.Count - forwardCount);
            backward.RemoveRange(backwardCount, backward.Count - backwardCount);
            throw;
        }

        undos.Reverse();
        return new CompositeEdit(Description, undos);
    }
}
