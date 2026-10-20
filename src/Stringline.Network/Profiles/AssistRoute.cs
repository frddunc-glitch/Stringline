using System.Text.Json.Serialization;
using Stringline.Core;

namespace Stringline.Network.Profiles;

/// <summary>
/// A train's move at a location as far as the route it needs (S19): the location, the platform or line it uses there,
/// and the timing locations it comes from and heads for. The key that <see cref="AssistRoute"/>s are remembered by.
/// Use <see cref="Of"/>, which tidies the text so equal moves compare equal.
/// </summary>
public sealed record AssistMove(Tiploc Location, string? Platform = null, string? Line = null, Tiploc? From = null, Tiploc? Towards = null)
{
    /// <summary>The move with platform and line trimmed and upper-cased (null when empty), and empty TIPLOCs as null.</summary>
    public static AssistMove Of(Tiploc location, string? platform, string? line, Tiploc? from, Tiploc? towards) =>
        new(location, Tidy(platform), Tidy(line), from is { IsEmpty: false } f ? f : null, towards is { IsEmpty: false } t ? t : null);

    private static string? Tidy(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim().ToUpperInvariant();
}

/// <summary>
/// A route the user asked SimSig to set for a move (S19, AS-1), remembered in the profile so the next train making
/// the same move gets it offered. Either a route ID (<c>RWL1A</c>) or the signals at each end.
/// </summary>
public sealed record AssistRoute(AssistMove Move, string? Route = null, string? FromSignal = null, string? ToSignal = null)
{
    /// <summary>A route ID, or both signals.</summary>
    [JsonIgnore]
    public bool IsComplete => !string.IsNullOrWhiteSpace(Route) || (!string.IsNullOrWhiteSpace(FromSignal) && !string.IsNullOrWhiteSpace(ToSignal));

    public override string ToString() => !string.IsNullOrWhiteSpace(Route) ? Route : $"{FromSignal} to {ToSignal}";
}
