using System.Globalization;
using System.Text.Json.Serialization;
using Stringline.Core;

namespace Stringline.Planning;

/// <summary>
/// One re-plan step (<c>06-…md</c> §1). Commands are plain records, serialisable with <see cref="PlanCommandJson"/>
/// for the session store, and deterministic: the same command on the same plan always gives the same result.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ShiftTrain), "shiftTrain")]
[JsonDerivedType(typeof(SetDwell), "setDwell")]
[JsonDerivedType(typeof(ChangePlatform), "changePlatform")]
[JsonDerivedType(typeof(ChangeLine), "changeLine")]
[JsonDerivedType(typeof(SkipStop), "skipStop")]
[JsonDerivedType(typeof(AddStop), "addStop")]
[JsonDerivedType(typeof(TerminateAt), "terminateAt")]
[JsonDerivedType(typeof(CancelTrain), "cancelTrain")]
[JsonDerivedType(typeof(ReinstateTrain), "reinstateTrain")]
[JsonDerivedType(typeof(CloneTrain), "cloneTrain")]
[JsonDerivedType(typeof(AddDisruption), "addDisruption")]
[JsonDerivedType(typeof(RemoveDisruption), "removeDisruption")]
[JsonDerivedType(typeof(EditDisruption), "editDisruption")]
[JsonDerivedType(typeof(LinkLiveTrain), "linkLiveTrain")]
[JsonDerivedType(typeof(UnlinkLiveTrain), "unlinkLiveTrain")]
[JsonDerivedType(typeof(AcceptSuggestion), "acceptSuggestion")]
public interface IPlanCommand
{
    /// <summary>Applies the command. An error result must leave nothing changed (the builder rolls back for it).</summary>
    PlanCommandResult Apply(WorkingPlanBuilder b);

    /// <summary>What the command does, in words for the edit history, e.g. "Shift 1A23 by +5 min from VAUX".</summary>
    string Describe(WorkingPlan plan);
}

public enum PlanWarningKind
{
    /// <summary>A run was made longer to keep to the minimum run time.</summary>
    RunTimeClamped,

    /// <summary>A departure was moved to the arrival.</summary>
    DwellClamped,
}

/// <summary>A change the plan made to a command's request to keep it realistic.</summary>
/// <param name="Index">The point that was moved.</param>
/// <param name="Seconds">How far it was moved later.</param>
public sealed record PlanWarning(PlanWarningKind Kind, TrainId Train, int Index, Tiploc Location, int Seconds, string Message);

/// <summary>The outcome of a command: applied (perhaps with warnings), or refused with an error and nothing changed.</summary>
public sealed record PlanCommandResult(bool Applied, string? Error, IReadOnlyList<PlanWarning> Warnings)
{
    public static PlanCommandResult Ok { get; } = new(true, null, []);

    public static PlanCommandResult Fail(string error) => new(false, error, []);

    /// <summary><see cref="Ok"/> if <paramref name="error"/> is null, else a failure with it.</summary>
    public static PlanCommandResult From(string? error) => error is null ? Ok : Fail(error);
}

/// <summary>Moves a train's points from <see cref="FromIndex"/> to the end by <see cref="DeltaSeconds"/> (negative = earlier).</summary>
public sealed record ShiftTrain(TrainId Train, int FromIndex, int DeltaSeconds) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, FromIndex, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        if (DeltaSeconds == 0)
        {
            return PlanCommandResult.Fail("A shift of zero makes no change to the plan.");
        }

        var points = plan.Points.Select(p => p.Index >= FromIndex ? p.Shift(DeltaSeconds) : p).ToArray();
        return PlanCommandResult.From(b.Update(plan with { Points = points }));
    }

    public string Describe(WorkingPlan plan) =>
        $"Shift {Checks.Name(plan, Train)} by {PlanText.WithSign(DeltaSeconds)} from {Checks.Where(plan, Train, FromIndex)}";
}

