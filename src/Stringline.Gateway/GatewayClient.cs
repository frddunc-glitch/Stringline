using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.Gateway.Stomp;

namespace Stringline.Gateway;

/// <summary>
/// A minimal STOMP 1.1 client for the SimSig Interface Gateway. The handshake is deliberately plain —
/// CONNECT, wait for CONNECTED, SUBSCRIBE — because unusual sequences have crashed SimSigLoader.
/// <see cref="ReadAllAsync"/> drives the connection: it connects, reconnects with exponential back-off
/// after a lost or refused connection, resubscribes, and yields every MESSAGE frame.
/// </summary>
/// <remarks>
/// <see cref="ConnectionChanged"/> is raised on the client's read loop; handlers must be quick and
/// must not block. <see cref="SendAsync"/> is safe to call from any thread while connected.
/// </remarks>
public sealed partial class GatewayClient : IAsyncDisposable
{
    private readonly GatewayClientOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposed = new();
    private Stream? _stream;
    private int _reading;
    private bool _reachedConnected;

    public GatewayClient(GatewayClientOptions options, ILogger<GatewayClient>? logger = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, 65535);
        _options = options;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised on every connection state change. See the class remarks for threading.</summary>
    public event EventHandler<GatewayConnectionChange>? ConnectionChanged;

    public GatewayConnectionState State { get; private set; } = GatewayConnectionState.Disconnected;

