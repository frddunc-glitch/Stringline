using System.Globalization;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Validation;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>A code offered in a drop-down (a TIPLOC, platform, line or path), with how often other timetables use it.</summary>
internal sealed record CodeChoice(string Code, string Label)
{
    public override string ToString() => Code;
}

/// <summary>How the timetable editor writes and reads values in its cells (<c>08-…md</c> §7).</summary>
internal static class DraftText
{
    public static string Time(SimTime? time) => time is { } t && t.Seconds != 0 ? SimTimeNotation.Format(t) : string.Empty;

    /// <summary>The departure or pass cell: <c>12:34</c>, <c>12/34</c> for a pass, <c>12d34</c> set down, <c>12t34</c>, <c>12w34</c>, <c>12r34</c>.</summary>
    public static string Departure(TripNode trip)
    {
        if (trip.DepPassTime is not { Seconds: not 0 } time)
        {
            return string.Empty;
        }

        var mark = trip.IsPassTime ? TimeMark.Pass
            : trip.SetDownOnly ? TimeMark.SetDown
            : trip.AllowStopsOnThroughLines ? TimeMark.ThruLine
            : trip.WaitForBookedTime ? TimeMark.DwellWait
            : trip.RequestPercent is > 0 ? TimeMark.Request
            : TimeMark.None;
        return SimTimeNotation.Format(time, mark);
    }

    /// <summary>Allowances in minutes, <c>H</c> for a half: 90 s is <c>1H</c>, 30 s <c>0H</c>, none is empty.</summary>
    public static string Allowance(int seconds) =>
        seconds == 0 ? string.Empty : (seconds / 60).ToString(CultureInfo.InvariantCulture) + (seconds % 60 >= 30 ? "H" : string.Empty);

    /// <summary>Reads <c>1</c>, <c>1H</c>, <c>1½</c>, <c>1.5</c> or <c>H</c> as minutes, in whole half minutes; empty is none.</summary>
    public static bool TryAllowance(string? text, out int seconds)
    {
        seconds = 0;
        var t = text?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            return true;
        }

        var half = t.EndsWith('H') || t.EndsWith('h') || t.EndsWith('½');
        if (half)
        {
            t = t[..^1];
        }

        if (t.Length == 0)
        {
            seconds = half ? 30 : 0;
            return true;
        }

        if (!decimal.TryParse(t, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var minutes) || minutes < 0 || minutes > 999)
        {
            return false;
        }

        var halves = minutes * 2;
        if (halves != decimal.Truncate(halves))
        {
            return false;
        }

        seconds = (int)(halves * 30) + (half ? 30 : 0);
        return true;
    }

    public static string Number(int? value) => value is { } v ? v.ToString(CultureInfo.InvariantCulture) : string.Empty;

    /// <summary>A whole number in range, or null for an empty cell.</summary>
    public static bool TryNumber(string? text, int least, int most, out int? value)
    {
        value = null;
        var t = text?.Trim() ?? string.Empty;
        if (t.Length == 0)
        {
            return true;
        }

        if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= least && n <= most)
        {
            value = n;
            return true;
        }

        return false;
    }

    /// <summary>A trip's activities as the grid shows them: <c>N: 2B44</c>, <c>DR: 2B45</c>, <c>CC</c>.</summary>
    public static string Activities(TripNode trip) =>
        string.Join("; ", trip.Activities.Select(a =>
        {
            var code = Code(a.Code is { } c ? TrainActivity.KindFromCode(c) : ActivityKind.Unknown, a.Code);
            var train = a.AssociatedUid ?? a.AssociatedTrain;
            return train is null ? code : $"{code}: {train}";
        }));

    private static string Code(ActivityKind kind, int? code) =>
        kind switch
        {
            ActivityKind.Next => "N",
            ActivityKind.DivideRear => "DR",
            ActivityKind.DivideFront => "DF",
            ActivityKind.Join => "J",
            ActivityKind.DetachEngineRear => "DER",
            ActivityKind.DetachEngineFront => "DEF",
            ActivityKind.DropCoachesRear => "DCR",
            ActivityKind.DropCoachesFront => "DCF",
            ActivityKind.JoinedBy => "JB",
            ActivityKind.PlatformShare => "PS",
            ActivityKind.CrewChange => "CC",
            _ => "?" + Number(code),
        };

    /// <summary>The issues' text for a hover: each message, its hint and its quick fix.</summary>
    public static string IssueTip(IEnumerable<ValidationIssue> issues) =>
        string.Join("\n\n", issues.Select(i => $"{i.Severity}: {i.Message}\n{i.Hint}{(i.QuickFix is { } fix ? $"\nQuick fix: {fix.Title}" : string.Empty)}"));
}

/// <summary>A row of the editor's train list: a train of the draft, by its element.</summary>
internal sealed class DraftTrainRow(XElement element) : ObservableObject
{
    public XElement Element { get; } = element;