/// <summary>Sets the departure from a stop (the dwell). Later points move only if they must, to keep minimum run times.</summary>
public sealed record SetDwell(TrainId Train, int Index, SimTime Depart) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, Index, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        var point = plan.Points[Index];
        if (point.IsPass)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} passes {point.Location}; add a stop there first.");
        }

        if (point.Depart is null || Index >= plan.LastIndex)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} does not leave {point.Location}.");
        }

        return PlanCommandResult.From(b.Update(Checks.With(plan, point with { Depart = Depart })));
    }

    public string Describe(WorkingPlan plan) => $"{Checks.Name(plan, Train)} departs {Checks.Where(plan, Train, Index)} at {Depart}";
}

/// <summary>Plans a train into a different platform at one point (null or empty for none).</summary>
public sealed record ChangePlatform(TrainId Train, int Index, string? Platform) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, Index, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        var platform = string.IsNullOrWhiteSpace(Platform) ? null : Platform.Trim();
        return PlanCommandResult.From(b.Update(Checks.With(plan, plan.Points[Index] with { Platform = platform })));
    }

    public string Describe(WorkingPlan plan) =>
        $"{Checks.Name(plan, Train)} to platform {(string.IsNullOrWhiteSpace(Platform) ? "(none)" : Platform.Trim())} at {Checks.Where(plan, Train, Index)}";
}

/// <summary>Plans a train out of one point on a different line (null or empty for none).</summary>
public sealed record ChangeLine(TrainId Train, int Index, string? Line) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, Index, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        var line = string.IsNullOrWhiteSpace(Line) ? null : Line.Trim();
        return PlanCommandResult.From(b.Update(Checks.With(plan, plan.Points[Index] with { Line = line })));
    }

    public string Describe(WorkingPlan plan) =>
        $"{Checks.Name(plan, Train)} out of {Checks.Where(plan, Train, Index)} on line {(string.IsNullOrWhiteSpace(Line) ? "(none)" : Line.Trim())}";
}

/// <summary>Makes an intermediate stop a pass at its arrival time. Later points keep their times (the train gains the dwell).</summary>
public sealed record SkipStop(TrainId Train, int Index) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, Index, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        var point = plan.Points[Index];
        if (Index == 0 || Index >= plan.LastIndex)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} starts or ends at {point.Location}; only an intermediate stop can be skipped.");
        }

        if (!point.IsCall)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} does not stop at {point.Location}.");
        }

        return PlanCommandResult.From(b.Update(Checks.With(plan, point with { Arrive = null, Depart = point.Arrive, IsPass = true })));
    }

    public string Describe(WorkingPlan plan) => $"{Checks.Name(plan, Train)} runs through {Checks.Where(plan, Train, Index)} without stopping";
}

/// <summary>Makes a pass a stop: it arrives at the pass time and leaves <see cref="DwellSeconds"/> later.</summary>
public sealed record AddStop(TrainId Train, int Index, int DwellSeconds) : IPlanCommand
{
    /// <summary>The longest dwell a stop can be given in one command.</summary>
    public const int MaxDwellSeconds = 6 * SimTime.SecondsPerHour;

    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, Index, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        var point = plan.Points[Index];
        if (!point.IsPass || point.Depart is not { } pass || Index >= plan.LastIndex)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} already stops at {point.Location}.");
        }

        if (DwellSeconds is <= 0 or > MaxDwellSeconds)
        {
            return PlanCommandResult.Fail($"A dwell must be between 1 s and {PlanText.Duration(MaxDwellSeconds)}.");
        }

        return PlanCommandResult.From(b.Update(Checks.With(plan, point with { Arrive = pass, Depart = pass + DwellSeconds, IsPass = false })));
    }

    public string Describe(WorkingPlan plan) =>
        $"{Checks.Name(plan, Train)} stops at {Checks.Where(plan, Train, Index)} for {PlanText.Duration(DwellSeconds)}";
}

/// <summary>Terminates a train short at an intermediate point; it no longer runs to the points after it.</summary>
public sealed record TerminateAt(TrainId Train, int Index) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Point(b, Train, Index, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        var point = plan.Points[Index];
        if (Index == 0)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} starts at {point.Location}; cancel it instead.");
        }

        if (!point.IsTimed)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} has no time at {point.Location} to terminate at.");
        }

        if (Index >= plan.LastIndex)
        {
            return PlanCommandResult.Fail($"{plan.Headcode} already terminates at {point.Location}.");
        }

        return PlanCommandResult.From(b.Update(plan with { TerminatesAt = Index }));
    }

    public string Describe(WorkingPlan plan) => $"Terminate {Checks.Name(plan, Train)} short at {Checks.Where(plan, Train, Index)}";
}