    /// <summary>
    /// Connects and yields MESSAGE frames until cancelled. With <see cref="GatewayClientOptions.Reconnect"/>
    /// (the default) connection failures are retried forever; without it, the sequence ends when the
    /// connection closes, or throws if it fails.
    /// </summary>
    /// <exception cref="GatewayConnectException">The gateway rejected CONNECT with an ERROR frame.</exception>
    /// <exception cref="InvalidOperationException">Another enumeration is already running.</exception>
    public async IAsyncEnumerable<GatewayMessage> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed.IsCancellationRequested, this);
        if (Interlocked.Exchange(ref _reading, 1) == 1)
        {
            throw new InvalidOperationException("The gateway client is already being read.");
        }

        var channel = Channel.CreateUnbounded<GatewayMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposed.Token);
        var pump = Task.Run(() => RunAsync(channel.Writer, stop.Token), CancellationToken.None);
        try
        {
            await foreach (var message in channel.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                yield return message;
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Volatile.Write(ref _reading, 0);
        }
    }

    /// <summary>Sends a command built by <see cref="GatewayCommands"/>.</summary>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    public async Task SendAsync(GatewayCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stream = _stream ?? throw new InvalidOperationException("Not connected to the SimSig gateway.");
            await WriteFrameAsync(stream, SendFrame(command), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.IsCancellationRequested)
        {
            await _disposed.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task RunAsync(ChannelWriter<GatewayMessage> output, CancellationToken cancellationToken)
    {
        var delay = _options.MinReconnectDelay;
        Exception? failure = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string reason;
                Exception? error = null;
                _reachedConnected = false;
                try
                {
                    await ConnectAndReadAsync(output, cancellationToken).ConfigureAwait(false);
                    reason = "The gateway closed the connection.";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Raise(GatewayConnectionState.Disconnected, "Stopped.");
                    break;
                }
                catch (GatewayConnectException ex)
                {
                    Raise(GatewayConnectionState.Disconnected, ex.Message);
                    LogConnectRejected(ex.Message);
                    failure = ex;
                    break;
                }
                catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
                {
                    reason = ex.Message;
                    error = ex;
                }

                if (_reachedConnected)
                {
                    delay = _options.MinReconnectDelay;
                }

                if (!_options.Reconnect)
                {
                    Raise(GatewayConnectionState.Disconnected, reason);
                    failure = error;
                    break;
                }

                Raise(GatewayConnectionState.Disconnected, reason, retryIn: delay);
                LogDisconnected(reason, delay.TotalSeconds);
                try
                {
                    await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            output.TryComplete(failure);
        }
    }

    /// <summary>One connection's lifetime; sets <see cref="_reachedConnected"/> once CONNECTED arrives.</summary>
    private async Task ConnectAndReadAsync(ChannelWriter<GatewayMessage> output, CancellationToken cancellationToken)
    {
        var target = _options.ToString();
        Raise(GatewayConnectionState.Connecting, target);
        LogConnecting(target);

        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(_options.Host, _options.Port, cancellationToken).ConfigureAwait(false);
        var stream = tcp.GetStream();
        var reader = new StompFrameReader(stream, _options.MaxFrameBytes);
        var connected = false;
        try
        {
            await WriteFrameAsync(stream, ConnectFrame(), cancellationToken).ConfigureAwait(false);
            var reply = await ReadConnectReplyAsync(reader, cancellationToken).ConfigureAwait(false);

            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (var i = 0; i < _options.Topics.Count; i++)
                {
                    await WriteFrameAsync(stream, SubscribeFrame(i, _options.Topics[i]), cancellationToken).ConfigureAwait(false);
                }

                if (_options.SendIdRequestOnConnect && _options.Topics.Contains(GatewayTopics.SimSig))
                {
                    await WriteFrameAsync(stream, SendFrame(GatewayCommands.IdRequest()), cancellationToken).ConfigureAwait(false);
                }

                if (_options.RequestSnapshotOnConnect && _options.Topics.Contains(GatewayTopics.TrainDescriber))
                {
                    await WriteFrameAsync(stream, SendFrame(GatewayCommands.Snapshot()), cancellationToken).ConfigureAwait(false);
                }

                _stream = stream;
            }
            finally
            {
                _writeLock.Release();
            }

            connected = true;
            _reachedConnected = true;
            var server = reply.GetHeader("server");
            var version = reply.GetHeader("version");
            Raise(GatewayConnectionState.Connected, server, reply.Headers);
            LogConnected(version ?? "?", server ?? "?");

            while (true)
            {
                var frame = await reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    return;
                }

                switch (frame.Command)
                {
                    case StompCommands.Message:
                        output.TryWrite(new GatewayMessage(_time.GetUtcNow(), frame.GetHeader("destination") ?? string.Empty, frame.Body));
                        break;
                    case StompCommands.Error:
                        throw new IOException($"Gateway sent ERROR: {Describe(frame)}");
                    default:
                        LogIgnoredFrame(frame.Command);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested && connected)
        {
            await TryDisconnectAsync(stream).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await _writeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _stream = null;
            _writeLock.Release();
        }
    }

    private async Task<StompFrame> ReadConnectReplyAsync(StompFrameReader reader, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.ConnectTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        StompFrame? reply;
        try
        {
            reply = await reader.ReadFrameAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No CONNECTED reply within {_options.ConnectTimeout.TotalSeconds:0} s.");
        }

        return reply switch
        {
            null => throw new IOException("The gateway closed the connection before CONNECTED."),
            { Command: StompCommands.Connected } => reply,
            { Command: StompCommands.Error } => throw new GatewayConnectException($"The gateway refused the connection: {Describe(reply)}"),
            _ => throw new StompProtocolException($"Expected CONNECTED but got {reply.Command}."),
        };
    }

    private async Task TryDisconnectAsync(Stream stream)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1), _time);
            await WriteFrameAsync(stream, new StompFrame(StompCommands.Disconnect), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Best effort: we are closing anyway.
        }
    }

    private StompFrame ConnectFrame()
    {
        List<KeyValuePair<string, string>> headers = [new("accept-version", "1.1"), new("host", _options.Host)];
        if (!string.IsNullOrEmpty(_options.Login))
        {
            headers.Add(new("login", _options.Login));
            headers.Add(new("passcode", _options.Passcode ?? string.Empty));
        }

        return new StompFrame(StompCommands.Connect, headers);
    }

    private static StompFrame SubscribeFrame(int id, string topic) =>
        new(StompCommands.Subscribe, [new("id", id.ToString(System.Globalization.CultureInfo.InvariantCulture)), new("destination", topic), new("ack", "auto")]);

    private static StompFrame SendFrame(GatewayCommand command) =>
        new(StompCommands.Send, [new("destination", command.Topic)], command.Body);

    private static async Task WriteFrameAsync(Stream stream, StompFrame frame, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(StompFrameWriter.Encode(frame), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(StompFrame error)
    {
        var message = error.GetHeader("message");
        var body = error.BodyText.Trim();
        return (message, body) switch
        {
            (null or "", "") => "(no details)",
            (null or "", _) => body,
            (_, "") => message,
            _ => $"{message} — {body}",
        };
    }

    private void Raise(GatewayConnectionState state, string? detail, IReadOnlyList<KeyValuePair<string, string>>? headers = null, TimeSpan? retryIn = null)
    {
        State = state;
        try
        {
            ConnectionChanged?.Invoke(this, new GatewayConnectionChange(_time.GetUtcNow(), state, detail, headers, retryIn));
        }
#pragma warning disable CA1031 // A faulty handler must not take down the read loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogHandlerFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to SimSig gateway {Options}")]
    private partial void LogConnecting(string options);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected (STOMP {Version}, server {Server})")]
    private partial void LogConnected(string version, string server);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disconnected: {Reason} Retrying in {Seconds} s")]
    private partial void LogDisconnected(string reason, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connection rejected: {Reason}")]
    private partial void LogConnectRejected(string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored {Command} frame")]
    private partial void LogIgnoredFrame(string command);

    [LoggerMessage(Level = LogLevel.Error, Message = "A ConnectionChanged handler threw")]
    private partial void LogHandlerFailed(Exception exception);
}