    public string Headcode { get; private set; } = string.Empty;

    public string Uid { get; private set; } = string.Empty;

    /// <summary>The entry or seed time, in SimSig's notation.</summary>
    public string EntryTime { get; private set; } = string.Empty;

    /// <summary>Seconds, for sorting by entry time.</summary>
    public int EntrySeconds { get; private set; }

    /// <summary>The entry point, seed point (signal) or first location.</summary>
    public string Entry { get; private set; } = string.Empty;

    public string Category { get; private set; } = string.Empty;

    public string SeedGroup { get; private set; } = string.Empty;

    public int Issues { get; private set; }

    /// <summary>The worst issue's severity, or null if none.</summary>
    public IssueSeverity? Worst { get; private set; }

    public string? IssueTip { get; private set; }

    public void Load(TrainNode train, IReadOnlyDictionary<string, string> categories, IReadOnlyList<ValidationIssue> issues)
    {
        var first = train.Trips.FirstOrDefault();
        var entry = train.DepartTime ?? first?.DepPassTime ?? first?.ArrTime;
        Set(train.Headcode ?? "(no headcode)", Headcode, v => Headcode = v, nameof(Headcode));
        Set(train.Uid ?? string.Empty, Uid, v => Uid = v, nameof(Uid));
        Set(DraftText.Time(entry), EntryTime, v => EntryTime = v, nameof(EntryTime));
        Set(entry?.Seconds ?? int.MaxValue, EntrySeconds, v => EntrySeconds = v, nameof(EntrySeconds));
        Set(train.EntryPoint ?? (train.SeedPoint is { } seed ? $"seed {seed}" : first?.Location ?? string.Empty), Entry, v => Entry = v, nameof(Entry));
        Set(train.Category is { } id ? categories.GetValueOrDefault(id, id) : string.Empty, Category, v => Category = v, nameof(Category));
        Set(train.SeedGroup ?? string.Empty, SeedGroup, v => SeedGroup = v, nameof(SeedGroup));
        Set(issues.Count, Issues, v => Issues = v, nameof(Issues));
        Set(issues.Count == 0 ? null : issues.Max(i => i.Severity), Worst, v => Worst = v, nameof(Worst));
        Set(issues.Count == 0 ? null : DraftText.IssueTip(issues), IssueTip, v => IssueTip = v, nameof(IssueTip));
    }

    /// <summary>Whether the filter's text is in the headcode, UID, entry, category or seed group.</summary>
    public bool Matches(string filter) =>
        filter.Length == 0
        || Headcode.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Uid.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Entry.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || EntryTime.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
        || SeedGroup.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private void Set<T>(T value, T current, Action<T> set, string property)
    {
        if (!EqualityComparer<T>.Default.Equals(value, current))
        {
            set(value);
            OnPropertyChanged(property);
        }
    }
}

/// <summary>A row of the Issues panel.</summary>
internal sealed record DraftIssueRow(ValidationIssue Issue, string Where)
{
    public IssueSeverity Severity => Issue.Severity;

    public string Train => Issue.Headcode ?? string.Empty;

    public string Message => Issue.Message;

    public string Hint => Issue.Hint;

    public string Code => Issue.Code;

    public string Fix => Issue.QuickFix?.Title ?? string.Empty;

    public bool HasFix => Issue.QuickFix is not null;
}

/// <summary>
/// A timing point in the trip grid. Its cells show the trip in SimSig's notation; typing in one asks the train's view
/// model to make the edit, and if it is refused, the cell shows its value again and the editor says why.
/// </summary>
internal sealed class TripRow(DraftTrainViewModel train, XElement element) : ObservableObject
{
    private readonly Dictionary<string, string> _cells = [];

    public XElement Element { get; } = element;

    public int Index { get; private set; }

    public string Location { get => Cell(nameof(Location)); set => Edit(nameof(Location), value); }

    public string Arrival { get => Cell(nameof(Arrival)); set => Edit(nameof(Arrival), value); }

    public string Departure { get => Cell(nameof(Departure)); set => Edit(nameof(Departure), value); }

    public string Platform { get => Cell(nameof(Platform)); set => Edit(nameof(Platform), value); }

    public string Line { get => Cell(nameof(Line)); set => Edit(nameof(Line), value); }

    public string Path { get => Cell(nameof(Path)); set => Edit(nameof(Path), value); }

    public string EngAllowance { get => Cell(nameof(EngAllowance)); set => Edit(nameof(EngAllowance), value); }

    public string PathAllowance { get => Cell(nameof(PathAllowance)); set => Edit(nameof(PathAllowance), value); }

    public string PerfAllowance { get => Cell(nameof(PerfAllowance)); set => Edit(nameof(PerfAllowance), value); }

    public string Dwell { get => Cell(nameof(Dwell)); set => Edit(nameof(Dwell), value); }

    public string RequestPercent { get => Cell(nameof(RequestPercent)); set => Edit(nameof(RequestPercent), value); }