public sealed record CancelTrain(TrainId Train) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Train(b, Train, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        return plan.Cancelled
            ? PlanCommandResult.Fail($"{plan.Headcode} is already cancelled.")
            : PlanCommandResult.From(b.Update(plan with { Cancelled = true }));
    }

    public string Describe(WorkingPlan plan) => $"Cancel {Checks.Name(plan, Train)}";
}

/// <summary>Undoes a cancellation and any short termination, so the train runs its whole plan again.</summary>
public sealed record ReinstateTrain(TrainId Train) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Train(b, Train, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        return !plan.Cancelled && plan.TerminatesAt is null
            ? PlanCommandResult.Fail($"{plan.Headcode} is neither cancelled nor terminated short.")
            : PlanCommandResult.From(b.Update(plan with { Cancelled = false, TerminatesAt = null }));
    }

    public string Describe(WorkingPlan plan) => $"Reinstate {Checks.Name(plan, Train)}";
}

/// <summary>
/// Adds a new train: a copy of <see cref="Source"/>'s current plan (not cancelled) with a new headcode, moved by
/// <see cref="DeltaSeconds"/>. <see cref="NewId"/> is chosen by the caller (see <see cref="WorkingPlan.NewCloneId"/>)
/// so replaying the command always gives the same train.
/// </summary>
public sealed record CloneTrain(TrainId Source, Headcode NewHeadcode, int DeltaSeconds, TrainId NewId) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Checks.Train(b, Source, out var plan) is { } error)
        {
            return PlanCommandResult.Fail(error);
        }

        if (NewId.IsEmpty || NewHeadcode.Value.Length == 0)
        {
            return PlanCommandResult.Fail("A clone needs a new train ID and headcode.");
        }

        var clone = plan with
        {
            Id = NewId,
            Headcode = NewHeadcode,
            Origin = PlanOrigin.Clone,
            Points = DeltaSeconds == 0 ? plan.Points : plan.Points.Select(p => p.Shift(DeltaSeconds)).ToArray(),
            Cancelled = false,
        };
        return PlanCommandResult.From(b.Add(clone));
    }

    public string Describe(WorkingPlan plan) =>
        $"Clone {Checks.Name(plan, Source)} as {NewHeadcode}{(DeltaSeconds == 0 ? string.Empty : " " + PlanText.WithSign(DeltaSeconds))}";
}

public sealed record AddDisruption(Disruption Disruption) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return Disruption is null ? PlanCommandResult.Fail("No disruption given.") : PlanCommandResult.From(b.AddDisruption(Disruption));
    }

    public string Describe(WorkingPlan plan) => $"Add {PlanText.Describe(Disruption)}";
}

public sealed record RemoveDisruption(string Id) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return PlanCommandResult.From(b.RemoveDisruption(Id));
    }

    public string Describe(WorkingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.FindDisruption(Id) is { } d ? $"Remove {PlanText.Describe(d)}" : $"Remove disruption {Id}";
    }
}

/// <summary>Replaces the disruption with the same <see cref="Disruption.Id"/>.</summary>
public sealed record EditDisruption(Disruption Disruption) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return Disruption is null ? PlanCommandResult.Fail("No disruption given.") : PlanCommandResult.From(b.ReplaceDisruption(Disruption));
    }

    public string Describe(WorkingPlan plan) => $"Change {PlanText.Describe(Disruption)}";
}

/// <summary>
/// Links a live working (sim uid and headcode) to a WTT train by hand, or with a null <see cref="Train"/> marks it
/// "do not match". Not a change to the plan itself, but kept in its history so it can be undone (Fred, S10).
/// </summary>
public sealed record LinkLiveTrain(string SimUid, Headcode Headcode, TrainId? Train) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return PlanCommandResult.From(b.SetLink(new LiveLink(SimUid, Headcode, Train)));
    }

    public string Describe(WorkingPlan plan) =>
        Train is { } id ? $"Link live {Headcode} to {Checks.Name(plan, id)}" : $"Do not match live {Headcode}";
}

