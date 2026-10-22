using System.Collections.ObjectModel;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>
/// The selected train in the timetable editor (S43, <c>08-…md</c> §7): its details and its trip grid. Every change is
/// a <see cref="TimetableEdit"/> made through the editor, so undo, redo, the checks and the autosave see it; a value
/// that cannot be read is refused with the reason.
/// </summary>
internal sealed partial class DraftTrainViewModel : ObservableObject
{
    private readonly TimetableEditorViewModel _owner;
    private readonly Dictionary<string, string> _details = [];

    public DraftTrainViewModel(TimetableEditorViewModel owner, XElement element)
    {
        _owner = owner;
        Element = element;
    }

    /// <summary>The train's element: its identity, kept through renumbering.</summary>
    public XElement Element { get; }

    public TrainNode Node => new(_owner.Document, Element);

    public ObservableCollection<TripRow> Trips { get; } = [];

    /// <summary>Every TIPLOC known, for the location cell's completion.</summary>
    public IReadOnlyList<CodeChoice> Locations => _owner.Locations;

    /// <summary>The trip row the grid's current cell is on (set by the grid).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InsertPointCommand), nameof(InsertPointAfterCommand))]
    public partial TripRow? CurrentTrip { get; set; }

    public string Headcode { get => Detail(nameof(Headcode)); set => EditDetail(nameof(Headcode), value); }

    public string Uid { get => Detail(nameof(Uid)); set => EditDetail(nameof(Uid), value); }

    public string Description { get => Detail(nameof(Description)); set => EditDetail(nameof(Description), value); }

    /// <summary>The description SimSig shows: a <c>$template</c> filled in from the header's template.</summary>
    public string DescriptionPreview { get; private set; } = string.Empty;

    public bool UsesTemplate { get; private set; }

    public string Category { get => Detail(nameof(Category)); set => EditDetail(nameof(Category), value); }

    public IReadOnlyList<CodeChoice> CategoryChoices { get; private set; } = [];

    public string EntryPoint { get => Detail(nameof(EntryPoint)); set => EditDetail(nameof(EntryPoint), value); }

    public string DepartTime { get => Detail(nameof(DepartTime)); set => EditDetail(nameof(DepartTime), value); }

    public string SeedPoint { get => Detail(nameof(SeedPoint)); set => EditDetail(nameof(SeedPoint), value); }

    public string SeedingGap { get => Detail(nameof(SeedingGap)); set => EditDetail(nameof(SeedingGap), value); }

    public string SeedGroup { get => Detail(nameof(SeedGroup)); set => EditDetail(nameof(SeedGroup), value); }

    public string AsRequiredPercent { get => Detail(nameof(AsRequiredPercent)); set => EditDetail(nameof(AsRequiredPercent), value); }

    public string MaxSpeed { get => Detail(nameof(MaxSpeed)); set => EditDetail(nameof(MaxSpeed), value); }

    public string TrainLength { get => Detail(nameof(TrainLength)); set => EditDetail(nameof(TrainLength), value); }

    public bool IsFreight { get => Detail(nameof(IsFreight)) == "1"; set => EditDetail(nameof(IsFreight), value ? "1" : string.Empty); }

    public bool CanUseGoodsLines { get => Detail(nameof(CanUseGoodsLines)) == "1"; set => EditDetail(nameof(CanUseGoodsLines), value ? "1" : string.Empty); }

    public string OriginName { get => Detail(nameof(OriginName)); set => EditDetail(nameof(OriginName), value); }

    public string OriginTime { get => Detail(nameof(OriginTime)); set => EditDetail(nameof(OriginTime), value); }

    public string DestinationName { get => Detail(nameof(DestinationName)); set => EditDetail(nameof(DestinationName), value); }

    public string DestinationTime { get => Detail(nameof(DestinationTime)); set => EditDetail(nameof(DestinationTime), value); }

