using Microsoft.Data.Sqlite;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Planning;

namespace Stringline.Persistence.Tests;

/// <summary>S16: the session file — what is kept, when the file is made, reading it back, resuming and listing.</summary>
public sealed class SessionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly SessionWriterOptions Manual = new() { AutosaveInterval = TimeSpan.Zero };

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-sessions-");
    private readonly MessageParser _parser = new();

    private string Folder => _root.FullName;

    public void Dispose() => _root.Delete(recursive: true);

    private static GatewayMessage Clock(int seconds, int at = 0) =>
        new(Wall.AddSeconds(at), "/topic/SimSig", $$$"""{"clock_msg":{"area_id":"waterloo","clock":{{{seconds}}},"interval":500,"paused":false}}""");

    private static GatewayMessage Location(string headcode, int seconds, int at = 0) =>
        new(Wall.AddSeconds(at), "/topic/TRAIN_MVT_ALL_TOC", $$$"""{"train_location":{"headcode":"{{{headcode}}}","uid":"9001","action":"pass","location":"WATRLMN","platform":"","time":{{{seconds}}}}}""");

    private void Feed(SessionWriter writer, GatewayMessage message) => writer.OnMessage(message, _parser.Parse(message));

    private string[] Files() => Directory.GetFiles(Folder, "*" + SessionCatalogue.Extension);

    [Fact]
    public void A_live_session_file_is_made_with_the_first_frame_the_engine_uses()
    {
        using var writer = SessionWriter.Create(Folder, new SessionInfo { Kind = SavedSessionKind.Live, Host = "localhost", Port = 51515 }, Manual);
        writer.OnStep(new PlanStep(PlanStepKind.Reset));
        var signal = new GatewayMessage(Wall, "/topic/TD_ALL_SIG_AREA", """{"SG_MSG":{"area_id":"waterloo","obj_type":"signal","obj_id":"S1"}}""");
        Feed(writer, signal);
        writer.Flush();

        Files().Should().BeEmpty("a reset and a signal state are nothing to resume");
        writer.Path.Should().BeNull();

        Feed(writer, Clock(25200));
        writer.Flush();

        writer.Path.Should().NotBeNull();
        Path.GetFileName(writer.Path).Should().MatchRegex(@"^waterloo-\d{8}-\d{4}\.stringline$");
        Files().Should().ContainSingle();
    }

    [Fact]
    public void Frames_steps_and_what_the_session_is_read_back_as_saved()
    {
        string path;
        using (var writer = SessionWriter.Create(Folder, new SessionInfo { Kind = SavedSessionKind.Live, Host = "localhost", Port = 51515 }, Manual))
        {
            writer.SetTimetable(@"C:\tt\waterloo 2010.WTT", "Waterloo 2010", "waterloo", "London Waterloo");
            Feed(writer, Clock(25200));
            Feed(writer, Location("1A01", 25230, at: 1));
            writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01")), "Cancel 1A01", new SimTime(25230), Wall.AddSeconds(2)));
            writer.OnStep(new PlanStep(PlanStepKind.Undo, Description: "Cancel 1A01", SimTime: new SimTime(25260), At: Wall.AddSeconds(3)));
            writer.Flush();
            Feed(writer, Clock(25260, at: 60));
            path = writer.Path!;
        }

        var saved = SavedSession.Load(path);

        saved.Info.Should().BeEquivalentTo(new
        {
            Kind = SavedSessionKind.Live,
            AreaId = "waterloo",
            SimName = "London Waterloo",
            TimetablePath = @"C:\tt\waterloo 2010.WTT",
            TimetableName = "Waterloo 2010",
            Host = "localhost",
            Port = 51515,
            LastSimTime = new SimTime(25260),
            Ended = false,
            Frames = 3,
            Steps = 2,
            Path = path,
        });
        saved.Info.App.Should().StartWith("Stringline ");
        saved.Frames.Select(f => f.Sim).Should().Equal(new SimTime(25200), new SimTime(25200), new SimTime(25260));
        saved.Frames[1].BodyText.Should().Be(Location("1A01", 25230).BodyText);
        saved.Frames[1].At.Should().Be(Wall.AddSeconds(1));
        saved.Steps.Should().HaveCount(2);
        saved.Steps[0].Should().Be(PlanStep.Execute(new CancelTrain(new TrainId("1A01")), "Cancel 1A01", new SimTime(25230), Wall.AddSeconds(2)));
        saved.Steps[1].Kind.Should().Be(PlanStepKind.Undo);
        saved.Edits.Should().Be(0);
        saved.Problems.Should().BeEmpty();

        var events = saved.Events(_parser).ToList();
        events.Should().HaveCount(3);
        events.Select(e => e.ReceivedAt).Should().Equal(Wall, Wall.AddSeconds(1), Wall.AddSeconds(60));
        var recording = saved.ToRecording();
        recording.Messages.Should().HaveCount(3);
        recording.FirstSim.Should().Be(new SimTime(25200));
    }

    [Fact]
    public void Ending_a_session_marks_it_and_resuming_appends_to_the_same_file()
    {
        string path;
        using (var writer = SessionWriter.Create(Folder, new SessionInfo(), Manual))
        {
            Feed(writer, Clock(25200));
            writer.End();
            path = writer.Path!;
        }

        SessionCatalogue.ReadInfo(path)!.Ended.Should().BeTrue();
        SessionCatalogue.LatestUnfinished(Folder).Should().BeNull();

        using (var writer = SessionWriter.Append(path, Manual))
        {
            writer.Info.Ended.Should().BeFalse();
            Feed(writer, Clock(25260, at: 60));
            writer.OnStep(new PlanStep(PlanStepKind.Reset)); // after a frame-only session: still nothing to reset
            writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01"))));
        }

        var info = SessionCatalogue.ReadInfo(path)!;
        info.Should().BeEquivalentTo(new { Ended = false, Frames = 2, Steps = 1, LastSimTime = new SimTime(25260) });
        SessionCatalogue.LatestUnfinished(Folder)!.Path.Should().Be(path);
        Files().Should().ContainSingle();
    }

    [Fact]
    public void Once_there_are_edits_a_reset_is_kept()
    {
        string path;
        using (var writer = SessionWriter.Create(Folder, new SessionInfo(), Manual))
        {
            writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01"))));
            writer.OnStep(new PlanStep(PlanStepKind.Reset));
            writer.Flush();
            path = writer.Path!;
        }

        SavedSession.Load(path).Steps.Select(s => s.Kind).Should().Equal(PlanStepKind.Execute, PlanStepKind.Reset);
        SavedSession.Load(path).Edits.Should().Be(0);
    }

    [Fact]
    public void A_replay_session_is_made_with_its_first_edit_and_keeps_its_position_but_no_frames()
    {
        using var writer = SessionWriter.Create(Folder, new SessionInfo { Kind = SavedSessionKind.Replay, RecordingPath = @"C:\rec\waterloo-4x.sslog" }, Manual);
        writer.SetPosition(new SimTime(25200));
        writer.Flush();
        Files().Should().BeEmpty("a replay watched without edits is not worth resuming");

        writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01")), "Cancel 1A01", new SimTime(25200)));
        writer.SetPosition(new SimTime(26000));
        writer.Flush();

        var saved = SavedSession.Load(writer.Path!);
        saved.Info.Should().BeEquivalentTo(new
        {
            Kind = SavedSessionKind.Replay,
            RecordingPath = @"C:\rec\waterloo-4x.sslog",
            Position = new SimTime(26000),
            LastSimTime = new SimTime(26000),
            Frames = 0,
            Steps = 1,
        });
        Path.GetFileName(writer.Path).Should().StartWith("simsig-", "no sim ID was known");
    }

    [Fact]
    public void Autosave_saves_on_a_timer()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Wall);
        using var writer = SessionWriter.Create(Folder, new SessionInfo(), new SessionWriterOptions { AutosaveInterval = TimeSpan.FromSeconds(2) }, time);
        Feed(writer, Clock(25200));
        Files().Should().BeEmpty();

        time.Advance(TimeSpan.FromSeconds(2));

        Files().Should().ContainSingle();
        SessionCatalogue.ReadInfo(Files()[0])!.Frames.Should().Be(1);
    }

    [Fact]
    public void A_failed_save_is_retried_with_nothing_lost()
    {
        var folder = Path.Combine(Folder, "blocked");
        File.WriteAllText(folder, "a file where the folder should be");
        using var writer = SessionWriter.Create(folder, new SessionInfo(), Manual);
        Feed(writer, Clock(25200));
        writer.Flush();
        writer.Path.Should().BeNull();

        File.Delete(folder);
        Feed(writer, Clock(25260, at: 60));
        writer.Flush();

        SessionCatalogue.ReadInfo(writer.Path!)!.Frames.Should().Be(2);
    }

    [Fact]
    public void Steps_that_cannot_be_read_are_skipped_and_reported()
    {
        string path;
        using (var writer = SessionWriter.Create(Folder, new SessionInfo(), Manual))
        {
            writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01")), "Cancel 1A01"));
            writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("2B01")), "Cancel 2B01"));
            writer.Flush();
            path = writer.Path!;
        }

        Sql(path, "UPDATE steps SET command = '{\"type\":\"teleport\"}' WHERE seq = 1");

        var saved = SavedSession.Load(path);
        saved.Steps.Should().ContainSingle().Which.Description.Should().Be("Cancel 2B01");
        saved.Problems.Should().ContainSingle().Which.Should().Contain("Cancel 1A01").And.Contain("skipped");
    }

    [Fact]
    public void Files_that_are_not_sessions_or_are_newer_are_refused_and_not_listed()
    {
        var text = Path.Combine(Folder, "notes" + SessionCatalogue.Extension);
        File.WriteAllText(text, "not a database");
        string newer;
        using (var writer = SessionWriter.Create(Folder, new SessionInfo(), Manual))
        {
            Feed(writer, Clock(25200));
            writer.Flush();
            newer = writer.Path!;
        }

        Sql(newer, "UPDATE meta SET value = '99' WHERE key = 'format'");

        FluentActions.Invoking(() => SavedSession.Load(text)).Should().Throw<SessionFormatException>().WithMessage("*not a Stringline session*");
        FluentActions.Invoking(() => SavedSession.Load(newer)).Should().Throw<SessionFormatException>().WithMessage("*newer version*");
        FluentActions.Invoking(() => SavedSession.Load(Path.Combine(Folder, "missing.stringline"))).Should().Throw<FileNotFoundException>();
        SessionCatalogue.List(Folder).Should().BeEmpty();
        SessionCatalogue.List(Path.Combine(Folder, "nowhere")).Should().BeEmpty();
    }

    [Fact]
    public void The_catalogue_lists_newest_first()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Wall);
        string Make(string area)
        {
            using var writer = SessionWriter.Create(Folder, new SessionInfo { AreaId = area }, Manual, time);
            writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01"))));
            writer.Flush();
            time.Advance(TimeSpan.FromMinutes(5));
            return writer.Path!;
        }

        var first = Make("waterloo");
        var second = Make("penrith");

        SessionCatalogue.List(Folder).Select(s => s.Path).Should().Equal(second, first);
        SessionCatalogue.List(Folder)[0].DisplayName.Should().StartWith("penrith ");
        SessionWriter.NewPath(Folder, "waterloo", Wall).Should().NotBe(first, "a name already taken gets a suffix")
            .And.EndWith("-2.stringline");
    }

    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
