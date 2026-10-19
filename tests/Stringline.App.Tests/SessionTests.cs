using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Tests;
using Stringline.Persistence;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S16 in the shell: sessions autosave, are offered and resumed after a restart, and are reviewed read-only.</summary>
public sealed class SessionTests : IDisposable
{
    private const string Waterloo = "waterloo 2010.WTT";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-sessions-app-");

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

    private TestApp App() => new(root: _root.FullName);

    private static string[] SessionFiles(TestApp app) =>
        Directory.Exists(app.Paths.Sessions) ? Directory.GetFiles(app.Paths.Sessions, "*" + SessionCatalogue.Extension) : [];

    /// <summary>Replays Waterloo 4× flat out with its WTT to the end.</summary>
    private static async Task<MainViewModel> ReplayToEnd(TestApp app)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(
            () =>
            {
                vm.Tick();
                return vm.Transport?.IsFinished == true && vm.Snapshot.Matches.Matched > 100;
            },
            "the replay played to its end");
        return vm;
    }

    private static async Task<TrainId> Cancel(TestApp app, int index = 0)
    {
        var vm = app.ViewModel;
        var train = await app.Ui.Run(() => vm.Snapshot.Timetable!.Trains[index].Id);
        var result = await app.Ui.Run(() => vm.ExecuteAsync(new CancelTrain(train)));
        (await result).Applied.Should().BeTrue();
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Get(train).Cancelled, "the edit");
        return train;
    }

    [Fact]
    public async Task A_replay_session_is_offered_after_a_restart_and_resumes_where_it_was_with_its_plan()
    {
        TrainId cancelled;
        SimTime last;
        await using (var first = App())
        {
            var vm = await ReplayToEnd(first);
            SessionFiles(first).Should().BeEmpty("nothing has been edited yet");
            cancelled = await Cancel(first);
            last = await first.Ui.Run(() => vm.Snapshot.Clock.LastReported);
            await first.Ui.WaitUntil(() => vm.SessionPath is not null, "the session file", seconds: 10);
        }

        await using var second = App();
        var app = second.ViewModel;
        var file = SessionFiles(second).Should().ContainSingle().Subject;
        Path.GetFileName(file).Should().StartWith("waterloo-");

        await second.Ui.RunAsync(app.OfferResumeAsync);
        await second.Ui.Run(() =>
        {
            app.InfoHasResume.Should().BeTrue();
            app.InfoShowsBrowse.Should().BeFalse();
            app.InfoMessage.Should().Contain("was not ended").And.Contain("replay of waterloo-4x.sslog");
        });

        await second.Ui.RunAsync(() => app.ResumeOfferedCommand.ExecuteAsync(null));
        await second.Ui.WaitUntil(() => app.Snapshot.Plan.Find(cancelled)?.Cancelled == true && app.Snapshot.Clock.HasTime, "the resumed plan and replay");
        await second.Ui.Run(() =>
        {
            app.InfoHasResume.Should().BeFalse();
            app.InfoMessage.Should().StartWith("Resumed ").And.Contain("1 edit").And.Contain("Press play");
            app.LoadedTimetable!.Path.Should().Be(Fixtures.Path(Waterloo));
            app.Transport.Should().NotBeNull();
            app.Transport!.IsPlaying.Should().BeFalse("it resumes paused");
            app.History.Entries.Should().ContainSingle().Which.Description.Should().StartWith("Cancel ");
            app.SessionPath.Should().Be(file, "it carries on in the same file");
        });
        await second.Ui.WaitUntil(() => app.Snapshot.Clock.LastReported >= last - 120, "the replay caught up to where it was");

        // Disconnecting ends it: no longer offered.
        await second.Ui.RunAsync(() => app.DisconnectCommand.ExecuteAsync(null));
        SessionCatalogue.ReadInfo(file)!.Ended.Should().BeTrue();
        SessionCatalogue.LatestUnfinished(second.Paths.Sessions).Should().BeNull();
    }

    [Fact]
    public async Task Restarting_a_finished_replay_keeps_the_plan()
    {
        await using var app = App();
        var vm = await ReplayToEnd(app);
        var cancelled = await Cancel(app);

        await app.Ui.Run(() =>
        {
            vm.Transport!.Position = vm.Transport.Minimum + 600;
            vm.Transport.CommitSeek();
        });
        await app.Ui.WaitUntil(() => vm.Transport?.IsFinished == false && vm.Snapshot.Clock.HasTime, "the replay started again");
        await Task.Delay(200);

        (await app.Ui.Run(() => vm.Snapshot.Plan.Get(cancelled).Cancelled)).Should().BeTrue();
        (await app.Ui.Run(() => vm.Snapshot.History.Done.Count)).Should().Be(1);
    }

    [Fact]
    public async Task Reviewing_replays_the_plan_as_it_was_and_refuses_edits()
    {
        string file;
        TrainId cancelled;
        await using (var first = App())
        {
            await ReplayToEnd(first);
            cancelled = await Cancel(first);
            await first.Ui.RunAsync(() => first.ViewModel.DisconnectCommand.ExecuteAsync(null));
            file = SessionFiles(first).Single();
        }

        await using var second = App();
        var vm = second.ViewModel;
        await second.Ui.RunAsync(vm.OfferResumeAsync);
        (await second.Ui.Run(() => vm.InfoHasResume)).Should().BeFalse("an ended session is not offered");

        await second.Ui.RunAsync(() => vm.ReviewSessionAsync(file));
        await second.Ui.WaitUntil(() => vm.Snapshot.Timetable is not null && vm.Transport is not null, "the review loaded");
        await Task.Delay(200);
        await second.Ui.Run(() =>
        {
            vm.IsReviewing.Should().BeTrue();
            vm.Title.Should().EndWith("(review)");
            vm.InfoMessage.Should().StartWith("Reviewing ");
            vm.Snapshot.Plan.Get(cancelled).Cancelled.Should().BeFalse("the edit was made at the end of the run");
            vm.SessionPath.Should().BeNull("nothing is saved while reviewing");
        });

        var refused = await (await second.Ui.Run(() => vm.ExecuteAsync(new CancelTrain(cancelled))));
        refused.Applied.Should().BeFalse();
        (await second.Ui.Run(() => vm.InfoMessage)).Should().StartWith("Reviewing a saved session");

        await second.Ui.Run(() => vm.Transport!.PlayPauseCommand.Execute(null));
        await second.Ui.WaitUntil(
            () =>
            {
                vm.Tick();
                return vm.Transport?.IsFinished == true && vm.Snapshot.Plan.Get(cancelled).Cancelled;
            },
            "the edit re-applied when the replay reached it");
        await second.Ui.Run(() =>
        {
            vm.UndoCommand.CanExecute(null).Should().BeFalse("edits are off while reviewing");
            vm.History.Entries.Should().ContainSingle();
        });

        SessionCatalogue.ReadInfo(file)!.Should().BeEquivalentTo(new { Ended = true, Steps = 1 }, "review writes nothing");
        SessionFiles(second).Should().ContainSingle();

        // Starting something else leaves review.
        await second.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), null, double.PositiveInfinity, null));
        (await second.Ui.Run(() => vm.IsReviewing)).Should().BeFalse();
    }

    [Fact]
    public async Task A_live_session_is_rebuilt_after_a_restart_and_carries_on_when_connected_again()
    {
        await using var server = FakeStompServer.Start();
        string file;
        TrainId cancelled;
        void Connect(TestApp app) => app.Dialogs.Connect = d =>
        {
            d.Host = "127.0.0.1";
            d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            d.TimetablePath = Fixtures.Path(Waterloo);
            return true;
        };

        await using (var first = App())
        {
            var vm = first.ViewModel;
            Connect(first);
            await first.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
            await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
            await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"waterloo","clock":21600,"interval":500}}""");
            await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"2O03","uid":"7","action":"depart","location":"WLOO","platform":"5","time":21630}}""");
            await first.Ui.WaitUntil(() => vm.Snapshot.Trains.Count == 1, "the live train");
            cancelled = await Cancel(first, 3);
            await first.Ui.WaitUntil(() => vm.SessionPath is not null, "the session file");
            file = vm.SessionPath!;
        }

        await using var second = App();
        var app = second.ViewModel;
        await second.Ui.RunAsync(() => app.ResumeSessionAsync(file));
        await second.Ui.WaitUntil(() => app.Snapshot.Trains.Count == 1 && app.Snapshot.Plan.Find(cancelled)?.Cancelled == true, "the session rebuilt");
        await second.Ui.Run(() =>
        {
            app.IsResumedIdle.Should().BeTrue();
            app.HasSession.Should().BeFalse();
            app.InfoMessage.Should().Contain("Connect to SimSig to carry on");
            app.Tick().Should().Be(app.Snapshot.Clock.LastReported.Seconds, "the clock stands at its last report until connected");
            app.Snapshot.Clock.LastReported.Seconds.Should().BeGreaterThanOrEqualTo(21600);
            app.Trains.Rows.Single().Headcode.Should().Be("2O03");
        });

        // Carry on: the same file, the same trains, and new frames appended.
        Connect(second);
        second.Dialogs.Choices.Enqueue(0);
        await second.Ui.RunAsync(() => app.ConnectCommand.ExecuteAsync(null));
        await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"2O05","uid":"8","action":"depart","location":"WLOO","platform":"6","time":21700}}""");
        await second.Ui.WaitUntil(() => app.Snapshot.Trains.Count == 2, "a train after reconnecting");
        await second.Ui.Run(() =>
        {
            second.Dialogs.Asked.Should().ContainSingle().Which.Should().Contain("Carry on");
            app.Snapshot.Plan.Get(cancelled).Cancelled.Should().BeTrue();
            app.IsResumedIdle.Should().BeFalse();
            app.SessionPath.Should().Be(file);
        });

        await second.Ui.RunAsync(() => app.DisconnectCommand.ExecuteAsync(null));
        var info = SessionCatalogue.ReadInfo(file)!;
        info.Should().BeEquivalentTo(new { Kind = SavedSessionKind.Live, Ended = true, Frames = 3, Steps = 1, AreaId = "waterloo", Host = "127.0.0.1" });
        SessionFiles(second).Should().ContainSingle();
    }

    [Fact]
    public async Task A_new_connection_after_resuming_starts_a_new_session_file()
    {
        await using var server = FakeStompServer.Start();
        string file;
        await using (var first = App())
        {
            var vm = first.ViewModel;
            first.Dialogs.Connect = d =>
            {
                d.Host = "127.0.0.1";
                d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                d.TimetablePath = Fixtures.Path(Waterloo);
                return true;
            };
            await first.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
            await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
            await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"waterloo","clock":21600,"interval":500}}""");
            await first.Ui.WaitUntil(() => vm.SessionPath is not null || vm.Snapshot.Clock.HasTime, "the clock");
            await Cancel(first);
            await first.Ui.WaitUntil(() => vm.SessionPath is not null, "the session file");
            file = vm.SessionPath!;
        }

        await using var second = App();
        var app = second.ViewModel;
        await second.Ui.RunAsync(() => app.ResumeSessionAsync(file));
        await second.Ui.WaitUntil(() => app.IsResumedIdle, "resumed");
        second.Dialogs.Connect = d =>
        {
            d.Host = "127.0.0.1";
            d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return true;
        };
        second.Dialogs.Choices.Enqueue(1); // Start a new session
        await second.Ui.RunAsync(() => app.ConnectCommand.ExecuteAsync(null));
        await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"waterloo","clock":30000,"interval":500}}""");
        await second.Ui.WaitUntil(() => app.Snapshot.Clock.LastReported == new SimTime(30000) && !app.Snapshot.History.CanUndo, "a fresh session");
        await second.Ui.WaitUntil(() => app.SessionPath is not null && app.SessionPath != file, "a new file");

        SessionCatalogue.ReadInfo(file)!.Ended.Should().BeTrue();
    }
}

public sealed class StartupOptionsTests
{
    [Fact]
    public void Resume_and_review_take_a_session_file()
    {
        Services.StartupOptions.Parse(["--resume", "a.stringline"]).Options.Resume.Should().Be("a.stringline");
        Services.StartupOptions.Parse(["--review", "b.stringline"]).Options.Review.Should().Be("b.stringline");
        Services.StartupOptions.Parse(["--resume"]).Error.Should().Be("--resume needs a value.");
        Services.StartupOptions.Parse(["--bogus", "x"]).Error.Should().Contain("--review s.stringline");
    }
}
