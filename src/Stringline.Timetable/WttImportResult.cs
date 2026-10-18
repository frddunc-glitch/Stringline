using Stringline.Core.Timetables;

namespace Stringline.Timetable;

public enum ImportSeverity
{
    /// <summary>Worth knowing, nothing lost (e.g. a time that goes backwards, an unresolved rule).</summary>
    Info,

    /// <summary>Something was dropped or guessed (e.g. a train or timing point skipped, a link not found).</summary>
    Warning,

    /// <summary>The file is damaged and part of it could not be read.</summary>
    Error,
}

/// <param name="Train">The train concerned, as a headcode or ID, if the problem belongs to one.</param>
public sealed record ImportWarning(ImportSeverity Severity, string Message, string? Train = null)
{
    public override string ToString() => Train is null ? $"{Severity}: {Message}" : $"{Severity}: {Train}: {Message}";
}

/// <summary>A WTT import: the timetable and everything that did not import cleanly.</summary>
/// <param name="UnknownElements">
/// Elements the reader does not know, by path (e.g. <c>Trip/Foo</c>), with how often they occurred. Elements it knows
/// and deliberately ignores (saved-game state, decisions, caution speeds…) are not listed.
/// </param>
/// <param name="SkippedTrains">Trains in the file that could not be imported (each has a warning).</param>
public sealed record WttImportResult(
    WorkingTimetable Timetable,
    IReadOnlyList<ImportWarning> Warnings,
    IReadOnlyDictionary<string, int> UnknownElements,
    int SkippedTrains)
{
    public IEnumerable<ImportWarning> WarningsAtLeast(ImportSeverity severity) => Warnings.Where(w => w.Severity >= severity);
}

/// <summary>The file is not a SimSig timetable at all (not a zip or XML, no <c>SavedTimetable.xml</c>, wrong root element).</summary>
public sealed class WttFormatException : Exception
{
    public WttFormatException()
    {
    }

    public WttFormatException(string message)
        : base(message)
    {
    }

    public WttFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
