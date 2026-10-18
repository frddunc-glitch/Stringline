using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Core.Json;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Stomp;

namespace Stringline.Gateway.Replay;

/// <summary>How a <see cref="ReplayServer"/> listens and plays.</summary>
public sealed record ReplayServerOptions
{
    /// <summary>Address to listen on; loopback by default, so only this PC can connect.</summary>
    public IPAddress Address { get; init; } = IPAddress.Loopback;

    /// <summary>Port to listen on; 0 picks a free one (see <see cref="ReplayServer.Port"/>).</summary>
    public int Port { get; init; } = GatewayTopics.DefaultPort;

    /// <summary>Speed and start time. <see cref="ReplayOptions.StartPaused"/> is honoured as well as <see cref="WaitForClient"/>.</summary>
    public ReplayOptions Replay { get; init; } = new();

    /// <summary>
    /// Hold playback until the first client is ready, so nothing is missed while starting the app: until it
    /// sends its first command (Stringline sends <c>idrequest</c> once subscribed), or <see cref="SubscribeGrace"/>
    /// after its first SUBSCRIBE, whichever comes first.
    /// </summary>
    public bool WaitForClient { get; init; } = true;

    /// <summary>With <see cref="WaitForClient"/>, how long after a client's first SUBSCRIBE to start anyway.</summary>
    public TimeSpan SubscribeGrace { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// A stand-in for SimSig's Interface Gateway that plays a <c>.sslog</c> recording to real STOMP clients, so
/// <see cref="GatewayClient"/> and the app can be exercised end to end without SimSig.
/// <list type="bullet">
/// <item>Answers <c>CONNECT</c> with <c>CONNECTED</c> (<c>version:1.1</c>, <c>server:SimSig</c>), accepting any login.</item>
/// <item>Sends each recorded frame, paced by a <see cref="ReplayTimeline"/>, to the clients subscribed to its topic.
/// Like a running sim, it does not wait for slow or absent clients.</item>
/// <item>Answers <c>idrequest</c> with the latest <c>clock_msg</c>, <c>snapshot</c> with the latest <c>SG_MSG</c> of every
/// signalling object seen so far, and <c>ttsummary</c> with the latest recorded summary. Replies go to the topic,
/// so every subscriber sees them.</item>
/// <item>Logs and ignores other commands; malformed JSON is logged as the error SimSig would show.</item>
/// <item>Sends a RECEIPT for any client frame with a <c>receipt</c> header, after handling it.</item>
/// </list>
/// A start time (<see cref="ReplayOptions.StartAt"/>) is applied before listening: earlier frames build up the
/// clock, signalling and summary state but are not sent, as if the client joined a sim already running.
/// </summary>
public sealed partial class ReplayServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ILogger _logger;
    private readonly ReplayServerOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly SimState _state = new();
    private readonly TimeProvider _time;
    private Task _acceptLoop = Task.CompletedTask;
    private Task _playback = Task.CompletedTask;
    private int _nextSession;
    private long _sent;
    private long _messageId;
    private int _started;

    private ReplayServer(SslogRecording recording, ReplayServerOptions options, ILogger logger, TimeProvider time)
    {
        _options = options;
        _logger = logger;
        _time = time;
        _listener = new TcpListener(options.Address, options.Port);

        // Pre-roll to the start time, so a client connecting straight away gets the right clock and snapshot.
        var start = options.Replay.StartAt is { } at ? recording.IndexOf(at) : 0;
        for (var i = 0; i < start; i++)
        {
            _state.Apply(recording.Messages[i]);
        }

        var rest = start == 0 ? recording : new SslogRecording(recording.Header, [.. recording.Messages.Skip(start)], recording.Connections);
        Timeline = new ReplayTimeline(
            rest,
            options.Replay with { StartAt = null, StartPaused = options.Replay.StartPaused || options.WaitForClient },
            time);
    }

    /// <summary>Speed and pause controls. Seeking forward works (skipped frames update state but are not sent); seeking back is not supported by SimSig either.</summary>
    public ReplayTimeline Timeline { get; }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Clients currently connected (after CONNECT).</summary>
    public int ClientCount => _sessions.Values.Count(s => s.IsConnected);

    /// <summary>MESSAGE frames sent so far, over all clients.</summary>
    public long MessagesSent => Interlocked.Read(ref _sent);

    /// <summary>Completes when the last recorded frame has been played. The server keeps serving until disposed.</summary>
    public Task Completion => _playback;

    /// <summary>Raised when a client completes CONNECT. The argument is the session number.</summary>
    public event EventHandler<int>? ClientConnected;

    /// <summary>Loads nothing itself: pass a loaded recording. Throws <see cref="SocketException"/> if the port is in use.</summary>
    public static ReplayServer Start(SslogRecording recording, ReplayServerOptions? options = null, ILogger<ReplayServer>? logger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var server = new ReplayServer(recording, options ?? new ReplayServerOptions(), (ILogger?)logger ?? NullLogger.Instance, timeProvider ?? TimeProvider.System);
        server._listener.Start();
        server._acceptLoop = server.AcceptLoopAsync();
        server._playback = server.PlaybackAsync();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var session in _sessions.Values)
        {
            session.Close();
        }

        await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await _playback.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task PlaybackAsync()
    {
        try
        {
            await foreach (var frame in Timeline.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                _state.Apply(frame.Message);
                if (!frame.FastForward)
                {
                    Publish(frame.Message.Topic, frame.Message.Body);
                }
            }

            LogFinished(Timeline.Recording.Messages.Count);
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private void Publish(string topic, ReadOnlyMemory<byte> body)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.IsConnected && session.SubscriptionFor(topic) is { } id)
            {
                List<KeyValuePair<string, string>> headers =
                [
                    new("destination", topic),
                    new("message-id", Interlocked.Increment(ref _messageId).ToString(CultureInfo.InvariantCulture)),
                    new("subscription", id),
                    new("content-length", body.Length.ToString(CultureInfo.InvariantCulture)),
                ];
                if (session.Send(new StompFrame(StompCommands.Message, headers, body)))
                {
                    Interlocked.Increment(ref _sent);
                }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        LogListening(_listener.LocalEndpoint);
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            var id = Interlocked.Increment(ref _nextSession);
            var session = new Session(client);
            _sessions[id] = session;
            _ = Task.Run(() => SessionLoopAsync(id, session));
        }
    }

    private async Task SessionLoopAsync(int id, Session session)
    {
        var endpoint = session.Client.Client.RemoteEndPoint?.ToString() ?? "?";
        var writer = session.RunWriterAsync(_stop.Token);
        try
        {
            var reader = new StompFrameReader(session.Client.GetStream());
            while (await reader.ReadFrameAsync(_stop.Token).ConfigureAwait(false) is { } frame)
            {
                if (!Handle(id, session, frame, endpoint))
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Client went away or sent garbage; either way the session is over.
            LogSessionEnded(id, ex.Message);
        }
        finally
        {
            _sessions.TryRemove(id, out _);
            session.Close();
            await writer.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            session.Dispose();
            if (session.WasConnected)
            {
                LogClientLeft(id, endpoint);
            }
        }
    }

    /// <summary>Handles one client frame; false ends the session.</summary>
    private bool Handle(int id, Session session, StompFrame frame, string endpoint)
    {
        if (!session.IsConnected && frame.Command is not (StompCommands.Connect or "STOMP"))
        {
            session.Send(new StompFrame(StompCommands.Error, [new("message", "Expected CONNECT")]));
            return false;
        }

        switch (frame.Command)
        {
            case StompCommands.Connect or "STOMP":
                session.Send(new StompFrame(StompCommands.Connected, [new("version", "1.1"), new("server", "SimSig")]));
                session.MarkConnected();
                LogClientConnected(id, endpoint, frame.GetHeader("login") is { Length: > 0 } login ? login : "(none)");
                ClientConnected?.Invoke(this, id);
                break;
            case StompCommands.Subscribe when frame.GetHeader("destination") is { } destination:
                session.Subscribe(destination, frame.GetHeader("id") ?? "0");
                if (_options.WaitForClient && Volatile.Read(ref _started) == 0)
                {
                    _ = StartAfterGraceAsync();
                }

                break;
            case StompCommands.Unsubscribe when frame.GetHeader("id") is { } subscription:
                session.Unsubscribe(subscription);
                break;
            case StompCommands.Send:
                OnCommand(frame.GetHeader("destination") ?? string.Empty, frame.Body);
                StartPlayback();
                break;
        }

        // Any frame but CONNECT may ask for a receipt once it has been handled (STOMP 1.1).
        if (frame.Command != StompCommands.Connect && frame.GetHeader("receipt") is { } receipt)
        {
            session.Send(new StompFrame(StompCommands.Receipt, [new("receipt-id", receipt)]));
        }

        return frame.Command != StompCommands.Disconnect;
    }

    /// <summary>Starts playback held by <see cref="ReplayServerOptions.WaitForClient"/>; only the first call counts.</summary>
    private void StartPlayback()
    {
        if (_options.WaitForClient && Interlocked.Exchange(ref _started, 1) == 0)
        {
            LogStarting();
            if (!_options.Replay.StartPaused)
            {
                Timeline.Resume();
            }
        }
    }

    private async Task StartAfterGraceAsync()
    {
        try
        {
            await Task.Delay(_options.SubscribeGrace, _time, _stop.Token).ConfigureAwait(false);
            StartPlayback();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Waits until every client has been sent everything queued for it, or <paramref name="timeout"/> passes.</summary>
    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetTimestamp();
        while (_sessions.Values.Any(s => s.Pending > 0) && _time.GetElapsedTime(deadline) < timeout)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnCommand(string destination, ReadOnlyMemory<byte> body)
    {
        List<string> names;
        try
        {
            names = TopLevelNames(body.Span);
        }
        catch (JsonException ex)
        {
            LogMalformedCommand(destination, ex.Message, Encoding.UTF8.GetString(body.Span));
            return;
        }

        foreach (var name in names)
        {
            var (topic, reply) = name.ToUpperInvariant() switch
            {
                "IDREQUEST" => (GatewayTopics.SimSig, _state.Clock),
                "SNAPSHOT" => (GatewayTopics.TrainDescriber, _state.Snapshot()),
                "TTSUMMARY" => (GatewayTopics.TrainMovementSummary, _state.Summary),
                _ => (null, null),
            };

            if (topic is null)
            {
                LogIgnoredCommand(name, destination);
            }
            else if (reply is null)
            {
                LogNothingToAnswer(name);
            }
            else
            {
                Publish(topic, reply);
            }
        }
    }

    private static List<string> TopLevelNames(ReadOnlySpan<byte> body)
    {
        var names = new List<string>();
        var reader = new Utf8JsonReader(body);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The command is not a JSON object.");
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            names.Add(reader.GetString()!);
            reader.Read();
            reader.Skip();
        }

        if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
        {
            throw new JsonException("Trailing data after the command object.");
        }

        return names;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Replay server listening on {Endpoint}")]
    private partial void LogListening(EndPoint endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Session} connected from {Endpoint} (login {Login})")]
    private partial void LogClientConnected(int session, string endpoint, string login);

    [LoggerMessage(Level = LogLevel.Information, Message = "Client {Session} ({Endpoint}) disconnected")]
    private partial void LogClientLeft(int session, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ignored {Command} on {Destination}: a replay cannot act on commands")]
    private partial void LogIgnoredCommand(string command, string destination);

    [LoggerMessage(Level = LogLevel.Information, Message = "Nothing to answer {Command} with yet: the recording has not reached that state")]
    private partial void LogNothingToAnswer(string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed command on {Destination} (SimSig would show an error dialog): {Reason} {Body}")]
    private partial void LogMalformedCommand(string destination, string reason, string body);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Session {Session} ended: {Reason}")]
    private partial void LogSessionEnded(int session, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "First client ready: playing")]
    private partial void LogStarting();

    [LoggerMessage(Level = LogLevel.Information, Message = "End of recording: all {Count} frames played")]
    private partial void LogFinished(int count);

    /// <summary>What the sim "knows" at the playhead: enough to answer idrequest, snapshot and ttsummary.</summary>
    private sealed class SimState
    {
        private readonly Lock _lock = new();

        // Latest SG_MSG value per object, in first-seen order (Dictionary keeps insertion order without removals).
        private readonly Dictionary<string, byte[]> _signalling = new(StringComparer.Ordinal);
        private byte[]? _clock;
        private byte[]? _summary;

        public byte[]? Clock
        {
            get
            {
                lock (_lock)
                {
                    return _clock;
                }
            }
        }

        public byte[]? Summary
        {
            get
            {
                lock (_lock)
                {
                    return _summary;
                }
            }
        }

        public void Apply(SslogMessage message)
        {
            var body = message.Body.Span;
            try
            {
                var reader = new Utf8JsonReader(body);
                if (!reader.Read())
                {
                    return;
                }

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    lock (_lock)
                    {
                        _summary = body.ToArray();
                    }

                    return;
                }

                if (reader.TokenType != JsonTokenType.StartObject)
                {
                    return;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var name = reader.GetString();
                    reader.Read();
                    var start = (int)reader.TokenStartIndex;
                    var value = reader;
                    reader.Skip();
                    var raw = body[start..(int)reader.BytesConsumed];
                    if (string.Equals(name, "clock_msg", StringComparison.OrdinalIgnoreCase))
                    {
                        lock (_lock)
                        {
                            _clock = body.ToArray();
                        }
                    }
                    else if (string.Equals(name, "SG_MSG", StringComparison.OrdinalIgnoreCase) && ObjectId(ref value) is { } objectId)
                    {
                        lock (_lock)
                        {
                            _signalling[objectId] = raw.ToArray();
                        }
                    }
                }
            }
            catch (JsonException)
            {
                // A malformed recorded body: the parser reports it; it simply adds no state here.
            }
        }

        /// <summary>One object with an <c>SG_MSG</c> key per signalling object, as SimSig sends it; null before any are seen.</summary>
        public byte[]? Snapshot()
        {
            var output = new ArrayBufferWriter<byte>();
            output.Write("{"u8);
            lock (_lock)
            {
                if (_signalling.Count == 0)
                {
                    return null;
                }

                var first = true;
                foreach (var value in _signalling.Values)
                {
                    output.Write(first ? "\"SG_MSG\":"u8 : ",\"SG_MSG\":"u8);
                    output.Write(value);
                    first = false;
                }
            }

            output.Write("}"u8);
            return output.WrittenSpan.ToArray();
        }

        private static string? ObjectId(ref Utf8JsonReader reader)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                if (string.Equals(name, "obj_id", StringComparison.OrdinalIgnoreCase) && LenientJson.TryGetString(ref reader, out var id))
                {
                    return id.Trim();
                }

                reader.Skip();
            }

            return null;
        }
    }

    private sealed class Session(TcpClient client) : IDisposable
    {
        private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>();
        private readonly ConcurrentDictionary<string, string> _subscriptions = new(StringComparer.Ordinal);
        private volatile bool _connected;

        public TcpClient Client { get; } = client;

        public bool IsConnected => _connected;

        public bool WasConnected { get; private set; }

        /// <summary>Frames queued but not yet written.</summary>
        public int Pending => _outbound.Reader.Count;

        public void MarkConnected()
        {
            _connected = true;
            WasConnected = true;
        }

        public string? SubscriptionFor(string topic) => _subscriptions.TryGetValue(topic, out var id) ? id : null;

        public void Subscribe(string topic, string id) => _subscriptions[topic] = id;

        public void Unsubscribe(string id)
        {
            foreach (var (topic, existing) in _subscriptions)
            {
                if (existing == id)
                {
                    _subscriptions.TryRemove(topic, out _);
                }
            }
        }

        /// <summary>Queues a frame; false if the session is closing.</summary>
        public bool Send(StompFrame frame) => _outbound.Writer.TryWrite(StompFrameWriter.Encode(frame));

        /// <summary>Writes queued frames until the session closes, then flushes what is left (e.g. an ERROR).</summary>
        public async Task RunWriterAsync(CancellationToken cancellationToken)
        {
            try
            {
                var stream = Client.GetStream();
                await foreach (var bytes in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    if (_outbound.Reader.Count == 0)
                    {
                        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException or SocketException)
            {
                _outbound.Writer.TryComplete();
            }
            finally
            {
                Client.Close();
            }
        }

        public void Close()
        {
            _connected = false;
            _outbound.Writer.TryComplete();
        }

        public void Dispose() => Client.Dispose();
    }
}
