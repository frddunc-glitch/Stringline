using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;

namespace Stringline.Persistence;

/// <summary>
/// Recordings and saved sessions as sources of what trains did in play (S41's sim knowledge): which files show a sim,
/// and their events.
/// </summary>
public static class PlayFiles
{
    /// <summary>Where the app saves sessions by default: <c>Documents\Stringline\sessions</c>.</summary>
    public static string DefaultSessionsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify), "Stringline", "sessions");

    /// <summary>
    /// The play files for a sim among the saved sessions in <paramref name="folder"/>: each live session itself, and
    /// each replay session's recording (once, and only if it is still there). A missing folder gives none.
    /// </summary>
    public static IReadOnlyList<string> ForArea(string folder, string areaId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        var files = new List<string>();
        foreach (var session in SessionCatalogue.List(folder ?? string.Empty).Where(s => string.Equals(s.AreaId?.Trim(), areaId.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var path = session.Kind == SavedSessionKind.Live ? session.Path : session.RecordingPath;
            if (path is not null && File.Exists(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(path);
            }
        }

        return [.. files.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The events of a recording (<c>.sslog</c>) or saved session: a live one's frames, a replay's recording.
    /// </summary>
    /// <exception cref="IOException">It cannot be read.</exception>
    /// <exception cref="InvalidDataException">It is damaged.</exception>
    public static IEnumerable<GatewayEvent> Read(string path, MessageParser? parser = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        parser ??= new MessageParser();
        if (!string.Equals(Path.GetExtension(path), SessionCatalogue.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return Parse(SslogRecording.Load(path), parser);
        }

        SavedSession saved;
        try
        {
            saved = SavedSession.Load(path);
        }
        catch (Exception ex) when (ex is SessionFormatException or Microsoft.Data.Sqlite.SqliteException)
        {
            throw new InvalidDataException(ex.Message, ex);
        }

        if (saved.Info.Kind == SavedSessionKind.Live)
        {
            return saved.Events(parser);
        }

        return saved.Info.RecordingPath is { } recording ? Parse(SslogRecording.Load(recording), parser) : [];
    }

    private static IEnumerable<GatewayEvent> Parse(SslogRecording recording, MessageParser parser) =>
        recording.Messages.SelectMany(m => parser.Parse(m.ToGatewayMessage()));
}
