using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stringline.Core.Json;

/// <summary>Base for string-backed identifier converters; also supports use as dictionary keys.</summary>
public abstract class IdentifierJsonConverter<T> : JsonConverter<T>
    where T : struct
{
    protected abstract string Name { get; }

    protected abstract bool TryCreate(string? value, out T result);

    protected abstract string ToText(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        LenientJson.TryGetString(ref reader, out var text) && TryCreate(text, out var result)
            ? result
            : throw LenientJson.Unexpected(ref reader, $"a {Name}");

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(ToText(value));

    public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TryCreate(reader.GetString(), out var result) ? result : throw LenientJson.Unexpected(ref reader, $"a {Name} property name");

    public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WritePropertyName(ToText(value));
}

public sealed class TiplocJsonConverter : IdentifierJsonConverter<Tiploc>
{
    protected override string Name => "TIPLOC";

    protected override bool TryCreate(string? value, out Tiploc result) => Tiploc.TryCreate(value, out result);

    protected override string ToText(Tiploc value) => value.Code;
}

public sealed class HeadcodeJsonConverter : IdentifierJsonConverter<Headcode>
{
    protected override string Name => "headcode";

    protected override bool TryCreate(string? value, out Headcode result) => Headcode.TryCreate(value, out result);

    protected override string ToText(Headcode value) => value.Value;
}

public sealed class TrainIdJsonConverter : IdentifierJsonConverter<TrainId>
{
    protected override string Name => "train ID";

    protected override bool TryCreate(string? value, out TrainId result) => TrainId.TryCreate(value, out result);

    protected override string ToText(TrainId value) => value.Value;
}
