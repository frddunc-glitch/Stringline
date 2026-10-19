using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Disruptions and notes from the shell (S13, DIS-1 to DIS-3): the background's right-click menu, B+drag to draw a
/// block, the dialogs that set them up (Fred: B+drag opens the block dialog filled in), and their tooltips. Each is a
/// plan command, so it undoes like any other edit.
/// </summary>
internal sealed partial class MainViewModel
{
    /// <summary>How long a block or restriction made with a right-click lasts at first.</summary>
    private const int DefaultDisruptionSeconds = SimTime.SecondsPerHour;

    /// <summary>Where a B+drag started, in world time and axis position, so the view can follow the now line under it.</summary>
    private (double Time, double Axis)? _blockFrom;

    private GraphSpan? _blockSpan;

    /// <summary>The block being drawn with B+drag; null when not drawing one.</summary>
    [ObservableProperty]
    public partial GraphDisruption? DraftDisruption { get; private set; }

    /// <summary>Show the plan's blocks, speed restrictions and notes on the graph.</summary>
    [ObservableProperty]
    public partial bool ShowDisruptions { get; set; } = true;

    partial void OnShowDisruptionsChanged(bool value) => RequestScene();

    /// <summary>The tooltip for a note or disruption under the cursor, when no train line is.</summary>
    public HoverInfo? DescribeAt(ScreenPoint at)
    {
        if (Scene is not { } scene)
        {
            return null;
        }

        var view = Navigator.Viewport;
        return GraphAnnotations.NoteAt(scene, view, at) is { } note ? GraphAnnotations.Describe(note, scene)
            : GraphAnnotations.DisruptionAt(scene, view, at) is { } disruption ? GraphAnnotations.Describe(disruption, scene)
            : null;
    }

    /// <summary>
    /// The right-click menu at a point: a note's, else the train line's (<see cref="MenuFor(GraphHit?)"/>), else the
    /// background's: the disruption there (Edit…, Remove), then Add block/possession here…, Add speed restriction here…
    /// and Add note here….
    /// </summary>
    public IReadOnlyList<PlanMenuItem> MenuFor(GraphHit? hit, ScreenPoint at)
    {
        if (_drag is not null || _blockFrom is not null || Scene is not { } scene || SelectedCorridor is null)
        {
            return [];
        }

        var view = Navigator.Viewport;
        if (GraphAnnotations.NoteAt(scene, view, at) is { } note)
        {
            var id = note.Id;
            return
            [
                new PlanMenuItem($"Note at {Name(note.Note.Location)} {PlanInput.Format(note.Note.Time)}"),
                new PlanMenuItem("_Edit note…", new AsyncRelayCommand(() => EditNoteAsync(id))),
                new PlanMenuItem("_Remove note", new AsyncRelayCommand(() => ExecuteAsync(new RemoveNote(id)))),
            ];
        }

        if (hit is not null)
        {
            return MenuFor(hit);
        }

        var items = new List<PlanMenuItem>();
        if (GraphAnnotations.DisruptionAt(scene, view, at) is { } shown)
        {
            var d = shown.Disruption;
            var what = d.Kind == DisruptionKind.Block ? "block" : "speed restriction";
            items.Add(new PlanMenuItem($"{(d.Kind == DisruptionKind.Block ? "Block" : "Speed restriction")} {Where(d.From, d.To)} {PlanInput.Format(d.Start)}–{PlanInput.Format(d.End)}"));
            items.Add(new PlanMenuItem($"_Edit {what}…", new AsyncRelayCommand(() => EditDisruptionAsync(d.Id))));
            items.Add(new PlanMenuItem($"_Remove {what}", new AsyncRelayCommand(() => ExecuteAsync(new RemoveDisruption(d.Id)))));
            items.Add(PlanMenuItem.Separator);
        }

        if (GraphAnnotations.SpanBetween(scene, view, at, at) is { } span)
        {
            var here = span with { End = span.Start + DefaultDisruptionSeconds };
            items.Add(new PlanMenuItem("Add _block/possession here…", new AsyncRelayCommand(() => AddDisruptionAsync(DisruptionKind.Block, here))));
            items.Add(new PlanMenuItem("Add _speed restriction here…", new AsyncRelayCommand(() => AddDisruptionAsync(DisruptionKind.SpeedRestriction, here))));
        }

        if (GraphAnnotations.NearestLocation(scene, view, at) is { } location)
        {
            var time = new SimTime((int)Math.Round(view.PixelToTime(view.TimePixelOf(at)) / SimTime.SecondsPerMinute) * SimTime.SecondsPerMinute);
            items.Add(new PlanMenuItem("Add _note here…", new AsyncRelayCommand(() => AddNoteAsync(location.Tiploc, time))));
            var tiploc = location.Tiploc;
            items.Add(new PlanMenuItem($"Regulation _list at {Name(tiploc)}", new RelayCommand(() => ShowRegulation(tiploc))));
        }

        return items;
    }

