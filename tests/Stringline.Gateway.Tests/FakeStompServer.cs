using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Stringline.Gateway.Stomp;

namespace Stringline.Gateway.Tests;

/// <summary>
/// An in-process STOMP server standing in for SimSig. Records every frame clients send, answers CONNECT
/// (configurably), and pushes MESSAGE frames to subscribers on demand.
/// </summary>
internal sealed class FakeStompServer : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, Session> _sessions = new();
    private readonly Channel<(int Session, StompFrame Frame)> _received = Channel.CreateUnbounded<(int, StompFrame)>();
    private readonly List<(int Session, StompFrame Frame)> _history = [];
    private Task _acceptLoop = Task.CompletedTask;
    private int _nextSession;

    private FakeStompServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Decides the reply to CONNECT. Return null to send nothing (the client should time out).</summary>
    public Func<StompFrame, StompFrame?> OnConnect { get; set; } =
        _ => new StompFrame(StompCommands.Connected, [new("version", "1.1"), new("server", "FakeSimSig/1.0")]);

    public int ConnectionCount => Volatile.Read(ref _nextSession);

    public static FakeStompServer Start(int port = 0)
    {
        var server = new FakeStompServer(port);
        server._listener.Start();
        server._acceptLoop = server.AcceptLoopAsync();
        return server;
    }

    /// <summary>A port nothing is listening on (briefly bound, then released).</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Waits for the next frame any client sends that matches <paramref name="predicate"/>.</summary>
    public async Task<StompFrame> WaitForFrameAsync(Func<StompFrame, bool> predicate)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            var (_, frame) = await _received.Reader.ReadAsync(timeout.Token);
            if (predicate(frame))
            {
                return frame;
            }
        }
    }

    public Task<StompFrame> WaitForFrameAsync(string command) => WaitForFrameAsync(f => f.Command == command);

    public IReadOnlyList<StompFrame> ReceivedFrames()
    {
        lock (_history)
        {
            return [.. _history.Select(h => h.Frame)];
        }
    }

    /// <summary>Waits until <paramref name="count"/> sessions have subscribed to <paramref name="topic"/>.</summary>
    public async Task WaitForSubscribersAsync(string topic, int count = 1)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (_sessions.Values.Count(s => s.Subscriptions.ContainsKey(topic)) < count)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Sends a MESSAGE to every session subscribed to <paramref name="topic"/>.</summary>
    public Task PublishAsync(string topic, string body) => PublishAsync(topic, Encoding.UTF8.GetBytes(body));

    public async Task PublishAsync(string topic, byte[] body, bool withContentLength = true)
    {
        foreach (var session in _sessions.Values)
        {
            if (session.Subscriptions.TryGetValue(topic, out var id))
            {
                List<KeyValuePair<string, string>> headers =
                [
                    new("destination", topic),
                    new("message-id", Guid.NewGuid().ToString("N")),
                    new("subscription", id),
                ];
                if (withContentLength)
                {
                    headers.Add(new("content-length", body.Length.ToString(CultureInfo.InvariantCulture)));
                }

                await session.WriteAsync(StompFrameWriter.Encode(new StompFrame(StompCommands.Message, headers, body)));
            }
        }
    }

    /// <summary>Writes raw bytes to every connected session.</summary>
    public async Task SendRawAsync(byte[] bytes)
    {
        foreach (var session in _sessions.Values)
        {
            await session.WriteAsync(bytes);
        }
    }

    /// <summary>Abruptly closes every client connection; the listener stays up.</summary>
    public void DropConnections()
    {
        foreach (var (id, session) in _sessions)
        {
            session.Client.Close();
            _sessions.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropConnections();
        await _acceptLoop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
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
        try
        {
            var reader = new StompFrameReader(session.Client.GetStream());
            while (await reader.ReadFrameAsync(_stop.Token) is { } frame)
            {
                lock (_history)
                {
                    _history.Add((id, frame));
                }

                switch (frame.Command)
                {
                    case StompCommands.Connect:
                        if (OnConnect(frame) is { } reply)
                        {
                            await session.WriteAsync(StompFrameWriter.Encode(reply));
                            if (reply.Command == StompCommands.Error)
                            {
                                session.Client.Close();
                            }
                        }

                        break;
                    case StompCommands.Subscribe:
                        session.Subscriptions[frame.GetHeader("destination")!] = frame.GetHeader("id")!;
                        break;
                    case StompCommands.Disconnect:
                        session.Client.Close();
                        break;
                }

                // Publish after state changes, so a test that saw a SUBSCRIBE can publish straight away.
                await _received.Writer.WriteAsync((id, frame));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Client went away.
        }
        finally
        {
            _sessions.TryRemove(id, out _);
            session.Dispose();
        }
    }

    private sealed class Session(TcpClient client) : IDisposable
    {
        private readonly SemaphoreSlim _writeLock = new(1, 1);

        public TcpClient Client { get; } = client;

        public ConcurrentDictionary<string, string> Subscriptions { get; } = new();

        public void Dispose()
        {
            Client.Dispose();
            _writeLock.Dispose();
        }

        /// <summary>Writes to the client; a session that is closing is silently skipped.</summary>
        public async Task WriteAsync(byte[] bytes)
        {
            await _writeLock.WaitAsync();
            try
            {
                var stream = Client.GetStream();
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                _writeLock.Release();
            }
        }
    }
}
