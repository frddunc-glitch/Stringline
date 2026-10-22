namespace Stringline.Editing.Validation;

/// <summary>The checks of the document as a whole: those that would make the saved file unreadable (<c>08-…md</c> §6).</summary>
internal static class DocumentChecks
{
    public static IReadOnlyList<ValidationIssue> Check(ValidationContext c)
    {
        var issues = new List<ValidationIssue>();
        var header = c.Document.Header;
        if (header.AreaId is null)
        {
            var entry = IssueCatalogue.Of(IssueCodes.NoAreaId);
            issues.Add(new ValidationIssue(entry.Severity, IssueCodes.NoAreaId, null, null, "The timetable has no sim ID", entry.Hint) { Element = header.Element });
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var category in c.Document.Categories)
        {
            if (category.Id is { } id && !seen.Add(id))
            {
                var entry = IssueCatalogue.Of(IssueCodes.DuplicateCategory);
                issues.Add(new ValidationIssue(entry.Severity, IssueCodes.DuplicateCategory, null, null, $"Category {id} ({category.Description}) is defined twice", entry.Hint) { Element = category.Element });
            }
        }

        return issues;
    }
}
