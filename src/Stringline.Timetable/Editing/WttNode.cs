using System.Globalization;
using System.Xml.Linq;
using Stringline.Core;

namespace Stringline.Timetable.Editing;

/// <summary>
/// A typed view of one element of a <see cref="WttDocument"/>. Properties read from and write through to the element's
/// children, in the file's conventions: yes/no in the file's own style, a value back at its default removed where SimSig
/// leaves defaults out, and a new child in SimSig's order. Children the view does not know are never touched.
/// </summary>
/// <remarks>
/// Writing a property changes the document directly, with no undo. To change a document that is being edited, make a
/// <see cref="TimetableEdit"/> with <see cref="TimetableEdit.Change{T}(T, Action{T}, string?)"/>, which runs the same
/// setters on a copy and turns what changed into an edit.
/// </remarks>
public abstract class WttNode
{
    protected WttNode(WttDocument document, XElement element)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(element);
        Document = document;
        Element = element;
    }

    public WttDocument Document { get; }

    /// <summary>The element itself: the node's identity, kept through every edit of its fields.</summary>
    public XElement Element { get; }

    private string Kind => Element.Name.LocalName;

    /// <summary>The same kind of view over another element (a copy, for <see cref="TimetableEdit.Change{T}(T, Action{T}, string?)"/>).</summary>
    internal abstract WttNode Wrap(XElement element);

    /// <summary>A child's text, trimmed; null if the child is missing or empty.</summary>
    public string? GetText(string field)
    {
        var value = Element.Element(field)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Sets a child's text as given; null removes the child.</summary>
    public void SetText(string field, string? value) => FieldWriter.Set(Element, field, string.IsNullOrEmpty(value) ? null : value);

    public int? GetInt(string field) =>
        int.TryParse(GetText(field), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    public void SetInt(string field, int? value) => FieldWriter.Set(Element, field, FormatInt(field, value));

    /// <summary><c>True</c>, <c>-1</c> and <c>1</c> are yes; anything else, or no child, is no.</summary>
    public bool GetBool(string field) => GetText(field)?.ToUpperInvariant() is "TRUE" or "-1" or "1";

    public void SetBool(string field, bool value) => FieldWriter.Set(Element, field, FormatBool(value));

    /// <summary>Times are whole seconds after midnight of day 0; SimSig treats 0 as no time.</summary>
    public SimTime? GetTime(string field) => GetInt(field) is { } seconds ? new SimTime(seconds) : null;

    public void SetTime(string field, SimTime? value) => SetInt(field, value?.Seconds);

    /// <summary>An attribute's value (a category's or the root's <c>ID</c>), trimmed; null if missing or empty.</summary>
    public string? GetAttribute(string name)
    {
        var value = ((string?)Element.Attribute(name))?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void SetAttribute(string name, string? value) => Element.SetAttributeValue(name, string.IsNullOrEmpty(value) ? null : value);

    /// <summary>Allowances are written in half minutes; the view gives seconds.</summary>
    protected int GetAllowance(string field) =>
        double.TryParse(GetText(field), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var units) && units is >= 0 and < 10_000
            ? (int)Math.Round(units * 30, MidpointRounding.AwayFromZero)
            : 0;

    protected void SetAllowance(string field, int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        FieldWriter.Set(Element, field, seconds == 0 && WttSchema.LeavesOutDefaults(Kind) ? null : (seconds / 30m).ToString("0.##", CultureInfo.InvariantCulture));
    }

    private string? FormatInt(string field, int? value) =>
        value switch
        {
            null => null,
            0 when WttSchema.LeavesOutDefaults(Kind) && !WttSchema.KeepsZero(Kind, field) => null,
            _ => value.Value.ToString(CultureInfo.InvariantCulture),
        };

    private string? FormatBool(bool value) =>
        !value && WttSchema.LeavesOutDefaults(Kind) ? null
        : Document.BooleanStyle == WttBooleanStyle.TrueFalse ? (value ? "True" : "False")
        : value ? "-1" : "0";
}

/// <summary>Any element, read and written by field name: the items of the lists the editor shows as tables.</summary>
public sealed class ElementNode(WttDocument document, XElement element) : WttNode(document, element)
{
    internal override WttNode Wrap(XElement element) => new ElementNode(Document, element);
}

/// <summary>
/// A list the editor shows as a table without modelling it: <c>Decisions</c>, <c>TTClassOfServiceRules</c>,
/// <c>CautionSpeedSets</c>. Items are edited by field name; nested lists (a decision's choices) are reached through
/// <see cref="ListNode.Items"/>' elements.
/// </summary>
public sealed class ListNode(WttDocument document, XElement element) : WttNode(document, element)
{
    public IEnumerable<ElementNode> Items => Element.Elements().Select(e => new ElementNode(Document, e));

    internal override WttNode Wrap(XElement element) => new ListNode(Document, element);
}

/// <summary>Writes children in SimSig's order.</summary>
internal static class FieldWriter
{
    /// <summary>Sets the first child of this name to <paramref name="text"/>, creating it in SimSig's order; null removes it.</summary>
    public static void Set(XElement owner, string field, string? text)
    {
        var child = owner.Element(field);
        if (text is null)
        {
            child?.Remove();
        }
        else if (child is null)
        {
            InsertInOrder(owner, new XElement(field, text));
        }
        else if (child.HasElements)
        {
            throw new InvalidOperationException($"{owner.Name.LocalName}/{field} holds other elements, so it has no text to set.");
        }
        else
        {
            child.Value = text;
        }
    }

    /// <summary>
    /// Adds a child where SimSig puts it: after the last sibling that comes before or with it in SimSig's order (so a
    /// new trip goes after the last trip), else before the first that comes after it; a child SimSig's order does not
    /// list goes last.
    /// </summary>
    public static void InsertInOrder(XElement parent, XElement child)
    {
        var parentName = parent.Name.LocalName;
        var rank = WttSchema.OrderOf(parentName, child.Name.LocalName);
        if (rank < 0)
        {
            parent.Add(child);
            return;
        }

        XElement? after = null;
        XElement? before = null;
        foreach (var sibling in parent.Elements())
        {
            var siblingRank = WttSchema.OrderOf(parentName, sibling.Name.LocalName);
            if (siblingRank < 0)
            {
                continue;
            }

            if (siblingRank <= rank)
            {
                after = sibling;
            }
            else
            {
                before ??= sibling;
            }
        }

        if (after is not null)
        {
            after.AddAfterSelf(child);
        }
        else if (before is not null)
        {
            before.AddBeforeSelf(child);
        }
        else
        {
            parent.Add(child);
        }
    }
}
