using System.Globalization;
using System.Text;
using Stringline.Core;
using Stringline.Network.Profiles;

namespace Stringline.Planning;

/// <summary>
/// Writes a <see cref="DelayReport"/> as CSV (SES-2, <c>05-…md</c> §11): one row per train and timing point
/// (<see cref="WritePoints"/>), or one per train (<see cref="WriteTrains"/>). RFC 4180: a header row, fields quoted
/// where needed, CRLF line ends. Times are <c>HH:mm:ss</c> with a <c>+1</c> day marker after midnight, delays are
/// signed seconds (negative = early), and blank means unknown.
/// </summary>
public static class DelayReportCsv
{
    /// <summary>UTF-8 with a byte order mark, so Excel reads location names with accents correctly.</summary>
    public static Encoding Encoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static readonly string[] PointColumns =
    [
        "headcode", "train_id", "sim_uid", "category", "seq", "tiploc", "location", "activity", "runs",
        "wtt_arr", "wtt_dep", "plan_arr", "plan_dep", "arr", "arr_basis", "dep", "dep_basis",
        "wtt_platform", "plan_platform", "delay_vs_wtt_s", "delay_vs_plan_s", "delay_basis",
    ];

    public static readonly string[] TrainColumns =
    [
        "headcode", "train_id", "sim_uid", "category", "origin", "state", "replanned", "from", "to", "start",
        "latest_tiploc", "latest_time", "latest_delay_s", "worst_tiploc", "worst_delay_s", "final_tiploc", "final_delay_s", "final_basis",
    ];

    /// <summary>One row per train and timing point, trains in report order.</summary>
    /// <param name="profile">For location display names; TIPLOCs are used without one.</param>
    public static void WritePoints(TextWriter writer, DelayReport report, StringlineProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);
        WriteRow(writer, PointColumns);
        foreach (var train in report.Trains)
        {
            foreach (var p in train.Points)
            {
                WriteRow(writer,
                [
                    train.Headcode.Value,
                    train.Train.Value,
                    train.LiveUid,
                    train.CategoryId,
                    Number(p.Index + 1),
                    p.Location.Code,
                    profile?.DisplayName(p.Location) ?? p.Location.Code,
                    Activity(p, train),
                    p.Runs ? "yes" : "no",
                    Time(p.WttArrive),
                    Time(p.WttDepart),
                    Time(p.PlanArrive),
                    Time(p.PlanDepart),
                    Time(p.Arrive),
                    Basis(p.ArriveBasis),
                    Time(p.Depart),
                    Basis(p.DepartBasis),
                    p.WttPlatform?.Trim(),
                    p.Platform?.Trim(),
                    Number(p.WttDelaySeconds),
                    Number(p.PlanDelaySeconds),
                    Basis(p.DelayBasis),
                ]);
            }
        }
    }

    /// <summary>One row per train: where it is, its worst actual delay and its final (actual or forecast) delay.</summary>
    public static void WriteTrains(TextWriter writer, DelayReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);
        WriteRow(writer, TrainColumns);
        foreach (var t in report.Trains)
        {
            WriteRow(writer,
            [
                t.Headcode.Value,
                t.Train.Value,
                t.LiveUid,
                t.CategoryId,
                t.Origin == PlanOrigin.Clone ? "clone" : "wtt",
                State(t.State),
                t.IsModified ? "yes" : "no",
                t.From?.Code,
                t.To?.Code,
                Time(t.Start),
                t.Latest?.Location.Code,
                Time(t.Latest is { } l ? l.DepartBasis == DelayBasis.Actual ? l.Depart : l.Arrive : null),
                Number(t.LatestDelaySeconds),
                t.Worst?.Location.Code,
                Number(t.WorstDelaySeconds),
                t.Final?.Location.Code,
                Number(t.FinalDelaySeconds),
                t.Final is { } f ? Basis(f.DelayBasis) : null,
            ]);
        }
    }

    public static string State(DelayTrainState state) => state switch
    {
        DelayTrainState.NotStarted => "not started",
        DelayTrainState.Running => "running",
        DelayTrainState.Finished => "finished",
        DelayTrainState.Cancelled => "cancelled",
        _ => "no forecast",
    };

    /// <summary>
    /// What the train does at a point by the plan: start (its first point it runs to), terminate (its last, unless it
    /// passes out of the area there), pass, or stop. Points after a short termination keep their booked activity.
    /// </summary>
    private static string Activity(DelayReportPoint p, DelayReportTrain train)
    {
        if (p.IsPass)
        {
            return "pass";
        }

        if (p.Runs && ReferenceEquals(p, train.Points.FirstOrDefault(x => x.Runs)))
        {
            return "start";
        }

        return p.Runs && ReferenceEquals(p, train.Points.LastOrDefault(x => x.Runs)) ? "terminate" : "stop";
    }

    private static string? Basis(DelayBasis basis) => basis switch
    {
        DelayBasis.Actual => "actual",
        DelayBasis.Forecast => "forecast",
        _ => null,
    };

    private static string? Time(SimTime? time) => time?.ToString("T", CultureInfo.InvariantCulture);

    private static string? Number(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static void WriteRow(TextWriter writer, string?[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(Quote(fields[i]));
        }

        writer.Write("\r\n");
    }

    /// <summary>A field as RFC 4180 has it: quoted (with quotes doubled) if it holds a comma, quote or line break.</summary>
    internal static string Quote(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        return field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }
}
