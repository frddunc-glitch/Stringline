using System.Collections.Immutable;
using Stringline.Core.Events;

namespace Stringline.Core.Live;

/// <summary>
/// The signalling names seen from the sim (S19): every route, signal and TD berth ID, and what each berth holds now.
/// The assist dialogs offer them, since the WTT names none of them. Filled from <c>SG_MSG</c> (SimSig's snapshot names
/// every object at connect) and from TD steps, so it stays empty without the TD topic. Immutable: <see cref="With"/>
/// returns this same instance when an event adds nothing, so a reader can tell cheaply whether it changed.
/// </summary>
public sealed class SignallingIndex
{
    private SignallingIndex(ImmutableSortedSet<string> routes, ImmutableSortedSet<string> signals, ImmutableSortedSet<string> berths, ImmutableDictionary<string, string> descriptions)
    {
        Routes = routes;
        Signals = signals;
        Berths = berths;
        Descriptions = descriptions;
    }

    public static SignallingIndex Empty { get; } = new(
        ImmutableSortedSet.Create<string>(StringComparer.Ordinal),
        ImmutableSortedSet.Create<string>(StringComparer.Ordinal),
        ImmutableSortedSet.Create<string>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal));

    /// <summary>Route IDs (<c>R…</c>), from the route objects' <c>SG_MSG</c>s.</summary>
    public ImmutableSortedSet<string> Routes { get; }

    /// <summary>Signal IDs (<c>S…</c>), from the signal objects' <c>SG_MSG</c>s.</summary>
    public ImmutableSortedSet<string> Signals { get; }

    /// <summary>Every berth a TD step has named, empty or not.</summary>
    public ImmutableSortedSet<string> Berths { get; }

    /// <summary>Berth → the description in it now. Empty berths are left out.</summary>
    public ImmutableDictionary<string, string> Descriptions { get; }

    public bool IsEmpty => Routes.IsEmpty && Signals.IsEmpty && Berths.IsEmpty;

    /// <summary>The berths holding this description now (usually none or one).</summary>
    public IEnumerable<string> BerthsOf(string description) =>
        Descriptions.Where(d => string.Equals(d.Value, description, StringComparison.OrdinalIgnoreCase)).Select(d => d.Key).Order(StringComparer.Ordinal);

    /// <summary>The index with what <paramref name="e"/> names; this same instance if it changes nothing.</summary>
    public SignallingIndex With(GatewayEvent e) => e switch
    {
        SignallingStateEvent s when string.IsNullOrWhiteSpace(s.ObjectId) => this,
        SignallingStateEvent { ObjectType: SignallingObjectType.Route } s => WithSets(Routes.Add(s.ObjectId.Trim()), Signals),
        SignallingStateEvent { ObjectType: SignallingObjectType.Signal } s => WithSets(Routes, Signals.Add(s.ObjectId.Trim())),
        TdStepEvent step => WithStep(step),
        _ => this,
    };

    private SignallingIndex WithSets(ImmutableSortedSet<string> routes, ImmutableSortedSet<string> signals) =>
        ReferenceEquals(routes, Routes) && ReferenceEquals(signals, Signals) ? this
        : new SignallingIndex(routes, signals, Berths, Descriptions);

    private SignallingIndex WithStep(TdStepEvent step)
    {
        var berths = Berths;
        var descriptions = Descriptions;
        var description = step.Description.Trim();
        switch (step.Kind)
        {
            case TdStepKind.Step:
                Clear(step.From);
                Put(step.To, description);
                break;
            case TdStepKind.Cancel:
                Clear(step.From);
                break;
            case TdStepKind.Interpose:
                Put(step.To, description);
                break;
            default:
                return this;
        }

        return ReferenceEquals(berths, Berths) && ReferenceEquals(descriptions, Descriptions) ? this : new SignallingIndex(Routes, Signals, berths, descriptions);

        void Clear(string berth)
        {
            berth = berth.Trim();
            if (berth.Length > 0)
            {
                berths = berths.Add(berth);
                descriptions = descriptions.Remove(berth);
            }
        }

        void Put(string berth, string text)
        {
            berth = berth.Trim();
            if (berth.Length == 0)
            {
                return;
            }

            berths = berths.Add(berth);
            descriptions = text.Length == 0 ? descriptions.Remove(berth) : descriptions.SetItem(berth, text);
        }
    }
}
