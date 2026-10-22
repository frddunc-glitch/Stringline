using System.Text.Json.Serialization;
using Stringline.Core.Timetables;

namespace Stringline.Editing.Knowledge;

/// <summary>
/// A train's maximum speed in fixed bands (Fred, S41): few enough that each band has samples, and narrow enough that a
/// 75 mph train never borrows a 100 mph train's run time.
/// </summary>
public enum SpeedBand
{
    /// <summary>No maximum speed on the train or its category.</summary>
    Unknown,

    /// <summary>Up to 50 mph.</summary>
    UpTo50,

    /// <summary>51–75 mph.</summary>
    UpTo75,

    /// <summary>76–90 mph.</summary>
    UpTo90,

    /// <summary>91–100 mph.</summary>
    UpTo100,

    /// <summary>101–125 mph.</summary>
    UpTo125,

    /// <summary>Over 125 mph.</summary>
    Over125,
}

/// <summary>
/// The key run times and dwells are learned by: a speed band and whether the train is freight. Written as text, for
/// example <c>76-90</c> or <c>51-75 freight</c>.
/// </summary>
[JsonConverter(typeof(RunBandJsonConverter))]
public readonly record struct RunBand(SpeedBand Speed, bool Freight)
{
    /// <summary>The band of a speed in mph; <see cref="SpeedBand.Unknown"/> for none (or nonsense, 0 or less).</summary>
    public static SpeedBand BandOf(int? mph) => mph switch
    {
        null or <= 0 => SpeedBand.Unknown,
        <= 50 => SpeedBand.UpTo50,
        <= 75 => SpeedBand.UpTo75,
        <= 90 => SpeedBand.UpTo90,
        <= 100 => SpeedBand.UpTo100,
        <= 125 => SpeedBand.UpTo125,
        _ => SpeedBand.Over125,
    };

    /// <summary>The band of a speed in mph and a freight flag.</summary>
    public static RunBand Of(int? mph, bool freight) => new(BandOf(mph), freight);

    /// <summary>A train's band, from its own maximum speed and freight flag (the reader fills them from its category).</summary>
    public static RunBand Of(Train train)
    {
        ArgumentNullException.ThrowIfNull(train);
        return Of(train.MaxSpeedMph, train.IsFreight);
    }

    public static string Name(SpeedBand speed) => speed switch
    {
        SpeedBand.UpTo50 => "0-50",
        SpeedBand.UpTo75 => "51-75",
        SpeedBand.UpTo90 => "76-90",
        SpeedBand.UpTo100 => "91-100",
        SpeedBand.UpTo125 => "101-125",
        SpeedBand.Over125 => "126+",
        _ => "unknown",
    };

    public override string ToString() => Freight ? Name(Speed) + " freight" : Name(Speed);

    public static bool TryParse(string? text, out RunBand band)
    {
        band = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2 || (parts.Length == 2 && !string.Equals(parts[1], "freight", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        foreach (var speed in Enum.GetValues<SpeedBand>())
        {
            if (string.Equals(Name(speed), parts[0], StringComparison.OrdinalIgnoreCase))
            {
                band = new RunBand(speed, parts.Length == 2);
                return true;
            }
        }

        return false;
    }
}

/// <summary>Writes a <see cref="RunBand"/> as its text, also as a dictionary key.</summary>
public sealed class RunBandJsonConverter : JsonConverter<RunBand>
{
    public override RunBand Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        RunBand.TryParse(reader.GetString(), out var band) ? band : throw new System.Text.Json.JsonException($"'{reader.GetString()}' is not a run band.");

    public override void Write(System.Text.Json.Utf8JsonWriter writer, RunBand value, System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }

    public override RunBand ReadAsPropertyName(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
        Read(ref reader, typeToConvert, options);

    public override void WriteAsPropertyName(System.Text.Json.Utf8JsonWriter writer, RunBand value, System.Text.Json.JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WritePropertyName(value.ToString());
    }
}
