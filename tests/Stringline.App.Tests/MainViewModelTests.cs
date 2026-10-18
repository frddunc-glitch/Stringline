using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Graph;

namespace Stringline.App.Tests;

/// <summary>The shell's flows end to end: replay a real recording with its WTT, find a timetable, connect live and record.</summary>
public sealed class MainViewModelTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    [Fact]
    public async Task Starts_empty_and_asks_for_a_session()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;

        (await app.Ui.Run(() => vm.Placeholder)).Should().Contain("Connect to SimSig");
        (await app.Ui.Run(() => vm.ConnectionText)).Should().Be("Not connected");
        (await app.Ui.Run(() => vm.Scene)).Should().BeNull();
        (await app.Ui.Run(() => vm.Tick())).Should().BeNull("there is no clock and no timetable");
        (await app.Ui.Run(() => vm.ClockText)).Should().Be("Sim —");
        (await app.Ui.Run(() => vm.DisconnectCommand.CanExecute(null))).Should().BeFalse();
        (await app.Ui.Run(() => vm.ToggleRecordingCommand.CanExecute(null))).Should().BeFalse();
    }

    [Fact]
    public async Task Replays_a_recording_with_its_timetable_into_a_live_graph()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        app.Dialogs.Replay = d =>
        {
            d.RecordingPath = Fixtures.Path(Fixtures.Waterloo4x);
            d.TimetablePath = Fixtures.Path(Waterloo);
            d.Speed = ReplaySpeedOption.All[^1];
            return true;
        };

        await app.Ui.RunAsync(() => vm.OpenReplayCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Matches.Matched > 100 && vm.Scene?.InLayer(GraphLayer.Actual).Any() == true, "matched trains on the graph");

        await app.Ui.Run(() =>
        {
            vm.Corridors.Should().HaveCountGreaterThanOrEqualTo(2);
            vm.SelectedCorridor.Should().Be(vm.Corridors[0]);
            vm.Title.Should().StartWith("Stringline — ");
            vm.Placeholder.Should().BeNull();
            vm.Transport.Should().NotBeNull();
            vm.ConnectionText.Should().StartWith("Replay waterloo-4x.sslog");
            vm.InfoMessage.Should().StartWith("Loaded ");
            vm.TrainsText.Should().MatchRegex(@"^\d+ trains");
            vm.Trains.Rows.Should().NotBeEmpty();
            vm.Trains.Rows.Should().Contain(r => r.DelayText.Length > 0);
            vm.Scene!.InLayer(GraphLayer.Wtt).Should().NotBeEmpty();
            vm.HasSession.Should().BeTrue();
            vm.IsLive.Should().BeFalse();
        });

        var now = await app.Ui.Run(() => vm.Tick());
        now.Should().BeGreaterThan(SimTime.Parse("05:52").Seconds);
        (await app.Ui.Run(() => vm.ClockText)).Should().StartWith("Sim 0");

        // Hover and select a train's actual line.
        await app.Ui.Run(() =>
        {
            var path = vm.Scene!.InLayer(GraphLayer.Actual).First(p => p.Train is not null);
            var hit = new GraphHit(path, 0, 0, 1, path.Vertices[1].Time);
            var info = vm.Describe(hit)!;
            info.Heading.Should().StartWith(path.Headcode.Value);
            info.ValueOf(GraphHover.Actual).Should().NotBeNull();

            vm.SelectPath(hit);
            vm.Selection.Should().Be(new GraphSelection(path.Train, path.LiveUid));
            vm.Trains.Selected!.Uid.Should().Be(path.LiveUid);

            vm.SelectPath(null);
            vm.Selection.Should().BeNull();
            vm.Trains.Selected.Should().BeNull();
        });

        // Selecting in the list selects on the graph.
        await app.Ui.Run(() =>
        {
            var row = vm.Trains.Rows[0];
            vm.Trains.Selected = row;
            vm.Selection.Should().Be(new GraphSelection(row.Train, row.Uid));
            vm.CentreOnSelectedTrain();
            vm.Navigator.IsFollowing.Should().BeFalse();
            vm.FollowCommand.Execute(null);
            vm.Navigator.IsFollowing.Should().BeTrue();
        });

        // Layers, zoom presets and corridor tabs.
        await app.Ui.Run(() =>
        {
            vm.ZoomCommand.Execute("1");
            vm.Navigator.Viewport.TimeSpan.Should().Be(3600);
            vm.ShowWtt = false;
            vm.SelectedCorridor = vm.Corridors[1];
        });
        await app.Ui.WaitUntil(() => vm.Scene?.CorridorId == vm.Corridors[1].Id && !vm.Scene.InLayer(GraphLayer.Wtt).Any(), "the second corridor without the WTT");

        await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
        await app.Ui.Run(() =>
        {
            vm.HasSession.Should().BeFalse();
            vm.Transport.Should().BeNull();
            vm.ConnectionText.Should().Be("Not connected");
            vm.Scene.Should().NotBeNull("the last picture stays on screen");
        });

        var saved = app.Settings.Load();
        saved.LastReplayPath.Should().Be(Fixtures.Path(Fixtures.Waterloo4x));
        saved.ReplaySpeed.Should().Be(0, "flat out is stored as 0");
        saved.TimetableFor("waterloo").Should().Be(Fixtures.Path(Waterloo));
        File.Exists(Path.Combine(app.Paths.Profiles, "waterloo.json")).Should().BeTrue("a profile is created and saved");
    }

    [Fact]
    public async Task Suggests_timetables_for_the_sim_when_none_is_loaded()
    {
        await using var app = new TestApp(s => s with { TimetableFolder = Path.GetDirectoryName(Fixtures.Path(Waterloo)) });
        var vm = app.ViewModel;

        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), null, double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.InfoHasSuggestions, "timetable suggestions");

        await app.Ui.Run(() =>
        {
            vm.Placeholder.Should().Be("Load the sim's timetable to draw the graph.");
            vm.InfoMessage.Should().Contain("No timetable is loaded for waterloo");
            vm.Suggestions.Should().ContainSingle().Which.FileName.Should().Be(Waterloo);
            vm.SelectedSuggestion.Should().Be(vm.Suggestions[0]);
            vm.Snapshot.Matches.Matched.Should().Be(0, "nothing can match without a timetable");
        });

        await app.Ui.RunAsync(() => vm.LoadSuggestionCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.Snapshot.Timetable is not null && vm.Snapshot.Matches.Matched > 0, "matches after loading the suggestion");
        (await app.Ui.Run(() => vm.InfoHasSuggestions)).Should().BeFalse();
    }

    [Fact]
    public async Task Warns_when_the_timetable_is_for_another_sim()
    {
        await using var app = new TestApp(s => s with { TimetableFolder = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid()) });
        var vm = app.ViewModel;

        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(Fixtures.Waterloo4x), Fixtures.Path("llangollen 2012 B.WTT"), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(() => vm.InfoMessage?.Contains("but the sim is waterloo", StringComparison.Ordinal) == true, "a mismatch warning");

        (await app.Ui.Run(() => vm.InfoSeverity)).Should().Be(InfoSeverity.Warning);
    }

    [Fact]
    public async Task A_bad_timetable_or_recording_is_reported_not_thrown()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        var junk = Path.Combine(app.Paths.Recordings, "junk.wtt");
        Directory.CreateDirectory(app.Paths.Recordings);
        await File.WriteAllTextAsync(junk, "not a timetable");

        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(junk));
        (await app.Ui.Run(() => vm.InfoSeverity)).Should().Be(InfoSeverity.Error);
        (await app.Ui.Run(() => vm.InfoMessage)).Should().StartWith("Cannot load the timetable");

        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Path.Combine(app.Paths.Recordings, "missing.sslog"), null, 1, null));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().StartWith("Cannot open the recording");
        (await app.Ui.Run(() => vm.HasSession)).Should().BeFalse();
    }

    [Fact]
    public async Task Shows_import_warnings_on_request()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;

        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.Run(() => vm.ShowWarningsCommand.Execute(null));

        app.Dialogs.Lists.Should().ContainSingle().Which.Title.Should().Be("Timetable import warnings");
        (await app.Ui.Run(() => vm.Placeholder)).Should().BeNull("a timetable alone draws the WTT");
        (await app.Ui.Run(() => vm.Tick())).Should().Be((await app.Ui.Run(() => vm.LoadedTimetable!.Timetable.Header.StartTime)).Seconds);
    }

    [Fact]
    public async Task Connects_live_records_and_remembers_the_settings()
    {
        await using var server = FakeStompServer.Start();
        await using var app = new TestApp();
        var vm = app.ViewModel;
        app.Dialogs.Connect = d =>
        {
            d.Host = "127.0.0.1";
            d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            d.UserName = "fred";
            d.Password = "secret";
            d.RememberPassword = true;
            d.TimetablePath = string.Empty;
            return true;
        };

        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
        await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
        await app.Ui.WaitUntil(() => vm.ConnectionLight == StatusLight.Good, "connected");
        (await app.Ui.Run(() => vm.ConnectionText)).Should().Be($"Connected 127.0.0.1:{server.Port}");
        (await app.Ui.Run(() => vm.IsLive)).Should().BeTrue();

        await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"waterloo","clock":21600,"interval":500}}""");
        await app.Ui.WaitUntil(() => vm.Snapshot.AreaId == "waterloo", "the sim's clock");
        await app.Ui.Run(() => vm.ToggleRecordingCommand.Execute(null));
        await app.Ui.WaitUntil(() => vm.IsRecording, "recording");
        await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"2O03","uid":"7","action":"depart","location":"WLOO","platform":"5","time":21630}}""");
        await app.Ui.WaitUntil(() => vm.Snapshot.Trains.Count == 1, "the live train");
        (await app.Ui.Run(() => vm.SimName)).Should().Be("waterloo");
        (await app.Ui.Run(() => vm.Trains.Rows.Single().StatusText)).Should().Be("Unmatched", "no timetable is loaded");

        var path = app.Recorder.Path!;
        await app.Ui.Run(() => vm.ToggleRecordingCommand.Execute(null));
        (await app.Ui.Run(() => vm.IsRecording)).Should().BeFalse();
        Path.GetFileName(path).Should().StartWith("waterloo-");
        SslogRecording.Load(path).Messages.Should().ContainSingle().Which.Sim.Should().Be(new SimTime(21600), "frames are stamped with the clock seen before recording started");

        await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
        var saved = app.Settings.Load();
        saved.Host.Should().Be("127.0.0.1");
        saved.UserName.Should().Be("fred");
        saved.ProtectedPassword.Should().NotBeNullOrEmpty().And.NotContain("secret");
        app.Settings.PasswordOf(saved).Should().Be("secret");
        (await File.ReadAllTextAsync(app.Paths.Settings)).Should().NotContain("secret");
    }

    [Fact]
    public async Task A_refused_login_ends_the_session_with_the_reason()
    {
        await using var server = FakeStompServer.Start();
        server.OnConnect = _ => new Gateway.Stomp.StompFrame(Gateway.Stomp.StompCommands.Error, [new("message", "Authentication failed")], "Bad password"u8.ToArray());
        await using var app = new TestApp();
        var vm = app.ViewModel;
        app.Dialogs.Connect = d =>
        {
            d.Host = "127.0.0.1";
            d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            d.TimetablePath = string.Empty;
            return true;
        };

        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.ConnectionLight == StatusLight.Bad, "the failure");

        (await app.Ui.Run(() => vm.InfoMessage)).Should().StartWith("SimSig refused the connection");
        (await app.Ui.Run(() => vm.ToggleRecordingCommand.CanExecute(null))).Should().BeFalse("there is nothing to record");
        (await app.Ui.Run(() => vm.DisconnectCommand.CanExecute(null))).Should().BeTrue();
    }

    [Fact]
    public async Task A_cancelled_dialog_changes_nothing()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;

        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
        await app.Ui.RunAsync(() => vm.OpenReplayCommand.ExecuteAsync(null));
        await app.Ui.RunAsync(() => vm.LoadTimetableCommand.ExecuteAsync(null));

        (await app.Ui.Run(() => vm.HasSession)).Should().BeFalse();
        (await app.Ui.Run(() => vm.LoadedTimetable)).Should().BeNull();
        File.Exists(app.Paths.Settings).Should().BeFalse();
    }
}