    public bool SetDown { get => Cell(nameof(SetDown)) == "1"; set => Edit(nameof(SetDown), value ? "1" : string.Empty); }

    public bool ThroughLine { get => Cell(nameof(ThroughLine)) == "1"; set => Edit(nameof(ThroughLine), value ? "1" : string.Empty); }

    public string StopLocation { get => Cell(nameof(StopLocation)); set => Edit(nameof(StopLocation), value); }

    public string NewCategory { get => Cell(nameof(NewCategory)); set => Edit(nameof(NewCategory), value); }

    public string Activities { get; private set; } = string.Empty;

    /// <summary>Stringline inserted this point and it has not been edited: shown greyed.</summary>
    public bool IsAdded { get; private set; }

    public IssueSeverity? Worst { get; private set; }

    public string? IssueTip { get; private set; }

    public IReadOnlyList<CodeChoice> PlatformChoices { get; private set; } = [];

    public IReadOnlyList<CodeChoice> LineChoices { get; private set; } = [];

    public IReadOnlyList<CodeChoice> PathChoices { get; private set; } = [];

    /// <summary>The cells' names that can be typed in (for fill down).</summary>
    public static IReadOnlyList<string> EditableCells { get; } =
        [nameof(Location), nameof(Arrival), nameof(Departure), nameof(Platform), nameof(Line), nameof(Path), nameof(EngAllowance), nameof(PathAllowance),
         nameof(PerfAllowance), nameof(Dwell), nameof(RequestPercent), nameof(SetDown), nameof(ThroughLine), nameof(StopLocation), nameof(NewCategory)];

    /// <summary>A cell's text as shown (<c>1</c> for a ticked box).</summary>
    public string Cell(string name) => _cells.GetValueOrDefault(name, string.Empty);

    public void Load(int index, TripNode trip, bool added, IReadOnlyList<ValidationIssue> issues, Func<string?, (IReadOnlyList<CodeChoice> Platforms, IReadOnlyList<CodeChoice> Lines, IReadOnlyList<CodeChoice> Paths)> choices)
    {
        if (Index != index)
        {
            Index = index;
            OnPropertyChanged(nameof(Index));
        }

        Show(nameof(Location), trip.Location ?? string.Empty);
        Show(nameof(Arrival), DraftText.Time(trip.ArrTime));
        Show(nameof(Departure), DraftText.Departure(trip));
        Show(nameof(Platform), trip.Platform ?? string.Empty);
        Show(nameof(Line), trip.Line ?? string.Empty);
        Show(nameof(Path), trip.Path ?? string.Empty);
        Show(nameof(EngAllowance), DraftText.Allowance(trip.EngAllowanceSeconds));
        Show(nameof(PathAllowance), DraftText.Allowance(trip.PathAllowanceSeconds));
        Show(nameof(PerfAllowance), DraftText.Allowance(trip.PerfAllowanceSeconds));
        Show(nameof(Dwell), DraftText.Number(trip.DwellTime));
        Show(nameof(RequestPercent), DraftText.Number(trip.RequestPercent));
        Show(nameof(SetDown), trip.SetDownOnly ? "1" : string.Empty);
        Show(nameof(ThroughLine), trip.AllowStopsOnThroughLines ? "1" : string.Empty);
        Show(nameof(StopLocation), DraftText.Number(trip.StopLocation));
        Show(nameof(NewCategory), trip.NewCategory ?? string.Empty);
        Set(DraftText.Activities(trip), Activities, a => Activities = a, nameof(Activities));
        Set(added, IsAdded, a => IsAdded = a, nameof(IsAdded));
        Set(issues.Count == 0 ? null : issues.Max(i => i.Severity), Worst, w => Worst = w, nameof(Worst));
        Set(issues.Count == 0 ? null : DraftText.IssueTip(issues), IssueTip, t => IssueTip = t, nameof(IssueTip));
        var (platforms, lines, paths) = choices(trip.Location);
        (PlatformChoices, LineChoices, PathChoices) = (platforms, lines, paths);
        OnPropertyChanged(nameof(PlatformChoices));
        OnPropertyChanged(nameof(LineChoices));
        OnPropertyChanged(nameof(PathChoices));
    }

    private void Show(string name, string text)
    {
        if (_cells.GetValueOrDefault(name) != text)
        {
            _cells[name] = text;
            OnPropertyChanged(name);
        }
    }

    private void Set<T>(T value, T current, Action<T> set, string property)
    {
        if (!EqualityComparer<T>.Default.Equals(value, current))
        {
            set(value);
            OnPropertyChanged(property);
        }
    }

    private void Edit(string name, string? typed)
    {
        typed = typed?.Trim() ?? string.Empty;
        if (typed == Cell(name))
        {
            return;
        }

        if (!train.EditCell(this, name, typed))
        {
            // The grid has the typed text; tell it to read the old value back.
            OnPropertyChanged(name);
        }
    }
}
