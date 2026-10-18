namespace Stringline.Gateway;

/// <summary>Settings for <see cref="GatewayClient"/>.</summary>
public sealed class GatewayClientOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = GatewayTopics.DefaultPort;

    /// <summary>SimSig forum username, for payware sims. Null or empty to connect without credentials.</summary>
    public string? Login { get; init; }

    /// <summary>SimSig forum password. Never logged.</summary>
    public string? Passcode { get; init; }

    /// <summary>Topics to subscribe to after each (re)connect.</summary>
    public IReadOnlyList<string> Topics { get; init; } = GatewayTopics.Default;

    /// <summary>
    /// Send <c>idrequest</c> after subscribing, so SimSig re-sends its last <c>clock_msg</c> straight away
    /// instead of at the next sim minute.
    /// </summary>
    public bool SendIdRequestOnConnect { get; init; } = true;

    /// <summary>Request a TD snapshot after subscribing, when the TD topic is among <see cref="Topics"/>.</summary>
    public bool RequestSnapshotOnConnect { get; init; } = true;

    /// <summary>How long to wait for CONNECTED. Payware sims authenticate online, which can take a while.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Keep reconnecting after a lost or refused connection. When false, the first failure ends the stream.</summary>
    public bool Reconnect { get; init; } = true;

    /// <summary>First reconnect delay; doubles on each consecutive failure up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan MinReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxFrameBytes { get; init; } = Stomp.StompFrameReader.DefaultMaxFrameBytes;

    /// <summary>A copy safe to log or display: the passcode is masked.</summary>
    public override string ToString() =>
        $"{Host}:{Port} login={(string.IsNullOrEmpty(Login) ? "(none)" : Login)} passcode={(string.IsNullOrEmpty(Passcode) ? "(none)" : "***")} topics=[{string.Join(", ", Topics)}]";
}

/// <summary>Gateway topic names and the default port.</summary>
public static class GatewayTopics
{
    public const int DefaultPort = 51515;

    /// <summary><c>train_location</c> and <c>train_delay</c>.</summary>
    public const string TrainMovements = "/topic/TRAIN_MVT_ALL_TOC";

    /// <summary><c>clock_msg</c>; accepts <c>idrequest</c>.</summary>
    public const string SimSig = "/topic/SimSig";

    /// <summary>TD berth steps and <c>SG_MSG</c> signalling state; accepts snapshot and assist commands.</summary>
    public const string TrainDescriber = "/topic/TD_ALL_SIG_AREA";

    /// <summary>Carries the reply to <c>ttsummary</c> (sent to <see cref="SimSig"/>): a JSON array, one entry per train.</summary>
    public const string TrainMovementSummary = "/topic/TRAIN_MVT_SUMMARY";

    /// <summary>Movements and clock — what the graph needs.</summary>
    public static IReadOnlyList<string> Default { get; } = [TrainMovements, SimSig];

    /// <summary><see cref="Default"/> plus the TD topic.</summary>
    public static IReadOnlyList<string> WithTrainDescriber { get; } = [TrainMovements, SimSig, TrainDescriber];
}