    // ---- B+drag ---------------------------------------------------------------------------------

    /// <summary>A press with B held: start drawing a block. False if there is no graph to draw on, or another edit is under way.</summary>
    public bool BeginBlock(ScreenPoint at)
    {
        if (Scene is null || SelectedCorridor is null || _drag is not null || _blockFrom is not null)
        {
            return false;
        }

        if (ReviewRefusal is { } refusal)
        {
            EditStatus = refusal;
            return false;
        }

        var view = Navigator.Viewport;
        _blockFrom = (view.PixelToTime(view.TimePixelOf(at)), view.PixelToAxis(view.AxisPixelOf(at)));
        MoveBlock(at);
        return true;
    }

    /// <summary>The mouse moves while drawing a block: the draft spans the locations and five-minute steps between the press and here.</summary>
    public void MoveBlock(ScreenPoint at)
    {
        if (_blockFrom is not { } from || Scene is not { } scene)
        {
            return;
        }

        var view = Navigator.Viewport;
        _blockSpan = GraphAnnotations.SpanBetween(scene, view, view.ToScreen(from.Time, from.Axis), at);
        if (_blockSpan is not { } span)
        {
            DraftDisruption = null;
            return;
        }

        var draft = new Disruption("draft", DisruptionKind.Block, span.From.Tiploc, span.To.Tiploc, null, span.Start, span.End);
        DraftDisruption = GraphDisruption.For(draft, span.From.Axis, span.To.Axis, isDraft: true);
        EditStatus = $"Block {Where(span.From.Tiploc, span.To.Tiploc)} {PlanInput.Format(span.Start)}–{PlanInput.Format(span.End)}: release to set it up, Esc to cancel.";
    }

    /// <summary>The button is released (open the block dialog filled in from the draft) or Esc is pressed (drop it).</summary>
    public async Task EndBlockAsync(bool commit)
    {
        if (_blockFrom is null)
        {
            return;
        }

        var span = _blockSpan;
        _blockFrom = null;
        _blockSpan = null;
        DraftDisruption = null;
        EditStatus = commit ? string.Empty : "Block cancelled.";
        if (commit && span is { } s)
        {
            await AddDisruptionAsync(DisruptionKind.Block, s);
        }
    }

    // ---- Dialogs ------------------------------------------------------------------------------

    private Task AddDisruptionAsync(DisruptionKind kind, GraphSpan span)
    {
        var id = Snapshot.Plan.NewDisruptionId(kind);
        var draft = new Disruption(id, kind, span.From.Tiploc, span.To.Tiploc, null, span.Start, span.End, kind == DisruptionKind.SpeedRestriction ? 1.5 : 1);
        return AskDisruption(draft, isNew: true) is { } disruption ? ExecuteAsync(new AddDisruption(disruption)) : Task.CompletedTask;
    }

    private Task EditDisruptionAsync(string id) =>
        Snapshot.Plan.FindDisruption(id) is { } old && AskDisruption(old, isNew: false) is { } changed
            ? ExecuteAsync(new EditDisruption(changed))
            : Task.CompletedTask;

