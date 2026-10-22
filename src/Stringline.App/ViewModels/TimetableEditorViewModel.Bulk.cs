using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using Stringline.Core;
using Stringline.Editing.Bulk;
using Stringline.Editing.Validation;
using Stringline.Planning;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>
/// The Bulk menu (S46, <c>08-…md</c> §8): each tool asks what to do, shows what it would change (listed, and drawn as
/// ghosts on the draft board), and is applied as one edit.
/// </summary>
internal sealed partial class TimetableEditorViewModel
{
    private static readonly string[] Codes = ["Platform", "Line", "Path"];
    private static readonly string[] Allowances = ["Engineering", "Pathing", "Performance"];
    private static readonly string[] ReplaceFields = ["Everything", "Headcodes", "UIDs", "Descriptions", "Locations and codes"];

    [RelayCommand]
    private void ShiftTimes()
    {
        var prompt = new PromptViewModel(
            "Shift times",
            Selection("Move the selected trains' times"),
            new PromptField("By (minutes)", "+5", hint: "Whole or half minutes: +5, -2H"),
            new PromptField("From location", string.Empty, hint: "Blank for the whole train, or a TIPLOC to move from there on"))
        {
            Validate = p => Minutes(p[0]) is null ? "Type the minutes to move by, such as +5 or -2H." : null,
        };
        if (Ask(prompt))
        {
            var seconds = Minutes(prompt[0])!.Value;
            var from = prompt[1].Length == 0 ? null : prompt[1].ToUpperInvariant();
            RunTool((d, trains) => BulkTools.Shift(trains, seconds, from));
        }
    }

    [RelayCommand]
    private void RenumberTrains()
    {
        var first = SelectedNodes().FirstOrDefault()?.Headcode ?? "2A01";
        var prompt = new PromptViewModel(
            "Renumber",
            Selection("Renumber the selected trains in list order; activities and rules that name them follow"),
            new PromptField("First headcode", first),
            new PromptField("Count by", "2"))
        {
            Validate = p => !Headcode.TryCreate(p[0], out _) ? "Type a headcode." : int.TryParse(p[1], out _) ? null : "Count by a whole number.",
        };
        if (Ask(prompt))
        {
            var step = int.Parse(prompt[1], System.Globalization.CultureInfo.InvariantCulture);
            RunTool((d, trains) => BulkTools.Renumber(d, trains, prompt[0].ToUpperInvariant(), step));
        }
    }

    [RelayCommand]
    private void ChangeCategory()
    {
        var choices = Document.Categories.Where(c => c.Id is not null).Select(c => $"{c.Id}  {c.Description}").ToList();
        if (choices.Count == 0)
        {
            Refuse("The timetable has no categories.");
            return;
        }

        var prompt = new PromptViewModel("Change category", Selection("Give the selected trains another category"), new PromptField("Category", choices[0], choices));
        if (Ask(prompt))
        {
            var id = prompt[0].Split(' ', 2)[0];
            RunTool((d, trains) => BulkTools.SetCategory(d, trains, id));
        }
    }

    [RelayCommand]
    private void SetCodeAtLocation()
    {
        var prompt = new PromptViewModel(
            "Platform, line or path at a location",
            Selection("Set a code where the selected trains call"),
            new PromptField("Location", string.Empty, hint: "A TIPLOC"),
            new PromptField("Set", Codes[0], Codes),
            new PromptField("To", string.Empty, hint: "Blank clears it"))
        {
            Validate = p => p[0].Length == 0 ? "Type the location." : null,
        };
        if (Ask(prompt))
        {
            var code = (TripCode)Array.IndexOf(Codes, prompt[1]);
            var location = prompt[0].ToUpperInvariant();
            RunTool((d, trains) => BulkTools.SetAtLocation(trains, location, code, prompt[2]));
        }
    }

    [RelayCommand]
    private void SetAllowances()
    {
        var prompt = new PromptViewModel(
            "Allowances",
            Selection("Set or clear an allowance on the selected trains"),
            new PromptField("Allowance", Allowances[2], Allowances),
            new PromptField("Minutes", "0", hint: "0 clears it; H for a half"),
            new PromptField("At location", string.Empty, hint: "Blank for every point"))
        {
            Validate = p => DraftText.TryAllowance(p[1], out _) ? null : "Type minutes, with H for a half (1H).",
        };
        if (Ask(prompt))
        {
            DraftText.TryAllowance(prompt[1], out var seconds);
            var kind = (AllowanceKind)Array.IndexOf(Allowances, prompt[0]);
            var location = prompt[2].Length == 0 ? null : prompt[2].ToUpperInvariant();
            RunTool((d, trains) => BulkTools.SetAllowance(trains, kind, seconds, location));
        }
    }

