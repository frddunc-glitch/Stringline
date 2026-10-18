using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Stringline.Core.Json;

/// <summary>
/// Lenient scalar readers for SimSig gateway JSON, where numbers and booleans may arrive either as
/// native JSON values or as strings (<c>"True"</c>, <c>"16250"</c>). Each method reads the value at
/// the reader's current token and does not advance it, so they suit hand-written
/// <see cref="Utf8JsonReader"/> loops as well as <see cref="System.Text.Json.Serialization.JsonConverter{T}"/>s.
/// </summary>
/// <remarks>
/// Accepted booleans: <c>true</c>/<c>false</c>, the strings <c>"true"</c>/<c>"false"</c> in any case,
/// and <c>0</c>/<c>1</c> as numbers or strings. Accepted integers: JSON numbers or strings holding an
/// invariant-culture integer (surrounding whitespace allowed, or an integral decimal such as <c>"12.0"</c>).
/// <c>null</c>, empty strings and anything else fail the <c>TryGet…</c> methods and throw
/// <see cref="JsonException"/> from the <c>Get…</c> methods.
/// </remarks>
public static class LenientJson
{
    public static bool TryGetBoolean(ref Utf8JsonReader reader, out bool value)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                value = true;
                return true;
            case JsonTokenType.False:
                value = false;
                return true;
            case JsonTokenType.Number when reader.TryGetInt32(out var n) && n is 0 or 1:
                value = n == 1;
                return true;
            case JsonTokenType.String:
                return TryParseBoolean(reader.GetString(), out value);
            default:
                value = false;
                return false;
        }
    }

    public static bool TryParseBoolean(string? s, out bool value)
    {
        var text = s.AsSpan().Trim();
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("1"))
        {
            value = true;
            return true;
        }

        value = false;
        return text.Equals("false", StringComparison.OrdinalIgnoreCase) || text.SequenceEqual("0");
    }

    public static bool TryGetInt32(ref Utf8JsonReader reader, out int value)
    {
        value = 0;
        if (!TryGetInt64(ref reader, out var wide) || wide is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        value = (int)wide;
        return true;
    }

    public static bool TryGetInt64(ref Utf8JsonReader reader, out long value)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt64(out value))
                {
                    return true;
                }

                value = 0;
                return reader.TryGetDouble(out var d) && TryIntegral(d, out value);
            case JsonTokenType.String:
                return TryParseInt64(reader.GetString(), out value);
            default:
                value = 0;
                return false;
        }
    }

    public static bool TryParseInt64(string? s, out long value)
    {
        var text = s.AsSpan().Trim();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0;
        return double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)
            && TryIntegral(d, out value);
    }

    /// <summary>
    /// Reads a string, a number (as its raw JSON text) or a boolean (as <c>"true"</c>/<c>"false"</c>).
    /// Returns false for <c>null</c>, objects and arrays.
    /// </summary>
    public static bool TryGetString(ref Utf8JsonReader reader, out string value)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                value = reader.GetString()!;
                return true;
            case JsonTokenType.Number:
                value = Encoding.UTF8.GetString(reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan);
                return true;
            case JsonTokenType.True:
                value = "true";
                return true;
            case JsonTokenType.False:
                value = "false";
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    public static bool GetBoolean(ref Utf8JsonReader reader) =>
        TryGetBoolean(ref reader, out var value) ? value : throw Unexpected(ref reader, "a boolean");

    public static int GetInt32(ref Utf8JsonReader reader) =>
        TryGetInt32(ref reader, out var value) ? value : throw Unexpected(ref reader, "a 32-bit integer");

    public static long GetInt64(ref Utf8JsonReader reader) =>
        TryGetInt64(ref reader, out var value) ? value : throw Unexpected(ref reader, "a 64-bit integer");

    public static string GetString(ref Utf8JsonReader reader) =>
        TryGetString(ref reader, out var value) ? value : throw Unexpected(ref reader, "a string");

    /// <summary>True if the current token is <c>null</c> or a blank string — "no value" in gateway JSON.</summary>
    public static bool IsNullOrBlank(ref Utf8JsonReader reader) =>
        reader.TokenType == JsonTokenType.Null
        || (reader.TokenType == JsonTokenType.String && string.IsNullOrWhiteSpace(reader.GetString()));

    internal static JsonException Unexpected(ref Utf8JsonReader reader, string expected) =>
        new($"Expected {expected} but found {reader.TokenType}" + (reader.TokenType == JsonTokenType.String ? $" \"{reader.GetString()}\"." : "."));

    private static bool TryIntegral(double d, out long value)
    {
        // 2^63 is exactly representable; long.MaxValue is not, so compare against the bound exclusively.
        var ok = double.IsFinite(d) && Math.Floor(d) == d && d >= -9.2233720368547758E18 && d < 9.2233720368547758E18;
        value = ok ? (long)d : 0;
        return ok;
    }
}
