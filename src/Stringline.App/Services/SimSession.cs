using System.IO;
using Microsoft.Extensions.Logging;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Persistence;
using Stringline.Planning;

namespace Stringline.App.Services;

internal enum SessionKind
{
    Live,
    Replay,
}

internal enum SessionState
{
    Connecting,
    Connected,

    /// <summary>Live: the connection dropped and is being retried.</summary>
    Reconnecting,
    Playing,
    Paused,

    /// <summary>Replay: the recording has been played to the end.</summary>
    Finished,
    Stopped,

    /// <summary>Ended with an error, e.g. SimSig refused the login.</summary>
    Failed,
}

/// <param name="Detail">Why, for a failure or a dropped connection; otherwise informational.</param>
internal sealed record SessionStatus(SessionState State, string? Detail = null, TimeSpan? RetryIn = null)
{
    public bool IsActive => State is not (SessionState.Stopped or SessionState.Failed or SessionState.Finished);
}

/// <summary>
/// One source of gateway events feeding the <see cref="PlanningEngine"/>: a live SimSig connection (optionally recorded)
/// or a replayed <c>.sslog</c>. Status changes are raised from background threads.
/// </summary>
internal sealed partial class SimSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly GatewayClient? _client;
    private readonly SessionRecorder? _recorder;
    private readonly Lock _lock = new();
    private SessionStatus _status;
    private Task _pump = Task.CompletedTask;

    private SimSession(SessionKind kind, string description, SessionStatus initial, ILogger logger, GatewayClient? client, ReplaySource? replay, SessionRecorder? recorder, GatewayClientOptions? options)
    {
        Kind = kind;
        Description = description;
        _status = initial;
        _logger = logger;
        _client = client;
        Replay = replay;
        _recorder = recorder;
        Options = options;
    }

    public event EventHandler<SessionStatus>? StatusChanged;

    public SessionKind Kind { get; }

    /// <summary>host:port, or the recording's file name.</summary>
    public string Description { get; }

    public GatewayClientOptions? Options { get; }

    public ReplaySource? Replay { get; }

    public ReplayTimeline? Timeline => Replay?.Timeline;

    public SessionStatus Status
    {
        get
        {
            lock (_lock)
            {
                return _status;
            }
        }
    }

    /// <summary>Completes when the source ends, fails or is stopped.</summary>
    public Task Completion => _pump;

    /// <summary>
    /// Connect to SimSig and feed the engine. A recorder, if given, sees every frame and connection change; the session
    /// store, if given, the frames it keeps. <paramref name="fresh"/> false carries on from the engine's state (a resumed
    /// session) instead of starting a new one.
    /// </summary>
    public static SimSession StartLive(GatewayClientOptions options, PlanningEngine engine, SessionRecorder? recorder, ILoggerFactory loggerFactory, SessionWriter? store = null, bool fresh = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(engine);
        var client = new GatewayClient(options, loggerFactory.CreateLogger<GatewayClient>());
        var source = new LiveGatewaySource(client, new MessageParser(loggerFactory.CreateLogger<MessageParser>()));
        var session = new SimSession(SessionKind.Live, $"{options.Host}:{options.Port}", new SessionStatus(SessionState.Connecting), loggerFactory.CreateLogger<SimSession>(), client, null, recorder, options);
        client.ConnectionChanged += session.OnConnectionChanged;
        if (recorder is not null || store is not null)
        {
            source.MessageParsed += (_, m) =>
            {
                recorder?.OnMessage(m.Message, m.Events);
                store?.OnMessage(m.Message, m.Events);
            };
        }

        session.Start(engine, source, fresh);
        return session;
    }

    /// <summary>
    /// Replay a recording into the engine. <paramref name="fresh"/> false keeps the plan and drops only the live state,
    /// as a seek back does (restarting a finished replay).
    /// </summary>
    public static SimSession StartReplay(SslogRecording recording, string fileName, ReplayOptions options, PlanningEngine engine, ILoggerFactory loggerFactory, bool fresh = true)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(engine);
        var replay = new ReplaySource(recording, options, new MessageParser(loggerFactory.CreateLogger<MessageParser>()));
        var initial = new SessionStatus(options.StartPaused ? SessionState.Paused : SessionState.Playing);
        var session = new SimSession(SessionKind.Replay, fileName, initial, loggerFactory.CreateLogger<SimSession>(), null, replay, null, null);
        session.Start(engine, replay, fresh);
        return session;
    }

    /// <summary>Send a command to SimSig (live only). Failures are logged, not thrown.</summary>
    /// <returns>True if it was sent; false for a replay, or if it could not be (not connected, say).</returns>
    public async Task<bool> SendAsync(GatewayCommand command)
    {
        if (_client is null)
        {
            return false;
        }

        try
        {
            await _client.SendAsync(command, _stop.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
        {
            LogSendFailed(ex);
            return false;
        }
    }

    public void Pause()
    {
        if (Timeline is { } timeline && Status.State == SessionState.Playing)
        {
            timeline.Pause();
            SetStatus(new SessionStatus(SessionState.Paused));
        }
    }

    public void Resume()
    {
        if (Timeline is { } timeline && Status.State == SessionState.Paused)
        {
            timeline.Resume();
            SetStatus(new SessionStatus(SessionState.Playing));
        }
    }

    /// <summary>Stop reading and close the connection. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        if (!_stop.IsCancellationRequested)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await _pump.ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Already logged and reported by the pump.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_client is not null)
        {
            _client.ConnectionChanged -= OnConnectionChanged;
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }

    private void Start(PlanningEngine engine, ISimEventSource source, bool fresh)
    {
        if (fresh)
        {
            engine.Clear();
        }
        else if (Kind == SessionKind.Replay)
        {
            engine.Post(new ReplayRewoundEvent(DateTimeOffset.UtcNow, default));
        }

        _pump = Task.Run(() => PumpAsync(engine, source));
    }

    private async Task PumpAsync(PlanningEngine engine, ISimEventSource source)
    {
        try
        {
            await engine.PumpAsync(source, _stop.Token).ConfigureAwait(false);
            SetStatus(new SessionStatus(Kind == SessionKind.Replay ? SessionState.Finished : SessionState.Stopped, Kind == SessionKind.Live ? "The connection closed." : null));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            SetStatus(new SessionStatus(SessionState.Stopped));
        }
        catch (GatewayConnectException ex)
        {
            LogFailed(ex, Description);
            SetStatus(new SessionStatus(SessionState.Failed, $"SimSig refused the connection: {ex.Message}"));
        }
#pragma warning disable CA1031 // Any failure ends the session and is shown to the user.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(ex, Description);
            SetStatus(new SessionStatus(SessionState.Failed, ex.Message));
        }
    }

    private void OnConnectionChanged(object? sender, GatewayConnectionChange change)
    {
        _recorder?.OnConnection(change);
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        var status = change.State switch
        {
            GatewayConnectionState.Connecting => new SessionStatus(Status.State == SessionState.Connecting ? SessionState.Connecting : SessionState.Reconnecting, change.Detail),
            GatewayConnectionState.Connected => new SessionStatus(SessionState.Connected),
            _ => new SessionStatus(SessionState.Reconnecting, change.Detail, change.RetryIn),
        };
        SetStatus(status);
    }

    private void SetStatus(SessionStatus status)
    {
        lock (_lock)
        {
            if (_status == status || (!_status.IsActive && status.State != SessionState.Failed))
            {
                return; // never revive an ended session
            }

            _status = status;
        }

        try
        {
            StatusChanged?.Invoke(this, status);
        }
#pragma warning disable CA1031 // A failing handler must not end the session.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandlerFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Session {Session} ended with an error")]
    private partial void LogFailed(Exception ex, string session);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not send a command to SimSig")]
    private partial void LogSendFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A session status handler failed")]
    private partial void LogHandlerFailed(Exception ex);
}
