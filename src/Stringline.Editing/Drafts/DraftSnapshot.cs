using System.Xml.Linq;
using Stringline.Core.Timetables;
using Stringline.Editing.Validation;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Drafts;

/// <summary>
/// One revision of a draft as the editor published it (<c>08-…md</c> §3.2): the document's import and its issues, and
/// where the draft stands (unsaved changes, undo and redo, version, file). The import and the issues never change;
/// <see cref="Document"/> is the live document, which only the editor's thread may read.
/// </summary>
public sealed class DraftSnapshot
{
    internal DraftSnapshot(
        int sequence,
        WttDocumentImport import,
        ValidationResult validation,
        bool isDirty,
        string? undoDescription,
        string? redoDescription,
        string? sourcePath,
        string? targetPath,
        IReadOnlySet<XElement> addedPoints)
    {
        Sequence = sequence;
        Import = import;
        Validation = validation;
        IsDirty = isDirty;
        UndoDescription = undoDescription;
        RedoDescription = redoDescription;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        AddedPoints = addedPoints;
        Version = import.Document.Header.Version;
    }

    /// <summary>Goes up by one with each snapshot the editor publishes.</summary>
    public int Sequence { get; }

    public WttDocument Document => Import.Document;

    /// <summary>The document's revision the snapshot is of.</summary>
    public long Revision => Import.Revision;

    public WttDocumentImport Import { get; }

    /// <summary>The draft as the saved file would import: what the graph and the checks see.</summary>
    public WorkingTimetable Timetable => Import.Timetable;

    public ValidationResult Validation { get; }

    /// <summary>Changes not yet saved to the timetable file (the draft itself is autosaved).</summary>
    public bool IsDirty { get; }

    public string? UndoDescription { get; }

    public string? RedoDescription { get; }

    public bool CanUndo => UndoDescription is not null;

    public bool CanRedo => RedoDescription is not null;

    /// <summary>The timetable the draft was made from, if any.</summary>
    public string? SourcePath { get; }

    /// <summary>Where Save writes, once the draft has been saved (or chosen to replace its source).</summary>
    public string? TargetPath { get; }

    public WttVersion? Version { get; }

    /// <summary>Timing points Stringline inserted (a quick fix's chain), shown greyed until the user edits them.</summary>
    public IReadOnlySet<XElement> AddedPoints { get; }
}
