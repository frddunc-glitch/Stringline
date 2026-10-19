using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>A location the regulation list can be shown for.</summary>
internal sealed record RegulationLocation(Tiploc Tiploc, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One movement in the regulation list, as text.</summary>
/// <param name="Order">"Ahead of 1A23" or "Behind 2B45" where the plan's order differs from the WTT's; else empty.</param>
internal sealed record RegulationRowViewModel(
    string Key,
    string Time,
    string Headcode,
    string Move,
    string Platform,
    string Forecast,
    string Delay,
    string Order,
    bool OutOfOrder,
    bool IsChanged,
    string Tip)
{
    public static RegulationRowViewModel Of(RegulationRow r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var move = r.Move switch
        {
            RegulationMove.Start => "starts",
            RegulationMove.Pass => "passes",
            RegulationMove.Terminate => "terminates",
            _ => r.PlannedArrive is { } arrive ? $"calls {PlanInput.Format(arrive)}–" : "calls",
        };
        var order = r.Ahead.Count > 0 ? $"Ahead of {Names(r.Ahead)}"
            : r.Behind.Count > 0 ? $"Behind {Names(r.Behind)}"
            : r.IsAdded ? "Added"
            : string.Empty;
        // Whole minutes late (or early), as railway delay minutes go; under a minute is right time.
        var delay = r.DelaySeconds is { } d && Math.Abs(d) >= SimTime.SecondsPerMinute
            ? string.Create(CultureInfo.InvariantCulture, $"{(d > 0 ? "+" : "−")}{Math.Abs(d) / SimTime.SecondsPerMinute}")
            : string.Empty;
        var tip = new List<string>
        {
            r.Booked is { } booked ? $"Booked {PlanInput.Format(booked)}, planned {PlanInput.Format(r.Planned)}" : $"Planned {PlanInput.Format(r.Planned)} (not in the WTT)",
        };
        if (r.Forecast is { } forecast)
        {
            tip.Add($"Forecast {PlanInput.Format(forecast)}");
        }

        if (!string.IsNullOrWhiteSpace(r.Line))
        {
            tip.Add($"Line out: {r.Line}");
        }

        if (r.Ahead.Count > 0)
        {
            tip.Add($"Now goes before {Names(r.Ahead)}, booked before it");
        }

        if (r.Behind.Count > 0)
        {
            tip.Add($"Now goes after {Names(r.Behind)}, booked after it");
        }

        return new RegulationRowViewModel(
            string.Create(CultureInfo.InvariantCulture, $"{r.Train}:{r.Index}"),
            PlanInput.Format(r.Planned),
            r.Headcode.Value,
            move,
            r.Platform?.Trim() ?? string.Empty,
            r.Forecast is { } f ? PlanInput.Format(f) : string.Empty,
            delay,
            order,
            r.OutOfOrder,
            r.IsChanged,
            string.Join(Environment.NewLine, tip));
    }

    private static string Names(IReadOnlyList<Headcode> headcodes) =>
        headcodes.Count <= 3 ? string.Join(", ", headcodes) : $"{string.Join(", ", headcodes.Take(3))} and {headcodes.Count - 3} more";
}

/// <summary>
/// The regulation list (RP-9, <c>05-…md</c> §6): the movements still to come at one location in working-plan order,
/// marking where the order differs from the WTT's. A tab beside the conflicts (Fred). Kept in step in place, so the
/// scroll position survives live updates.
/// </summary>
internal sealed partial class RegulationViewModel : ObservableObject
{
    private (long Sequence, Tiploc? Location) _shown;

    public ObservableCollection<RegulationLocation> Locations { get; } = [];

    public ObservableCollection<RegulationRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial RegulationLocation? Location { get; set; }

    /// <summary>"14 movements in the next 3 hours · 2 out of WTT order".</summary>
    [ObservableProperty]
    public partial string Summary { get; private set; } = "Pick a location.";

    /// <summary>The location changed: the owner refreshes the rows.</summary>
    public event EventHandler? LocationChanged;

    partial void OnLocationChanged(RegulationLocation? value) => LocationChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>The locations of the profile's corridors, in corridor order; keeps the one picked if it is still there.</summary>
    public void SetLocations(StringlineProfile? profile)
    {
        var list = new List<RegulationLocation>();
        var seen = new HashSet<Tiploc>();
        foreach (var corridor in profile?.Corridors ?? [])
        {
            foreach (var l in corridor.Locations)
            {
                if (seen.Add(l.Tiploc))
                {
                    list.Add(new RegulationLocation(l.Tiploc, profile!.DisplayName(l.Tiploc)));
                }
            }
        }

        var picked = Location?.Tiploc;
        if (list.SequenceEqual(Locations))
        {
            return;
        }

        Locations.Clear();
        foreach (var l in list)
        {
            Locations.Add(l);
        }

        Location = picked is { } t ? Locations.FirstOrDefault(l => l.Tiploc == t) : null;
        _shown = default;
    }

    /// <summary>Picks a location (adding it to the list if no corridor has it).</summary>
    public void Pick(Tiploc tiploc, StringlineProfile? profile)
    {
        if (Locations.FirstOrDefault(l => l.Tiploc == tiploc) is not { } location)
        {
            location = new RegulationLocation(tiploc, profile?.DisplayName(tiploc) ?? tiploc.Code);
            Locations.Add(location);
        }

        Location = location;
    }

    public void Update(PlanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_shown == (snapshot.Sequence, Location?.Tiploc))
        {
            return;
        }

        _shown = (snapshot.Sequence, Location?.Tiploc);
        if (Location is not { } location || snapshot.Plan.Count == 0)
        {
            Rows.Clear();
            Summary = snapshot.Plan.Count == 0 ? "No timetable is loaded." : "Pick a location.";
            return;
        }

        var now = snapshot.Forecast.Now ?? (snapshot.Clock.HasTime ? snapshot.Clock.LastReported : null);
        var rows = RegulationList.For(snapshot.Plan, snapshot.Forecast, location.Tiploc, now).Select(RegulationRowViewModel.Of).ToList();
        Sync(rows);
        var outOfOrder = rows.Count(r => r.OutOfOrder);
        var span = now is null ? "planned" : string.Create(CultureInfo.InvariantCulture, $"in the next {RegulationList.DefaultHorizonSeconds / SimTime.SecondsPerHour} hours");
        Summary = rows.Count switch
        {
            0 => now is null ? "No movements planned here." : "No movements here in the next 3 hours.",
            1 => $"1 movement {span}",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} movements {span}"),
        } + (outOfOrder > 0 ? string.Create(CultureInfo.InvariantCulture, $" · {outOfOrder} out of WTT order") : string.Empty);
    }

    /// <summary>Makes <see cref="Rows"/> equal <paramref name="rows"/>, replacing only the rows that differ.</summary>
    private void Sync(List<RegulationRowViewModel> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (i >= Rows.Count)
            {
                Rows.Add(rows[i]);
            }
            else if (Rows[i] != rows[i])
            {
                Rows[i] = rows[i];
            }
        }

        while (Rows.Count > rows.Count)
        {
            Rows.RemoveAt(Rows.Count - 1);
        }
    }
}
