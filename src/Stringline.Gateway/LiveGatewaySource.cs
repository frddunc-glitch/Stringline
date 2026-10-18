using System.Runtime.CompilerServices;
using Stringline.Core.Events;

namespace Stringline.Gateway;

/// <summary>A gateway frame and the events parsed from it.</summary>
public sealed record ParsedMessage(GatewayMessage Message, IReadOnlyList<GatewayEvent> Events);

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

    /// <summary>
    /// Raised on the read loop for every frame, with its events, before they are yielded: lets a recorder see the raw
    /// frames of the same connection. Handlers must be quick.
    /// </summary>
    public event EventHandler<ParsedMessage>? MessageParsed;

    public GatewayClient Client => _client;

    public async IAsyncEnumerable<GatewayEvent> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var message in _client.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var events = _parser.Parse(message);
            MessageParsed?.Invoke(this, new ParsedMessage(message, events));
            foreach (var gatewayEvent in events)
            {
                yield return gatewayEvent;
            }
        }
    }
}
