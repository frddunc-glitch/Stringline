using System.Xml.Linq;

namespace Stringline.Timetable.Editing;

/// <summary>
/// One immutable change to a <see cref="WttDocument"/> (<c>docs/08-…md</c> §3.2). <see cref="Apply"/> makes the change
/// and returns the edit that undoes it, which restores the exact tree: the same elements, in the same places, with the
/// same text. Undoing that gives the redo, and so on.
/// </summary>
/// <remarks>
/// Edits hold the elements they change, so they belong to one document. An edit that does not fit the document as it
/// is (an element no longer in it) throws <see cref="InvalidOperationException"/> and changes nothing.
/// </remarks>
public abstract class TimetableEdit
{
    private protected TimetableEdit(string description)
    {
        Description = description;
    }

    /// <summary>What the edit does, for undo and redo menus ("Renumber 2P05 as 2P07").</summary>
    public string Description { get; }

    /// <summary>Makes the change; returns its undo.</summary>
    public TimetableEdit Apply(WttDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return ApplyCore(document);
    }

    private protected abstract TimetableEdit ApplyCore(WttDocument document);

    /// <summary>
    /// The elements the edit changes, adds to or takes from (a removed node's parent too), so incremental checks can
    /// find the trains it touches.
    /// </summary>
    public abstract IEnumerable<XElement> Targets { get; }

    /// <summary>Sets the first child named <paramref name="field"/> to <paramref name="text"/> (made in SimSig's order if missing); null removes it.</summary>
    public static TimetableEdit SetField(XElement owner, string field, string? text, string? description = null) =>
        new SetFieldEdit(owner, field, text, description ?? $"Set {owner.Name.LocalName} {field}");

    /// <summary>Sets an attribute (a category's <c>ID</c>); null removes it.</summary>
    public static TimetableEdit SetAttribute(XElement element, string name, string? value, string? description = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        var attributes = element.Attributes().Select(a => (a.Name, a.Value)).ToList();
        var at = attributes.FindIndex(a => a.Name == name);
        if (value is null && at >= 0)
        {
            attributes.RemoveAt(at);
        }
        else if (value is not null)
        {
            if (at >= 0)
            {
                attributes[at] = (name, value);
            }
            else
            {
                attributes.Add((name, value));
            }
        }

        return new AttributesEdit(element, attributes, description ?? $"Set {element.Name.LocalName} {name}");
    }

    /// <summary>
    /// Adds <paramref name="node"/> (an element with no parent) to <paramref name="parent"/>: at <paramref name="index"/>
    /// among its child elements, or, if null, where SimSig puts it (after the last of its kind).
    /// </summary>
    public static TimetableEdit Insert(XElement parent, XElement node, int? index = null, string? description = null) =>
        new InsertNodeEdit(parent, node, index, description ?? $"Add {node.Name.LocalName}");

    public static TimetableEdit Remove(XElement node, string? description = null) =>
        new RemoveNodeEdit(node, description ?? $"Remove {node.Name.LocalName}");

    /// <summary>Moves a node among its siblings, to <paramref name="index"/> among its parent's child elements once moved.</summary>
    public static TimetableEdit Move(XElement node, int index, string? description = null) =>
        new MoveNodeEdit(node, index, description ?? $"Move {node.Name.LocalName}");

    /// <summary>Several edits as one, applied in order and undone in reverse. If one fails, those already applied are undone.</summary>
    public static TimetableEdit Composite(string description, IEnumerable<TimetableEdit> edits) => new CompositeEdit(description, [.. edits]);

