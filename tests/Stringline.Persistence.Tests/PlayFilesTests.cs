using Stringline.Core;
using Stringline.Core.Events;
using Stringline.Gateway;
using Stringline.Gateway.Tests;
using Stringline.Planning;

namespace Stringline.Persistence.Tests;

/// <summary>S41: recordings and saved sessions as sources of play for sim knowledge.</summary>
public sealed class PlayFilesTests : IDisposable
{
    private static readonly DateTimeOffset Wall = new(2026, 10, 19, 9, 0, 0, TimeSpan.Zero);
    private static readonly SessionWriterOptions Manual = new() { AutosaveInterval = TimeSpan.Zero };

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-play-");
    private readonly MessageParser _parser = new();

    private string Folder => _root.FullName;

    public void Dispose() => _root.Delete(recursive: true);

    private string Live(string area)
    {
        using var writer = SessionWriter.Create(Folder, new SessionInfo { Kind = SavedSessionKind.Live, AreaId = area }, Manual);
        var clock = new GatewayMessage(Wall, "/topic/SimSig", $$$"""{"clock_msg":{"area_id":"{{{area}}}","clock":25200,"interval":500,"paused":false}}""");
        var location = new GatewayMessage(Wall, "/topic/TRAIN_MVT_ALL_TOC", """{"train_location":{"headcode":"1A01","uid":"9001","action":"pass","location":"WATRLMN","platform":"","time":25210}}""");
        writer.OnMessage(clock, _parser.Parse(clock));
        writer.OnMessage(location, _parser.Parse(location));
        writer.Flush();
        return writer.Path!;
    }

    private string Replay(string area, string recording)
    {
        using var writer = SessionWriter.Create(Folder, new SessionInfo { Kind = SavedSessionKind.Replay, AreaId = area, RecordingPath = recording }, Manual);
        writer.OnStep(PlanStep.Execute(new CancelTrain(new TrainId("1A01"))));
        writer.Flush();
        return writer.Path!;
    }

    [Fact]
    public void A_sims_play_is_its_live_sessions_and_its_replays_recordings()
    {
        var recording = Fixtures.Path(Fixtures.Waterloo4x);
        var live = Live("waterloo");
        Live("penrith");
        Replay("waterloo", recording);
        Replay("waterloo", recording);
        Replay("waterloo", Path.Combine(Folder, "gone.sslog"));

        PlayFiles.ForArea(Folder, "Waterloo").Should().BeEquivalentTo([live, recording], "a recording replayed twice counts once, and a missing one not at all");
        PlayFiles.ForArea(Path.Combine(Folder, "none"), "waterloo").Should().BeEmpty();
    }

    [Fact]
    public void Play_is_read_from_a_recording_a_live_session_or_a_replay()
    {
        var recording = Fixtures.Path(Fixtures.Waterloo4x);

        PlayFiles.Read(Live("waterloo")).OfType<TrainLocationEvent>().Should().ContainSingle().Which.Location.Should().Be(new Tiploc("WATRLMN"));
        var fromRecording = PlayFiles.Read(recording).OfType<TrainLocationEvent>().Count();
        fromRecording.Should().BeGreaterThan(1000);
        PlayFiles.Read(Replay("waterloo", recording)).OfType<TrainLocationEvent>().Should().HaveCount(fromRecording);
    }

    [Fact]
    public void A_damaged_session_is_invalid_data()
    {
        var path = Path.Combine(Folder, "damaged" + SessionCatalogue.Extension);
        File.WriteAllText(path, "not a database");

        FluentActions.Invoking(() => PlayFiles.Read(path)).Should().Throw<InvalidDataException>();
    }
}
