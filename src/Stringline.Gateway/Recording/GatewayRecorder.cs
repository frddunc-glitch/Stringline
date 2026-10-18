using Stringline.Core;
using Stringline.Core.Events;

namespace Stringline.Gateway.Recording;

/// <summary>
/// Records every frame from a <see cref="GatewayClient"/> to an <see cref="SslogWriter"/>, together with
/// connection changes. Each frame is also parsed, to stamp lines with the latest sim clock and to keep
/// <see cref="Stats"/> for the console.
/// </summary>
public sealed class GatewayRecorder
{
    private readonly GatewayClient _client;
    private readonly SslogWriter _writer;
    private readonly MessageParser _parser;
    private readonly TimeProvider _time;

    public GatewayRecorder(GatewayClient client, SslogWriter writer, MessageParser parser, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(parser);
        _client = client;
        _writer = writer;
        _parser = parser;
        _time = timeProvider ?? TimeProvider.System;
        Stats = new RecordingStats(_time.GetUtcNow());
    }

    public RecordingStats Stats { get; }

    /// <summary>Raised for every parsed event, on the recording loop.</summary>
    public event EventHandler<GatewayEvent>? EventParsed;

    /// <summary>Records until cancelled (returns normally) or until the client's stream ends or fails.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        void OnConnectionChanged(object? sender, GatewayConnectionChange change)
        {
            _writer.WriteConnection(change);
            Stats.OnConnection(change);
        }

        _client.ConnectionChanged += OnConnectionChanged;
        try
        {
            await foreach (var message in _client.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var events = _parser.Parse(message);
                foreach (var e in events)
                {
                    Stats.OnEvent(e);
                }

                _writer.WriteMessage(message, Stats.LastClock);
                Stats.OnMessage(message);
                foreach (var e in events)
                {
                    EventParsed?.Invoke(this, e);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop.
        }
        finally
        {
            _client.ConnectionChanged -= OnConnectionChanged;
            _writer.Flush();
        }
    }
}

/// <summary>Running totals for a recording. Updated on the recording loop; read from any thread.</summary>
public sealed class RecordingStats
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, long> _byKind = new(StringComparer.Ordinal);

    public RecordingStats(DateTimeOffset started)
    {
        Started = started;
    }

    public DateTimeOffset Started { get; }

    public long Messages { get; private set; }

    public long Bytes { get; private set; }

    public long UnknownEvents { get; private set; }

    public int Connections { get; private set; }

    public string? AreaId { get; private set; }

    public SimTime? LastClock { get; private set; }

    /// <summary>From the latest clock: sim seconds per real second, or infinity when SimSig runs flat out.</summary>
    public double? SpeedRatio { get; private set; }

    public bool Paused { get; private set; }

    public GatewayConnectionState State { get; private set; } = GatewayConnectionState.Disconnected;

    /// <summary>Event counts keyed by kind: <c>train_location</c>, <c>SG_MSG:signal</c>, <c>unknown:ttsummary</c>, …</summary>
    public IReadOnlyDictionary<string, long> ByKind
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, long>(_byKind, StringComparer.Ordinal);
            }
        }
    }

    internal void OnMessage(GatewayMessage message)
    {
        lock (_lock)
        {
            Messages++;
            Bytes += message.Body.Length;
        }
    }

    internal void OnConnection(GatewayConnectionChange change)
    {
        lock (_lock)
        {
            State = change.State;
            if (change.State == GatewayConnectionState.Connected)
            {
                Connections++;
            }
        }
    }

    internal void OnEvent(GatewayEvent e)
    {
        var kind = e switch
        {
            ClockEvent => "clock_msg",
            TrainLocationEvent => "train_location",
            TrainDelayEvent => "train_delay",
            TdStepEvent { Kind: TdStepKind.Step } => "CA_MSG",
            TdStepEvent { Kind: TdStepKind.Cancel } => "CB_MSG",
            TdStepEvent => "CC_MSG",
            SignallingStateEvent s => "SG_MSG:" + s.ObjectType.ToString().ToLowerInvariant(),
            TimetableSummaryEvent => "ttsummary",
            UnknownEvent u => "unknown:" + (u.MessageType.Length > 0 ? u.MessageType : "(malformed)"),
            _ => e.GetType().Name,
        };

        lock (_lock)
        {
            _byKind[kind] = _byKind.GetValueOrDefault(kind) + 1;
            switch (e)
            {
                case ClockEvent clock:
                    AreaId = clock.AreaId;
                    LastClock = clock.Clock;
                    SpeedRatio = clock.IsMaximumSpeed ? double.PositiveInfinity : clock.SpeedRatio;
                    Paused = clock.Paused;
                    break;
                case UnknownEvent:
                    UnknownEvents++;
                    break;
            }
        }
    }
}
