using Stringline.Core;
using Stringline.Core.Timetables;

namespace Stringline.Timetable;

/// <summary>
/// Gives imported trains their <see cref="TrainId"/>s and resolves the trains named by activities and rules.
/// Older WTTs name associated trains by headcode, newer ones by UID; neither is unique (UIDs repeat for alternative
/// paths), so a name with several candidates goes to the one at the same location nearest in time.
/// </summary>
internal static class TrainLinker
{
    private const int NoTime = int.MaxValue;

    /// <summary>
    /// IDs are the UID if the train has one, else the headcode; the second and later trains with the same ID get
    /// <c>#2</c>, <c>#3</c>… in file order, so re-importing the same file gives the same IDs.
    /// </summary>
    public static List<Train> AssignIds(IReadOnlyList<Train> trains)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<Train>(trains.Count);
        foreach (var train in trains)
        {
            var name = string.IsNullOrWhiteSpace(train.Uid) ? train.Headcode.Value : train.Uid.Trim();
            var n = counts.GetValueOrDefault(name);
            string id;
            do
            {
                n++;
                id = n == 1 ? name : $"{name}#{n}";
            }
            while (!used.Add(id));

            counts[name] = n;
            result.Add(train with { Id = new TrainId(id) });
        }

        return result;
    }

    public static List<Train> Link(IReadOnlyList<Train> drafts, Action<ImportSeverity, string, string?> warn)
    {
        var trains = AssignIds(drafts);
        var index = new Index(trains);
        var formedFrom = new Dictionary<int, TrainId>();
        var linked = new List<Train>(trains.Count);
        for (var i = 0; i < trains.Count; i++)
        {
            var train = trains[i];
            var points = train.Points.Select(point => point.Activities.Count == 0
                ? point
                : point with { Activities = point.Activities.Select(a => Resolve(i, point, a)).ToList() }).ToList();
            linked.Add(train with { Points = points });
        }

        for (var i = 0; i < linked.Count; i++)
        {
            if (formedFrom.TryGetValue(i, out var source))
            {
                linked[i] = linked[i] with { FormedFrom = source };
            }
        }

        return linked;

        TrainActivity Resolve(int self, TimingPoint point, TrainActivity activity)
        {
            var candidates = index.Candidates(activity.AssociatedUid, activity.AssociatedHeadcode?.Value).Where(c => c != self).ToList();
            if (candidates.Count == 0)
            {
                // A crew change often names a crew or diagram number rather than a train.
                if (activity.AssociatedText is { } named && activity.Kind != ActivityKind.CrewChange)
                {
                    warn(
                        ImportSeverity.Warning,
                        $"{activity.Kind} at {point.Location} names {named}, which is not in the timetable; the link is lost.",
                        trains[self].Id.Value);
                }

                return activity;
            }

            var best = candidates.MinBy(c => Distance(trains[c], point, activity.FormsAssociatedTrain));
            if (activity.FormsAssociatedTrain)
            {
                formedFrom.TryAdd(best, trains[self].Id);
            }

            return activity with { Train = trains[best].Id };
        }
    }

    /// <summary>How well a candidate fits an activity at <paramref name="point"/>: same location first, then nearest in time.</summary>
    private static (int Place, int Seconds) Distance(Train candidate, TimingPoint point, bool startsHere)
    {
        var when = point.LastTime ?? point.FirstTime;
        if (startsHere && candidate.Points[0].Location == point.Location)
        {
            return (0, Gap(candidate.Points[0].FirstTime ?? candidate.Points[0].LastTime, when));
        }

        var gaps = candidate.Points.Where(p => p.Location == point.Location).Select(p => Gap(p.FirstTime ?? p.LastTime, when)).ToList();
        return gaps.Count > 0 ? (1, gaps.Min()) : (2, Gap(candidate.FirstTime, when));
    }

    private static int Gap(SimTime? a, SimTime? b) => a is { } x && b is { } y ? Math.Abs(x - y) : NoTime;

    public static List<TimetableRule> ResolveRules(IReadOnlyList<WttReader.RawRule> rules, IReadOnlyList<Train> trains, Action<ImportSeverity, string, string?> warn)
    {
        var index = new Index(trains);
        var unresolved = 0;
        var result = new List<TimetableRule>(rules.Count);
        foreach (var rule in rules)
        {
            var x = Unique(rule.TrainUid, rule.Train);
            var y = Unique(rule.Train2Uid, rule.Train2);
            if ((x is null && (rule.TrainUid ?? rule.Train) is not null) || (y is null && (rule.Train2Uid ?? rule.Train2) is not null))
            {
                unresolved++;
            }

            result.Add(new TimetableRule(TimetableRule.KindFromCode(rule.Code), rule.Code, rule.TrainUid ?? rule.Train, rule.Train2Uid ?? rule.Train2, x, y, rule.Time, rule.Location));
        }

        if (unresolved > 0)
        {
            warn(ImportSeverity.Info, $"{unresolved} of {rules.Count} timetable rules name a train that is missing or not unique; they are kept with the train unresolved.", null);
        }

        return result;

        TrainId? Unique(string? uid, string? headcode)
        {
            var candidates = index.Candidates(uid, headcode).Take(2).ToList();
            return candidates.Count == 1 ? trains[candidates[0]].Id : null;
        }
    }

    /// <summary>Train positions by UID (case-insensitive) and by headcode.</summary>
    private sealed class Index
    {
        private readonly ILookup<string, int> _byUid;
        private readonly ILookup<string, int> _byHeadcode;

        public Index(IReadOnlyList<Train> trains)
        {
            _byUid = trains.Select((t, i) => (t.Uid, i)).Where(x => x.Uid is not null).ToLookup(x => x.Uid!.Trim(), x => x.i, StringComparer.OrdinalIgnoreCase);
            _byHeadcode = trains.Select((t, i) => (t.Headcode.Value, i)).ToLookup(x => x.Value, x => x.i, StringComparer.Ordinal);
        }

        /// <summary>Trains with this UID; if none (or no UID given), trains with this headcode. A UID field sometimes holds a headcode.</summary>
        public IEnumerable<int> Candidates(string? uid, string? headcode)
        {
            if (uid is not null)
            {
                var byUid = _byUid[uid.Trim()].ToList();
                if (byUid.Count > 0)
                {
                    return byUid;
                }

                headcode ??= uid;
            }

            return headcode is not null && Headcode.TryCreate(headcode, out var h) ? _byHeadcode[h.Value] : [];
        }
    }
}
