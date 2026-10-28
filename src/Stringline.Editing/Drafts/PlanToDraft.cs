using System.Globalization;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Bulk;
using Stringline.Planning;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Drafts;

/// <summary>
/// A working plan made into a draft timetable (S47, <c>08-…md</c> §9): the session's re-plans written into a fresh read
/// of the timetable it was made from, as one edit. Times, platforms and lines are set; a skipped stop becomes a pass;
/// a train terminated short loses the points after it (its next working is reported as no longer formed); a
/// cancelled train is removed or kept as asked; a clone becomes a new train. Disruptions, notes and signal holds have
/// no place in a timetable, so they are listed.
/// </summary>
public static class PlanToDraft
{
    /// <param name="draft">The timetable the plan was made from, read again (its trains have the plan's IDs).</param>
    /// <param name="removeCancelled">True to remove cancelled trains; false to keep them as booked.</param>
    public static BulkResult Build(WttDocument draft, WorkingPlan plan, bool removeCancelled)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(plan);
        var import = draft.Import();
        var edits = new List<TimetableEdit>();
        var changes = new List<string>();
        var problems = new List<string>();
        var uids = draft.Trains.Select(t => t.Uid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usesUids = TimetableParts.UsesUids(draft) || uids.Count > 0;

        foreach (var train in plan.Modified)
        {
            if (train.Origin == PlanOrigin.Clone)
            {
                Clone(draft, import, train, usesUids, uids, edits, changes, problems);
                continue;
            }

            if (import.Timetable.Find(train.Id) is not { } wtt)
            {
                problems.Add($"{train.Headcode} ({train.Id}) is not in the timetable read again; left out.");
                continue;
            }

            var node = import.NodeOf(wtt);
            if (train.Cancelled)
            {
                if (removeCancelled)
                {
                    edits.Add(TimetableEdit.Remove(node.Element, $"Remove {train.Headcode}"));
                    changes.Add($"{train.Headcode}: cancelled, removed");
                    foreach (var other in FormedBy(import, wtt))
                    {
                        problems.Add($"{other}: names {train.Headcode}, which is removed (the checks will show it).");
                    }
                }
                else
                {
                    changes.Add($"{train.Headcode}: cancelled, kept as booked");
                }

                continue;
            }

            var before = edits.Count;
            var trips = node.Trips.ToList();
            SetPoints(trips, train, edits);
            if (train.TerminatesAt is { } end && end < trips.Count - 1)
            {
                var dropped = trips.Skip(end + 1).ToList();
                foreach (var trip in dropped)
                {
                    edits.Add(TimetableEdit.Remove(trip.Element));
                }

                var next = trips.Skip(end).SelectMany(t => t.Activities)
                    .Where(a => a.Code is { } c && TimetableParts.NamesATrain((ActivityKind)c) && c != (int)ActivityKind.CrewChange)
                    .Select(a => a.AssociatedTrain ?? a.AssociatedUid).OfType<string>().ToList();
                if (next.Count > 0)
                {
                    problems.Add($"{train.Headcode} terminates short at {trips[end].Location}: {string.Join(", ", next)} {(next.Count == 1 ? "is" : "are")} no longer formed there.");
                }

                changes.Add($"{train.Headcode}: terminates at {trips[end].Location} ({dropped.Count} point{(dropped.Count == 1 ? "" : "s")} removed)");
            }

            if (train.SignalHolds.Count > 0)
            {
                problems.Add($"{train.Headcode}: {train.SignalHolds.Count} hold{(train.SignalHolds.Count == 1 ? "" : "s")} at a signal (a timetable cannot book one).");
            }

            if (edits.Count > before && !changes.Any(c => c.StartsWith(train.Headcode + ":", StringComparison.Ordinal)))
            {
                changes.Add($"{train.Headcode}: retimed{Platforms(trips, train)}");
            }
        }

        foreach (var disruption in plan.Disruptions)
        {
            problems.Add($"{disruption.Kind} {disruption.Id} {disruption.From}–{disruption.To}: not part of a timetable.");
        }

        foreach (var note in plan.Notes)
        {
            problems.Add($"Note at {note.Location} {SimTimeNotation.Format(note.Time)}: {note.Text}");
        }

        return new BulkResult(edits.Count == 0 ? null : TimetableEdit.Composite("Draft from the working plan", edits), changes, problems);
    }

