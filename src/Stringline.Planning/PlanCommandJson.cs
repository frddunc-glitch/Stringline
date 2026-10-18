using System.Text.Json;

namespace Stringline.Planning;

/// <summary>
/// Plan commands as JSON, for the session store (S16): one object per command, with a <c>type</c> discriminator
/// (<c>shiftTrain</c>, <c>setDwell</c>…), camelCase properties, times as text. A suggestion's steps nest.
/// </summary>
public static class PlanCommandJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(IPlanCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return JsonSerializer.Serialize(command, Options);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <exception cref="JsonException">The text is not a known command.</exception>
    public static IPlanCommand Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return JsonSerializer.Deserialize<IPlanCommand>(json, Options) ?? throw new JsonException("A plan command cannot be null.");
    }
}
