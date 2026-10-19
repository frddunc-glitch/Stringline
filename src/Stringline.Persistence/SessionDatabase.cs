using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Stringline.Core;
using Stringline.Gateway.Recording;
using Stringline.Planning;

namespace Stringline.Persistence;

/// <summary>A session file is not one, is damaged, or is from a newer version of Stringline.</summary>
public sealed class SessionFormatException : Exception
{
    public SessionFormatException(string message)
        : base(message)
    {
    }

    public SessionFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SessionFormatException()
    {
    }
}

/// <summary>
/// The SQLite file behind a session (<c>02-…md</c> §5): <c>meta</c> (key/value), <c>frames</c> (gateway frames of a
/// live session, as in a <c>.sslog</c>) and <c>steps</c> (the plan's edit history as <see cref="PlanStep"/>s, commands as
/// <see cref="PlanCommandJson"/>). Disruptions, notes and manual links are plan commands, so they are steps too. Not
/// thread-safe; <see cref="SessionWriter"/> serialises its use.
/// </summary>
internal sealed class SessionDatabase : IDisposable
{
    public const int Format = 1;

    public const string Extension = ".stringline";

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS frames (seq INTEGER PRIMARY KEY, at INTEGER NOT NULL, sim INTEGER, topic TEXT NOT NULL, body BLOB NOT NULL);
        CREATE TABLE IF NOT EXISTS steps (seq INTEGER PRIMARY KEY, at INTEGER NOT NULL, sim INTEGER, kind TEXT NOT NULL, command TEXT, description TEXT);
        """;

    private readonly SqliteConnection _connection;

    private SessionDatabase(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Create a new session file; fails if it exists.</summary>
    public static SessionDatabase Create(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (File.Exists(path))
        {
            throw new IOException($"{path} already exists.");
        }

        var db = Connect(path, SqliteOpenMode.ReadWriteCreate);
        try
        {
            db.Execute(Schema);
            db.SetMeta([new("format", Format.ToString(CultureInfo.InvariantCulture))]);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    /// <summary>Open an existing session file.</summary>
    /// <exception cref="SessionFormatException">It is not a session file, or is from a newer version.</exception>
    public static SessionDatabase Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} was not found.", path);
        }

        var db = Connect(path, SqliteOpenMode.ReadWrite);
        try
        {
            var meta = db.ReadMeta();
            if (!meta.TryGetValue("format", out var text) || !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var format))
            {
                throw new SessionFormatException($"{System.IO.Path.GetFileName(path)} is not a Stringline session.");
            }

            if (format > Format)
            {
                throw new SessionFormatException($"{System.IO.Path.GetFileName(path)} was saved by a newer version of Stringline (session format {format}).");
            }

            db.Execute(Schema);
            return db;
        }
        catch (SqliteException ex)
        {
            db.Dispose();
            throw new SessionFormatException($"{System.IO.Path.GetFileName(path)} is not a Stringline session or is damaged: {ex.Message}", ex);
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    public Dictionary<string, string> ReadMeta()
    {
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM meta";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(1))
            {
                meta[reader.GetString(0)] = reader.GetString(1);
            }
        }

        return meta;
    }

    public SessionInfo ReadInfo()
    {
        var info = ToInfo(ReadMeta()) with { Path = Path };
        return info with { Frames = Count("SELECT COUNT(*) FROM frames"), Steps = Count("SELECT COUNT(*) FROM steps") };
    }

    /// <summary>Write frames, steps and meta in one transaction.</summary>
    public void Save(IReadOnlyList<SslogMessage> frames, IReadOnlyList<PlanStep> steps, IReadOnlyList<KeyValuePair<string, string?>> meta)
    {
        using var transaction = _connection.BeginTransaction();
        if (frames.Count > 0)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO frames (at, sim, topic, body) VALUES ($at, $sim, $topic, $body)";
            var at = command.Parameters.Add("$at", SqliteType.Integer);
            var sim = command.Parameters.Add("$sim", SqliteType.Integer);
            var topic = command.Parameters.Add("$topic", SqliteType.Text);
            var body = command.Parameters.Add("$body", SqliteType.Blob);
            foreach (var frame in frames)
            {
                at.Value = frame.At.ToUnixTimeMilliseconds();
                sim.Value = frame.Sim is { } s ? s.Seconds : DBNull.Value;
                topic.Value = frame.Topic;
                body.Value = frame.Body.ToArray();
                command.ExecuteNonQuery();
            }
        }

        if (steps.Count > 0)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "INSERT INTO steps (at, sim, kind, command, description) VALUES ($at, $sim, $kind, $command, $description)";
            var at = command.Parameters.Add("$at", SqliteType.Integer);
            var sim = command.Parameters.Add("$sim", SqliteType.Integer);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var json = command.Parameters.Add("$command", SqliteType.Text);
            var description = command.Parameters.Add("$description", SqliteType.Text);
            foreach (var step in steps)
            {
                at.Value = step.At.ToUnixTimeMilliseconds();
                sim.Value = step.SimTime is { } s ? s.Seconds : DBNull.Value;
                kind.Value = KindText(step.Kind);
                json.Value = step.Command is { } c ? PlanCommandJson.Serialize(c) : DBNull.Value;
                description.Value = (object?)step.Description ?? DBNull.Value;
                command.ExecuteNonQuery();
            }
        }

        SetMeta(meta);
        transaction.Commit();
    }

    public List<SslogMessage> ReadFrames()
    {
        var frames = new List<SslogMessage>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT at, sim, topic, body FROM frames ORDER BY seq";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(0));
            SimTime? sim = reader.IsDBNull(1) ? null : new SimTime(reader.GetInt32(1));
            frames.Add(new SslogMessage(at, reader.GetString(2), (byte[])reader.GetValue(3), sim));
        }

        return frames;
    }

    /// <summary>The plan steps in order; those that cannot be read (e.g. a command unknown to this version) go in <paramref name="problems"/>.</summary>
    public List<PlanStep> ReadSteps(List<string> problems)
    {
        var steps = new List<PlanStep>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT seq, at, sim, kind, command, description FROM steps ORDER BY seq";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var seq = reader.GetInt64(0);
            var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));
            SimTime? sim = reader.IsDBNull(2) ? null : new SimTime(reader.GetInt32(2));
            var description = reader.IsDBNull(5) ? null : reader.GetString(5);
            if (ParseKind(reader.GetString(3)) is not { } kind)
            {
                problems.Add($"Step {seq}: unknown kind '{reader.GetString(3)}'; skipped.");
                continue;
            }

            IPlanCommand? planCommand = null;
            if (kind == PlanStepKind.Execute)
            {
                try
                {
                    planCommand = PlanCommandJson.Deserialize(reader.IsDBNull(4) ? "null" : reader.GetString(4));
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
                {
                    problems.Add($"Step {seq} ({description ?? "a plan edit"}) could not be read and was skipped: {ex.Message}");
                    continue;
                }
            }

            steps.Add(new PlanStep(kind, planCommand, description, sim, at));
        }

        return steps;
    }

    public void Dispose() => _connection.Dispose();

    internal static SessionInfo ToInfo(IReadOnlyDictionary<string, string> meta)
    {
        string? Text(string key) => meta.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        int? Int(string key) => Text(key) is { } v && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
        DateTimeOffset Time(string key) => Text(key) is { } v && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : default;
        return new SessionInfo
        {
            Kind = Text("kind") == "replay" ? SavedSessionKind.Replay : SavedSessionKind.Live,
            AreaId = Text("area_id"),
            SimName = Text("sim_name"),
            TimetablePath = Text("timetable_path"),
            TimetableName = Text("timetable_name"),
            TimetableVersion = Text("timetable_version"),
            Host = Text("host"),
            Port = Int("port"),
            RecordingPath = Text("recording_path"),
            Position = Int("position") is { } p ? new SimTime(p) : null,
            LastSimTime = Int("last_sim") is { } l ? new SimTime(l) : null,
            Created = Time("created"),
            Updated = Time("updated"),
            Ended = Text("ended") == "1",
            App = Text("app"),
        };
    }

    internal static List<KeyValuePair<string, string?>> ToMeta(SessionInfo info)
    {
        static string? Int(int? v) => v?.ToString(CultureInfo.InvariantCulture);
        static string Time(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        return
        [
            new("kind", info.Kind == SavedSessionKind.Replay ? "replay" : "live"),
            new("area_id", info.AreaId),
            new("sim_name", info.SimName),
            new("timetable_path", info.TimetablePath),
            new("timetable_name", info.TimetableName),
            new("timetable_version", info.TimetableVersion),
            new("host", info.Host),
            new("port", Int(info.Port)),
            new("recording_path", info.RecordingPath),
            new("position", Int(info.Position?.Seconds)),
            new("last_sim", Int(info.LastSimTime?.Seconds)),
            new("created", Time(info.Created)),
            new("updated", Time(info.Updated)),
            new("ended", info.Ended ? "1" : "0"),
            new("app", info.App),
        ];
    }

    private static SessionDatabase Connect(string path, SqliteOpenMode mode)
    {
        // No pooling: the file is closed when we are done with it, so it can be moved or deleted.
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            var db = new SessionDatabase(connection, path);
            db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            return db;
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new SessionFormatException($"{System.IO.Path.GetFileName(path)} is not a Stringline session or is damaged: {ex.Message}", ex);
        }
    }

    private static string KindText(PlanStepKind kind) => kind switch
    {
        PlanStepKind.Execute => "execute",
        PlanStepKind.Undo => "undo",
        PlanStepKind.Redo => "redo",
        _ => "reset",
    };

    private static PlanStepKind? ParseKind(string text) => text switch
    {
        "execute" => PlanStepKind.Execute,
        "undo" => PlanStepKind.Undo,
        "redo" => PlanStepKind.Redo,
        "reset" => PlanStepKind.Reset,
        _ => null,
    };

    private void SetMeta(IReadOnlyList<KeyValuePair<string, string?>> meta)
    {
        if (meta.Count == 0)
        {
            return;
        }

        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        foreach (var (k, v) in meta)
        {
            key.Value = k;
            value.Value = (object?)v ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private int Count(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