    /// <summary>
    /// Changes a node's fields with its typed setters: <paramref name="change"/> runs on a copy of the element, and every
    /// field it changed, added or removed becomes one edit of the original, so the element and its children keep their
    /// identity. Adding or removing trips, activities and other repeated items needs <see cref="Insert"/> and
    /// <see cref="Remove"/> instead; a change that does so throws.
    /// </summary>
    public static TimetableEdit Change<T>(T node, Action<T> change, string? description = null)
        where T : WttNode
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(change);
        var copy = new XElement(node.Element);
        change((T)node.Wrap(copy));
        var edits = new List<TimetableEdit>();
        Diff(node.Element, copy, edits);
        return Composite(description ?? $"Change {node.Element.Name.LocalName}", edits);
    }

    /// <summary>
    /// Renumbers a train, keeping its links: the activities and rules that name it (as <see cref="WttReader"/> resolves
    /// them: by UID first, else the nearest train with the headcode) are changed with it, by headcode or by UID, whichever
    /// they use. References to other trains with the same headcode are left alone.
    /// </summary>
    /// <param name="uid">The new UID, or null to keep the train's.</param>
    public static TimetableEdit Renumber(WttDocument document, TrainNode train, string headcode, string? uid = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(train);
        if (!Core.Headcode.TryCreate(headcode, out var newHeadcode))
        {
            throw new ArgumentException($"'{headcode}' is not a headcode.", nameof(headcode));
        }

        var oldHeadcode = train.Headcode;
        var oldUid = train.Uid;
        var newUid = uid?.Trim();
        var edits = new List<TimetableEdit>
        {
            Change(train, t =>
            {
                t.Headcode = newHeadcode.Value;
                if (newUid is not null)
                {
                    t.Uid = newUid;
                }
            }),
        };

        var import = document.Import();
        if (import.TrainOf(train) is { } target)
        {
            foreach (var other in import.Timetable.Trains)
            {
                foreach (var point in other.Points)
                {
                    for (var a = 0; a < point.Activities.Count; a++)
                    {
                        if (point.Activities[a].Train == target.Id)
                        {
                            var activity = import.ActivityOf(other, point.Index, a);
                            AddRenamed(activity, "AssociatedTrain");
                            AddRenamed(activity, "AssociatedUID");
                        }
                    }
                }
            }

            for (var r = 0; r < import.Timetable.Rules.Count; r++)
            {
                var rule = import.Timetable.Rules[r];
                if (rule.Train == target.Id)
                {
                    AddRenamed(import.RuleOf(r), "Train");
                    AddRenamed(import.RuleOf(r), "TrainUID");
                }

                if (rule.Train2 == target.Id)
                {
                    AddRenamed(import.RuleOf(r), "Train2");
                    AddRenamed(import.RuleOf(r), "Train2UID");
                }
            }
        }

        return Composite($"Renumber {oldHeadcode} as {newHeadcode}", edits);

        // A UID field sometimes holds a headcode, so each field follows whichever of the train's names it holds.
        void AddRenamed(WttNode node, string field)
        {
            var value = node.GetText(field);
            if (value is null)
            {
                return;
            }

            if (newUid is not null && oldUid is not null && string.Equals(value, oldUid, StringComparison.OrdinalIgnoreCase))
            {
                edits.Add(SetField(node.Element, field, newUid));
            }
            else if (string.Equals(value, oldHeadcode, StringComparison.Ordinal))
            {
                edits.Add(SetField(node.Element, field, newHeadcode.Value));
            }
        }
    }

    /// <summary>Edits that make <paramref name="original"/> like <paramref name="changed"/>: removals, then changes, then additions.</summary>
    private static void Diff(XElement original, XElement changed, List<TimetableEdit> edits)
    {
        if (!original.Attributes().Select(a => (a.Name, a.Value)).SequenceEqual(changed.Attributes().Select(a => (a.Name, a.Value))))
        {
            edits.Add(new AttributesEdit(original, changed.Attributes().Select(a => (a.Name, a.Value)).ToList(), "Set attributes"));
        }

        var removals = new List<TimetableEdit>();
        var changes = new List<TimetableEdit>();
        var additions = new List<TimetableEdit>();
        var names = original.Elements().Concat(changed.Elements()).Select(e => e.Name).Distinct();
        foreach (var name in names)
        {
            var before = original.Elements(name).ToList();
            var after = changed.Elements(name).ToList();
            if (before.Count != after.Count && (before.Count > 1 || after.Count > 1))
            {
                throw new InvalidOperationException(
                    $"The change adds or removes {name.LocalName} items under {original.Name.LocalName}; use Insert or Remove for those.");
            }

            if (after.Count == 0 && before.Count == 1)
            {
                removals.Add(new RemoveNodeEdit(before[0], $"Remove {name.LocalName}"));
            }
            else if (before.Count == 0 && after.Count == 1)
            {
                additions.Add(new InsertNodeEdit(original, new XElement(after[0]), null, $"Add {name.LocalName}"));
            }
            else
            {
                for (var i = 0; i < before.Count; i++)
                {
                    Compare(before[i], after[i], changes);
                }
            }
        }

        edits.AddRange(removals);
        edits.AddRange(changes);
        edits.AddRange(additions);
    }

    private static void Compare(XElement original, XElement changed, List<TimetableEdit> edits)
    {
        if (original.HasElements || changed.HasElements)
        {
            Diff(original, changed, edits);
        }
        else if (original.IsEmpty != changed.IsEmpty || original.Value != changed.Value)
        {
            edits.Add(new SetLeafEdit(original, changed.IsEmpty ? null : changed.Value, $"Set {original.Name.LocalName}"));
        }
        else if (!original.Attributes().Select(a => (a.Name, a.Value)).SequenceEqual(changed.Attributes().Select(a => (a.Name, a.Value))))
        {
            edits.Add(new AttributesEdit(original, changed.Attributes().Select(a => (a.Name, a.Value)).ToList(), "Set attributes"));
        }
    }

    private protected static void CheckIn(WttDocument document, XElement element)
    {
        if (element.Document != document.Xml)
        {
            throw new InvalidOperationException($"The {element.Name.LocalName} element is not in this document.");
        }
    }

    private protected static int IndexAmongElements(XElement node) => node.ElementsBeforeSelf().Count();

    private protected static void InsertAt(XElement parent, XElement node, int index)
    {
        var at = parent.Elements().ElementAtOrDefault(index);
        if (at is null)
        {
            parent.Add(node);
        }
        else
        {
            at.AddBeforeSelf(node);
        }
    }
}

