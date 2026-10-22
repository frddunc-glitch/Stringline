using System.Security.Cryptography;
using System.Text.Json;
using Stringline.Timetable;
using Stringline.Timetable.Editing;

namespace Stringline.Editing.Drafts;

/// <summary>One undo step as saved: its records both ways (<see cref="TimetableEdit.ApplyRecorded"/>).</summary>
public sealed record DraftHistoryEntry(long Id, string Description, EditRecord Forward, EditRecord Backward);

/// <summary>
/// A draft's <c>.draft.json</c> (<c>08-…md</c> §6), beside its <c>.wtt</c>: where it came from and is saved to, its
/// undo history, and whether it was open, so a draft open when the app stopped can be offered back at the next start.
/// </summary>
public sealed record DraftState
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;

    public string? SourcePath { get; init; }

    public string? TargetPath { get; init; }

    public string? AreaId { get; init; }

    /// <summary>The draft was open in the editor when this was written: true until it is closed.</summary>
    public bool Open { get; init; }

    /// <summary>The <c>.wtt</c>'s SHA-256 when this was written; the history fits only that file.</summary>
    public string? WttHash { get; init; }

    public IReadOnlyList<DraftHistoryEntry> Undo { get; init; } = [];

    public IReadOnlyList<DraftHistoryEntry> Redo { get; init; } = [];

    /// <summary>The undo step the timetable file was last saved at (0: none), so unsaved changes are known again.</summary>
    public long CleanId { get; init; }

    /// <summary>Timing points Stringline inserted that the user has not edited (greyed), by path.</summary>
    public IReadOnlyList<string> AddedPoints { get; init; } = [];

    public DateTimeOffset Written { get; init; }
}

/// <summary>A draft in the drafts folder.</summary>
/// <param name="Path">The draft's <c>.wtt</c>.</param>
/// <param name="Name">The timetable's name.</param>
public sealed record DraftFile(string Path, string Name, DraftState State, DateTime LastWriteUtc);

/// <summary>A draft read back: its document and state, and why its history was dropped, if it was.</summary>
public sealed record DraftOpened(WttDocument Document, DraftState State, string? Problem);

/// <summary>
/// The drafts folder (<c>Documents\Stringline\drafts</c>, <c>08-…md</c> §6): each draft is a <c>.wtt</c>, written as the
/// editor's document is (no version change), and a <c>.draft.json</c>. Both are written atomically, so a crash loses at
/// most the edits since the last autosave.
/// </summary>
public sealed class DraftStore(string folder)
{
    public const string StateExtension = ".draft.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Folder { get; } = folder ?? throw new ArgumentNullException(nameof(folder));

    public static string StatePath(string draftPath) => Path.ChangeExtension(draftPath, null) + StateExtension;

    /// <summary>A new draft's path: the source's name (or the sim's, for a blank draft), made unique in the folder.</summary>
    public string NewPath(string? sourcePath, string? areaId)
    {
        var stem = sourcePath is null ? $"{(string.IsNullOrWhiteSpace(areaId) ? "timetable" : areaId)} new" : Path.GetFileNameWithoutExtension(sourcePath);
        return TimetableNames.Unique(Folder, stem + ".wtt");
    }

    /// <summary>Writes the draft's document and state (the state's hash set to the document's).</summary>
    public static void Write(string draftPath, WttDocument document, DraftState state)
    {
        ArgumentException.ThrowIfNullOrEmpty(draftPath);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(state);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(draftPath))!);
        var bytes = WttWriter.ToBytes(document);
        WriteAtomically(draftPath, bytes);
        WriteState(draftPath, state with { WttHash = Hash(bytes), Written = DateTimeOffset.Now });
    }

    /// <summary>Marks a draft closed, so it is not offered back at the next start (it stays in the folder).</summary>
    public static void MarkClosed(string draftPath)
    {
        if (ReadState(draftPath) is { } state && state.Open)
        {
            WriteState(draftPath, state with { Open = false });
        }
    }

    public static void Delete(string draftPath)
    {
        File.Delete(draftPath);
        File.Delete(StatePath(draftPath));
    }

    /// <summary>Reads a draft. A history that no longer fits its <c>.wtt</c> (written apart, or damaged) is dropped and said why.</summary>
    /// <exception cref="WttFormatException">The <c>.wtt</c> is not a timetable.</exception>
    /// <exception cref="IOException">It cannot be read.</exception>
    public static DraftOpened Open(string draftPath)
    {
        var bytes = File.ReadAllBytes(draftPath);
        var document = WttDocument.Load(new MemoryStream(bytes));
        document.SourcePath = Path.GetFullPath(draftPath);
        var state = ReadState(draftPath);
        if (state is null)
        {
            return new DraftOpened(document, new DraftState { AreaId = document.Header.AreaId }, File.Exists(StatePath(draftPath)) ? "Its history could not be read." : null);
        }

        if (state.WttHash is { } hash && hash != Hash(bytes))
        {
            return new DraftOpened(document, state with { Undo = [], Redo = [], AddedPoints = [], CleanId = -1 }, "Its undo history does not match the timetable saved beside it, so it was left out.");
        }

        return new DraftOpened(document, state, null);
    }

    /// <summary>The drafts in the folder, newest first.</summary>
    public IReadOnlyList<DraftFile> List()
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var drafts = new List<DraftFile>();
        foreach (var path in Directory.EnumerateFiles(Folder, "*.wtt"))
        {
            var state = ReadState(path) ?? new DraftState();
            var name = WttCatalogue.ReadInfo(path)?.Name is { Length: > 0 } title ? title : Path.GetFileNameWithoutExtension(path);
            drafts.Add(new DraftFile(path, name, state, File.GetLastWriteTimeUtc(path)));
        }

        return [.. drafts.OrderByDescending(d => d.LastWriteUtc)];
    }

    /// <summary>The newest draft that was open when the app last stopped (a crash, or Windows shutting down), if any.</summary>
    public DraftFile? FindUnclosed() => List().FirstOrDefault(d => d.State.Open);

    public static DraftState? ReadState(string draftPath)
    {
        try
        {
            var state = JsonSerializer.Deserialize<DraftState>(File.ReadAllText(StatePath(draftPath)), Json);
            return state is { Format: DraftState.CurrentFormat } ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void WriteState(string draftPath, DraftState state) =>
        WriteAtomically(StatePath(draftPath), JsonSerializer.SerializeToUtf8Bytes(state, Json));

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