    [RelayCommand]
    private void FindReplace()
    {
        var prompt = new PromptViewModel(
            "Find and replace",
            Selection("Replace a whole value in the selected trains (headcodes and UIDs are renumbered, so their links follow)"),
            new PromptField("Find", string.Empty),
            new PromptField("Replace with", string.Empty),
            new PromptField("In", ReplaceFields[0], ReplaceFields))
        {
            Validate = p => p[0].Length == 0 ? "Type what to find." : null,
        };
        if (Ask(prompt))
        {
            var where = Array.IndexOf(ReplaceFields, prompt[2]) switch
            {
                1 => ReplaceIn.Headcodes,
                2 => ReplaceIn.Uids,
                3 => ReplaceIn.Descriptions,
                4 => ReplaceIn.Codes,
                _ => ReplaceIn.All,
            };
            RunTool((d, trains) => BulkTools.FindReplace(d, trains, prompt[0], prompt[1], where));
        }
    }

    [RelayCommand]
    private void RepeatPattern()
    {
        if (SelectedNodes().Count != 1)
        {
            Refuse("Choose one train to repeat.");
            return;
        }

        var prompt = new PromptViewModel(
            "Repeat as pattern",
            $"Copy {SelectedNodes()[0].Headcode} every so many minutes. Its next working and other links are not copied: pair them with Bulk ▸ Pair next workings.",
            new PromptField("Every (minutes)", "30"),
            new PromptField("Last start", DraftText.Time(Document.Header.FinishTime), hint: "The last copy starts by then"),
            new PromptField("Headcode counts by", "2"))
        {
            Validate = p => !int.TryParse(p[0], out var m) || m <= 0 ? "Repeat every one minute or more."
                : !SimTimeNotation.TryParse(p[1], null, out _) ? "Type the last start time, such as 2300."
                : int.TryParse(p[2], out _) ? null : "Count by a whole number.",
        };
        if (Ask(prompt))
        {
            var every = int.Parse(prompt[0], System.Globalization.CultureInfo.InvariantCulture);
            SimTimeNotation.TryParse(prompt[1], null, out var until);
            var step = int.Parse(prompt[2], System.Globalization.CultureInfo.InvariantCulture);
            RunTool((d, trains) => BulkTools.RepeatPattern(d, trains[0], every, until.Time, step));
        }
    }

    [RelayCommand]
    private void PairNextWorkings()
    {
        var prompt = new PromptViewModel(
            "Pair next workings",
            Selection("Pair the selected trains that end at a terminus with those that start there"),
            new PromptField("At", SelectedNodes().FirstOrDefault()?.Trips.LastOrDefault()?.Location ?? string.Empty, hint: "The terminus TIPLOC"),
            new PromptField("Least turnaround (minutes)", "5"))
        {
            Validate = p => p[0].Length == 0 ? "Type the terminus." : int.TryParse(p[1], out var m) && m >= 0 ? null : "Type whole minutes.",
        };
        if (Ask(prompt))
        {
            var turnaround = int.Parse(prompt[1], System.Globalization.CultureInfo.InvariantCulture) * 60;
            var at = prompt[0].ToUpperInvariant();
            RunTool((d, trains) => BulkTools.PairNextWorkings(d, trains, trains, at, turnaround));
        }
    }

    [RelayCommand]
    private void AutoFill()
    {
        if (Editor.Knowledge is not { } knowledge)
        {
            Refuse("Stringline is still learning the sim's run times from its other timetables; try again in a moment.");
            return;
        }

        RunTool((d, trains) => Combine($"Fill timings of {trains.Count} train{(trains.Count == 1 ? "" : "s")}", trains.Select(t => BulkTools.AutoFill(d, knowledge, t))));
    }

    [RelayCommand]
    private void SetLines()
    {
        if (Editor.Knowledge is not { } knowledge)
        {
            Refuse("Stringline is still learning the sim's lines from its other timetables; try again in a moment.");
            return;
        }

        RunTool((d, trains) => BulkTools.SetLine(knowledge, trains));
    }

    /// <summary>The "no known path" fix for every such issue of the selected trains, as one edit.</summary>
    [RelayCommand]
    private void InsertTimingPoints()
    {
        var trains = SelectedNodes().Select(t => t.Element).ToHashSet();
        var fixes = Snapshot.Validation.Issues
            .Where(i => i.Code == IssueCodes.NoKnownPath && i.TrainElement is { } e && trains.Contains(e) && i.QuickFix is not null)
            .ToList();
        if (fixes.Count == 0)
        {
            Refuse(trains.Count == 0 ? "Choose the trains in the list first." : "None of the selected trains has a gap other timetables fill with timing points.");
            return;
        }

        var result = new BulkResult(
            TimetableEdit.Composite($"Insert timing points in {trains.Count} train{(trains.Count == 1 ? "" : "s")}", fixes.Select(f => f.QuickFix!.Edit)),
            [.. fixes.Select(f => $"{f.Headcode}: {f.QuickFix!.Title}")],
            []);
        Confirm("Insert timing points", result, preview: null);
    }

    // ---- Running a tool ----

