using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Core.Live;
using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;
using Stringline.Gateway.Tests;
using Stringline.Planning;
using Stringline.Timetable.Live;

namespace Stringline.App.Tests;

public sealed class AppSettingsStoreTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-settings-");

    public void Dispose() => _root.Delete(recursive: true);

    private AppSettingsStore Store(ISecretProtector? protector = null) =>
        new(AppPaths.Under(_root.FullName), protector ?? new DpapiSecretProtector(), NullLogger<AppSettingsStore>.Instance);

    [Fact]
    public void Defaults_without_a_file()
    {
        var settings = Store().Load();

        settings.Host.Should().Be("localhost");
        settings.Port.Should().Be(51515);
        settings.RememberPassword.Should().BeFalse();
        settings.TimetableFor("waterloo").Should().BeNull();
        settings.TimetableFor(null).Should().BeNull();
    }

    [Fact]
    public void Round_trips_with_the_password_encrypted()
    {
        var store = Store();
        var settings = store.WithPassword(new AppSettings { Host = "pc2", UserName = "fred" }, "hunter2", remember: true).WithTimetable("Waterloo", @"C:\tt\w.wtt");

        store.Save(settings);
        var loaded = store.Load();

        loaded.Host.Should().Be("pc2");
        loaded.TimetableFor("WATERLOO").Should().Be(@"C:\tt\w.wtt");
        loaded.LastTimetablePath.Should().Be(@"C:\tt\w.wtt");
        store.PasswordOf(loaded).Should().Be("hunter2");
        File.ReadAllText(store.Path).Should().NotContain("hunter2");
    }

    [Fact]
    public void Forgets_the_password_unless_asked_to_remember_it()
    {
        var store = Store();
        var remembered = store.WithPassword(new AppSettings(), "pw", remember: true);

        var forgotten = store.WithPassword(remembered, "pw", remember: false);

        forgotten.ProtectedPassword.Should().BeNull();
        store.PasswordOf(forgotten).Should().BeNull();
        store.WithPassword(new AppSettings(), null, remember: true).ProtectedPassword.Should().BeNull();
    }

    [Fact]
    public void A_password_that_cannot_be_decrypted_is_dropped()
    {
        var store = Store();

        store.PasswordOf(new AppSettings { RememberPassword = true, ProtectedPassword = "bm90IERQQVBJ" }).Should().BeNull();
        store.PasswordOf(new AppSettings { RememberPassword = true, ProtectedPassword = "%%%" }).Should().BeNull();
    }

    [Fact]
    public void A_damaged_file_gives_the_defaults()
    {
        var store = Store();
        File.WriteAllText(store.Path, "{ not json");

        store.Load().Should().BeEquivalentTo(new AppSettings());
    }
}

