namespace Stringline.Core.Events;

/// <summary>
/// A stream of gateway events from a live SimSig connection or a replayed recording. Single consumer;
/// the sequence ends when the source is exhausted and throws <see cref="OperationCanceledException"/>
/// when cancelled.
/// </summary>
public interface ISimEventSource
{
    IAsyncEnumerable<GatewayEvent> ReadAllAsync(CancellationToken cancellationToken = default);
}