    public string OperatorCode { get => Detail(nameof(OperatorCode)); set => EditDetail(nameof(OperatorCode), value); }

    public string Notes { get => Detail(nameof(Notes)); set => EditDetail(nameof(Notes), value); }

    /// <summary>The train's issues, for the details' hover.</summary>
    public string? IssueTip { get; private set; }

    /// <summary>Shows the train as the snapshot has it, keeping the trip rows of the same elements (so the grid keeps its place).</summary>
    public void Load(DraftSnapshot snapshot)
    {
        var node = Node;
        Show(nameof(Headcode), node.Headcode);
        Show(nameof(Uid), node.Uid);
        Show(nameof(Description), node.Description);
        Show(nameof(Category), node.Category);
        Show(nameof(EntryPoint), node.EntryPoint);
        Show(nameof(DepartTime), DraftText.Time(node.DepartTime));
        Show(nameof(SeedPoint), node.SeedPoint);
        Show(nameof(SeedingGap), DraftText.Number(node.SeedingGap));
        Show(nameof(SeedGroup), node.SeedGroup);
        Show(nameof(AsRequiredPercent), DraftText.Number(node.AsRequiredPercent));
        Show(nameof(MaxSpeed), DraftText.Number(node.MaxSpeed));
        Show(nameof(TrainLength), DraftText.Number(node.TrainLength));
        Show(nameof(IsFreight), node.IsFreight ? "1" : string.Empty);
        Show(nameof(CanUseGoodsLines), node.CanUseGoodsLines ? "1" : string.Empty);
        Show(nameof(OriginName), node.OriginName);
        Show(nameof(OriginTime), DraftText.Time(node.OriginTime));
        Show(nameof(DestinationName), node.DestinationName);
        Show(nameof(DestinationTime), DraftText.Time(node.DestinationTime));
        Show(nameof(OperatorCode), node.OperatorCode);
        Show(nameof(Notes), node.Notes);

        var imported = snapshot.Import.TrainOf(node);
        UsesTemplate = string.Equals(node.Description, "$template", StringComparison.OrdinalIgnoreCase);
        DescriptionPreview = imported?.Description ?? string.Empty;
        CategoryChoices = [.. snapshot.Document.Categories.Where(c => c.Id is not null).Select(c => new CodeChoice(c.Id!, $"{c.Id}  {c.Description}"))];
        var issues = snapshot.Validation.For(Element).ToList();
        IssueTip = issues.Count == 0 ? null : DraftText.IssueTip(issues);
        OnPropertyChanged(nameof(UsesTemplate));
        OnPropertyChanged(nameof(DescriptionPreview));
        OnPropertyChanged(nameof(CategoryChoices));
        OnPropertyChanged(nameof(IssueTip));
        OnPropertyChanged(nameof(Locations));

        var trips = node.Trips.ToList();
        if (!trips.Select(t => t.Element).SequenceEqual(Trips.Select(r => r.Element)))
        {
            var rows = Trips.ToDictionary(r => r.Element);
            var current = CurrentTrip?.Element;
            Trips.Clear();
            foreach (var trip in trips)
            {
                Trips.Add(rows.GetValueOrDefault(trip.Element) ?? new TripRow(this, trip.Element));
            }

            CurrentTrip = Trips.FirstOrDefault(r => r.Element == current);
        }

        for (var i = 0; i < trips.Count; i++)
        {
            var trip = trips[i];
            var pointIssues = issues.Where(x => x.Element == trip.Element).ToList();
            Trips[i].Load(i, trip, snapshot.AddedPoints.Contains(trip.Element), pointIssues, _owner.ChoicesAt);
        }

        LoadActivities(snapshot);
    }

    /// <summary>Makes a typed cell's edit; false (and the reason in the editor's status) if it is refused.</summary>
    public bool EditCell(TripRow row, string cell, string text)
    {
        var edit = MakeCellEdit(row, cell, text, out var error);
        if (edit is null)
        {
            _owner.Refuse(error ?? "That cannot be changed here.");
            return false;
        }

        return _owner.TryApply(edit);
    }