public sealed class StatusTextTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Connecting", "Connecting to h:1…", "Busy")]
    [InlineData("Connected", "Connected h:1", "Good")]
    [InlineData("Reconnecting", "Disconnected — reconnecting…", "Warning")]
    [InlineData("Stopped", "Disconnected", "Off")]
    public void Describes_a_live_session(string state, string text, string light)
    {
        StatusText.Connection(SessionKind.Live, new SessionStatus(Enum.Parse<SessionState>(state)), "h:1").Should().Be((text, Enum.Parse<StatusLight>(light)));
    }

    [Fact]
    public void Describes_replays_failures_and_retries()
    {
        StatusText.Connection(null, null, null).Should().Be(("Not connected", StatusLight.Off));
        StatusText.Connection(SessionKind.Replay, new SessionStatus(SessionState.Playing), "a.sslog").Text.Should().Be("Replay a.sslog");
        StatusText.Connection(SessionKind.Replay, new SessionStatus(SessionState.Paused), "a.sslog").Text.Should().Be("Replay a.sslog (paused)");
        StatusText.Connection(SessionKind.Replay, new SessionStatus(SessionState.Finished), "a.sslog").Text.Should().Be("Replay a.sslog finished");
        StatusText.Connection(SessionKind.Replay, new SessionStatus(SessionState.Stopped), "a.sslog").Text.Should().Be("Replay closed");
        StatusText.Connection(SessionKind.Live, new SessionStatus(SessionState.Failed, "Bad password"), "h").Should().Be(("Bad password", StatusLight.Bad));
        StatusText.Connection(SessionKind.Live, new SessionStatus(SessionState.Reconnecting, null, TimeSpan.FromSeconds(4.4)), "h").Text.Should().Be("Disconnected — retrying in 4 s");
    }

    [Fact]
    public void Formats_the_clock_and_speed()
    {
        var clock = new SimClock();
        StatusText.Clock(clock.Reading, SimTime.Zero).Should().Be("Sim —");
        StatusText.ClockTime(clock.Reading, SimTime.Zero).Should().Be("—");
        StatusText.Speed(clock.Reading).Should().BeEmpty();

        clock.Observe(new ClockEvent(T0, "test", SimTime.Parse("09:14:32"), 125, false));
        StatusText.Clock(clock.Reading, SimTime.Parse("09:14:32")).Should().Be("Sim 09:14:32");
        StatusText.ClockTime(clock.Reading, SimTime.Parse("09:14:32")).Should().Be("09:14:32", "the toolbar's clock (#13)");
        StatusText.Speed(clock.Reading).Should().Be("×4");

        clock.Observe(new ClockEvent(T0, "test", SimTime.Parse("09:15"), 1, false));
        StatusText.Speed(clock.Reading).Should().Be("max");

        clock.Observe(new ClockEvent(T0, "test", SimTime.Parse("09:16"), 333, false));
        StatusText.Speed(clock.Reading).Should().Be("×1.5");

        clock.Observe(new ClockEvent(T0, "test", SimTime.Parse("09:16"), 25, false));
        StatusText.Speed(clock.Reading).Should().Be("×20");

        clock.Observe(new ClockEvent(T0, "test", SimTime.Parse("09:16"), 500, true));
        StatusText.Speed(clock.Reading).Should().Be("paused");
    }

    [Theory]
    [InlineData("Normal", 16)]
    [InlineData("Large", 24)]
    [InlineData("Huge", 36)]
    public void Sizes_the_toolbar_clock(string size, double pixels) => StatusText.ClockFontSize(Enum.Parse<ClockSize>(size)).Should().Be(pixels);

    [Fact]
    public void Counts_trains()
    {
        StatusText.Trains(new MatchSummary(1, 1, 1, 0, 0, new Dictionary<MatchMethod, int>())).Should().Be("1 train");
        StatusText.Trains(new MatchSummary(212, 215, 210, 2, 3, new Dictionary<MatchMethod, int>())).Should().Be("212 trains (3 unmatched, 2 matching)");
    }
}

public sealed class TrainListViewModelTests
{
    private static readonly WorkingTimetable Tt = new(
        new TimetableHeader("test", "5", "Test", "", null, SimTime.FromClock(6, 0), SimTime.FromClock(9, 0)),
        [],
        [Train("1A01", "06:00"), Train("2B02", "06:10"), Train("3C03", "06:20")],
        []);

    private static Core.Timetables.Train Train(string headcode, string depart) => new()
    {
        Id = new TrainId(headcode),
        Headcode = new Headcode(headcode),
        Points = [new TimingPoint { Index = 0, Location = new Tiploc("WLOO"), Depart = SimTime.Parse(depart) }],
    };

    private static LiveTrain Live(string uid, string headcode, MatchStatus status, string departed) => new(
        uid,
        [new LiveWorking(new Headcode(headcode), 0, status, status == MatchStatus.Matched ? new TrainId(headcode) : null)],
        [new ActualEvent(new Tiploc("WLOO"), TrainLocationAction.Depart, SimTime.Parse(departed), "", true, 0)],
        false);

    private static PlanSnapshot Snapshot(params LiveTrain[] trains) =>
        new(1, Tt, trains, SimClockReading.None, new MatchSummary(trains.Length, trains.Length, 0, 0, 0, new Dictionary<MatchMethod, int>()), 1, null);

    [Theory]
    [InlineData("+3", "3 min late")]
    [InlineData("0", "On time")]
    [InlineData("−1", "1 min early")]
    [InlineData(" +12 ", "12 min late")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("08:03", null)]
    public void A_delay_cell_reads_as_words_in_its_tooltip(string? cell, string? words)
    {
        TrainListViewModel.DelayWords(cell).Should().Be(words);
        if (words is not null)
        {
            TrainListViewModel.DelayWords(TrainListViewModel.FormatDelay(int.Parse(cell!.Trim().Replace('−', '-'), CultureInfo.InvariantCulture))).Should().Be(words, "it reads what FormatDelay writes");
        }
    }