    private static void Clone(WttDocument draft, WttDocumentImport import, TrainPlan train, bool usesUids, HashSet<string> uids, List<TimetableEdit> edits, List<string> changes, List<string> problems)
    {
        if (import.Timetable.Find(train.Source) is not { } source)
        {
            problems.Add($"{train.Headcode} is a copy of {train.Source}, which is not in the timetable; left out.");
            return;
        }

        var sourceNode = import.NodeOf(source);
        var copy = DraftDocuments.CopyTrain(sourceNode, train.Headcode.Value);
        var node = new TrainNode(draft, copy);
        if (usesUids)
        {
            var uid = train.Headcode.Value;
            for (var n = 2; !uids.Add(uid); n++)
            {
                uid = string.Create(CultureInfo.InvariantCulture, $"{train.Headcode.Value}-{n}");
            }

            node.Uid = uid;
        }

        foreach (var activity in node.Trips.SelectMany(t => t.Activities).ToList())
        {
            if (activity.Code is { } code && TimetableParts.NamesATrain((ActivityKind)code))
            {
                var list = activity.Element.Parent!;
                activity.Element.Remove();
                if (!list.HasElements)
                {
                    list.Remove();
                }
            }
        }

        var trips = node.Trips.ToList();
        var shifted = train.Points.Count > 0 && train.Points[0].FirstTime is { } first && source.Points.Count > 0 && source.Points[0].FirstTime is { } was ? first - was : 0;
        if (node.DepartTime is { } entry && shifted != 0)
        {
            node.DepartTime = entry + shifted;
        }

        // The copy is not in the document yet: its fields are set directly, and adding it is the edit.
        foreach (var point in train.Points.Where(p => p.Index < trips.Count))
        {
            Set(trips[point.Index], point);
        }

        edits.Add(TimetableEdit.Insert(sourceNode.Element.Parent!, copy, sourceNode.Element.ElementsBeforeSelf().Count() + 1, $"Add {train.Headcode}"));
        changes.Add($"{train.Headcode}: new train, a copy of {source.Headcode}{(node.Uid is { } u ? $" (UID {u})" : string.Empty)}");
    }

    /// <summary>The trip fields from the plan's points, as edits (only what differs).</summary>
    private static void SetPoints(List<TripNode> trips, TrainPlan train, List<TimetableEdit> edits)
    {
        foreach (var point in train.Points)
        {
            if (point.Index >= trips.Count)
            {
                continue;
            }

            var edit = TimetableEdit.Change(trips[point.Index], t => Set(t, point));
            if (edit is not CompositeEdit { Edits.Count: 0 })
            {
                edits.Add(edit);
            }
        }
    }

    private static void Set(TripNode t, PlannedPoint point)
    {
        if (point.IsPass)
        {
            t.ArrTime = null;
            t.DepPassTime = point.Depart ?? point.Arrive;
            t.IsPassTime = true;
        }
        else
        {
            t.ArrTime = point.Arrive;
            t.DepPassTime = point.Depart;
            t.IsPassTime = false;
        }

        t.Platform = point.Platform;
        t.Line = point.Line;
    }

    private static string Platforms(List<TripNode> trips, TrainPlan train)
    {
        var moved = train.Points.Where(p => p.Index < trips.Count && p.Platform != trips[p.Index].Platform).Select(p => $"{p.Location} platform {p.Platform}").ToList();
        return moved.Count == 0 ? string.Empty : ", " + string.Join(", ", moved);
    }

    /// <summary>The trains whose activities name <paramref name="train"/>.</summary>
    private static IEnumerable<string> FormedBy(WttDocumentImport import, Train train) =>
        import.Timetable.Trains.Where(t => t.Points.Any(p => p.Activities.Any(a => a.Train == train.Id))).Select(t => t.Headcode.Value);
}
