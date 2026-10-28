using System.IO;
using CommunityToolkit.Mvvm.Input;
using Stringline.Editing.Testing;
using Stringline.Editing.Validation;
using Stringline.Persistence;

namespace Stringline.App.ViewModels;

/// <summary>
/// Testing a draft in SimSig (S48, <c>08-…md</c> §9): the steps, and a recording of the test run read back as issues in
/// the Issues panel (with quick fixes where the report has them).
/// </summary>
internal sealed partial class TimetableEditorViewModel
{
    /// <summary>The steps for testing a timetable in SimSig (also on the main window's Timetable menu).</summary>
    public static IReadOnlyList<string> TestSteps { get; } =
    [
        "1. Save the draft (Draft ▸ Save) as a new version.",
        "2. In SimSig, load the sim with the new timetable, and run F4 ▸ Tools 1 ▸ Timetable Analyser. Fix what it finds here, and save again.",
        "3. Start the sim with Stringline connected and recording (File ▸ Record, or the Tools record command), and let it run for as long as you want to test.",
        "4. Back here, Draft ▸ Test report from a recording… reads the recording against the draft: trains that never appeared, trains that ran but did not match, sections every train ran slower than booked (with a fix that adds the time) and platforms two trains shared. They show in the Issues panel.",
    ];

    /// <summary>Issues from the last test report (S48), shown with the checks' until fixed or cleared.</summary>
    public IReadOnlyList<ValidationIssue> TestIssues { get; private set; } = [];

    [RelayCommand]
    private void ShowTestSteps() => _dialogs.ShowList("Test in SimSig", "To test the draft in SimSig:", TestSteps);

    /// <summary>Draft ▸ Test report from a recording…: a recording (or replay session) of the timetable in SimSig, read against the draft.</summary>
    [RelayCommand]
    private async Task TestReportAsync()
    {
        if (_dialogs.PickFile("Test report from a recording", "Recordings and sessions (*.sslog;*.stringline)|*.sslog;*.stringline|All files (*.*)|*.*", null) is not { } path)
        {
            return;
        }

        var import = Snapshot.Import;
        IReadOnlyList<ValidationIssue> issues;
        try
        {
            issues = await Task.Run(() => TimetableTestReport.Build(import, PlayFiles.Read(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Refuse($"Cannot read {path}: {ex.Message}");
            return;
        }

        TestIssues = issues;
        OnPropertyChanged(nameof(TestIssues));
        ClearTestReportCommand.NotifyCanExecuteChanged();
        RefreshIssues();
        Status = issues.Count == 0
            ? $"The test run in {Path.GetFileName(path)} showed nothing to fix."
            : $"The test run in {Path.GetFileName(path)}: {issues.Count} issue{(issues.Count == 1 ? "" : "s")} in the Issues panel.";
        if (issues.Count > 0)
        {
            IssuesRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand(CanExecute = nameof(HasTestIssues))]
    private void ClearTestReport()
    {
        TestIssues = [];
        OnPropertyChanged(nameof(TestIssues));
        ClearTestReportCommand.NotifyCanExecuteChanged();
        RefreshIssues();
    }

    private bool HasTestIssues() => TestIssues.Count > 0;

    private void ForgetTestIssues(IReadOnlyCollection<ValidationIssue> fixedIssues)
    {
        if (TestIssues.Count > 0 && fixedIssues.Any(TestIssues.Contains))
        {
            TestIssues = [.. TestIssues.Where(i => !fixedIssues.Contains(i))];
            OnPropertyChanged(nameof(TestIssues));
            ClearTestReportCommand.NotifyCanExecuteChanged();
            RefreshIssues();
        }
    }
}