    [Fact]
    public void Lists_trains_by_delay_with_unknown_delays_last()
    {
        var list = new TrainListViewModel();

        list.Update(Snapshot(
            Live("1", "1A01", MatchStatus.Matched, "06:02"),
            Live("2", "2B02", MatchStatus.Matched, "06:19:30"),
            Live("3", "9Z99", MatchStatus.Unmatched, "06:30"),
            Live("4", "3C03", MatchStatus.Pending, "06:21")), null);

        list.Rows.Select(r => r.Headcode).Should().Equal("2B02", "1A01", "9Z99", "3C03");
        list.Rows[0].DelayText.Should().Be("+9");
        list.Rows[0].StatusText.Should().Be("2B02");
        list.Rows[2].StatusText.Should().Be("Unmatched");
        list.Rows[2].IsUnmatched.Should().BeTrue();
        list.Rows[2].Colour.Should().Be("#8A8A8A");
        list.Rows[3].StatusText.Should().Be("Matching…");
        list.Rows[1].LastLocation.Should().Be("WLOO");
        list.Rows[1].LastSeenText.Should().Be("06:02:00");
        list.Summary.Should().Be("4 trains, 1 unmatched");
    }

    [Fact]
    public void Filters_sorts_and_keeps_rows_between_snapshots()
    {
        var list = new TrainListViewModel();
        list.Update(Snapshot(Live("1", "1A01", MatchStatus.Matched, "06:02"), Live("2", "2B02", MatchStatus.Matched, "06:11")), null);
        var row = list.Find("1")!;
        list.Selected = row;

        list.Update(Snapshot(Live("1", "1A01", MatchStatus.Matched, "06:05"), Live("2", "2B02", MatchStatus.Matched, "06:11")), null);
        list.Find("1").Should().BeSameAs(row);
        list.Rows[0].Should().BeSameAs(row, "now 5 min late, it sorts first");
        row.DelayText.Should().Be("+5");
        list.Selected.Should().BeSameAs(row);

        list.Sort = TrainSort.Headcode;
        list.Rows.Select(r => r.Headcode).Should().Equal("1A01", "2B02");
        list.Sort = TrainSort.LastSeen;
        list.Rows.Select(r => r.Headcode).Should().Equal("2B02", "1A01");

        list.Filter = "2b";
        list.Rows.Should().ContainSingle().Which.Headcode.Should().Be("2B02");
        list.Selected.Should().BeNull("the selected row was filtered out");
        list.Summary.Should().Be("1 of 2 trains");

        list.Filter = string.Empty;
        list.UnmatchedOnly = true;
        list.Rows.Should().BeEmpty();

        list.UnmatchedOnly = false;
        list.Update(Snapshot(), null);
        list.Rows.Should().BeEmpty();
        list.Summary.Should().Be("No trains");
    }
}

public sealed class DialogViewModelTests
{
    private static readonly ScriptedDialogs Dialogs = new();

    [Fact]
    public void Connect_checks_its_input_and_builds_the_options()
    {
        var vm = new ConnectViewModel(Dialogs) { Host = " pc2 ", Port = "51515", UserName = "fred", Password = "pw", SubscribeTd = true };

        vm.Error.Should().BeNull();
        var options = vm.ToOptions();
        options.Host.Should().Be("pc2");
        options.Login.Should().Be("fred");
        options.Passcode.Should().Be("pw");
        options.Topics.Should().Contain(GatewayTopics.TrainDescriber);
        options.ToString().Should().NotContain("pw");

        vm.Port = "99999";
        vm.Error.Should().Contain("port");
        vm.Port = "1";
        vm.Host = " ";
        vm.Error.Should().Contain("host");
        vm.Host = "h";
        vm.TimetablePath = @"C:\nope\missing.wtt";
        vm.Error.Should().Contain("timetable");
        vm.TimetablePath = string.Empty;
        vm.UserName = string.Empty;
        vm.Password = string.Empty;
        vm.ToOptions().Login.Should().BeNull();
        vm.ToOptions().Passcode.Should().BeNull();
    }