    /// <summary>
    /// The block or speed restriction dialog: ends (corridor locations), track (all lines, a corridor track or a WTT
    /// line, Fred), times, running (a restriction) and a note. Returns the disruption to make, or null if cancelled.
    /// </summary>
    private Disruption? AskDisruption(Disruption d, bool isNew)
    {
        if (SelectedCorridor?.Corridor is not { } corridor)
        {
            return null;
        }

        var block = d.Kind == DisruptionKind.Block;
        var places = corridor.Locations.Select(l => (Text: LocationText(l.Tiploc), l.Tiploc)).ToList();
        var tracks = DisruptionTracks.For(corridor, d.From, d.To, Snapshot.Plan).ToList();
        var current = TrackChoice.Of(d.Track, d.Direction);
        if (!tracks.Exists(t => t.Label == current.Label))
        {
            tracks.Add(current);
        }

        var fields = new List<PromptField>
        {
            new("From", LocationText(d.From), [.. places.Select(p => p.Text)]),
            new("To", LocationText(d.To), [.. places.Select(p => p.Text)], "The same location for one place only."),
            new("Track", current.Label, [.. tracks.Select(t => t.Label)], "A corridor track applies to trains running its way; a WTT line to trains planned on it."),
            new("From time", PlanInput.Format(d.Start)),
            new("Until", PlanInput.Format(d.End)),
        };
        if (!block)
        {
            fields.Add(new PromptField("Running-time factor", d.RunTimeFactor.ToString("0.##", CultureInfo.InvariantCulture), hint: "Minimum run times are multiplied by this, e.g. 1.5."));
            fields.Add(new PromptField("Added (minutes)", PlanInput.FormatMinutes(d.AddedSeconds), hint: "Added to each minimum run, e.g. 0.5 or 2."));
        }

        fields.Add(new PromptField("Note", d.Note, hint: "Optional: the reason, e.g. engineering work."));
        var noteField = fields.Count - 1;

        Disruption? result = null;
        var prompt = new PromptViewModel(
            (isNew ? "Add " : "Edit ") + (block ? "block/possession" : "speed restriction"),
            block ? "No train may run through the block: the forecast holds trains until it ends." : "Trains run slower through the restriction while it is in force.",
            [.. fields])
        {
            Validate = p =>
            {
                var from = places.Find(x => Matches(x.Text, x.Tiploc, p[0]));
                var to = places.Find(x => Matches(x.Text, x.Tiploc, p[1]));
                if (from.Text is null || to.Text is null)
                {
                    return "Choose both ends from the list of the corridor's locations.";
                }

                if (!PlanInput.TryParseTime(p[3], d.Start, out var start) || !PlanInput.TryParseTime(p[4], start, out var end))
                {
                    return "Enter the times such as 10:25 or 10:25:30.";
                }

                var factor = 1.0;
                var added = 0;
                if (!block && (!double.TryParse(p[5], NumberStyles.Float, CultureInfo.InvariantCulture, out factor) || !PlanInput.TryParseMinutes(p[6], out added)))
                {
                    return "Enter a running-time factor such as 1.5, and the added minutes (0 for none).";
                }

                // The Up end of the corridor is the disruption's From, so a track's direction reads the corridor's way.
                if (corridor.IndexOf(from.Tiploc) > corridor.IndexOf(to.Tiploc))
                {
                    (from, to) = (to, from);
                }

                var track = tracks.Find(t => string.Equals(t.Label, p[2], StringComparison.OrdinalIgnoreCase))
                    ?? tracks.Find(t => t.Track is not null && string.Equals(t.Track, p[2], StringComparison.OrdinalIgnoreCase))
                    ?? (p[2].Length == 0 ? TrackChoice.AllLines : TrackChoice.Of(p[2], null));
                result = d with
                {
                    From = from.Tiploc,
                    To = to.Tiploc,
                    Track = track.Track,
                    Direction = track.Direction,
                    Start = start,
                    End = end,
                    RunTimeFactor = factor,
                    AddedSeconds = added,
                    Note = p[noteField],
                };
                return result.Problem() is { } problem ? problem
                    : !block && factor == 1 && added == 0 ? "A speed restriction must make running slower: a factor over 1, or added minutes."
                    : null;
            },
        };
        return _dialogs.Prompt(prompt) ? result : null;
    }

    private async Task AddNoteAsync(Tiploc location, SimTime time)
    {
        if (AskNote(new PlanNote(Snapshot.Plan.NewNoteId(), location, time, string.Empty), isNew: true) is { } note)
        {
            await ExecuteAsync(new AddNote(note));
        }
    }

    private async Task EditNoteAsync(string id)
    {
        if (Snapshot.Plan.FindNote(id) is { } old && AskNote(old, isNew: false) is { } note)
        {
            await ExecuteAsync(new EditNote(note));
        }
    }

    private PlanNote? AskNote(PlanNote n, bool isNew)
    {
        if (SelectedCorridor?.Corridor is not { } corridor)
        {
            return null;
        }

        var places = corridor.Locations.Select(l => (Text: LocationText(l.Tiploc), l.Tiploc)).ToList();
        PlanNote? result = null;
        var prompt = new PromptViewModel(
            isNew ? "Add note" : "Edit note",
            "A note pinned to the graph at a time and place.",
            new PromptField("At", LocationText(n.Location), [.. places.Select(p => p.Text)]),
            new PromptField("Time", PlanInput.Format(n.Time)),
            new PromptField("Note", n.Text))
        {
            Validate = p =>
            {
                var place = places.Find(x => Matches(x.Text, x.Tiploc, p[0]));
                if (place.Text is null)
                {
                    return "Choose a location from the list of the corridor's locations.";
                }

                if (!PlanInput.TryParseTime(p[1], n.Time, out var time))
                {
                    return "Enter a time such as 10:25 or 10:25:30.";
                }

                result = n with { Location = place.Tiploc, Time = time, Text = p[2] };
                return result.Problem();
            },
        };
        return _dialogs.Prompt(prompt) ? result : null;
    }

    /// <summary>"Waterloo (WATRLMN)", or the TIPLOC alone when it has no other name.</summary>
    private string LocationText(Tiploc tiploc) => Name(tiploc) is var name && name != tiploc.Code ? $"{name} ({tiploc.Code})" : tiploc.Code;

    private static bool Matches(string text, Tiploc tiploc, string typed) =>
        string.Equals(text, typed, StringComparison.OrdinalIgnoreCase) || string.Equals(tiploc.Code, typed, StringComparison.OrdinalIgnoreCase);

    private string Where(Tiploc from, Tiploc to) => from == to ? Name(from) : $"{Name(from)} – {Name(to)}";
}
