using Stringline.Core;

namespace Stringline.Persistence;

/// <summary>Where a saved session's gateway events came from.</summary>
public enum SavedSessionKind
{
    /// <summary>A live SimSig connection; the session file keeps the frames.</summary>
    Live,

    /// <summary>A replayed recording; the session file refers to the <c>.sslog</c> and keeps how far it got.</summary>
    Replay,
}

/// <summary>What a session file says about itself (its <c>meta</c> table), for listing, resuming and review.</summary>
public sealed record SessionInfo
{
    public SavedSessionKind Kind { get; init; }

    /// <summary>The sim's ID (<c>area_id</c>), from the clock or the timetable.</summary>
    public string? AreaId { get; init; }

    /// <summary>The sim's display name, e.g. from the profile.</summary>
    public string? SimName { get; init; }

    /// <summary>The timetable the plan was made on, as last loaded.</summary>
    public string? TimetablePath { get; init; }

    /// <summary>The timetable's own name, for checking a different file is the same timetable.</summary>
    public string? TimetableName { get; init; }

    /// <summary>Live: the gateway connected to.</summary>
    public string? Host { get; init; }

    public int? Port { get; init; }

    /// <summary>Replay: the recording replayed.</summary>
    public string? RecordingPath { get; init; }

    /// <summary>Replay: how far it had got.</summary>
    public SimTime? Position { get; init; }

    /// <summary>The latest sim time seen: from the frames, the plan's steps or the replay's position.</summary>
    public SimTime? LastSimTime { get; init; }

    public DateTimeOffset Created { get; init; }

    /// <summary>When the file was last saved.</summary>
    public DateTimeOffset Updated { get; init; }

    /// <summary>
    /// The session was ended (disconnected, or another one started). False if the app closed or stopped while it was
    /// running, so it is offered for resuming at the next start.
    /// </summary>
    public bool Ended { get; init; }

    /// <summary>The app that wrote it, e.g. <c>Stringline 0.1.0</c>.</summary>
    public string? App { get; init; }

    /// <summary>Frames kept (live). Read from the file; ignored when writing.</summary>
    public int Frames { get; init; }

    /// <summary>Plan steps kept. Read from the file; ignored when writing.</summary>
    public int Steps { get; init; }

    /// <summary>The file, when read from one.</summary>
    public string? Path { get; init; }

    /// <summary>A name for lists and the title: the sim, then when it was made.</summary>
    public string DisplayName => $"{(SimName is { Length: > 0 } name ? name : AreaId is { Length: > 0 } area ? area : "SimSig")} {Created.ToLocalTime():yyyy-MM-dd HH:mm}";
}
