using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Stringline.Core.Json;

namespace Stringline.Core;

/// <summary>
/// Stringline's stable identity for one planned train (a WTT working or an ad-hoc path). Opaque and
/// case-sensitive; assigned at import. Distinct from the headcode (which repeats), the WTT UID (which
/// may be absent) and the gateway's sim-internal uid (which identifies a live train).
/// </summary>
[JsonConverter(typeof(TrainIdJsonConverter))]
public readonly record struct TrainId
{
    private readonly string? _value;

    public TrainId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    /// <summary>True for <c>default(TrainId)</c>, which identifies no train.</summary>
    public bool IsEmpty => _value is null;

    public static bool TryCreate([NotNullWhen(true)] string? value, out TrainId id)
    {
        var ok = !string.IsNullOrWhiteSpace(value);
        id = ok ? new TrainId(value!) : default;
        return ok;
    }

    public override string ToString() => Value;
}
