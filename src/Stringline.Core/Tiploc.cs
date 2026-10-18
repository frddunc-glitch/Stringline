using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Stringline.Core.Json;

namespace Stringline.Core;

/// <summary>
/// A timing-point location code as SimSig uses it (TIPLOC). The gateway's <c>train_location.location</c>
/// can also carry a signal ID, which is held in the same type. Codes are trimmed and upper-cased so
/// comparisons are case-insensitive.
/// </summary>
[JsonConverter(typeof(TiplocJsonConverter))]
public readonly record struct Tiploc
{
    private readonly string? _code;

    public Tiploc(string code)
    {
        _code = Normalise(code) ?? throw new ArgumentException("A TIPLOC must not be empty or contain whitespace.", nameof(code));
    }

    /// <summary>The normalised code; empty for <c>default(Tiploc)</c>.</summary>
    public string Code => _code ?? string.Empty;

    /// <summary>True for <c>default(Tiploc)</c>, which is not a valid location.</summary>
    public bool IsEmpty => _code is null;

    public static bool TryCreate([NotNullWhen(true)] string? code, out Tiploc tiploc)
    {
        var normalised = Normalise(code);
        tiploc = normalised is null ? default : new Tiploc(normalised);
        return normalised is not null;
    }

    public override string ToString() => Code;

    private static string? Normalise(string? code)
    {
        var trimmed = code?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace) ? null : trimmed.ToUpperInvariant();
    }
}
