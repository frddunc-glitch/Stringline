using CommunityToolkit.Mvvm.ComponentModel;
using Stringline.Core;
using Stringline.Editing.Drafts;
using Stringline.Timetable.Editing;

namespace Stringline.App.ViewModels;

/// <summary>
/// The timetable editor's Header tab (S43, <c>08-…md</c> §7): the name, description, start and finish, the version
/// (raised only by saving) and the train description template. Each change is an edit, as in the grid.
/// </summary>
internal sealed class DraftHeaderViewModel(TimetableEditorViewModel owner) : ObservableObject
{
    private readonly Dictionary<string, string> _fields = [];

    public string Name { get => Field(nameof(Name)); set => Edit(nameof(Name), value); }

    /// <summary>The description with SimSig's <c>\n</c> shown as line breaks.</summary>
    public string Description { get => Field(nameof(Description)); set => Edit(nameof(Description), value); }

    public string StartTime { get => Field(nameof(StartTime)); set => Edit(nameof(StartTime), value); }

    public string FinishTime { get => Field(nameof(FinishTime)); set => Edit(nameof(FinishTime), value); }

    public string Template { get => Field(nameof(Template)); set => Edit(nameof(Template), value); }

    public string AreaId { get; private set; } = string.Empty;

    /// <summary>The version, raised by Save (build) and Release version (minor or major), never typed.</summary>
    public string Version { get; private set; } = string.Empty;

    public void Load(DraftSnapshot snapshot)
    {
        var header = snapshot.Document.Header;
        Show(nameof(Name), header.Name);
        Show(nameof(Description), header.Description?.Replace("\\n", "\n", StringComparison.Ordinal));
        Show(nameof(StartTime), DraftText.Time(header.StartTime));
        Show(nameof(FinishTime), DraftText.Time(header.FinishTime));
        Show(nameof(Template), header.TrainDescriptionTemplate);
        AreaId = header.AreaId ?? "(none)";
        Version = snapshot.Version?.ToString() ?? "none yet (the first save makes it 1.0.1)";
        OnPropertyChanged(nameof(AreaId));
        OnPropertyChanged(nameof(Version));
    }

    private string Field(string name) => _fields.GetValueOrDefault(name, string.Empty);

    private void Show(string name, string? text)
    {
        text ??= string.Empty;
        if (_fields.GetValueOrDefault(name) != text)
        {
            _fields[name] = text;
            OnPropertyChanged(name);
        }
    }

    private void Edit(string name, string? typed)
    {
        typed = name == nameof(Description) ? typed ?? string.Empty : typed?.Trim() ?? string.Empty;
        if (typed == Field(name))
        {
            return;
        }

        var header = owner.Document.Header;
        TimetableEdit? edit = null;
        switch (name)
        {
            case nameof(Name):
                edit = TimetableEdit.Change(header, h => h.Name = typed, "Timetable name");
                break;
            case nameof(Description):
                var text = typed.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
                edit = TimetableEdit.Change(header, h => h.Description = text, "Timetable description");
                break;
            case nameof(Template):
                edit = TimetableEdit.Change(header, h => h.TrainDescriptionTemplate = typed.Length == 0 ? null : typed, "Train description template");
                break;
            case nameof(StartTime) or nameof(FinishTime):
                if (!SimTimeNotation.TryParse(typed, null, out var time) || time.Mark != TimeMark.None)
                {
                    owner.Refuse($"'{typed}' is not a time: type 0430 or 04:30, and 25:30 for 01:30 the next day.");
                    break;
                }

                edit = name == nameof(StartTime)
                    ? TimetableEdit.Change(header, h => h.StartTime = time.Time, $"Start time {time}")
                    : TimetableEdit.Change(header, h => h.FinishTime = time.Time, $"Finish time {time}");
                break;
        }

        if (edit is null || !owner.TryApply(edit))
        {
            OnPropertyChanged(name);
        }
    }
}