    /// <summary>
    /// Runs a tool on a copy of the draft for its preview (the changes listed, the trains drawn as ghosts), then, if
    /// confirmed, on the draft itself as one edit.
    /// </summary>
    private void RunTool(Func<WttDocument, IReadOnlyList<TrainNode>, BulkResult> tool)
    {
        var selected = SelectedNodes();
        if (selected.Count == 0)
        {
            Refuse("Choose the trains in the list first (Ctrl+A for all).");
            return;
        }

        IReadOnlyList<TrainPlan>? preview;
        BulkResult result;
        try
        {
            preview = Preview(tool, selected);
            result = tool(Document, selected);
        }
        catch (InvalidOperationException ex)
        {
            Refuse(ex.Message);
            return;
        }

        Confirm(result.Edit?.Description ?? "Bulk edit", result, preview);
    }

    private void Confirm(string title, BulkResult result, IReadOnlyList<TrainPlan>? preview)
    {
        if (result.Edit is null)
        {
            Refuse(result.Problems.Count > 0 ? result.Problems[0] : "There is nothing to change.");
            return;
        }

        Board.ShowPreview(preview);
        try
        {
            var lines = result.Changes.Take(30).Select(c => "• " + c).ToList();
            if (result.Changes.Count > 30)
            {
                lines.Add($"… and {result.Changes.Count - 30} more");
            }

            if (result.Problems.Count > 0)
            {
                lines.Add(string.Empty);
                lines.Add("Not done:");
                lines.AddRange(result.Problems.Take(10).Select(p => "• " + p));
            }

            var message = $"{result.Changes.Count} change{(result.Changes.Count == 1 ? "" : "s")}{(preview is { Count: > 0 } ? " (drawn on the board)" : string.Empty)}:\n{string.Join("\n", lines)}";
            if (_dialogs.Choose(title, message, ["Apply", "Cancel"]) == 0)
            {
                TryApply(result.Edit);
            }
        }
        finally
        {
            Board.ShowPreview(null);
        }
    }

    /// <summary>The trains a tool changes or adds, as it would leave them, from a run on a copy of the draft.</summary>
    private IReadOnlyList<TrainPlan>? Preview(Func<WttDocument, IReadOnlyList<TrainNode>, BulkResult> tool, IReadOnlyList<TrainNode> selected)
    {
        var copy = Document.Clone();
        var trains = Document.Trains.Select(t => t.Element).ToList();
        var copies = copy.Trains.ToList();
        var mapped = selected.Select(t => copies[trains.IndexOf(t.Element)]).ToList();
        if (tool(copy, mapped).Edit is not { } edit)
        {
            return null;
        }

        var before = Document.Trains.Select(t => t.Element.ToString(SaveOptions.DisableFormatting)).ToHashSet();
        copy.Apply(edit);
        var import = copy.Import();
        return [.. copy.Trains
            .Where(t => !before.Contains(t.Element.ToString(SaveOptions.DisableFormatting)))
            .Select(import.TrainOf).OfType<Core.Timetables.Train>()
            .Select(TrainPlan.FromWtt)];
    }

    /// <summary>The trains selected in the list, else the current one, in list order.</summary>
    private List<TrainNode> SelectedNodes() =>
        [.. BulkRows().Select(r => new TrainNode(Document, r.Element)).OrderBy(t => t.Element.ElementsBeforeSelf().Count())];

    private IReadOnlyList<DraftTrainRow> BulkRows() => SelectedTrainRows.Count > 0 ? SelectedTrainRows : SelectedTrainRow is { } one ? [one] : [];

    private string Selection(string what)
    {
        var n = BulkRows().Count;
        return n == 0 ? $"{what}. (No trains are selected: choose them in the list first.)" : $"{what}: {n} train{(n == 1 ? "" : "s")} selected.";
    }

    private bool Ask(PromptViewModel prompt)
    {
        if (BulkRows().Count == 0)
        {
            Refuse("Choose the trains in the list first (Ctrl+A for all).");
            return false;
        }

        return _dialogs.Prompt(prompt);
    }

    /// <summary>Minutes typed as <c>+5</c>, <c>-2H</c> or <c>3</c>, in seconds.</summary>
    internal static int? Minutes(string text)
    {
        var t = text.Trim();
        var sign = 1;
        if (t.StartsWith('-') || t.StartsWith('−'))
        {
            (sign, t) = (-1, t[1..]);
        }
        else if (t.StartsWith('+'))
        {
            t = t[1..];
        }

        return DraftText.TryAllowance(t, out var seconds) && seconds > 0 ? sign * seconds : null;
    }

    private static BulkResult Combine(string description, IEnumerable<BulkResult> results)
    {
        var all = results.ToList();
        var edits = all.Select(r => r.Edit).OfType<TimetableEdit>().ToList();
        return new BulkResult(edits.Count == 0 ? null : TimetableEdit.Composite(description, edits), [.. all.SelectMany(r => r.Changes)], [.. all.SelectMany(r => r.Problems)]);
    }
}
