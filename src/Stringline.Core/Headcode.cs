using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Stringline.Core.Json;

namespace Stringline.Core;

/// <summary>
/// A train reporting number (headcode), e.g. <c>1A23</c>. Headcodes repeat across a day and are not
/// unique identities; see <see cref="TrainId"/>. Values are trimmed and upper-cased. SimSig allows
/// non-standard headcodes, so any non-empty value without whitespace is accepted.
/// </summary>
[JsonConverter(typeof(HeadcodeJsonConverter))]
public readonly record struct Headcode
{
    private readonly string? _value;

    public Headcode(string value)
    {
        _value = Normalise(value) ?? throw new ArgumentException("A headcode must not be empty or contain whitespace.", nameof(value));
    }

    public string Value => _value ?? string.Empty;

    /// <summary>True for <c>default(Headcode)</c>, which is not a valid headcode.</summary>
    public bool IsEmpty => _value is null;

    /// <summary>True for the standard four-character form: class digit, letter, two digits (e.g. <c>2R02</c>).</summary>
    public bool IsStandard =>
        _value is { Length: 4 } v && char.IsAsciiDigit(v[0]) && char.IsAsciiLetterUpper(v[1]) && char.IsAsciiDigit(v[2]) && char.IsAsciiDigit(v[3]);

    /// <summary>
    /// The train class from the leading digit (0–9), or null if the headcode does not start with a digit.
    /// Used as the default priority source.
    /// </summary>
    public int? ClassDigit => _value is { Length: > 0 } v && char.IsAsciiDigit(v[0]) ? v[0] - '0' : null;

    public static bool TryCreate([NotNullWhen(true)] string? value, out Headcode headcode)
    {
        var normalised = Normalise(value);
        headcode = normalised is null ? default : new Headcode(normalised);
        return normalised is not null;
    }

    public override string ToString() => Value;

    private static string? Normalise(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace) ? null : trimmed.ToUpperInvariant();
    }
}
