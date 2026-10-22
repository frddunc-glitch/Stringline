using System.Xml.Linq;
using Stringline.Editing.Knowledge;
using Stringline.Editing.Validation;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Drafts;

/// <summary>Who made an edit: the user, or Stringline (a quick fix or tool), whose inserted points show greyed.</summary>
public enum EditOrigin
{
    User,
    Stringline,
}

/// <summary>
/// The single writer for one draft (<c>08-…md</c> §3.2, S43): it applies <see cref="TimetableEdit"/>s in order, keeps
/// undo and redo (each step also as <see cref="EditRecord"/>s, so the history survives in the draft's
/// <c>.draft.json</c>), checks the draft again after each edit (<see cref="TimetableValidator.Revalidate"/>), and
/// publishes a <see cref="DraftSnapshot"/>. It saves the draft as a timetable (<see cref="WttWriter"/>), raising the
/// version, and knows whether there are changes not saved.
/// </summary>
/// <remarks>
/// Not thread-safe: it runs on its owner's thread (the app's UI thread), which is also the only thread that may read
/// the document. A session's timetable is never edited: a draft is always its own document (<see cref="WttDocument.Clone"/>
/// or a file read again).
/// </remarks>
public sealed class TimetableEditor
{
    /// <summary>Steps of undo kept; older ones are dropped.</summary>
    public const int HistoryLimit = 500;

    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];
    private readonly HashSet<XElement> _added = [];
    private TimetableValidator _validator;
    private long _nextId = 1;
    private long _cleanId;
    private int _sequence;

    /// <param name="document">The draft's own document (never a session's).</param>
    /// <param name="sourcePath">The timetable the draft was made from, if any.</param>
    /// <param name="targetPath">Where Save writes, if already chosen.</param>
    public TimetableEditor(WttDocument document, string? sourcePath = null, string? targetPath = null, SimKnowledge? knowledge = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        _validator = new TimetableValidator(knowledge);
        var import = document.Import();
        Current = Publish(import, _validator.Validate(import));
    }

    /// <summary>Published after every change: an edit, undo, redo, save, new knowledge or a full check.</summary>
    public event EventHandler<DraftSnapshot>? SnapshotPublished;

    public WttDocument Document { get; }

    public string? SourcePath { get; }

    public string? TargetPath { get; private set; }

    public SimKnowledge? Knowledge => _validator.Knowledge;

    public DraftSnapshot Current { get; private set; }

    /// <summary>The edit most recently applied made changes not saved to <see cref="TargetPath"/> (or the draft was never saved and has edits).</summary>
    public bool IsDirty => TopId != _cleanId;

    private long TopId => _undo.Count == 0 ? 0 : _undo[^1].Id;

    /// <summary>Applies an edit as one undo step and checks the trains it touched again.</summary>
    /// <param name="origin">Stringline's edits (quick fixes, tools) mark the timing points they insert, shown greyed.</param>
    /// <exception cref="InvalidOperationException">The edit does not fit the document; nothing was changed.</exception>
    public DraftSnapshot Apply(TimetableEdit edit, EditOrigin origin = EditOrigin.User)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var change = edit.ApplyRecorded(Document);
        _undo.Add(new HistoryEntry(_nextId++, edit.Description, change.Forward, change.Backward) { UndoEdit = change.Undo });
        _redo.Clear();
        if (_undo.Count > HistoryLimit)
        {
            _undo.RemoveAt(0);
        }

        if (origin == EditOrigin.Stringline)
        {
            foreach (var trip in Inserted(edit).Where(e => e.Name.LocalName == "Trip"))
            {
                _added.Add(trip);
            }
        }
        else
        {
            Touched(change.Undo);
        }

        return Changed(change.Undo);
    }

    public DraftSnapshot Undo()
    {
        if (_undo.Count == 0)
        {
            return Current;
        }

        var entry = _undo[^1];
        var redo = Document.Apply(entry.UndoEdit ?? entry.Backward.ToEdit());
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry with { UndoEdit = null, RedoEdit = redo });
        return Changed(redo);
    }

    public DraftSnapshot Redo()
    {
        if (_redo.Count == 0)
        {
            return Current;
        }

        var entry = _redo[^1];
        var undo = Document.Apply(entry.RedoEdit ?? entry.Forward.ToEdit());
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry with { RedoEdit = null, UndoEdit = undo });
        return Changed(undo);
    }

    /// <summary>Uses new knowledge of the sim (built in the background after the draft opened) and checks everything again.</summary>
    public DraftSnapshot SetKnowledge(SimKnowledge? knowledge)
    {
        _validator = new TimetableValidator(knowledge);
        return CheckAll();
    }

    /// <summary>A full check (Timetable ▸ Check timetable, and before saving).</summary>
    public DraftSnapshot CheckAll()
    {
        var import = Document.Import();
        return Current = Publish(import, _validator.Validate(import));
    }

    /// <summary>
    /// Saves the draft as a timetable (<c>08-…md</c> §6): the version goes up (<paramref name="options"/>' bump), a file
    /// being replaced is backed up first, and the file becomes the draft's <see cref="TargetPath"/>. The caller has
    /// asked about replacing a file and about Errors.
    /// </summary>
    public WttSaveResult Save(string path, WttSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var result = WttWriter.Save(Document, path, options);
        TargetPath = result.Path;
        _cleanId = TopId;
        CheckAll();
        return result;
    }

    /// <summary>Whether Stringline inserted this timing point and the user has not edited it since.</summary>
    public bool IsAdded(XElement trip) => _added.Contains(trip) && trip.Document == Document.Xml;

    /// <summary>The draft's state for its <c>.draft.json</c>: files, undo history and greyed points, by path.</summary>
    public DraftState ToState(bool open) =>
        new()
        {
            SourcePath = SourcePath,
            TargetPath = TargetPath,
            AreaId = Document.Header.AreaId,
            Open = open,
            Undo = [.. _undo.Select(e => e.ToSaved())],
            Redo = [.. _redo.Select(e => e.ToSaved())],
            CleanId = _cleanId,
            AddedPoints = [.. _added.Where(IsAdded).Select(e => ElementPath.Of(Document, e))],
        };

    /// <summary>A draft read back from its autosave: the document as it was saved, with its history and greyed points.</summary>
    public static TimetableEditor Restore(WttDocument document, DraftState state, SimKnowledge? knowledge = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(state);
        var editor = new TimetableEditor(document, state.SourcePath, state.TargetPath, knowledge);
        editor._undo.AddRange(state.Undo.Select(HistoryEntry.From));
        editor._redo.AddRange(state.Redo.Select(HistoryEntry.From));
        editor._nextId = state.Undo.Concat(state.Redo).Select(e => e.Id).DefaultIfEmpty(0).Max() + 1;
        editor._cleanId = state.CleanId;
        foreach (var path in state.AddedPoints)
        {
            if (ElementPath.Resolve(document, path) is { } trip)
            {
                editor._added.Add(trip);
            }
        }

        editor.Current = editor.Publish(editor.Current.Import, editor.Current.Validation);
        return editor;
    }

    /// <summary>The elements an edit inserts, however deep in composites.</summary>
    private static IEnumerable<XElement> Inserted(TimetableEdit edit) =>
        edit switch
        {
            InsertNodeEdit insert => [insert.Node],
            CompositeEdit composite => composite.Edits.SelectMany(Inserted),
            _ => [],
        };

    /// <summary>The user edited these points (or inside them): they are no longer greyed.</summary>
    private void Touched(TimetableEdit edit)
    {
        if (_added.Count == 0)
        {
            return;
        }

        foreach (var target in edit.Targets)
        {
            for (var e = target; e is not null; e = e.Parent)
            {
                if (_added.Remove(e))
                {
                    break;
                }
            }
        }
    }

    private DraftSnapshot Changed(TimetableEdit applied)
    {
        var import = Document.Import();
        return Current = Publish(import, _validator.Revalidate(Current.Validation, import, [applied]));
    }

    private DraftSnapshot Publish(WttDocumentImport import, ValidationResult validation)
    {
        var snapshot = new DraftSnapshot(
            ++_sequence,
            import,
            validation,
            IsDirty,
            _undo.Count > 0 ? _undo[^1].Description : null,
            _redo.Count > 0 ? _redo[^1].Description : null,
            SourcePath,
            TargetPath,
            _added.Where(IsAdded).ToHashSet());
        Current = snapshot;
        SnapshotPublished?.Invoke(this, snapshot);
        return snapshot;
    }

    /// <summary>One undo step: its records (always) and, in this session, the edit that undoes or redoes it.</summary>
    private sealed record HistoryEntry(long Id, string Description, EditRecord Forward, EditRecord Backward)
    {
        public TimetableEdit? UndoEdit { get; init; }

        public TimetableEdit? RedoEdit { get; init; }

        public static HistoryEntry From(DraftHistoryEntry saved) => new(saved.Id, saved.Description, saved.Forward, saved.Backward);

        public DraftHistoryEntry ToSaved() => new(Id, Description, Forward, Backward);
    }
}
