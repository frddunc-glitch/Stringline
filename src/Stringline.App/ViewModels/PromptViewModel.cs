using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Stringline.App.ViewModels;

/// <summary>One text box in a <see cref="PromptViewModel"/>, with optional choices to pick from (an editable list).</summary>
internal sealed partial class PromptField(string label, string value, IReadOnlyList<string>? options = null, string? hint = null) : ObservableObject
{
    public string Label { get; } = label;

    /// <summary>Choices offered in a drop-down; the text may still be typed. Null for a plain text box.</summary>
    public IReadOnlyList<string>? Options { get; } = options;

    public bool HasOptions => Options is { Count: > 0 };

    /// <summary>A note shown under the box, e.g. the format expected.</summary>
    public string? Hint { get; } = hint;

    [ObservableProperty]
    public partial string Value { get; set; } = value;
}

/// <summary>
/// A small dialog asking for one or a few values, for the plan edits that need them (Hold here…, Add stop…, Clone…).
/// <see cref="Validate"/> checks the values when OK is pressed; the dialog stays open, showing the error, until it passes.
/// </summary>
internal sealed partial class PromptViewModel(string title, string heading, params PromptField[] fields) : ObservableObject
{
    public string Title { get; } = title;

    public string Heading { get; } = heading;

    /// <summary>Text under the heading, e.g. a warning about what OK does; null for none.</summary>
    public string? Note { get; init; }

    /// <summary>The OK button's text, e.g. "Send to SimSig".</summary>
    public string AcceptText { get; init; } = "OK";

    public ObservableCollection<PromptField> Fields { get; } = [.. fields];

    /// <summary>Returns what is wrong with the values, or null if they can be used.</summary>
    public Func<PromptViewModel, string?>? Validate { get; init; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public string this[int index] => Fields[index].Value.Trim();

    /// <summary>Called by the dialog's OK button: true (and no error) if the values pass.</summary>
    public bool TryAccept()
    {
        Error = Validate?.Invoke(this);
        return Error is null;
    }
}
