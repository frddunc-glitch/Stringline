using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stringline.Core.Json;

/// <summary>Reads a <see cref="bool"/> leniently (see <see cref="LenientJson"/>); writes a native JSON boolean.</summary>
public sealed class LenientBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => LenientJson.GetBoolean(ref reader);

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

/// <summary>Reads a <see cref="bool"/>? leniently; <c>null</c> or a blank string reads as null.</summary>
public sealed class LenientNullableBooleanConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        LenientJson.IsNullOrBlank(ref reader) ? null : LenientJson.GetBoolean(ref reader);

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteBooleanValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>Reads an <see cref="int"/> leniently (see <see cref="LenientJson"/>); writes a native JSON number.</summary>
public sealed class LenientInt32Converter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => LenientJson.GetInt32(ref reader);

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>Reads an <see cref="int"/>? leniently; <c>null</c> or a blank string reads as null.</summary>
public sealed class LenientNullableInt32Converter : JsonConverter<int?>
{
    public override bool HandleNull => true;

    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        LenientJson.IsNullOrBlank(ref reader) ? null : LenientJson.GetInt32(ref reader);

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

/// <summary>Reads a <see cref="long"/> leniently (see <see cref="LenientJson"/>); writes a native JSON number.</summary>
public sealed class LenientInt64Converter : JsonConverter<long>
{
    public override long Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => LenientJson.GetInt64(ref reader);

    public override void Write(Utf8JsonWriter writer, long value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}

/// <summary>
/// Reads a string from a JSON string, number or boolean (gateway IDs such as <c>uid</c> may arrive as
/// either). Apply per property with <see cref="JsonConverterAttribute"/>; it is not registered globally.
/// </summary>
public sealed class LenientStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : LenientJson.GetString(ref reader);

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

/// <summary>
/// Reads <see cref="SimTime"/> from seconds (number or numeric string) or a clock string such as
/// <c>"09:30:00"</c>; writes seconds as a number.
/// </summary>
public sealed class SimTimeJsonConverter : JsonConverter<SimTime>
{
    public override SimTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (LenientJson.TryGetInt32(ref reader, out var seconds))
        {
            return new SimTime(seconds);
        }

        if (reader.TokenType == JsonTokenType.String && SimTime.TryParse(reader.GetString(), out var time))
        {
            return time;
        }

        throw LenientJson.Unexpected(ref reader, "a sim time");
    }

    public override void Write(Utf8JsonWriter writer, SimTime value, JsonSerializerOptions options) => writer.WriteNumberValue(value.Seconds);
}

/// <summary>Registers the lenient scalar converters on <see cref="JsonSerializerOptions"/>.</summary>
public static class LenientJsonOptions
{
    /// <summary>Adds lenient converters for <see cref="bool"/>, <see cref="int"/>, <see cref="long"/> and nullable <see cref="bool"/>/<see cref="int"/>.</summary>
    public static JsonSerializerOptions AddLenientConverters(this JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Converters.Add(new LenientBooleanConverter());
        options.Converters.Add(new LenientNullableBooleanConverter());
        options.Converters.Add(new LenientInt32Converter());
        options.Converters.Add(new LenientNullableInt32Converter());
        options.Converters.Add(new LenientInt64Converter());
        return options;
    }

    /// <summary>New options for reading gateway JSON: lenient scalars, case-insensitive property names.</summary>
    public static JsonSerializerOptions CreateForGateway() =>
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }.AddLenientConverters();
}