    /// <summary>The edit a cell's text makes, or null with the reason it cannot be read.</summary>
    public TimetableEdit? MakeCellEdit(TripRow row, string cell, string text, out string? error)
    {
        error = null;
        var trip = new TripNode(_owner.Document, row.Element);
        var where = $"{Node.Headcode} at {(trip.Location ?? "the new point")}";
        switch (cell)
        {
            case nameof(TripRow.Location):
                if (text.Length == 0)
                {
                    error = "A timing point needs a location (TIPLOC); Ctrl+Delete removes the point.";
                    return null;
                }

                var code = text.ToUpperInvariant();
                return TimetableEdit.Change(trip, t => t.Location = code, $"{Node.Headcode}: location {code}");

            case nameof(TripRow.Arrival):
                if (!TryTime(text, PreviousTime(row, false), out var arrival, out error))
                {
                    return null;
                }

                if (arrival?.Mark is { } mark && mark != TimeMark.None)
                {
                    error = "An arrival takes no mark: put /, d, t, w or r in the departure.";
                    return null;
                }

                return TimetableEdit.Change(trip, t => t.ArrTime = arrival?.Time, $"{where}: arrive {(text.Length == 0 ? "(none)" : arrival!.Value.ToString())}");

            case nameof(TripRow.Departure):
                if (!TryTime(text, PreviousTime(row, true), out var departure, out error))
                {
                    return null;
                }

                return TimetableEdit.Change(trip, t =>
                {
                    t.DepPassTime = departure?.Time;
                    t.IsPassTime = departure?.Mark == TimeMark.Pass;
                    // Set down, the through line and the request have columns of their own; waiting has only its mark.
                    t.WaitForBookedTime = departure?.Mark == TimeMark.DwellWait;
                    switch (departure?.Mark)
                    {
                        case TimeMark.SetDown: t.SetDownOnly = true; break;
                        case TimeMark.ThruLine: t.AllowStopsOnThroughLines = true; break;
                        case TimeMark.Request when t.RequestPercent is null or 0: t.RequestPercent = 50; break;
                    }
                }, $"{where}: {(departure?.Mark == TimeMark.Pass ? "pass" : "depart")} {(text.Length == 0 ? "(none)" : departure!.Value.ToString())}");

            case nameof(TripRow.Platform):
                return TimetableEdit.Change(trip, t => t.Platform = NullIfEmpty(text), $"{where}: platform {Shown(text)}");
            case nameof(TripRow.Line):
                return TimetableEdit.Change(trip, t => t.Line = NullIfEmpty(text), $"{where}: line {Shown(text)}");
            case nameof(TripRow.Path):
                return TimetableEdit.Change(trip, t => t.Path = NullIfEmpty(text), $"{where}: path {Shown(text)}");

            case nameof(TripRow.EngAllowance) or nameof(TripRow.PathAllowance) or nameof(TripRow.PerfAllowance):
                if (!DraftText.TryAllowance(text, out var seconds))
                {
                    error = $"'{text}' is not an allowance: give minutes, with H for a half (1H is 1½ minutes).";
                    return null;
                }

                return cell switch
                {
                    nameof(TripRow.EngAllowance) => TimetableEdit.Change(trip, t => t.EngAllowanceSeconds = seconds, $"{where}: engineering allowance {Shown(text)}"),
                    nameof(TripRow.PathAllowance) => TimetableEdit.Change(trip, t => t.PathAllowanceSeconds = seconds, $"{where}: pathing allowance {Shown(text)}"),
                    _ => TimetableEdit.Change(trip, t => t.PerfAllowanceSeconds = seconds, $"{where}: performance allowance {Shown(text)}"),
                };

            case nameof(TripRow.Dwell):
                return Number(text, 0, 86_400, "a dwell in seconds", out error) is var (ok, dwell) && ok
                    ? TimetableEdit.Change(trip, t => t.DwellTime = dwell, $"{where}: dwell {Shown(text)}") : null;
            case nameof(TripRow.RequestPercent):
                return Number(text, 0, 100, "a request stop's chance, 0 to 100 %", out error) is var (okRequest, percent) && okRequest
                    ? TimetableEdit.Change(trip, t => t.RequestPercent = percent, $"{where}: request {Shown(text)} %") : null;
            case nameof(TripRow.StopLocation):
                return Number(text, 0, 99, "a stop location number", out error) is var (okStop, stop) && okStop
                    ? TimetableEdit.Change(trip, t => t.StopLocation = stop, $"{where}: stop location {Shown(text)}") : null;
            case nameof(TripRow.SetDown):
                return TimetableEdit.Change(trip, t => t.SetDownOnly = text.Length > 0, $"{where}: set down {(text.Length > 0 ? "only" : "off")}");
            case nameof(TripRow.ThroughLine):
                return TimetableEdit.Change(trip, t => t.AllowStopsOnThroughLines = text.Length > 0, $"{where}: stop on the through line {(text.Length > 0 ? "allowed" : "off")}");
            case nameof(TripRow.NewCategory):
                return TimetableEdit.Change(trip, t => t.NewCategory = NullIfEmpty(text), $"{where}: new category {Shown(text)}");
            default:
                error = $"The {cell} column cannot be typed in.";
                return null;
        }
    }