/// <summary>Removes a manual link, so the working is matched automatically again.</summary>
public sealed record UnlinkLiveTrain(string SimUid, Headcode Headcode) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        return PlanCommandResult.From(b.RemoveLink(SimUid, Headcode));
    }

    public string Describe(WorkingPlan plan) => $"Match live {Headcode} automatically";
}

/// <summary>A suggestion's commands applied as one step: all of them, or (if any fails) none.</summary>
public sealed record AcceptSuggestion(string SuggestionId, IReadOnlyList<IPlanCommand> Commands) : IPlanCommand
{
    public PlanCommandResult Apply(WorkingPlanBuilder b)
    {
        ArgumentNullException.ThrowIfNull(b);
        if (Commands is not { Count: > 0 })
        {
            return PlanCommandResult.Fail("The suggestion has no steps.");
        }

        var warnings = new List<PlanWarning>();
        for (var i = 0; i < Commands.Count; i++)
        {
            var result = b.Run(Commands[i]);
            if (!result.Applied)
            {
                return PlanCommandResult.Fail(string.Create(CultureInfo.InvariantCulture, $"Step {i + 1} of the suggestion failed: {result.Error}"));
            }

            warnings.AddRange(result.Warnings);
        }

        return new PlanCommandResult(true, null, warnings);
    }

    public string Describe(WorkingPlan plan) =>
        Commands is { Count: > 0 } ? $"Accept suggestion: {string.Join("; ", Commands.Select(c => c.Describe(plan)))}" : "Accept suggestion";
}

/// <summary>Shared checks for the commands.</summary>
internal static class Checks
{
    public static string? Train(WorkingPlanBuilder b, TrainId id, out TrainPlan plan)
    {
        plan = b.Find(id)!;
        return plan is null ? $"No train {id} in the plan." : null;
    }

    public static string? Point(WorkingPlanBuilder b, TrainId id, int index, out TrainPlan plan)
    {
        if (Train(b, id, out plan) is { } error)
        {
            return error;
        }

        return index < 0 || index >= plan.Points.Count
            ? string.Create(CultureInfo.InvariantCulture, $"{plan.Headcode} has no point {index}.")
            : index > plan.LastIndex
                ? $"{plan.Headcode} is terminated short and no longer runs to {plan.Points[index].Location}."
                : null;
    }

    public static TrainPlan With(TrainPlan plan, PlannedPoint point)
    {
        var points = plan.Points.ToArray();
        points[point.Index] = point;
        return plan with { Points = points };
    }

    public static string Name(WorkingPlan plan, TrainId id)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.NameOf(id);
    }

    public static string Where(WorkingPlan plan, TrainId id, int index)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return plan.Find(id) is { } train && index >= 0 && index < train.Points.Count
            ? train.Points[index].Location.ToString()
            : string.Create(CultureInfo.InvariantCulture, $"point {index}");
    }
}

/// <summary>Wording for plan history and warnings.</summary>
public static class PlanText
{
    /// <summary>"45 s", "5 min", "1 min 30 s", "2 h 5 min".</summary>
    public static string Duration(int seconds)
    {
        seconds = Math.Abs(seconds);
        var hours = seconds / SimTime.SecondsPerHour;
        var minutes = seconds % SimTime.SecondsPerHour / SimTime.SecondsPerMinute;
        var secs = seconds % SimTime.SecondsPerMinute;
        var parts = new List<string>(3);
        if (hours > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hours} h"));
        }

        if (minutes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{minutes} min"));
        }

        if (secs > 0 || parts.Count == 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{secs} s"));
        }

        return string.Join(' ', parts);
    }

    /// <summary>"+5 min" or "−1 min 30 s" (with a true minus sign).</summary>
    public static string WithSign(int seconds) => (seconds < 0 ? "−" : "+") + Duration(seconds);

    public static string Describe(Disruption? d) => d is null
        ? "disruption"
        : $"{(d.Kind == DisruptionKind.Block ? "block" : "speed restriction")} {d.From}–{d.To} {d.Start}–{d.End}";
}