    [Fact]
    public void Browsing_fills_in_the_path()
    {
        var dialogs = new ScriptedDialogs();
        dialogs.Files.Enqueue(@"C:\a.wtt");
        dialogs.Files.Enqueue(@"C:\b.sslog");
        dialogs.Files.Enqueue(@"C:\c.wtt");
        var connect = new ConnectViewModel(dialogs);
        var replay = new ReplayViewModel(dialogs);

        connect.BrowseTimetableCommand.Execute(null);
        replay.BrowseRecordingCommand.Execute(null);
        replay.BrowseTimetableCommand.Execute(null);
        replay.BrowseTimetableCommand.Execute(null);

        connect.TimetablePath.Should().Be(@"C:\a.wtt");
        replay.RecordingPath.Should().Be(@"C:\b.sslog");
        replay.TimetablePath.Should().Be(@"C:\c.wtt", "a cancelled pick keeps the path");
    }

    [Fact]
    public void Replay_checks_its_input()
    {
        var vm = new ReplayViewModel(Dialogs);
        vm.Error.Should().Contain("recording");

        vm.RecordingPath = Fixtures.Path(Fixtures.Waterloo1x);
        vm.Error.Should().BeNull();
        vm.StartTime.Should().BeNull();

        vm.StartAt = "07:30";
        vm.StartTime.Should().Be(SimTime.Parse("07:30"));
        vm.StartAt = "soon";
        vm.Error.Should().Contain("Start at");
    }

    [Theory]
    [InlineData(1, "1×")]
    [InlineData(7, "8×")]
    [InlineData(45, "60×")]
    [InlineData(1000, "60×")]
    [InlineData(double.PositiveInfinity, "Max")]
    public void Replay_speeds_snap_to_the_nearest_choice(double speed, string expected)
    {
        ReplaySpeedOption.Nearest(speed).ToString().Should().Be(expected);
    }
}

public sealed class TransportViewModelTests
{
    [Fact]
    public async Task Plays_pauses_changes_speed_and_seeks()
    {
        var engine = new PlanningEngine();
        var recording = SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x));
        await using var session = SimSession.StartReplay(recording, "w.sslog", new ReplayOptions { StartPaused = true }, engine, NullLoggerFactory.Instance);
        SimTime? restartedAt = null;
        var vm = new TransportViewModel(session, 4, t => restartedAt = t);

        vm.Minimum.Should().Be(recording.FirstSim!.Value.Seconds);
        vm.Maximum.Should().Be(recording.LastSim!.Value.Seconds);
        vm.RangeText.Should().Be("04:30–05:52");
        vm.Speed.ToString().Should().Be("4×");
        session.Timeline!.Speed.Should().Be(4, "the transport applies its speed");
        vm.IsPlaying.Should().BeFalse();

        vm.PlayPauseCommand.Execute(null);
        vm.IsPlaying.Should().BeTrue();
        session.Status.State.Should().Be(SessionState.Playing);

        vm.Speed = ReplaySpeedOption.All[^1];
        session.Timeline.Speed.Should().Be(double.PositiveInfinity);

        vm.PlayPauseCommand.Execute(null);
        session.Status.State.Should().Be(SessionState.Paused);

        vm.IsSeeking = true;
        vm.Position = SimTime.Parse("05:00").Seconds;
        vm.PositionText.Should().Be("05:00");
        vm.Tick();
        vm.Position.Should().Be(SimTime.Parse("05:00").Seconds, "the bar does not jump while dragged");
        vm.CommitSeek();
        vm.IsSeeking.Should().BeFalse();
        restartedAt.Should().BeNull("a running replay seeks in place");

        await session.StopAsync();
        vm.Tick();
        vm.IsFinished.Should().BeFalse("stopped is not finished");
    }

    [Fact]
    public async Task Seeking_a_finished_replay_restarts_it()
    {
        var engine = new PlanningEngine();
        using var cts = new CancellationTokenSource();
        var loop = engine.RunAsync(cts.Token);
        var recording = SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo1x));
        await using var session = SimSession.StartReplay(recording, "w.sslog", new ReplayOptions { Speed = double.PositiveInfinity }, engine, NullLoggerFactory.Instance);
        SimTime? restartedAt = null;
        var vm = new TransportViewModel(session, double.PositiveInfinity, t => restartedAt = t);

        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        vm.Tick();
        vm.IsFinished.Should().BeTrue();
        vm.Position.Should().Be(vm.Maximum);

        vm.Position = SimTime.Parse("05:00").Seconds;
        vm.CommitSeek();
        restartedAt.Should().Be(SimTime.Parse("05:00"));

        restartedAt = null;
        vm.PlayPauseCommand.Execute(null);
        restartedAt.Should().Be(new SimTime((int)vm.Minimum), "play after the end starts again");

        await cts.CancelAsync();
        await loop;
    }
}
