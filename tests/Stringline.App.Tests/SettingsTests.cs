using Microsoft.Extensions.Time.Testing;
using Serilog.Events;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Persistence;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S18: the settings window, what applying it changes, logging and session housekeeping.</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-settings-app-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Cleaned up eventually.
        }
    }

    [Fact]
    public async Task The_window_shows_the_settings_and_OK_applies_and_saves_them()
    {
        var logging = new AppLogging(Path.Combine(_root.FullName, "logs"), detailed: false);
        await using var app = new TestApp(root: _root.FullName, logging: logging);
        var vm = app.ViewModel;
        var exports = Directory.CreateDirectory(Path.Combine(_root.FullName, "my exports")).FullName;
        SettingsViewModel? shown = null;
        app.Dialogs.Settings = s =>
        {
            shown = s;
            (s.DefaultDwell, s.DefaultTurnaround, s.PossiblyStopped, s.PlatformMargin, s.MinConflict).Should().Be(("0:30", "0:30", "5:00", "1:00", "0:30"));
            (s.PlatformChangePenalty, s.LineChangePenalty, s.TerminatePenalty, s.CancelPenalty).Should().Be(("2", "2", "30", "60"));
            (s.ForecastKnockOn, s.AllowCancellation, s.DetailedLogging, s.DeleteOldSessions).Should().Be((true, false, false, false));
            (s.TimetableFolder, s.SessionsFolder, s.LogRetentionDays).Should().Be((string.Empty, string.Empty, "14"));
            s.Defaults.Sessions.Should().Be(app.Paths.Sessions);

            s.DefaultDwell = "45s";
            s.PlatformMargin = "2:00";
            s.MinConflict = "1.5";
            s.CancelPenalty = "90";
            s.ForecastKnockOn = false;
            s.AllowCancellation = true;
            s.ExportsFolder = exports;
            s.DetailedLogging = true;
            s.LogRetentionDays = "30";
            return true;
        };

        await app.Ui.Run(() => vm.ShowSettingsCommand.Execute(null));

        shown.Should().NotBeNull();
        await app.Ui.Run(() =>
        {
            vm.ForecastKnockOn.Should().BeFalse();
            vm.SuggestionPanel.AllowCancellation.Should().BeTrue();
            vm.Paths.Exports.Should().Be(exports);
            vm.Paths.Sessions.Should().Be(app.Paths.Sessions, "a blank folder keeps the default");
        });
        logging.Level.MinimumLevel.Should().Be(LogEventLevel.Debug);
        await app.Ui.WaitUntil(
            () => app.Engine.Current.Settings is { Forecast: { DefaultDwellSeconds: 45, Interaction: false }, Conflicts: { PlatformMarginSeconds: 120, MinSeveritySeconds: 90 } },
            "the engine using the new numbers");

        var saved = app.Settings.Load();
        saved.Planning.Should().Be(new PlanningSettings { DefaultDwellSeconds = 45, PlatformMarginSeconds = 120, MinConflictSeconds = 90, CancelPenalty = 5400 });
        (saved.ForecastKnockOn, saved.SuggestCancellations, saved.ExportsFolder, saved.DetailedLogging, saved.LogRetentionDays).Should().Be((false, true, exports, true, 30));
        saved.Planning.ToSuggestionOptions(true).Should().Be(new SuggestionOptions { AllowCancellation = true, CancelPenalty = 5400 });
    }

    [Fact]
    public async Task A_restarted_app_starts_with_the_saved_numbers_and_folders()
    {
        var sessions = Path.Combine(_root.FullName, "elsewhere");
        await using var app = new TestApp(
            s => s with { Planning = new PlanningSettings { DefaultTurnaroundSeconds = 120, MinConflictSeconds = 0 }, SessionsFolder = sessions },
            root: _root.FullName);

        await app.Ui.WaitUntil(
            () => app.Engine.Current.Settings is { Forecast.DefaultTurnaroundSeconds: 120, Conflicts.MinSeveritySeconds: 0 },
            "the saved numbers in the engine");
        (await app.Ui.Run(() => app.ViewModel.Paths.Sessions)).Should().Be(sessions);
    }

    [Fact]
    public async Task Cancel_or_a_bad_value_changes_nothing()
    {
        await using var app = new TestApp(root: _root.FullName);
        var vm = app.ViewModel;
        var errors = new List<string?>();
        app.Dialogs.Settings = s =>
        {
            foreach (var (set, error) in new (Action, string)[]
            {
                (() => s.DefaultDwell = "soon", "*default dwell*m:ss*"),
                (() => s.DefaultDwell = "11:00", "*default dwell must be from 0 to 10 minutes*"),
                (() => s.PossiblyStopped = "0", "*from 1 to 60 minutes*"),
                (() => s.TerminatePenalty = "-1", "*terminate short penalty must be from 0 to 1440*"),
                (() => s.LineChangePenalty = "lots", "*line change penalty must be a number*"),
                (() => s.TimetableFolder = Path.Combine(_root.FullName, "missing"), "*timetables folder*does not exist*"),
                (() => s.SessionsFolder = "relative\\folder", "*sessions folder must be a full path*"),
                (() => s.LogRetentionDays = "0", "*days of logs to keep must be from 1 to 365*"),
            })
            {
                s.RestoreDefaultsCommand.Execute(null);
                set();
                s.TryAccept().Should().BeFalse();
                s.Error.Should().Match(error);
                s.Result.Should().BeNull();
                errors.Add(s.Error);
            }

            // The days box is only read when deleting is turned on.
            s.RestoreDefaultsCommand.Execute(null);
            s.DeleteSessionsAfterDays = "never";
            s.TryAccept().Should().BeTrue();
            s.DeleteOldSessions = true;
            s.TryAccept().Should().BeFalse();

            return false; // then Cancel
        };

        await app.Ui.Run(() => vm.ShowSettingsCommand.Execute(null));

        errors.Should().HaveCount(8);
        File.Exists(app.Paths.Settings).Should().BeFalse("nothing was saved");
        app.Engine.Current.Settings.Conflicts.Should().Be(new ConflictOptions());
    }

    [Fact]
    public async Task Restore_defaults_and_browse_fill_in_the_boxes()
    {
        await using var app = new TestApp(s => s with { Planning = new PlanningSettings { DefaultDwellSeconds = 90 }, ForecastKnockOn = false, RecordingsFolder = @"D:\rec" }, root: _root.FullName);
        var folder = Directory.CreateDirectory(Path.Combine(_root.FullName, "timetables")).FullName;
        app.Dialogs.Folders.Enqueue(folder);
        app.Dialogs.Folders.Enqueue(null);
        app.Dialogs.Settings = s =>
        {
            (s.DefaultDwell, s.ForecastKnockOn, s.RecordingsFolder).Should().Be(("1:30", false, @"D:\rec"));
            s.BrowseCommand.Execute("timetables");
            s.TimetableFolder.Should().Be(folder);
            s.BrowseCommand.Execute("exports");
            s.ExportsFolder.Should().BeEmpty("the picker was cancelled");

            s.RestoreDefaultsCommand.Execute(null);
            (s.DefaultDwell, s.ForecastKnockOn, s.RecordingsFolder, s.TimetableFolder).Should().Be(("0:30", true, string.Empty, string.Empty));

            s.OpenLogsCommand.Execute(null);
            return true;
        };

        await app.Ui.Run(() => app.ViewModel.ShowSettingsCommand.Execute(null));

        app.Dialogs.Opened.Should().Equal(app.Paths.Logs);
        var saved = app.Settings.Load();
        (saved.Planning, saved.ForecastKnockOn, saved.RecordingsFolder).Should().Be((new PlanningSettings(), true, null));
    }

    [Fact]
    public async Task Old_sessions_are_deleted_at_start_up_and_when_turned_on_after_asking()
    {
        var paths = AppPaths.Under(_root.FullName);
        var old = Session(paths.Sessions, "waterloo", new DateTimeOffset(2020, 1, 1, 9, 0, 0, TimeSpan.Zero));
        var older = Session(paths.Sessions, "exeter", new DateTimeOffset(2019, 6, 1, 9, 0, 0, TimeSpan.Zero));
        var newest = Session(paths.Sessions, "penrith", new DateTimeOffset(2020, 1, 2, 9, 0, 0, TimeSpan.Zero));

        // Off by default: nothing is deleted.
        await using (var app = new TestApp(root: _root.FullName))
        {
            await app.Ui.RunAsync(() => app.ViewModel.TidySessionsAsync());
            Directory.GetFiles(paths.Sessions, "*.stringline").Should().HaveCount(3);

            // Turned on: asked first. "Not now" keeps them.
            app.Dialogs.Settings = s =>
            {
                s.DeleteOldSessions = true;
                s.DeleteSessionsAfterDays = "30";
                return true;
            };
            app.Dialogs.Choices.Enqueue(1);
            await app.Ui.Run(() => app.ViewModel.ShowSettingsCommand.Execute(null));
            app.Dialogs.Asked.Should().ContainSingle().Which.Should().Contain("2 saved sessions").And.Contain("more than 30 days ago");
            Directory.GetFiles(paths.Sessions, "*.stringline").Should().HaveCount(3);
            app.Settings.Load().DeleteSessionsAfterDays.Should().Be(30);
        }

        // At the next start they go, but the newest stays (it can still be resumed), and so does one named to resume.
        await using (var app = new TestApp(root: _root.FullName))
        {
            await app.Ui.RunAsync(() => app.ViewModel.TidySessionsAsync(keep: older));
            Directory.GetFiles(paths.Sessions, "*.stringline").Should().BeEquivalentTo([older, newest]);
            app.Dialogs.Asked.Should().BeEmpty("the user already asked for this");
        }

        File.Exists(old).Should().BeFalse();
    }

    private static string Session(string folder, string area, DateTimeOffset at)
    {
        using var writer = SessionWriter.Create(folder, new SessionInfo { AreaId = area }, new SessionWriterOptions { AutosaveInterval = TimeSpan.Zero }, new FakeTimeProvider(at));
        writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01"))));
        writer.Flush();
        return writer.Path!;
    }
}
