using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>How the delay table's trains are ordered.</summary>
internal enum DelaySort
{
    /// <summary>Greatest actual delay first: the worst offenders (the default).</summary>
    Worst,

    /// <summary>Greatest delay at the last point (actual or forecast) first.</summary>
    Final,

    /// <summary>Greatest delay at the latest point reported first.</summary>
    Latest,

    Headcode,

    /// <summary>By first planned time.</summary>
    Start,
}

/// <summary>A sort option for the combo box.</summary>
internal sealed record DelaySortOption(DelaySort Sort, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One train in the delay table: updated in place, so the selection survives live updates.</summary>
internal sealed partial class DelayTrainRowViewModel(TrainId train) : ObservableObject
{
    public TrainId Train { get; } = train;

    public DelayReportTrain? Report { get; private set; }

    [ObservableProperty]
    public partial string Headcode { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string State { get; private set; } = string.Empty;

    /// <summary>"Vauxhall 07:12".</summary>
    [ObservableProperty]
    public partial string Latest { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LatestDelay { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WorstDelay { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string WorstAt { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FinalDelay { get; private set; } = string.Empty;

    /// <summary>The final delay is a forecast (shown in the forecast's blue).</summary>
    [ObservableProperty]
    public partial bool FinalIsForecast { get; private set; }

    [ObservableProperty]
    public partial string Tip { get; private set; } = string.Empty;

    public void Update(DelayReportTrain t, StringlineProfile? profile)
    {
        Report = t;
        Headcode = t.Headcode.Value;
        State = t.State switch
        {
            DelayTrainState.NotStarted => "Not started",
            DelayTrainState.Running => "Running",
            DelayTrainState.Finished => "Finished",
            DelayTrainState.Cancelled => "Cancelled",
            DelayTrainState.NotRunning => "Not running",
            _ => "—",
        };
        Latest = t.Latest is { } l ? $"{Name(l.Location, profile)} {PlanInput.Format(l.DepartBasis == DelayBasis.Actual ? l.Depart : l.Arrive)}" : string.Empty;
        LatestDelay = DelaysViewModel.Minutes(t.LatestDelaySeconds);
        WorstDelay = DelaysViewModel.Minutes(t.WorstDelaySeconds);
        WorstAt = t.Worst is { } w ? Name(w.Location, profile) : string.Empty;
        FinalDelay = DelaysViewModel.Minutes(t.FinalDelaySeconds);
        FinalIsForecast = t.Final?.DelayBasis == DelayBasis.Forecast;
        var route = t.From is { } from && t.To is { } to ? $"{PlanInput.Format(t.Start)} {Name(from, profile)} – {Name(to, profile)}" : string.Empty;
        var tip = new List<string> { $"{t.Headcode} {route}".TrimEnd() };
        if (t.Origin == PlanOrigin.Clone)
        {
            tip.Add("Ad-hoc copy: delays are against its plan");
        }
        else if (t.IsModified)
        {
            tip.Add("Re-planned: delays are against the WTT");
        }

        if (t.Final is { } f)
        {
            tip.Add($"{(f.DelayBasis == DelayBasis.Forecast ? "Forecast" : "Actual")} at {Name(f.Location, profile)}: {DelaysViewModel.Minutes(f.DelaySeconds)} min");
        }

        Tip = string.Join(Environment.NewLine, tip);
    }

    private static string Name(Tiploc tiploc, StringlineProfile? profile) => profile?.DisplayName(tiploc) ?? tiploc.Code;
}

/// <summary>One timing point of the selected train in the delay table, as text.</summary>
internal sealed record DelayPointRowViewModel(
    string Key,
    string Location,
    string Tiploc,
    string Booked,
    string Planned,
    string Actual,
    string Forecast,
    string WttDelay,
    string PlanDelay,
    bool Runs,
    bool IsActual)
{
    public static DelayPointRowViewModel Of(DelayReportPoint p, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(p);
        var dwell = (p.WttArrive is not null && p.WttDepart is not null) || (p.PlanArrive is not null && p.PlanDepart is not null);
        var actualArrive = p.ArriveBasis == DelayBasis.Actual ? p.Arrive : null;
        var actualDepart = p.DepartBasis == DelayBasis.Actual ? p.Depart : null;
        var forecastArrive = p.ArriveBasis == DelayBasis.Forecast ? p.Arrive : null;
        var forecastDepart = p.DepartBasis == DelayBasis.Forecast ? p.Depart : null;
        return new DelayPointRowViewModel(
            string.Create(CultureInfo.InvariantCulture, $"{p.Index}"),
            profile?.DisplayName(p.Location) ?? p.Location.Code,
            p.Location.Code,
            Times(p.WttArrive, p.WttDepart, dwell),
            Times(p.PlanArrive, p.PlanDepart, dwell),
            Times(actualArrive, actualDepart, dwell),
            Times(forecastArrive, forecastDepart, dwell),
            DelaysViewModel.Minutes(p.WttDelaySeconds),
            DelaysViewModel.Minutes(p.PlanDelaySeconds),
            p.Runs,
            p.IsActual);
    }

    /// <summary>"07:05–07:06" for a stop with a dwell; otherwise the one time there is.</summary>
    private static string Times(SimTime? arrive, SimTime? depart, bool dwell) =>
        dwell && (arrive is not null || depart is not null) ? $"{PlanInput.Format(arrive)}–{PlanInput.Format(depart)}" : PlanInput.Format(arrive ?? depart);
}

/// <summary>
/// The delay table (SES-2, <c>05-…md</c> §11): a tab beside the regulation list (Fred). Above, one row per train (worst
/// offenders first); below, the selected train's timing points with booked, planned, actual and forecast times and the
/// delay against the WTT and the plan. Delays are whole minutes, as railway delay minutes go.
/// </summary>
internal sealed partial class DelaysViewModel : ObservableObject
{
    private readonly Dictionary<TrainId, DelayTrainRowViewModel> _rows = [];
    private StringlineProfile? _profile;

    public static IReadOnlyList<DelaySortOption> SortOptions { get; } =
    [
        new(DelaySort.Worst, "Worst delay"),
        new(DelaySort.Final, "Final delay"),
        new(DelaySort.Latest, "Latest delay"),
        new(DelaySort.Headcode, "Headcode"),
        new(DelaySort.Start, "Start time"),
    ];

    /// <summary><see cref="SortOptions"/>, for binding.</summary>
    public IReadOnlyList<DelaySortOption> Options { get; } = SortOptions;

    public ObservableCollection<DelayTrainRowViewModel> Trains { get; } = [];

    public ObservableCollection<DelayPointRowViewModel> Points { get; } = [];

    /// <summary>The report shown, with <see cref="ShowAll"/>'s filter.</summary>
    public DelayReport Report { get; private set; } = DelayReport.Empty;

    [ObservableProperty]
    public partial DelayTrainRowViewModel? Selected { get; set; }

    /// <summary>List every train in the plan, not only those seen.</summary>
    [ObservableProperty]
    public partial bool ShowAll { get; set; }

    [ObservableProperty]
    public partial DelaySortOption Sort { get; set; } = SortOptions[0];

    /// <summary>"42 trains seen · 9 late (5+ min) · worst 2A94 +14 at Vauxhall".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "No timetable is loaded.";

    [ObservableProperty]
    public partial string PointsHeading { get; private set; } = string.Empty;

    public DelayReportFilter Filter => ShowAll ? DelayReportFilter.All : DelayReportFilter.Seen;

    /// <summary>The filter changed: the owner refreshes the report.</summary>
    public event EventHandler? FilterChanged;

    partial void OnShowAllChanged(bool value) => FilterChanged?.Invoke(this, EventArgs.Empty);

    partial void OnSortChanged(DelaySortOption value) => Order();

    partial void OnSelectedChanged(DelayTrainRowViewModel? value) => ShowPoints();

    /// <summary>Signed whole minutes ("+3", "−1", "0"), or blank if unknown.</summary>
    public static string Minutes(int? seconds) => seconds is { } s ? TrainListViewModel.FormatDelay(s / SimTime.SecondsPerMinute) : string.Empty;

    public DelayTrainRowViewModel? Find(TrainId train) => _rows.GetValueOrDefault(train);

    public void Update(PlanSnapshot snapshot, StringlineProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Show(snapshot.Plan.Count == 0 ? DelayReport.Empty : DelayReport.For(snapshot, Filter), profile, snapshot.Plan.Count > 0);
    }

    /// <summary>Shows a report (worked out by <see cref="Update"/>, or elsewhere).</summary>
    public void Show(DelayReport report, StringlineProfile? profile, bool hasPlan = true)
    {
        ArgumentNullException.ThrowIfNull(report);
        Report = report;
        _profile = profile;
        var keep = new HashSet<TrainId>();
        foreach (var t in report.Trains)
        {
            keep.Add(t.Train);
            if (!_rows.TryGetValue(t.Train, out var row))
            {
                row = new DelayTrainRowViewModel(t.Train);
                _rows.Add(t.Train, row);
            }

            row.Update(t, profile);
        }

        foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _rows.Remove(gone);
        }

        Order();
        ShowPoints();
        Summary = Summarise(report, hasPlan, profile);
    }

    private string Summarise(DelayReport report, bool hasPlan, StringlineProfile? profile)
    {
        if (!hasPlan)
        {
            return "No timetable is loaded.";
        }

        var count = report.Trains.Count;
        var what = ShowAll ? "in the plan" : "seen";
        if (count == 0)
        {
            return ShowAll ? "No trains in the plan." : "No trains seen yet.";
        }

        var late = report.Trains.Count(t => t.WorstDelaySeconds >= 5 * SimTime.SecondsPerMinute);
        var text = string.Create(CultureInfo.InvariantCulture, $"{count} train{(count == 1 ? string.Empty : "s")} {what} · {late} late (5+ min)");
        if (report.Trains.Where(t => t.WorstDelaySeconds >= SimTime.SecondsPerMinute).MaxBy(t => t.WorstDelaySeconds) is { } worst)
        {
            text += $" · worst {worst.Headcode} {Minutes(worst.WorstDelaySeconds)} at {profile?.DisplayName(worst.Worst!.Location) ?? worst.Worst!.Location.Code}";
        }

        if (report.UnmatchedLive > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {report.UnmatchedLive} not matched (not listed)");
        }

        if (report.NotRunning > 0)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" · {report.NotRunning} not running");
        }

        return text;
    }

    /// <summary>Puts <see cref="Trains"/> in the chosen order, moving rows rather than replacing them.</summary>
    private void Order()
    {
        var ordered = Sorted(_rows.Values).ToList();
        for (var i = Trains.Count - 1; i >= 0; i--)
        {
            if (!_rows.TryGetValue(Trains[i].Train, out var row) || !ReferenceEquals(row, Trains[i]))
            {
                if (ReferenceEquals(Selected, Trains[i]))
                {
                    Selected = null;
                }

                Trains.RemoveAt(i);
            }
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var at = Trains.IndexOf(ordered[i]);
            if (at < 0)
            {
                Trains.Insert(i, ordered[i]);
            }
            else if (at != i)
            {
                Trains.Move(at, i);
            }
        }
    }

    private IEnumerable<DelayTrainRowViewModel> Sorted(IEnumerable<DelayTrainRowViewModel> rows)
    {
        // Unknown delays go last; ties by start time, then headcode.
        static int Key(int? seconds) => seconds ?? int.MinValue;
        var start = (DelayTrainRowViewModel r) => r.Report?.Start?.Seconds ?? int.MaxValue;
        return Sort.Sort switch
        {
            DelaySort.Final => rows.OrderByDescending(r => Key(r.Report?.FinalDelaySeconds)).ThenBy(start).ThenBy(r => r.Headcode, StringComparer.Ordinal),
            DelaySort.Latest => rows.OrderByDescending(r => Key(r.Report?.LatestDelaySeconds)).ThenBy(start).ThenBy(r => r.Headcode, StringComparer.Ordinal),
            DelaySort.Headcode => rows.OrderBy(r => r.Headcode, StringComparer.Ordinal).ThenBy(start),
            DelaySort.Start => rows.OrderBy(start).ThenBy(r => r.Headcode, StringComparer.Ordinal),
            _ => rows.OrderByDescending(r => Key(r.Report?.WorstDelaySeconds)).ThenByDescending(r => Key(r.Report?.FinalDelaySeconds)).ThenBy(start).ThenBy(r => r.Headcode, StringComparer.Ordinal),
        };
    }

    private void ShowPoints()
    {
        if (Selected?.Report is not { } train)
        {
            Points.Clear();
            PointsHeading = Trains.Count == 0 ? string.Empty : "Pick a train to see its delays at each timing point.";
            return;
        }

        PointsHeading = Selected.Tip.Split(Environment.NewLine)[0];
        var rows = train.Points.Select(p => DelayPointRowViewModel.Of(p, _profile)).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Points.Count)
            {
                Points.Add(rows[i]);
            }
            else if (Points[i] != rows[i])
            {
                Points[i] = rows[i];
            }
        }

        while (Points.Count > rows.Count)
        {
            Points.RemoveAt(Points.Count - 1);
        }
    }
}