    /// <summary>Insert: a new timing point above the current one (at the top if there is none).</summary>
    [RelayCommand]
    private void InsertPoint() => Insert(CurrentTrip is { } row ? row.Index : 0);

    /// <summary>A new timing point below the current one (at the end if there is none).</summary>
    [RelayCommand]
    private void InsertPointAfter() => Insert(CurrentTrip is { } row ? row.Index + 1 : Trips.Count);

    /// <summary>Ctrl+Delete: removes the timing points (the selected rows, else the current one).</summary>
    public void DeletePoints(IReadOnlyList<TripRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var description = rows.Count == 1 ? $"{Node.Headcode}: remove {rows[0].Location}" : $"{Node.Headcode}: remove {rows.Count} points";
        _owner.TryApply(TimetableEdit.Composite(description, rows.Select(r => TimetableEdit.Remove(r.Element))));
    }

    /// <summary>
    /// Ctrl+D: fills a column down. With several rows selected, the first row's value goes into the others; with one,
    /// the row above's value comes into it. One edit.
    /// </summary>
    public void FillDown(string cell, IReadOnlyList<TripRow> rows)
    {
        if (!TripRow.EditableCells.Contains(cell))
        {
            _owner.Refuse("That column cannot be filled down.");
            return;
        }

        var ordered = rows.OrderBy(r => r.Index).ToList();
        if (ordered.Count == 1)
        {
            if (ordered[0].Index == 0)
            {
                _owner.Refuse("There is no row above to fill down from.");
                return;
            }

            ordered.Insert(0, Trips[ordered[0].Index - 1]);
        }

        if (ordered.Count < 2)
        {
            return;
        }

        var text = ordered[0].Cell(cell);
        var edits = new List<TimetableEdit>();
        foreach (var row in ordered.Skip(1).Where(r => r.Cell(cell) != text))
        {
            if (MakeCellEdit(row, cell, text, out var error) is not { } edit)
            {
                _owner.Refuse(error ?? "That cannot be filled down.");
                return;
            }

            edits.Add(edit);
        }

        if (edits.Count > 0)
        {
            _owner.TryApply(TimetableEdit.Composite($"{Node.Headcode}: fill {cell} down {edits.Count} rows", edits));
        }
    }

