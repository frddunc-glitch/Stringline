using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Core.Timetables;
using Stringline.Editing.Drafts;
using Stringline.Editing.Validation;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>A train offered in a list: its element and "1A23  10:15 WLOO".</summary>
internal sealed record TrainChoice(XElement Element, string Headcode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A row of one of the parts tabs (S45): shows the document, and makes an edit when a cell is typed in.</summary>
internal abstract class DraftPartRow(TimetableEditorViewModel owner, XElement element) : ObservableObject
{
    private readonly Dictionary<string, string> _cells = [];

    protected TimetableEditorViewModel Owner { get; } = owner;

    public XElement Element { get; } = element;

    /// <summary>The row's issues (from the checks), for its mark and hover.</summary>
    public string? IssueTip { get; private set; }

    public IssueSeverity? Worst { get; private set; }

    protected string Cell(string name) => _cells.GetValueOrDefault(name, string.Empty);

    protected void Show(string name, string? text)
    {
        text ??= string.Empty;
        if (_cells.GetValueOrDefault(name) != text)
        {
            _cells[name] = text;
            OnPropertyChanged(name);
        }
    }

    /// <summary>Makes the edit for a typed cell; if it is refused or there is none, the cell shows its value again.</summary>
    protected void Edit(string name, string? typed, Func<string, TimetableEdit?> edit)
    {
        typed = typed?.Trim() ?? string.Empty;
        if (typed == Cell(name))
        {
            return;
        }

        TimetableEdit? made;
        try
        {
            made = edit(typed);
        }
        catch (InvalidOperationException ex)
        {
            Owner.Refuse(ex.Message);
            made = null;
        }

        if (made is null || !Owner.TryApply(made))
        {
            OnPropertyChanged(name);
        }
    }

    public void ShowIssues(IReadOnlyList<ValidationIssue> issues)
    {
        var tip = issues.Count == 0 ? null : DraftText.IssueTip(issues);
        var worst = issues.Count == 0 ? (IssueSeverity?)null : issues.Max(i => i.Severity);
        if (tip != IssueTip || worst != Worst)
        {
            (IssueTip, Worst) = (tip, worst);
            OnPropertyChanged(nameof(IssueTip));
            OnPropertyChanged(nameof(Worst));
        }
    }
}

// ---- Activities (on the Train tab) ----

/// <summary>An activity of the trip the grid is on.</summary>
internal sealed class ActivityRow(TimetableEditorViewModel owner, XElement element) : DraftPartRow(owner, element)
{
    public ActivityNode Node => new(Owner.Document, Element);

    public ActivityKind Kind => Node.Code is { } code && Enum.IsDefined((ActivityKind)code) ? (ActivityKind)code : ActivityKind.Unknown;

    /// <summary>The kind by name; picking another changes it.</summary>
    public string KindName
    {
        get => Cell(nameof(KindName));
        set => Edit(nameof(KindName), value, typed =>
            DraftParts.ActivityKinds.FirstOrDefault(k => TimetableParts.Name(k) == typed) is var kind && kind != ActivityKind.Unknown
                ? TimetableParts.ChangeActivity(Node, a => a.Code = (int)kind, $"Activity: {typed}")
                : null);
    }

    /// <summary>The train it is with, by headcode or UID as the file has it.</summary>
    public string With
    {
        get => Cell(nameof(With));
        set => Edit(nameof(With), value, typed =>
        {
            var train = Owner.FindTrain(typed) ?? throw new InvalidOperationException($"There is no train {typed} in the timetable.");
            var (headcode, uid) = TimetableParts.NameOf(Owner.Document, train);
            return TimetableParts.ChangeActivity(Node, a => (a.AssociatedTrain, a.AssociatedUid) = (headcode, uid), $"Activity with {train.Headcode}");
        });
    }

    /// <summary>A crew change's time, as the grid types times.</summary>
    public string CrewTime
    {
        get => Cell(nameof(CrewTime));
        set => Edit(nameof(CrewTime), value, typed =>
            typed.Length == 0 ? TimetableParts.ChangeActivity(Node, a => a.CrewChangeManualTime = null, "Crew change: no time")
            : SimTimeNotation.TryParse(typed, null, out var time) && time.Mark == TimeMark.None
                ? TimetableParts.ChangeActivity(Node, a => a.CrewChangeManualTime = time.Time, $"Crew change at {time}")
                : throw new InvalidOperationException($"'{typed}' is not a time."));
    }

    /// <summary>How long it takes, in seconds; blank for the category's dwell.</summary>
    public string Duration
    {
        get => Cell(nameof(Duration));
        set => Edit(nameof(Duration), value, typed =>
            DraftText.TryNumber(typed, 0, 86_400, out var seconds)
                ? TimetableParts.ChangeActivity(Node, a => a.ActivityDuration = seconds is > 0 ? seconds : null, "Activity duration")
                : throw new InvalidOperationException("The duration is a number of seconds."));
    }

    public bool NamesATrain => TimetableParts.NamesATrain(Kind);

    public void Load()
    {
        var node = Node;
        Show(nameof(KindName), TimetableParts.Name(Kind));
        Show(nameof(With), node.AssociatedTrain ?? node.AssociatedUid);
        Show(nameof(CrewTime), DraftText.Time(node.CrewChangeManualTime));
        Show(nameof(Duration), DraftText.Number(node.ActivityDuration));
        OnPropertyChanged(nameof(NamesATrain));
    }
}

/// <summary>Shared lists for the parts tabs.</summary>
internal static class DraftParts
{
    public static IReadOnlyList<ActivityKind> ActivityKinds { get; } = [.. Enum.GetValues<ActivityKind>().Where(k => k != ActivityKind.Unknown)];

    public static IReadOnlyList<string> ActivityNames { get; } = [.. ActivityKinds.Select(TimetableParts.Name)];

    public static IReadOnlyList<RuleKind> RuleKinds { get; } = [.. Enum.GetValues<RuleKind>().Where(k => k != RuleKind.Unknown)];

    public static IReadOnlyList<string> RuleWordings { get; } = [.. RuleKinds.Select(TimetableParts.Wording)];

    /// <summary>A rule's time as minutes with <c>H</c> for a half (<c>0H</c>, <c>25</c>), as SimSig's editor shows it.</summary>
    public static string Minutes(int? seconds) => seconds is > 0 ? DraftText.Allowance(seconds.Value) : string.Empty;
}

// ---- Rules ----

/// <summary>A rule: kind, X, Y, Z and its time.</summary>
internal sealed class RuleRow(TimetableEditorViewModel owner, XElement element) : DraftPartRow(owner, element)
{
    public RuleNode Node => new(Owner.Document, Element);

    public string Kind
    {
        get => Cell(nameof(Kind));
        set => Edit(nameof(Kind), value, typed =>
        {
            var index = DraftParts.RuleWordings.ToList().IndexOf(typed);
            return index < 0 ? null : TimetableEdit.Change(Node, r => r.Code = (int)DraftParts.RuleKinds[index], $"Rule: {typed}");
        });
    }

    public string X { get => Cell(nameof(X)); set => Edit(nameof(X), value, typed => SetTrain(typed, second: false)); }

    public string Y { get => Cell(nameof(Y)); set => Edit(nameof(Y), value, typed => SetTrain(typed, second: true)); }

    /// <summary>The location Z, for the rules that have one.</summary>
    public string Location
    {
        get => Cell(nameof(Location));
        set => Edit(nameof(Location), value, typed => TimetableEdit.Change(Node, r => r.Location = typed.Length == 0 ? null : typed.ToUpperInvariant(), $"Rule at {typed}"));
    }

    /// <summary>Minutes after Y's event, <c>H</c> for a half.</summary>
    public string Minutes
    {
        get => Cell(nameof(Minutes));
        set => Edit(nameof(Minutes), value, typed =>
            DraftText.TryAllowance(typed, out var seconds)
                ? TimetableEdit.Change(Node, r => r.Time = seconds > 0 ? seconds : null, $"Rule time {typed}")
                : throw new InvalidOperationException("The time is minutes, with H for a half: 0H, 2, 25."));
    }

    /// <summary>True if the rule names a train among <paramref name="train"/>'s names.</summary>
    public bool Names(TrainNode train) =>
        new[] { Node.Train, Node.TrainUid, Node.Train2, Node.Train2Uid }.Any(n => n is not null && (n == train.Headcode || n == train.Uid));

    public void Load()
    {
        var node = Node;
        var kind = node.Code is { } code && Enum.IsDefined((RuleKind)code) ? (RuleKind)code : RuleKind.Unknown;
        Show(nameof(Kind), kind == RuleKind.Unknown ? $"Unknown rule {node.Code}" : TimetableParts.Wording(kind));
        Show(nameof(X), node.TrainUid ?? node.Train);
        Show(nameof(Y), node.Train2Uid ?? node.Train2);
        Show(nameof(Location), node.Location);
        Show(nameof(Minutes), DraftParts.Minutes(node.Time));
    }

    private TimetableEdit SetTrain(string typed, bool second) =>
        TimetableParts.SetRuleTrain(Node, second, Owner.FindTrain(typed) ?? throw new InvalidOperationException($"There is no train {typed} in the timetable."));
}

/// <summary>The Rules tab (S45): every rule, or those naming the selected train; add and delete.</summary>
internal sealed partial class DraftRulesViewModel(TimetableEditorViewModel owner) : ObservableObject
{
    private readonly Dictionary<XElement, RuleRow> _rows = [];

    public ObservableCollection<RuleRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial RuleRow? Selected { get; set; }

    /// <summary>Only the rules that name the train selected in the list.</summary>
    [ObservableProperty]
    public partial bool ThisTrainOnly { get; set; }

    public IReadOnlyList<string> Kinds { get; } = DraftParts.RuleWordings;

    [ObservableProperty]
    public partial string NewKind { get; set; } = DraftParts.RuleWordings[1];

    [ObservableProperty]
    public partial string NewX { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewY { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewMinutes { get; set; } = string.Empty;

    public IReadOnlyList<TrainChoice> TrainChoices => owner.TrainChoices;

    partial void OnThisTrainOnlyChanged(bool value) => Load(owner.Snapshot);

    [RelayCommand]
    private void Add()
    {
        try
        {
            var kind = DraftParts.RuleKinds[Math.Max(0, DraftParts.RuleWordings.ToList().IndexOf(NewKind))];
            var x = owner.FindTrain(NewX) ?? throw new InvalidOperationException($"Train X: there is no train '{NewX}'.");
            var y = owner.FindTrain(NewY) ?? throw new InvalidOperationException($"Train Y: there is no train '{NewY}'.");
            if (!DraftText.TryAllowance(NewMinutes, out var seconds))
            {
                throw new InvalidOperationException("The time is minutes, with H for a half: 0H, 2, 25.");
            }

            if (owner.TryApply(TimetableParts.AddRule(owner.Document, kind, x, y, NewLocation, seconds)))
            {
                Selected = Rows.LastOrDefault();
            }
        }
        catch (InvalidOperationException ex)
        {
            owner.Refuse(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Remove()
    {
        if (Selected is { } row)
        {
            owner.TryApply(TimetableParts.RemoveRule(row.Node));
        }
    }

    private bool HasSelected() => Selected is not null;

    public void Load(DraftSnapshot snapshot)
    {
        var train = owner.Train?.Node;
        var issues = snapshot.Validation.Issues.Where(i => i.Element is not null).ToLookup(i => i.Element!);
        var rows = new List<RuleRow>();
        foreach (var rule in snapshot.Document.Rules)
        {
            if (!_rows.TryGetValue(rule.Element, out var row))
            {
                _rows[rule.Element] = row = new RuleRow(owner, rule.Element);
            }

            row.Load();
            row.ShowIssues([.. issues[rule.Element]]);
            if (!ThisTrainOnly || train is null || row.Names(train))
            {
                rows.Add(row);
            }
        }

        foreach (var gone in _rows.Keys.Where(e => e.Parent is null).ToList())
        {
            _rows.Remove(gone);
        }

        if (!rows.SequenceEqual(Rows))
        {
            var selected = Selected;
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Selected = selected is not null && Rows.Contains(selected) ? selected : null;
        }

        OnPropertyChanged(nameof(TrainChoices));
    }
}

// ---- Seed groups ----

/// <summary>A seed group: title, start time, its trains and any seed points two of them share.</summary>
internal sealed class SeedGroupRow(TimetableEditorViewModel owner, XElement element) : DraftPartRow(owner, element)
{
    public SeedGroupNode Node => new(Owner.Document, Element);

    public string Title { get => Cell(nameof(Title)); set => Edit(nameof(Title), value, typed => TimetableParts.ChangeSeedGroup(Node, typed, null)); }

    public string Start
    {
        get => Cell(nameof(Start));
        set => Edit(nameof(Start), value, typed =>
            SimTimeNotation.TryParse(typed, null, out var time) && time.Mark == TimeMark.None
                ? TimetableParts.ChangeSeedGroup(Node, null, time.Time)
                : throw new InvalidOperationException($"'{typed}' is not a time: type 1200 or 12:00."));
    }

    public string Id => Cell(nameof(Id));

    /// <summary>"12 trains: 1A01, 2B02…" (referenced by).</summary>
    public string Trains => Cell(nameof(Trains));

    /// <summary>Seed points two or more of its trains share: SimSig puts one train at a signal.</summary>
    public string Clashes => Cell(nameof(Clashes));

    public void Load()
    {
        var node = Node;
        Show(nameof(Title), node.Title);
        Show(nameof(Start), DraftText.Time(node.StartTime));
        Show(nameof(Id), node.Id);
        Show(nameof(Trains), DraftParts2.TrainList(TimetableParts.TrainsIn(Owner.Document, node).ToList()));
        Show(nameof(Clashes), DraftParts2.ClashText(TimetableParts.SeedClashes(Owner.Document, node)));
    }
}

internal static class DraftParts2
{
    public static string TrainList(IReadOnlyList<TrainNode> trains) =>
        trains.Count == 0 ? "none"
        : string.Create(CultureInfo.CurrentCulture, $"{trains.Count} train{(trains.Count == 1 ? "" : "s")}: {string.Join(", ", trains.Take(12).Select(t => t.Headcode))}{(trains.Count > 12 ? ", …" : "")}");

    public static string ClashText(IReadOnlyList<(string SeedPoint, IReadOnlyList<TrainNode> Trains)> clashes) =>
        string.Join("; ", clashes.Select(c => $"{c.SeedPoint}: {string.Join(", ", c.Trains.Select(t => t.Headcode))}"));
}

/// <summary>The Seed groups tab (S45).</summary>
internal sealed partial class DraftSeedGroupsViewModel(TimetableEditorViewModel owner, IDialogService dialogs) : ObservableObject
{
    private readonly Dictionary<XElement, SeedGroupRow> _rows = [];

    public ObservableCollection<SeedGroupRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial SeedGroupRow? Selected { get; set; }

    /// <summary>The trains seeded at the timetable's start (in no group), and their clashes.</summary>
    [ObservableProperty]
    public partial string AtStart { get; private set; } = string.Empty;

    [RelayCommand]
    private void Add()
    {
        var prompt = new PromptViewModel(
            "New seed group",
            "Another time the sim can start at. Seed trains in it by giving them its ID on the Train tab.",
            new PromptField("Title", "Midday start"),
            new PromptField("Start time", "12:00"))
        {
            Validate = p => string.IsNullOrWhiteSpace(p[0]) ? "Give it a title."
                : SimTimeNotation.TryParse(p[1], null, out var t) && t.Mark == TimeMark.None ? null : "Type the start time as 1200 or 12:00.",
        };
        if (!dialogs.Prompt(prompt))
        {
            return;
        }

        SimTimeNotation.TryParse(prompt[1], null, out var start);
        if (owner.TryApply(TimetableParts.AddSeedGroup(owner.Document, prompt[0], start.Time)))
        {
            Selected = Rows.LastOrDefault();
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Remove()
    {
        if (Selected is not { } row)
        {
            return;
        }

        var trains = TimetableParts.TrainsIn(owner.Document, row.Node).ToList();
        if (trains.Count > 0 && dialogs.Choose(
                "Delete seed group",
                $"{row.Title} seeds {DraftParts2.TrainList(trains)}. Deleting it seeds them at the timetable's start instead.",
                ["Delete and seed them at the start", "Cancel"]) != 0)
        {
            return;
        }

        owner.TryApply(TimetableParts.RemoveSeedGroup(row.Node));
    }

    private bool HasSelected() => Selected is not null;

    public void Load(DraftSnapshot snapshot)
    {
        var rows = new List<SeedGroupRow>();
        foreach (var group in snapshot.Document.SeedGroups)
        {
            if (!_rows.TryGetValue(group.Element, out var row))
            {
                _rows[group.Element] = row = new SeedGroupRow(owner, group.Element);
            }

            row.Load();
            rows.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(e => e.Parent is null).ToList())
        {
            _rows.Remove(gone);
        }

        if (!rows.SequenceEqual(Rows))
        {
            var selected = Selected;
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Selected = selected is not null && Rows.Contains(selected) ? selected : null;
        }

        var clashes = DraftParts2.ClashText(TimetableParts.SeedClashes(snapshot.Document, null));
        AtStart = $"Seeded at the start ({DraftText.Time(snapshot.Document.Header.StartTime)}): {DraftParts2.TrainList([.. TimetableParts.SeededAtStart(snapshot.Document)])}"
            + (clashes.Length == 0 ? string.Empty : $". Sharing a seed point: {clashes}");
    }
}

// ---- Categories ----

/// <summary>A speed class bit as a tick box.</summary>
internal sealed class SpeedClassBit(DraftCategoryRow row, int bit) : ObservableObject
{
    public int Bit { get; } = bit;

    public bool IsSet
    {
        get => row.HasBit(Bit);
        set => row.SetBit(Bit, value);
    }

    public void Refresh() => OnPropertyChanged(nameof(IsSet));
}

/// <summary>A category (train type) and its dwell times.</summary>
internal sealed class DraftCategoryRow : DraftPartRow
{
    public static IReadOnlyList<string> DwellKinds { get; } =
        ["RedSignalMoveOff", "StationForward", "StationReverse", "TerminateForward", "TerminateReverse", "Join", "Divide", "CrewChange"];

    public DraftCategoryRow(TimetableEditorViewModel owner, XElement element)
        : base(owner, element)
    {
        Bits = [.. Enumerable.Range(1, 32).Select(b => new SpeedClassBit(this, b))];
    }

    public CategoryNode Node => new(Owner.Document, Element);

    public string Id { get => Cell(nameof(Id)); set => Edit(nameof(Id), value, typed => TimetableParts.RenameCategory(Node, typed)); }

    public string Description { get => Cell(nameof(Description)); set => Edit(nameof(Description), value, typed => Change(c => c.Description = typed, "description")); }

    public bool IsFreight { get => Cell(nameof(IsFreight)) == "1"; set => Edit(nameof(IsFreight), value ? "1" : "", typed => Change(c => c.IsFreight = typed == "1", "freight")); }

    public bool CanUseGoodsLines { get => Cell(nameof(CanUseGoodsLines)) == "1"; set => Edit(nameof(CanUseGoodsLines), value ? "1" : "", typed => Change(c => c.CanUseGoodsLines = typed == "1", "goods lines")); }

    public string MaxSpeed { get => Cell(nameof(MaxSpeed)); set => Edit(nameof(MaxSpeed), value, typed => Number(typed, 1, 300, v => Change(c => c.MaxSpeed = v, "max speed"))); }

    public string TrainLength { get => Cell(nameof(TrainLength)); set => Edit(nameof(TrainLength), value, typed => Number(typed, 1, 5000, v => Change(c => c.TrainLength = v, "length"))); }

    public string AccelBrake { get => Cell(nameof(AccelBrake)); set => Edit(nameof(AccelBrake), value, typed => Number(typed, 0, 99, v => Change(c => c.AccelBrakeIndex = v, "acceleration and braking"))); }

    public string PowerToWeight { get => Cell(nameof(PowerToWeight)); set => Edit(nameof(PowerToWeight), value, typed => Number(typed, 0, 99, v => Change(c => c.PowerToWeightCategory = v, "power to weight"))); }

    /// <summary>The electrification letters the train can use (as the file writes them, e.g. <c>D</c>, <c>O</c>, <c>V</c>).</summary>
    public string Electrification { get => Cell(nameof(Electrification)); set => Edit(nameof(Electrification), value, typed => Change(c => c.Electrification = typed, "electrification")); }

    /// <summary>The caution speed set by name (#31); <see cref="CautionSetChoice.Standard"/> for SimSig's standard rules.</summary>
    public string CautionSpeedSet
    {
        get => Cell(nameof(CautionSpeedSet));
        set => Edit(nameof(CautionSpeedSet), value, typed =>
        {
            var sets = DraftCategoriesViewModel.CautionSetChoicesOf(Owner.Document);
            var set = sets.FirstOrDefault(s => string.Equals(s.Name, typed, StringComparison.Ordinal))
                ?? sets.FirstOrDefault(s => string.Equals(s.Id, typed, StringComparison.OrdinalIgnoreCase))
                ?? (typed.Length == 0 ? sets[0] : throw new InvalidOperationException($"There is no caution speed set '{typed}': make it on Lists ▸ Driving."));
            return Change(c => c.CautionSpeedSet = set.Id, $"caution speed set {set.Name}");
        });
    }

    /// <summary>"used by 12 trains".</summary>
    public string UsedBy => Cell(nameof(UsedBy));

    public string SpeedClassText => Cell(nameof(SpeedClassText));

    public IReadOnlyList<SpeedClassBit> Bits { get; }

    public string this[string dwell]
    {
        get => Cell("Dwell" + dwell);
        set => Edit("Dwell" + dwell, value, typed => Number(typed, 0, 86_400, v => Change(c => c.SetDwell(dwell, v), $"{dwell} dwell")));
    }

    public bool HasBit(int bit) => TimetableParts.SpeedClassBits(Node.SpeedClass).Contains(bit);

    public void SetBit(int bit, bool set)
    {
        if (HasBit(bit) != set)
        {
            var value = TimetableParts.WithSpeedClassBit(Node.SpeedClass, bit, set);
            Owner.TryApply(Change(c => c.SpeedClass = value, $"speed class {(set ? "+" : "−")}{bit}"));
        }
    }

    public void Load()
    {
        var node = Node;
        Show(nameof(Id), node.Id);
        Show(nameof(Description), node.Description);
        Show(nameof(IsFreight), node.IsFreight ? "1" : "");
        Show(nameof(CanUseGoodsLines), node.CanUseGoodsLines ? "1" : "");
        Show(nameof(MaxSpeed), DraftText.Number(node.MaxSpeed));
        Show(nameof(TrainLength), DraftText.Number(node.TrainLength));
        Show(nameof(AccelBrake), DraftText.Number(node.AccelBrakeIndex));
        Show(nameof(PowerToWeight), DraftText.Number(node.PowerToWeightCategory));
        Show(nameof(Electrification), node.Electrification);
        Show(nameof(CautionSpeedSet), DraftCategoriesViewModel.CautionSetChoicesOf(Owner.Document).FirstOrDefault(s => string.Equals(s.Id, node.CautionSpeedSet ?? string.Empty, StringComparison.OrdinalIgnoreCase))?.Name
            ?? node.CautionSpeedSet);
        var users = node.Id is { } id ? TimetableParts.TrainsOf(Owner.Document, id).Count() : 0;
        Show(nameof(UsedBy), users.ToString(CultureInfo.CurrentCulture));
        Show(nameof(SpeedClassText), string.Join(", ", TimetableParts.SpeedClassBits(node.SpeedClass)));
        foreach (var dwell in DwellKinds)
        {
            var text = DraftText.Number(node.GetDwell(dwell));
            if (Cell("Dwell" + dwell) != text)
            {
                Show("Dwell" + dwell, text);
                OnPropertyChanged("Item[]");
            }
        }

        foreach (var bit in Bits)
        {
            bit.Refresh();
        }
    }

    private TimetableEdit Change(Action<CategoryNode> change, string what) =>
        TimetableEdit.Change(Node, change, $"Category {Node.Description ?? Node.Id}: {what}");

    private static TimetableEdit Number(string typed, int least, int most, Func<int?, TimetableEdit> edit) =>
        DraftText.TryNumber(typed, least, most, out var value) ? edit(value)
        : throw new InvalidOperationException(string.Create(CultureInfo.CurrentCulture, $"Type a whole number from {least} to {most}."));
}

/// <summary>The Categories tab (S45): every train type, with how many trains use it.</summary>
internal sealed partial class DraftCategoriesViewModel(TimetableEditorViewModel owner, IDialogService dialogs) : ObservableObject
{
    private readonly Dictionary<XElement, DraftCategoryRow> _rows = [];

    public ObservableCollection<DraftCategoryRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial DraftCategoryRow? Selected { get; set; }

    public IReadOnlyList<string> DwellKinds { get; } = DraftCategoryRow.DwellKinds;

    /// <summary>The names a category's caution speed set can take: SimSig's standard rules, then each set.</summary>
    public IReadOnlyList<string> CautionSetNames { get; private set; } = [CautionSetChoice.Standard];

    /// <summary>SimSig's standard rules (no set), then the timetable's caution speed sets by name.</summary>
    public static IReadOnlyList<CautionSetChoice> CautionSetChoicesOf(WttDocument document) =>
    [
        new(string.Empty, CautionSetChoice.Standard),
        .. (document.CautionSpeedSets?.Items ?? []).Select(i => (Id: (string?)i.Element.Attribute("ID"), Name: i.GetText("Name")))
            .Where(s => s.Id is not null)
            .Select(s => new CautionSetChoice(s.Id!, string.IsNullOrWhiteSpace(s.Name) ? s.Id! : s.Name!)),
    ];

    /// <summary>A new category, a copy of the selected one if any.</summary>
    [RelayCommand]
    private void Add()
    {
        var from = Selected?.Node;
        var prompt = new PromptViewModel(
            "New category",
            from is null ? "A new train type with SimSig's usual values." : $"A copy of {from.Description}, with a new ID.",
            new PromptField("Description", from is null ? string.Empty : $"{from.Description} (copy)"))
        {
            Validate = p => string.IsNullOrWhiteSpace(p[0]) ? "Describe the train type." : null,
        };
        if (dialogs.Prompt(prompt) && owner.TryApply(TimetableParts.AddCategory(owner.Document, prompt[0], from)))
        {
            Selected = Rows.LastOrDefault();
        }
    }

    /// <summary>Deletes the selected category; if trains use it, asks which category they move to.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Remove()
    {
        if (Selected is not { } row)
        {
            return;
        }

        var node = row.Node;
        var users = node.Id is { } id ? TimetableParts.TrainsOf(owner.Document, id).ToList() : [];
        CategoryNode? moveTo = null;
        if (users.Count > 0)
        {
            var others = owner.Document.Categories.Where(c => c.Element != node.Element).ToList();
            if (others.Count == 0)
            {
                owner.Refuse($"{DraftParts2.TrainList(users)} use {node.Description}, and there is no other category to move them to.");
                return;
            }

            var prompt = new PromptViewModel(
                "Delete category",
                $"{node.Description} is used by {DraftParts2.TrainList(users)}. Move them to:",
                new PromptField("Category", others[0].Description ?? others[0].Id ?? string.Empty, [.. others.Select(c => c.Description ?? c.Id ?? string.Empty)]))
            {
                AcceptText = "Move and delete",
            };
            if (!dialogs.Prompt(prompt))
            {
                return;
            }

            moveTo = others.FirstOrDefault(c => (c.Description ?? c.Id) == prompt[0]);
            if (moveTo is null)
            {
                owner.Refuse($"There is no category '{prompt[0]}'.");
                return;
            }
        }

        owner.TryApply(TimetableParts.RemoveCategory(node, moveTo));
    }

    private bool HasSelected() => Selected is not null;

    public void Load(DraftSnapshot snapshot)
    {
        var names = CautionSetChoicesOf(snapshot.Document).Select(c => c.Name).ToList();
        if (!names.SequenceEqual(CautionSetNames))
        {
            CautionSetNames = names;
            OnPropertyChanged(nameof(CautionSetNames));
        }

        var rows = new List<DraftCategoryRow>();
        foreach (var category in snapshot.Document.Categories)
        {
            if (!_rows.TryGetValue(category.Element, out var row))
            {
                _rows[category.Element] = row = new DraftCategoryRow(owner, category.Element);
            }

            row.Load();
            rows.Add(row);
        }

        foreach (var gone in _rows.Keys.Where(e => e.Parent is null).ToList())
        {
            _rows.Remove(gone);
        }

        if (!rows.SequenceEqual(Rows))
        {
            var selected = Selected;
            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Selected = selected is not null && Rows.Contains(selected) ? selected : null;
        }
    }
}

// ---- Lists: Decisions, Class of Service, Scenario options, Driving ----

/// <summary>An item of a list, edited by field name (<c>@ID</c> for its ID attribute).</summary>
internal sealed class ListItemRow(TimetableEditorViewModel owner, XElement element) : DraftPartRow(owner, element)
{
    public ElementNode Node => new(Owner.Document, Element);

    public string this[string field]
    {
        get => Cell(field);
        set => Edit(field, value, typed => TimetableParts.SetItemField(Node, field, typed));
    }

    public void Load(IEnumerable<string> fields)
    {
        foreach (var field in fields)
        {
            var text = field.StartsWith('@') ? (string?)Element.Attribute(field[1..]) : Node.GetText(field);
            if (Cell(field) != (text ?? string.Empty))
            {
                Show(field, text);
                OnPropertyChanged("Item[]");
            }
        }
    }
}

/// <summary>
/// A rule of a caution speed set (#31): the aspect passed, the line speeds it applies between, the speed to reduce to
/// now and to approach the next signal at (each absolute, a percentage of line speed, or not applicable), and how far
/// from the next signal the approach speed applies.
/// </summary>
internal sealed class CautionSpeedRow(TimetableEditorViewModel owner, XElement element) : DraftPartRow(owner, element)
{
    public ElementNode Node => new(Owner.Document, Element);

    /// <summary>R, sub, Y, FY, YY, FYY or G.</summary>
    public string Aspect { get => Cell(nameof(Aspect)); set => Edit(nameof(Aspect), value, typed => Pick("AspectPassed", typed, TimetableParts.CautionAspects, "an aspect")); }

    public string FromLineSpeed { get => Cell(nameof(FromLineSpeed)); set => Edit(nameof(FromLineSpeed), value, typed => Number("FromLineSpeed", typed, 999, "a line speed in mph")); }

    /// <summary>0 for no upper limit.</summary>
    public string ToLineSpeed { get => Cell(nameof(ToLineSpeed)); set => Edit(nameof(ToLineSpeed), value, typed => Number("ToLineSpeed", typed, 999, "a line speed in mph (0 for no limit)")); }

    public string ReduceNow { get => Cell(nameof(ReduceNow)); set => Edit(nameof(ReduceNow), value, typed => Number("ReduceNowValue", typed, 999, "a speed or percentage")); }

    public string NowType { get => Cell(nameof(NowType)); set => Edit(nameof(NowType), value, typed => Pick("NowValueType", typed, TimetableParts.CautionValueTypes, "a kind of value")); }

    public string ApproachNext { get => Cell(nameof(ApproachNext)); set => Edit(nameof(ApproachNext), value, typed => Number("ApproachNextValue", typed, 999, "a speed or percentage")); }

    public string NextType { get => Cell(nameof(NextType)); set => Edit(nameof(NextType), value, typed => Pick("NextValueType", typed, TimetableParts.CautionValueTypes, "a kind of value")); }

    public string Distance { get => Cell(nameof(Distance)); set => Edit(nameof(Distance), value, typed => Number("ApproachNextDistance", typed, 99_999, "a distance in metres")); }

    public void Load()
    {
        var node = Node;
        Show(nameof(Aspect), Named(node.GetText("AspectPassed"), TimetableParts.CautionAspects));
        Show(nameof(FromLineSpeed), node.GetText("FromLineSpeed") ?? "0");
        Show(nameof(ToLineSpeed), node.GetText("ToLineSpeed") ?? "0");
        Show(nameof(ReduceNow), node.GetText("ReduceNowValue") ?? "0");
        Show(nameof(NowType), Named(node.GetText("NowValueType"), TimetableParts.CautionValueTypes));
        Show(nameof(ApproachNext), node.GetText("ApproachNextValue") ?? "0");
        Show(nameof(NextType), Named(node.GetText("NextValueType"), TimetableParts.CautionValueTypes));
        Show(nameof(Distance), node.GetText("ApproachNextDistance") ?? "0");
    }

    /// <summary>A code's name (a missing field is SimSig's default, 0); a code the list does not know shows as it is.</summary>
    private static string Named(string? code, IReadOnlyList<string> names) =>
        int.TryParse(code ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < names.Count ? names[i] : code ?? string.Empty;

    private TimetableEdit Pick(string field, string typed, IReadOnlyList<string> names, string what)
    {
        var index = names.ToList().FindIndex(n => string.Equals(n, typed, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? TimetableParts.SetItemField(Node, field, index.ToString(CultureInfo.InvariantCulture))
            : throw new InvalidOperationException($"'{typed}' is not {what}: choose one of {string.Join(", ", names)}.");
    }

    private TimetableEdit Number(string field, string typed, int most, string what) =>
        DraftText.TryNumber(typed, 0, most, out var value)
            ? TimetableParts.SetItemField(Node, field, (value ?? 0).ToString(CultureInfo.InvariantCulture))
            : throw new InvalidOperationException($"'{typed}' is not {what}.");
}

/// <summary>A caution speed set as a category can choose it: its ID and name; an empty ID is SimSig's standard rules.</summary>
internal sealed record CautionSetChoice(string Id, string Name)
{
    public const string Standard = "(SimSig's standard rules)";

    public override string ToString() => Name;
}

/// <summary>A scenario option: its code and value (the header's <c>ScenarioOptions</c> pairs).</summary>
internal sealed partial class ScenarioOptionRow(DraftListsViewModel owner, int index) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    public partial string Option { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Value { get; set; } = string.Empty;

    public bool Loading { get; set; }

    partial void OnOptionChanged(string value) => owner.ScenarioChanged(this);

    partial void OnValueChanged(string value) => owner.ScenarioChanged(this);
}

/// <summary>
/// The Lists tab (S45): Decisions and their choices, Class of Service rules, scenario options and Driving (caution
/// speed sets), each over the fields the XML format page documents. What else they hold is kept as it is.
/// </summary>
internal sealed partial class DraftListsViewModel(TimetableEditorViewModel owner, IDialogService dialogs) : ObservableObject
{
    public static readonly string[] DecisionFields = ["@ID", "Description", "Chosen"];
    public static readonly string[] ChoiceFields = ["@ID", "Announcement", "Weight", "Chosen"];
    public static readonly string[] ClassOfServiceFields = ["ClassOfService", "IDmatch", "Freight", "SpeedClassMatchType", "MatchSpeedClass"];
    public static readonly string[] CautionSetFields = ["Name", "@ID"];

    private readonly Dictionary<XElement, ListItemRow> _rows = [];
    private readonly Dictionary<XElement, CautionSpeedRow> _cautionRows = [];

    public ObservableCollection<ListItemRow> Decisions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveDecisionCommand), nameof(AddChoiceCommand))]
    public partial ListItemRow? SelectedDecision { get; set; }

    public ObservableCollection<ListItemRow> Choices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveChoiceCommand))]
    public partial ListItemRow? SelectedChoice { get; set; }

    public ObservableCollection<ListItemRow> ClassOfService { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveClassOfServiceCommand))]
    public partial ListItemRow? SelectedClassOfService { get; set; }

    public ObservableCollection<ListItemRow> CautionSets { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCautionSetCommand), nameof(AddCautionSpeedCommand))]
    public partial ListItemRow? SelectedCautionSet { get; set; }

    /// <summary>The rules of the selected set, in the order SimSig tries them.</summary>
    public ObservableCollection<CautionSpeedRow> CautionSpeeds { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCautionSpeedCommand), nameof(MoveCautionSpeedUpCommand), nameof(MoveCautionSpeedDownCommand))]
    public partial CautionSpeedRow? SelectedCautionSpeed { get; set; }

    public IReadOnlyList<string> CautionAspects { get; } = TimetableParts.CautionAspects;

    public IReadOnlyList<string> CautionValueTypes { get; } = TimetableParts.CautionValueTypes;

    public ObservableCollection<ScenarioOptionRow> Scenario { get; } = [];

    partial void OnSelectedDecisionChanged(ListItemRow? value) => Sync(Choices, value?.Element.Element("Choices")?.Elements("Choice"), ChoiceFields);

    partial void OnSelectedCautionSetChanged(ListItemRow? value) => SyncCautionSpeeds(value);

    [RelayCommand]
    private void AddDecision()
    {
        var id = NextId("D", owner.Document.Decisions?.Items.Select(i => (string?)i.Element.Attribute("ID")));
        if (owner.TryApply(TimetableParts.AddListItem(owner.Document, "Decisions", TimetableParts.NewDecision(id, "New decision"), $"Add decision {id}")))
        {
            SelectedDecision = Decisions.LastOrDefault();
        }
    }

    [RelayCommand(CanExecute = nameof(HasDecision))]
    private void RemoveDecision()
    {
        if (SelectedDecision is { } row)
        {
            owner.TryApply(TimetableParts.RemoveListItem(row.Node, $"Delete decision {(string?)row.Element.Attribute("ID")}"));
        }
    }

    [RelayCommand(CanExecute = nameof(HasDecision))]
    private void AddChoice()
    {
        if (SelectedDecision is { } row)
        {
            var id = NextId("C", row.Element.Element("Choices")?.Elements("Choice").Select(c => (string?)c.Attribute("ID")));
            owner.TryApply(TimetableParts.AddChoice(row.Node, id));
        }
    }

    [RelayCommand(CanExecute = nameof(HasChoice))]
    private void RemoveChoice()
    {
        if (SelectedChoice is { } row)
        {
            owner.TryApply(TimetableParts.RemoveListItem(row.Node, $"Delete choice {(string?)row.Element.Attribute("ID")}"));
        }
    }

    [RelayCommand]
    private void AddClassOfService()
    {
        var rule = new XElement("TTCOSRule", new XElement("ClassOfService", 0), new XElement("IDmatch", "*"), new XElement("Freight", 0),
            new XElement("SpeedClassMatchType", 0), new XElement("MatchSpeedClass", 0));
        if (owner.TryApply(TimetableParts.AddListItem(owner.Document, "TTClassOfServiceRules", rule, "Add class of service rule")))
        {
            SelectedClassOfService = ClassOfService.LastOrDefault();
        }
    }

    [RelayCommand(CanExecute = nameof(HasClassOfService))]
    private void RemoveClassOfService()
    {
        if (SelectedClassOfService is { } row)
        {
            owner.TryApply(TimetableParts.RemoveListItem(row.Node, "Delete class of service rule"));
        }
    }

    [RelayCommand]
    private void AddScenarioOption()
    {
        var pairs = TimetableParts.ScenarioOptions(owner.Document).ToList();
        pairs.Add(("OPTION", "1"));
        owner.TryApply(TimetableParts.SetScenarioOptions(owner.Document, pairs));
    }

    [RelayCommand]
    private void RemoveScenarioOption(ScenarioOptionRow? row)
    {
        if (row is not null)
        {
            var pairs = TimetableParts.ScenarioOptions(owner.Document).Where((_, i) => i != row.Index);
            owner.TryApply(TimetableParts.SetScenarioOptions(owner.Document, pairs));
        }
    }

    /// <summary>A scenario option typed in: all the pairs set again.</summary>
    public void ScenarioChanged(ScenarioOptionRow row)
    {
        if (row.Loading)
        {
            return;
        }

        var pairs = Scenario.Select(r => (r.Option, r.Value)).ToList();
        try
        {
            if (!owner.TryApply(TimetableParts.SetScenarioOptions(owner.Document, pairs)))
            {
                Load(owner.Snapshot);
            }
        }
        catch (InvalidOperationException ex)
        {
            owner.Refuse(ex.Message);
            Load(owner.Snapshot);
        }
    }

    /// <summary>A new caution speed set, named by the user; its rules are added below it.</summary>
    [RelayCommand]
    private void AddCautionSet()
    {
        var prompt = new PromptViewModel(
            "New caution speed set",
            "A set of defensive driving rules, usually one per operator. A category that uses it follows its rules instead of SimSig's standard ones.",
            new PromptField("Name", string.Empty))
        {
            Validate = p => string.IsNullOrWhiteSpace(p[0]) ? "Name the set." : null,
        };
        if (dialogs.Prompt(prompt) && owner.TryApply(TimetableParts.AddCautionSpeedSet(owner.Document, prompt[0])))
        {
            SelectedCautionSet = CautionSets.LastOrDefault();
        }
    }

    /// <summary>Deletes the selected set; the categories that use it go back to SimSig's standard rules (asked first).</summary>
    [RelayCommand(CanExecute = nameof(HasCautionSet))]
    private void RemoveCautionSet()
    {
        if (SelectedCautionSet is not { } row)
        {
            return;
        }

        var users = TimetableParts.CategoriesUsing(owner.Document, (string?)row.Element.Attribute("ID"));
        if (users.Count > 0 && dialogs.Choose(
                "Delete caution speed set",
                $"{string.Join(", ", users.Select(c => c.Description ?? c.Id))} use{(users.Count == 1 ? "s" : "")} {row["Name"]}. Their trains will follow SimSig's standard driving rules.",
                ["Delete the set", "Cancel"]) != 0)
        {
            return;
        }

        owner.TryApply(TimetableParts.RemoveCautionSpeedSet(row.Node));
    }

    /// <summary>A new rule below the selected one (at the end if none).</summary>
    [RelayCommand(CanExecute = nameof(HasCautionSet))]
    private void AddCautionSpeed()
    {
        if (SelectedCautionSet is not { } set)
        {
            return;
        }

        var index = SelectedCautionSpeed is { } after ? CautionSpeeds.IndexOf(after) + 1 : (int?)null;
        if (owner.TryApply(TimetableParts.AddCautionSpeed(set.Node, index)))
        {
            SelectedCautionSpeed = CautionSpeeds.ElementAtOrDefault(index ?? CautionSpeeds.Count - 1);
        }
    }

    [RelayCommand(CanExecute = nameof(HasCautionSpeed))]
    private void RemoveCautionSpeed()
    {
        if (SelectedCautionSpeed is { } row)
        {
            owner.TryApply(TimetableParts.RemoveListItem(row.Node, $"Delete a caution speed rule from {SelectedCautionSet?["Name"]}"));
        }
    }

    [RelayCommand(CanExecute = nameof(HasCautionSpeed))]
    private void MoveCautionSpeedUp() => MoveCautionSpeed(-1);

    [RelayCommand(CanExecute = nameof(HasCautionSpeed))]
    private void MoveCautionSpeedDown() => MoveCautionSpeed(1);

    private void MoveCautionSpeed(int by)
    {
        if (SelectedCautionSpeed is not { } row)
        {
            return;
        }

        var to = CautionSpeeds.IndexOf(row) + by;
        if (to >= 0 && to < CautionSpeeds.Count && owner.TryApply(TimetableParts.MoveCautionSpeed(row.Node, to)))
        {
            SelectedCautionSpeed = row;
        }
    }

    private bool HasCautionSet() => SelectedCautionSet is not null;

    private bool HasCautionSpeed() => SelectedCautionSpeed is not null;

    private bool HasDecision() => SelectedDecision is not null;

    private bool HasChoice() => SelectedChoice is not null;

    private bool HasClassOfService() => SelectedClassOfService is not null;

    public void Load(DraftSnapshot snapshot)
    {
        var document = snapshot.Document;
        Sync(Decisions, document.Decisions?.Items.Select(i => i.Element), DecisionFields);
        Sync(Choices, SelectedDecision?.Element.Element("Choices")?.Elements("Choice"), ChoiceFields);
        Sync(ClassOfService, document.ClassOfServiceRules?.Items.Select(i => i.Element), ClassOfServiceFields);
        Sync(CautionSets, document.CautionSpeedSets?.Items.Select(i => i.Element), CautionSetFields);
        SelectedCautionSet = SelectedCautionSet is { } s && CautionSets.Contains(s) ? s : null;
        SyncCautionSpeeds(SelectedCautionSet);
        SelectedDecision = SelectedDecision is { } d && Decisions.Contains(d) ? d : null;
        SelectedClassOfService = SelectedClassOfService is { } c && ClassOfService.Contains(c) ? c : null;

        var pairs = TimetableParts.ScenarioOptions(document);
        if (Scenario.Count != pairs.Count)
        {
            Scenario.Clear();
            for (var i = 0; i < pairs.Count; i++)
            {
                Scenario.Add(new ScenarioOptionRow(this, i));
            }
        }

        for (var i = 0; i < pairs.Count; i++)
        {
            Scenario[i].Loading = true;
            (Scenario[i].Option, Scenario[i].Value) = pairs[i];
            Scenario[i].Loading = false;
        }
    }

    private void Sync(ObservableCollection<ListItemRow> rows, IEnumerable<XElement>? elements, IReadOnlyList<string> fields)
    {
        var wanted = new List<ListItemRow>();
        foreach (var element in elements ?? [])
        {
            if (!_rows.TryGetValue(element, out var row))
            {
                _rows[element] = row = new ListItemRow(owner, element);
            }

            row.Load(fields);
            wanted.Add(row);
        }

        if (!wanted.SequenceEqual(rows))
        {
            rows.Clear();
            foreach (var row in wanted)
            {
                rows.Add(row);
            }
        }
    }

    private void SyncCautionSpeeds(ListItemRow? set)
    {
        var wanted = new List<CautionSpeedRow>();
        foreach (var element in set?.Element.Element("CautionSpeeds")?.Elements("CautionSpeed") ?? [])
        {
            if (!_cautionRows.TryGetValue(element, out var row))
            {
                _cautionRows[element] = row = new CautionSpeedRow(owner, element);
            }

            row.Load();
            wanted.Add(row);
        }

        if (!wanted.SequenceEqual(CautionSpeeds))
        {
            var selected = SelectedCautionSpeed;
            CautionSpeeds.Clear();
            foreach (var row in wanted)
            {
                CautionSpeeds.Add(row);
            }

            SelectedCautionSpeed = selected is not null && CautionSpeeds.Contains(selected) ? selected : null;
        }
    }

    private static string NextId(string prefix, IEnumerable<string?>? taken)
    {
        var used = (taken ?? []).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
        {
            var id = string.Create(CultureInfo.InvariantCulture, $"{prefix}{n}");
            if (!used.Contains(id))
            {
                return id;
            }
        }
    }
}