/// <summary>Sets, makes or removes a field (<see cref="TimetableEdit.SetField"/>).</summary>
public sealed class SetFieldEdit : TimetableEdit
{
    internal SetFieldEdit(XElement owner, string field, string? text, string description)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrEmpty(field);
        Owner = owner;
        Field = field;
        Text = text;
    }

    public XElement Owner { get; }

    public string Field { get; }

    public string? Text { get; }

    public override IEnumerable<XElement> Targets => [Owner];

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        CheckIn(document, Owner);
        var child = Owner.Element(Field);
        if (child is null)
        {
            if (Text is null)
            {
                return this;
            }

            FieldWriter.Set(Owner, Field, Text);
            return new RemoveNodeEdit(Owner.Element(Field)!, Description);
        }

        if (Text is null)
        {
            var index = IndexAmongElements(child);
            child.Remove();
            return new InsertNodeEdit(Owner, child, index, Description);
        }

        return new SetLeafEdit(child, Text, Description).Apply(document);
    }
}

/// <summary>Sets a leaf element's text; null makes it self-closing (<c>&lt;DwellTimes/&gt;</c>), as some files have it.</summary>
internal sealed class SetLeafEdit(XElement leaf, string? text, string description) : TimetableEdit(description)
{
    public override IEnumerable<XElement> Targets => [leaf];

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        CheckIn(document, leaf);
        if (leaf.HasElements)
        {
            throw new InvalidOperationException($"{leaf.Name.LocalName} holds other elements, so it has no text to set.");
        }

        var undo = new SetLeafEdit(leaf, leaf.IsEmpty ? null : leaf.Value, Description);
        if (text is null)
        {
            leaf.RemoveNodes();
        }
        else
        {
            leaf.Value = text;
        }

        return undo;
    }
}

/// <summary>Replaces an element's attributes, in order.</summary>
internal sealed class AttributesEdit(XElement element, IReadOnlyList<(XName Name, string Value)> attributes, string description) : TimetableEdit(description)
{
    public override IEnumerable<XElement> Targets => [element];

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        CheckIn(document, element);
        var undo = new AttributesEdit(element, element.Attributes().Select(a => (a.Name, a.Value)).ToList(), Description);
        element.ReplaceAttributes(attributes.Select(a => new XAttribute(a.Name, a.Value)));
        return undo;
    }
}

public sealed class InsertNodeEdit : TimetableEdit
{
    internal InsertNodeEdit(XElement parent, XElement node, int? index, string description)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(node);
        Parent = parent;
        Node = node;
        Index = index;
    }

    public XElement Parent { get; }

    public XElement Node { get; }

    /// <summary>The place among the parent's child elements, or null for SimSig's order.</summary>
    public int? Index { get; }

    public override IEnumerable<XElement> Targets => [Parent, Node];

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        CheckIn(document, Parent);
        if (Node.Parent is not null || Node.Document is not null)
        {
            throw new InvalidOperationException($"The {Node.Name.LocalName} element to add is already in a document.");
        }

        if (Index is { } index)
        {
            InsertAt(Parent, Node, index);
        }
        else
        {
            FieldWriter.InsertInOrder(Parent, Node);
        }

        return new RemoveNodeEdit(Node, Description);
    }
}

public sealed class RemoveNodeEdit : TimetableEdit
{
    private readonly XElement? _parent;

    internal RemoveNodeEdit(XElement node, string description)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(node);
        Node = node;
        _parent = node.Parent;
    }

    public XElement Node { get; }

    public override IEnumerable<XElement> Targets => _parent is null ? [Node] : [_parent, Node];

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        CheckIn(document, Node);
        var parent = Node.Parent ?? throw new InvalidOperationException("The root element cannot be removed.");
        var index = IndexAmongElements(Node);
        Node.Remove();
        return new InsertNodeEdit(parent, Node, index, Description);
    }
}

public sealed class MoveNodeEdit : TimetableEdit
{
    internal MoveNodeEdit(XElement node, int index, string description)
        : base(description)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        Node = node;
        Index = index;
    }

    public XElement Node { get; }

    public int Index { get; }

    public override IEnumerable<XElement> Targets => [Node];

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        CheckIn(document, Node);
        var parent = Node.Parent ?? throw new InvalidOperationException("The root element cannot be moved.");
        var from = IndexAmongElements(Node);
        Node.Remove();
        InsertAt(parent, Node, Index);
        return new MoveNodeEdit(Node, from, Description);
    }
}

public sealed class CompositeEdit : TimetableEdit
{
    internal CompositeEdit(string description, IReadOnlyList<TimetableEdit> edits)
        : base(description)
    {
        Edits = edits;
    }

    public IReadOnlyList<TimetableEdit> Edits { get; }

    public override IEnumerable<XElement> Targets => Edits.SelectMany(e => e.Targets);

    private protected override TimetableEdit ApplyCore(WttDocument document)
    {
        var undos = new List<TimetableEdit>(Edits.Count);
        try
        {
            foreach (var edit in Edits)
            {
                undos.Add(edit.Apply(document));
            }
        }
        catch
        {
            for (var i = undos.Count - 1; i >= 0; i--)
            {
                undos[i].Apply(document);
            }

            throw;
        }

        undos.Reverse();
        return new CompositeEdit(Description, undos);
    }
}