    private void Insert(int index)
    {
        var node = Node;
        var trip = new XElement("Trip", new XElement("Location", string.Empty));
        TimetableEdit edit;
        if (node.TripsElement is { } trips)
        {
            edit = TimetableEdit.Insert(trips, trip, Math.Clamp(index, 0, Trips.Count), $"{node.Headcode}: insert a point");
        }
        else
        {
            edit = TimetableEdit.Insert(Element, new XElement("Trips", trip), null, $"{node.Headcode}: insert a point");
        }

        if (_owner.TryApply(edit))
        {
            CurrentTrip = Trips.FirstOrDefault(r => r.Element == trip);
        }
    }

    /// <summary>The time a typed time follows (for <c>+3</c> and past midnight): this point's arrival for a departure, else the last time before.</summary>
    private SimTime? PreviousTime(TripRow row, bool departure)
    {
        var trips = Node.Trips.ToList();
        var at = trips.FindIndex(t => t.Element == row.Element);
        if (departure && at >= 0 && trips[at].ArrTime is { Seconds: not 0 } arrival)
        {
            return arrival;
        }

        for (var i = at - 1; i >= 0; i--)
        {
            if ((trips[i].DepPassTime ?? trips[i].ArrTime) is { Seconds: not 0 } time)
            {
                return time;
            }
        }

        return Node.DepartTime;
    }

    private static bool TryTime(string text, SimTime? previous, out NotatedTime? time, out string? error)
    {
        time = null;
        error = null;
        if (text.Length == 0)
        {
            return true;
        }

        if (SimTimeNotation.TryParse(text, previous, out var parsed))
        {
            time = parsed;
            return true;
        }

        error = $"'{text}' is not a time: type 1234 or 12:34, 1234H for a half minute, 12/34 for a pass, or +3 for 3 minutes after the time before.";
        return false;
    }

