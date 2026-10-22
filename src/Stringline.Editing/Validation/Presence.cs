namespace Stringline.Editing.Validation;

/// <summary>
/// Some choices of a decision: those named, or (<see cref="Not"/>) all but those named, as SimSig writes them:
/// <c>WED</c>, <c>[any] MON | TUE | WED</c>, <c>[not] MON | TUE</c>.
/// </summary>
internal sealed record ChoiceSet(IReadOnlySet<string> Names, bool Not)
{
    /// <summary>The choices written, or null for none.</summary>
    public static ChoiceSet? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var body = text.Trim();
        var not = false;
        if (body.StartsWith('[') && body.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            not = body[1..close].Trim().Equals("not", StringComparison.OrdinalIgnoreCase);
            body = body[(close + 1)..];
        }

        var names = body.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        return names.Count == 0 ? null : new ChoiceSet(names, not);
    }

    /// <summary>Whether some choice is in both (two "all but" sets always share one: the choices are not all known).</summary>
    public bool Overlaps(ChoiceSet other) => (Not, other.Not) switch
    {
        (false, false) => Names.Overlaps(other.Names),
        (false, true) => Names.Except(other.Names).Any(),
        (true, false) => other.Names.Except(Names).Any(),
        _ => true,
    };

    /// <summary>The choices in both.</summary>
    public ChoiceSet And(ChoiceSet other) => (Not, other.Not) switch
    {
        (false, false) => new(Names.Intersect(other.Names).ToHashSet(StringComparer.Ordinal), false),
        (false, true) => new(Names.Except(other.Names).ToHashSet(StringComparer.Ordinal), false),
        (true, false) => new(other.Names.Except(Names).ToHashSet(StringComparer.Ordinal), false),
        _ => new(Names.Union(other.Names).ToHashSet(StringComparer.Ordinal), true),
    };

    /// <summary>The choices in either.</summary>
    public ChoiceSet Or(ChoiceSet other) => (Not, other.Not) switch
    {
        (false, false) => new(Names.Union(other.Names).ToHashSet(StringComparer.Ordinal), false),
        (false, true) => new(other.Names.Except(Names).ToHashSet(StringComparer.Ordinal), true),
        (true, false) => new(Names.Except(other.Names).ToHashSet(StringComparer.Ordinal), true),
        _ => new(Names.Intersect(other.Names).ToHashSet(StringComparer.Ordinal), true),
    };
}

/// <summary>
/// When a train (or an activity) can be in a game: the starts it can be in (the timetable's start, <c>""</c>, or a seed
/// group's ID, upper case; null for any) and the decision choices it needs (e.g. <c>DOTW</c> ∈ {MON, TUE}). Released
/// timetables write one working several times, once per seed group or day, so two trains that can never be in one
/// game do not clash (S42's corpus baseline). Rules are decisions too: "X not if Y" needs Y not to run, and a rule
/// that holds X for Y needs Y to run.
/// </summary>
internal sealed record Presence(IReadOnlySet<string>? Starts, IReadOnlyDictionary<string, ChoiceSet> Choices)
{
    public static Presence Always { get; } = new(null, new Dictionary<string, ChoiceSet>());

    /// <summary>Whether both can be in one game.</summary>
    public bool Overlaps(Presence other)
    {
        if (Starts is not null && other.Starts is not null && !Starts.Overlaps(other.Starts))
        {
            return false;
        }

        foreach (var (decision, choices) in Choices)
        {
            if (other.Choices.TryGetValue(decision, out var others) && !choices.Overlaps(others))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Also needing a choice of a decision (as written).</summary>
    public Presence With(string? decision, string? choice) =>
        string.IsNullOrWhiteSpace(decision) || ChoiceSet.Parse(choice) is not { } wanted ? this : With(decision.Trim().ToUpperInvariant(), wanted);

    public Presence With(string decision, ChoiceSet wanted)
    {
        var choices = new Dictionary<string, ChoiceSet>(Choices);
        choices[decision] = choices.TryGetValue(decision, out var already) ? already.And(wanted) : wanted;
        return this with { Choices = choices };
    }

    /// <summary>Either one: the starts of both, and only the decisions both need (with the choices of either).</summary>
    public Presence Or(Presence other)
    {
        IReadOnlySet<string>? starts = Starts is null || other.Starts is null ? null : Starts.Union(other.Starts).ToHashSet(StringComparer.Ordinal);
        var choices = new Dictionary<string, ChoiceSet>();
        foreach (var (decision, mine) in Choices)
        {
            if (other.Choices.TryGetValue(decision, out var theirs))
            {
                choices[decision] = mine.Or(theirs);
            }
        }

        return new Presence(starts, choices);
    }
}
