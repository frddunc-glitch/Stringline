using System.Xml.Linq;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Knowledge;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Validation;

/// <summary>Quick fixes too long to write inline in the checks.</summary>
internal static class QuickFixes
{
    /// <summary>Half a minute: SimSig books to the half minute.</summary>
    private const int Step = 30;

    /// <summary>
    /// The "no known path" fix: inserts the locations other timetables book between <paramref name="from"/> and
    /// <paramref name="to"/> as passes. When both ends are timed, the pass times share the run in proportion to the
    /// sim's usual run times for the train's band (equal shares if any link has none), to the half minute.
    /// </summary>
    public static TimetableEdit InsertChain(ValidationContext c, SimKnowledge k, Train train, TimingPoint from, TimingPoint to, IReadOnlyList<Tiploc> chain)
    {
        var fromTrip = c.TripOf(train, from.Index);
        var toTrip = c.TripOf(train, to.Index);
        var at = fromTrip.Element.ElementsBeforeSelf().Count() + 1;
        var middle = chain.Skip(1).SkipLast(1).ToList();
        var times = PassTimes(k, train, from, to, chain);
        var edits = new List<TimetableEdit>();
        for (var j = 0; j < middle.Count; j++)
        {
            var node = new TripNode(c.Document, new XElement("Trip"))
            {
                Location = middle[j].Code,
            };
            if (times?[j] is { } time)
            {
                node.DepPassTime = time;
                node.IsPassTime = true;
            }

            node.DownDirection = toTrip.DownDirection;
            edits.Add(TimetableEdit.Insert(fromTrip.Element.Parent!, node.Element, at + j, $"Add {middle[j]}"));
        }

        return TimetableEdit.Composite($"Insert {string.Join(", ", middle)} in {train.Headcode} between {from.Location} and {to.Location}", edits);
    }

    private static SimTime?[]? PassTimes(SimKnowledge k, Train train, TimingPoint from, TimingPoint to, IReadOnlyList<Tiploc> chain)
    {
        if (from.LastTime is not { } leaves || to.FirstTime is not { } reaches || reaches <= leaves)
        {
            return null;
        }

        var band = RunBand.Of(train);
        var weights = new int[chain.Count - 1];
        for (var j = 0; j < weights.Length; j++)
        {
            weights[j] = k.NearestRunTime(chain[j], chain[j + 1], band)?.Times.UsualSeconds ?? 0;
        }

        if (weights.Any(w => w <= 0))
        {
            Array.Fill(weights, 1);
        }

        var total = (double)weights.Sum();
        var span = reaches - leaves;
        var times = new SimTime?[chain.Count - 2];
        var sofar = 0;
        var previous = leaves.Seconds;
        for (var j = 0; j < times.Length; j++)
        {
            sofar += weights[j];
            var seconds = leaves.Seconds + (int)(Math.Round(span * (sofar / total) / Step, MidpointRounding.AwayFromZero) * Step);
            seconds = Math.Clamp(seconds, previous, reaches.Seconds);
            times[j] = new SimTime(seconds);
            previous = seconds;
        }

        return times;
    }
}