    private static (bool Ok, int? Value) Number(string text, int least, int most, string what, out string? error)
    {
        error = null;
        if (DraftText.TryNumber(text, least, most, out var value))
        {
            return (true, value);
        }

        error = $"'{text}' is not {what}.";
        return (false, null);
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static string Shown(string text) => text.Length == 0 ? "(none)" : text;

    private string Detail(string name) => _details.GetValueOrDefault(name, string.Empty);

    private void Show(string name, string? text)
    {
        text ??= string.Empty;
        if (_details.GetValueOrDefault(name) != text)
        {
            _details[name] = text;
            OnPropertyChanged(name);
        }
    }

    private void EditDetail(string name, string? typed)
    {
        typed = typed?.Trim() ?? string.Empty;
        if (typed == Detail(name))
        {
            return;
        }

        var edit = MakeDetailEdit(name, typed, out var error);
        if (edit is null)
        {
            _owner.Refuse(error ?? "That cannot be changed here.");
        }

        if (edit is null || !_owner.TryApply(edit))
        {
            OnPropertyChanged(name);
        }
    }

    private TimetableEdit? MakeDetailEdit(string name, string text, out string? error)
    {
        error = null;
        var node = Node;
        var who = node.Headcode ?? "The train";
        switch (name)
        {
            case nameof(Headcode):
                if (!Core.Headcode.TryCreate(text, out _))
                {
                    error = $"'{text}' is not a headcode.";
                    return null;
                }

                return TimetableEdit.Renumber(_owner.Document, node, text);

            case nameof(Uid):
                return text.Length > 0 && node.Headcode is { } headcode && Core.Headcode.TryCreate(headcode, out _)
                    ? TimetableEdit.Renumber(_owner.Document, node, headcode, text)
                    : TimetableEdit.Change(node, t => t.Uid = NullIfEmpty(text), $"{who}: UID {Shown(text)}");

            case nameof(DepartTime) or nameof(OriginTime) or nameof(DestinationTime):
                if (!TryTime(text, null, out var time, out error))
                {
                    return null;
                }

                return name switch
                {
                    nameof(DepartTime) => TimetableEdit.Change(node, t => t.DepartTime = time?.Time, $"{who}: entry time {Shown(text)}"),
                    nameof(OriginTime) => TimetableEdit.Change(node, t => t.OriginTime = time?.Time, $"{who}: origin time {Shown(text)}"),
                    _ => TimetableEdit.Change(node, t => t.DestinationTime = time?.Time, $"{who}: destination time {Shown(text)}"),
                };

            case nameof(SeedingGap) or nameof(AsRequiredPercent) or nameof(MaxSpeed) or nameof(TrainLength):
                var (least, most, what) = name switch
                {
                    nameof(SeedingGap) => (0, 9_999, "a seeding gap in metres"),
                    nameof(AsRequiredPercent) => (0, 100, "a percentage"),
                    nameof(MaxSpeed) => (0, 999, "a speed in mph"),
                    _ => (0, 9_999, "a length in metres"),
                };
                if (Number(text, least, most, what, out error) is not (true, var value))
                {
                    return null;
                }

                return name switch
                {
                    nameof(SeedingGap) => TimetableEdit.Change(node, t => t.SeedingGap = value, $"{who}: seeding gap {Shown(text)}"),
                    nameof(AsRequiredPercent) => TimetableEdit.Change(node, t =>
                    {
                        t.AsRequiredPercent = value;
                        t.AsRequired = value is > 0;
                    }, $"{who}: as required {Shown(text)} %"),
                    nameof(MaxSpeed) => TimetableEdit.Change(node, t => t.MaxSpeed = value, $"{who}: maximum speed {Shown(text)}"),
                    _ => TimetableEdit.Change(node, t => t.TrainLength = value, $"{who}: length {Shown(text)}"),
                };

            case nameof(IsFreight):
                return TimetableEdit.Change(node, t => t.IsFreight = text.Length > 0, $"{who}: {(text.Length > 0 ? "freight" : "not freight")}");
            case nameof(CanUseGoodsLines):
                return TimetableEdit.Change(node, t => t.CanUseGoodsLines = text.Length > 0, $"{who}: {(text.Length > 0 ? "may use goods lines" : "no goods lines")}");
            case nameof(Description):
                return TimetableEdit.Change(node, t => t.Description = NullIfEmpty(text), $"{who}: description");
            case nameof(Category):
                var category = CategoryChoices.FirstOrDefault(c => string.Equals(c.Label, text, StringComparison.Ordinal))?.Code ?? text;
                return TimetableEdit.Change(node, t => t.Category = NullIfEmpty(category), $"{who}: category {Shown(category)}");
            case nameof(EntryPoint):
                return TimetableEdit.Change(node, t => t.EntryPoint = NullIfEmpty(text), $"{who}: entry point {Shown(text)}");
            case nameof(SeedPoint):
                return TimetableEdit.Change(node, t => t.SeedPoint = NullIfEmpty(text), $"{who}: seed point {Shown(text)}");
            case nameof(SeedGroup):
                return TimetableEdit.Change(node, t => t.SeedGroup = NullIfEmpty(text), $"{who}: seed group {Shown(text)}");
            case nameof(OriginName):
                return TimetableEdit.Change(node, t => t.OriginName = NullIfEmpty(text), $"{who}: origin {Shown(text)}");
            case nameof(DestinationName):
                return TimetableEdit.Change(node, t => t.DestinationName = NullIfEmpty(text), $"{who}: destination {Shown(text)}");
            case nameof(OperatorCode):
                return TimetableEdit.Change(node, t => t.OperatorCode = NullIfEmpty(text), $"{who}: operator {Shown(text)}");
            case nameof(Notes):
                return TimetableEdit.Change(node, t => t.Notes = NullIfEmpty(text), $"{who}: notes");
            default:
                error = $"{name} cannot be changed here.";
                return null;
        }
    }
}
