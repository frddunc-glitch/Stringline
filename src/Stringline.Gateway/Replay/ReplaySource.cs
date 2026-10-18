using System.Runtime.CompilerServices;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway.Recording;

namespace Stringline.Gateway.Replay;

/// <summary>
/// Gateway events from a <c>.sslog</c> recording, paced like the live sim (see <see cref="ReplayTimeline"/>),
/// so everything downstream runs exactly as it would against SimSig. After a backward seek a
/// <see cref="ReplayRewoundEvent"/> comes first, telling consumers to drop their state.
/// </summary>
public sealed class ReplaySource : ISimEventSource
{
    private readonly MessageParser _parser;
    private readonly TimeProvider _time;
    private readonly bool _preserveTimestamps;

    public ReplaySource(SslogRecording recording, ReplayOptions? options = null, MessageParser? parser = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        options ??= new ReplayOptions();
        _parser = parser ?? new MessageParser();
        _time = timeProvider ?? TimeProvider.System;
        _preserveTimestamps = options.PreserveTimestamps;
        Timeline = new ReplayTimeline(recording, options, _time);
    }

    /// <summary>Speed, pause and seek controls; safe to use from any thread while reading.</summary>
    public ReplayTimeline Timeline { get; }

    public SslogRecording Recording => Timeline.Recording;

    public static ReplaySource Open(string path, ReplayOptions? options = null, MessageParser? parser = null, TimeProvider? timeProvider = null) =>
        new(SslogRecording.Load(path), options, parser, timeProvider);

    /// <summary>Seeks the replay; see <see cref="ReplayTimeline.SeekTo"/>.</summary>
    public void SeekTo(SimTime target) => Timeline.SeekTo(target);

    public async IAsyncEnumerable<GatewayEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in Timeline.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var at = _preserveTimestamps ? frame.Message.At : _time.GetUtcNow();
            if (frame.RewoundTo is { } target)
            {
                yield return new ReplayRewoundEvent(at, target);
            }

            foreach (var gatewayEvent in _parser.Parse(frame.Message.Topic, frame.Message.Body.Span, at))
            {
                yield return gatewayEvent;
            }
        }
    }
}
