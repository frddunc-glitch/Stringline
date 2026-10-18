using System.Runtime.CompilerServices;
using Stringline.Core.Events;

namespace Stringline.Gateway;

/// <summary>Gateway events from a live SimSig connection: a <see cref="GatewayClient"/> feeding a <see cref="MessageParser"/>.</summary>
public sealed class LiveGatewaySource : ISimEventSource
{
    private readonly GatewayClient _client;
    private readonly MessageParser _parser;

    public LiveGatewaySource(GatewayClient client, MessageParser parser)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(parser);
        _client = client;
        _parser = parser;
    }

    public async IAsyncEnumerable<GatewayEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _client.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var gatewayEvent in _parser.Parse(message))
            {
                yield return gatewayEvent;
            }
        }
    }
}
