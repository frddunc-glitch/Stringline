using System.Text;
using Stringline.Core;
using Stringline.Gateway.Recording;

namespace Stringline.Gateway.Tests;

public class SslogTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 12, 3, 123, TimeSpan.Zero);
    private static readonly SslogHeader Header = new(1, "Stringline test", T0, "localhost", 51515, [GatewayTopics.SimSig, GatewayTopics.TrainMovements]);

    private static string Write(Action<SslogWriter> write, SslogHeader? header = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new SslogWriter(stream, header ?? Header, ownsStream: false))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SslogReader Read(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Writes_header_messages_and_connection_lines_as_JSON_lines()
    {
        var text = Write(w =>
        {
            w.WriteConnection(new GatewayConnectionChange(T0, GatewayConnectionState.Connected, "FakeSimSig", [new("version", "1.1")]));
            w.WriteMessage(new GatewayMessage(T0.AddSeconds(1), GatewayTopics.SimSig, """{"clock_msg":{"clock":15720}}"""), new SimTime(15720));
            w.WriteMessage(new GatewayMessage(T0.AddSeconds(2), GatewayTopics.TrainMovements, "{}"));
            w.MessagesWritten.Should().Be(2);
        });

        text.Split('\n').Should().Equal(
            """{"sslog":1,"app":"Stringline test","created":"2026-10-18T09:12:03.123Z","host":"localhost","port":51515,"topics":["/topic/SimSig","/topic/TRAIN_MVT_ALL_TOC"]}""",
            """{"t":"2026-10-18T09:12:03.123Z","conn":"connected","detail":"FakeSimSig","headers":{"version":"1.1"}}""",
            """{"t":"2026-10-18T09:12:04.123Z","topic":"/topic/SimSig","sim":15720,"body":"{\"clock_msg\":{\"clock\":15720}}"}""",
            """{"t":"2026-10-18T09:12:05.123Z","topic":"/topic/TRAIN_MVT_ALL_TOC","body":"{}"}""",
            string.Empty);
    }

    [Fact]
    public void Round_trips_every_entry_kind()
    {
        byte[] notUtf8 = [0x7B, 0xC3, 0x28, 0x7D, 0x00];
        var text = Write(w =>
        {
            w.WriteConnection(new GatewayConnectionChange(T0, GatewayConnectionState.Connecting, "localhost:51515"));
            w.WriteConnection(new GatewayConnectionChange(T0, GatewayConnectionState.Connected, null, [new("server", "SimSig"), new("version", "1.1")]));
            w.WriteMessage(new GatewayMessage(T0, GatewayTopics.TrainDescriber, "{\"SG_MSG\":{\"obj_id\":\"é<>&\\u0001\"}}\r\n"), new SimTime(90000));
            w.WriteMessage(new GatewayMessage(T0, GatewayTopics.TrainMovements, notUtf8));
            w.WriteConnection(new GatewayConnectionChange(T0, GatewayConnectionState.Disconnected, "gone", RetryIn: TimeSpan.FromSeconds(1)));
        });

        using var reader = Read(text);
        reader.Header.Should().BeEquivalentTo(Header);
        var entries = reader.ReadEntries().ToList();
        reader.SkippedLines.Should().BeEmpty();

        entries.Should().HaveCount(5);
        entries[0].Should().Be(new SslogConnection(T0, GatewayConnectionState.Connecting, "localhost:51515"));
        entries[1].Should().BeOfType<SslogConnection>().Which.Headers.Should().Equal(new KeyValuePair<string, string>("server", "SimSig"), new KeyValuePair<string, string>("version", "1.1"));
        var td = entries[2].Should().BeOfType<SslogMessage>().Subject;
        td.BodyText.Should().Be("{\"SG_MSG\":{\"obj_id\":\"é<>&\\u0001\"}}\r\n");
        td.Sim.Should().Be(new SimTime(90000));
        td.At.Should().Be(T0);
        var raw = entries[3].Should().BeOfType<SslogMessage>().Subject;
        raw.Body.ToArray().Should().Equal(notUtf8);
        raw.Sim.Should().BeNull();
        raw.ToGatewayMessage().Topic.Should().Be(GatewayTopics.TrainMovements);
        entries[4].Should().BeOfType<SslogConnection>().Which.State.Should().Be(GatewayConnectionState.Disconnected);

        text.Should().Contain("\"body64\":").And.Contain("é<>&");
    }

    [Fact]
    public void Skips_malformed_and_truncated_lines()
    {
        var good = """{"t":"2026-10-18T09:12:03.123Z","topic":"/topic/SimSig","body":"{}"}""";
        var text = string.Join('\n',
            """{"sslog":1,"app":"x","created":"2026-10-18T09:12:03.123Z","topics":[],"future":{"a":1}}""",
            good,
            string.Empty,
            "not json",
            """{"topic":"/topic/SimSig","body":"{}"}""",
            """{"t":"2026-10-18T09:12:03.123Z","conn":"exploded"}""",
            """{"t":"2026-10-18T09:12:03.123Z","unrelated":true}""",
            """{"t":"yesterday","topic":"a","body":"{}"}""",
            """{"t":"2026-10-18T09:12:03.123Z","topic":"/topic/SimSig","extra":[1,{"b":2}],"body":"{}"}""",
            """{"t":"2026-10-18T09:12:03.123Z","topic":"/topic/Sim""");

        using var reader = Read(text);
        reader.Header.Host.Should().BeNull();
        reader.Header.Port.Should().BeNull();
        reader.Header.Topics.Should().BeEmpty();
        reader.ReadEntries().Should().HaveCount(2);
        reader.SkippedLines.Should().HaveCount(6);
        reader.SkippedLines[0].Should().StartWith("line 4:");
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("hello", "Not a .sslog")]
    [InlineData("""{"app":"x"}""", "no \"sslog\" version")]
    [InlineData("""{"sslog":2,"app":"x"}""", "format 2")]
    [InlineData("""{"sslog":"one"}""", "Not a .sslog")]
    public void Rejects_bad_headers(string firstLine, string message)
    {
        var act = () => Read(firstLine);

        act.Should().Throw<InvalidDataException>().WithMessage($"*{message}*");
    }

    [Fact]
    public void Live_header_names_this_build()
    {
        var header = SslogWriter.LiveHeader(T0, "simhost", 1234, [GatewayTopics.SimSig]);

        header.Format.Should().Be(SslogFormat.Version);
        header.App.Should().MatchRegex(@"^Stringline \d+\.\d+\.\d+$");
        header.Host.Should().Be("simhost");
    }

    [Fact]
    public void Create_refuses_to_overwrite_and_Open_reads_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stringline-{Guid.NewGuid():N}{SslogFormat.Extension}");
        try
        {
            using (var writer = SslogWriter.Create(path, Header))
            {
                writer.WriteMessage(new GatewayMessage(T0, "a", "{}"));
                writer.Flush();

                // Readable while still being written.
                using var live = SslogReader.Open(path);
                live.ReadEntries().Should().ContainSingle();
            }

            FluentActions.Invoking(() => SslogWriter.Create(path, Header)).Should().Throw<IOException>();
            using var reader = SslogReader.Open(path);
            reader.ReadEntries().Should().ContainSingle();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Writing_after_dispose_throws_and_double_dispose_is_safe()
    {
        var writer = new SslogWriter(new MemoryStream(), Header);
        writer.Dispose();
        writer.Dispose();
        writer.Flush();

        FluentActions.Invoking(() => writer.WriteMessage(new GatewayMessage(T0, "a", "{}"))).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Unsupported_entry_types_are_rejected()
    {
        using var writer = new SslogWriter(new MemoryStream(), Header);

        FluentActions.Invoking(() => writer.Write(new OtherEntry(T0))).Should().Throw<ArgumentException>();
    }

    private sealed record OtherEntry(DateTimeOffset At) : SslogEntry(At);

    [Fact]
    public void Opening_a_file_that_is_not_a_recording_does_not_leave_it_locked()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stringline-{Guid.NewGuid():N}.sslog");
        File.WriteAllText(path, "not a recording");
        try
        {
            var open = () => SslogReader.Open(path);
            open.Should().Throw<InvalidDataException>();
            var load = () => SslogRecording.Load(path);
            load.Should().Throw<InvalidDataException>();
        }
        finally
        {
            File.Delete(path);
        }

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void A_recording_loads_into_memory_with_its_connections_and_skipped_lines()
    {
        var text = Write(w =>
        {
            w.Write(new SslogConnection(T0, GatewayConnectionState.Connected));
            w.WriteMessage(new GatewayMessage(T0.AddSeconds(1), GatewayTopics.SimSig, "{}"), new SimTime(36000));
        }) + "{\"t\":\n";

        var recording = SslogRecording.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)));

        recording.Header.App.Should().Be("Stringline test");
        recording.Connections.Should().ContainSingle();
        recording.Messages.Should().ContainSingle().Which.Sim.Should().Be(new SimTime(36000));
        recording.SkippedLines.Should().ContainSingle();
    }
}
