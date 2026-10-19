using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Planning;

namespace Stringline.Persistence;

/// <summary>
/// A session read back from its file, to resume it or review it (SES-1, SES-3): what it is, the live frames it kept and
/// its plan steps. Resuming feeds <see cref="Events"/> to the engine and restores the plan from <see cref="Steps"/>;
/// review replays <see cref="ToRecording"/> (live) or the recording it refers to (replay).
/// </summary>
public sealed class SavedSession
{
    private SavedSession(SessionInfo info, IReadOnlyList<SslogMessage> frames, IReadOnlyList<PlanStep> steps, IReadOnlyList<string> problems)
    {
        Info = info;
        Frames = frames;
        Steps = steps;
        Problems = problems;
    }

    public SessionInfo Info { get; }

    public string Path => Info.Path!;

    /// <summary>Live: the frames the engine used, in order. Empty for a replay session.</summary>
    public IReadOnlyList<SslogMessage> Frames { get; }

    /// <summary>The plan's steps, in order.</summary>
    public IReadOnlyList<PlanStep> Steps { get; }

    /// <summary>Steps that could not be read and were skipped.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>The history the steps leave, for messages ("3 edits").</summary>
    public int Edits => PlanLog.Fold(Steps).Done.Count;

    /// <exception cref="SessionFormatException">It is not a session file, is damaged, or is from a newer version.</exception>
    /// <exception cref="IOException">It cannot be read.</exception>
    public static SavedSession Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var db = SessionDatabase.Open(path);
        try
        {
            var problems = new List<string>();
            var info = db.ReadInfo();
            return new SavedSession(info, db.ReadFrames(), db.ReadSteps(problems), problems);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            throw new SessionFormatException($"{System.IO.Path.GetFileName(path)} is damaged: {ex.Message}", ex);
        }
    }

    /// <summary>The live frames' events, stamped with the times they arrived, as the engine first saw them.</summary>
    public IEnumerable<GatewayEvent> Events(MessageParser? parser = null)
    {
        parser ??= new MessageParser();
        foreach (var frame in Frames)
        {
            foreach (var e in parser.Parse(frame.Topic, frame.Body.Span, frame.At))
            {
                yield return e;
            }
        }
    }

    /// <summary>A live session's frames as a recording, to replay for review.</summary>
    public SslogRecording ToRecording()
    {
        var header = new SslogHeader(SslogFormat.Version, Info.App ?? "Stringline", Info.Created, Info.Host, Info.Port, []);
        return new SslogRecording(header, Frames);
    }
}

/// <summary>The session files in a folder.</summary>
public static class SessionCatalogue
{
    public const string Extension = SessionDatabase.Extension;

    /// <summary>Every readable session in <paramref name="folder"/>, newest saved first. Unreadable files are skipped.</summary>
    public static IReadOnlyList<SessionInfo> List(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var sessions = new List<SessionInfo>();
        foreach (var path in Directory.EnumerateFiles(folder, "*" + Extension))
        {
            if (ReadInfo(path) is { } info)
            {
                sessions.Add(info);
            }
        }

        return [.. sessions.OrderByDescending(s => s.Updated)];
    }

    /// <summary>The most recently saved session, if it was left running (not ended): the one to offer for resuming.</summary>
    public static SessionInfo? LatestUnfinished(string folder) => List(folder) is [{ Ended: false } latest, ..] ? latest : null;

    /// <summary>What a session file says about itself, or null if it is not one or cannot be read.</summary>
    public static SessionInfo? ReadInfo(string path)
    {
        try
        {
            using var db = SessionDatabase.Open(path);
            return db.ReadInfo();
        }
        catch (Exception ex) when (ex is SessionFormatException or IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
    }
}
