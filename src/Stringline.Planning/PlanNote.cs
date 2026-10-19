using Stringline.Core;

namespace Stringline.Planning;

/// <summary>
/// A note pinned to a time at a location (DIS-3), such as "Driver for 2L45 relieved here". Kept in the working plan,
/// so adding, changing and removing one undoes like any other edit (Fred, S13).
/// </summary>
/// <param name="Id">Unique within the plan.</param>
public sealed record PlanNote(string Id, Tiploc Location, SimTime Time, string Text)
{
    /// <summary>The longest note, in characters.</summary>
    public const int MaxLength = 500;

    /// <summary>The reason this note cannot be used, or null if it is valid.</summary>
    public string? Problem() =>
        string.IsNullOrWhiteSpace(Id) ? "A note needs an ID."
        : string.IsNullOrWhiteSpace(Text) ? "A note needs some text."
        : Text.Length > MaxLength ? $"A note can be at most {MaxLength} characters."
        : null;

    /// <summary>The first line of the text, shortened to <paramref name="length"/> characters with an ellipsis.</summary>
    public string Summary(int length = 40)
    {
        var line = (Text ?? string.Empty).Trim().Split('\n')[0].Trim();
        return line.Length <= length ? line : string.Concat(line.AsSpan(0, Math.Max(1, length - 1)).TrimEnd(), "…");
    }

    public override string ToString() => $"Note {Location} {Time}: {Summary()}";
}
