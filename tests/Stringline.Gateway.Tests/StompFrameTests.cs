using System.Text;
using Stringline.Gateway.Stomp;

namespace Stringline.Gateway.Tests;

public class StompFrameTests
{
    private static StompFrameReader Reader(string wire, int chunk = int.MaxValue, int maxFrameBytes = StompFrameReader.DefaultMaxFrameBytes) =>
        Reader(Encoding.UTF8.GetBytes(wire), chunk, maxFrameBytes);

    private static StompFrameReader Reader(byte[] wire, int chunk = int.MaxValue, int maxFrameBytes = StompFrameReader.DefaultMaxFrameBytes) =>
        new(new ChunkedStream(wire, chunk), maxFrameBytes);

    private static async Task<List<StompFrame>> ReadAll(StompFrameReader reader)
    {
        var frames = new List<StompFrame>();
        while (await reader.ReadFrameAsync() is { } frame)
        {
            frames.Add(frame);
        }

        return frames;
    }

    // ---- Reader ----

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Reads_a_message_frame_whatever_the_read_size(int chunk)
    {
        var frames = await ReadAll(Reader("MESSAGE\ndestination:/topic/SimSig\nmessage-id:7\n\n{\"a\":1}\0", chunk));

        frames.Should().ContainSingle();
        var frame = frames[0];
        frame.Command.Should().Be("MESSAGE");
        frame.GetHeader("destination").Should().Be("/topic/SimSig");
        frame.GetHeader("message-id").Should().Be("7");
        frame.GetHeader("missing").Should().BeNull();
        frame.BodyText.Should().Be("{\"a\":1}");
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1)]
    public async Task Reads_back_to_back_frames_with_heartbeats_between(int chunk)
    {
        var frames = await ReadAll(Reader("\n\r\nCONNECTED\nversion:1.1\n\n\0\n\nMESSAGE\ndestination:a\n\nx\0\nMESSAGE\ndestination:b\n\ny\0\n", chunk));

        frames.Select(f => f.Command).Should().Equal("CONNECTED", "MESSAGE", "MESSAGE");
        frames.Select(f => f.BodyText).Should().Equal(string.Empty, "x", "y");
    }

    [Fact]
    public async Task Accepts_CR_LF_line_endings()
    {
        var frame = await Reader("MESSAGE\r\ndestination:/topic/x\r\n\r\nbody\0").ReadFrameAsync();

        frame!.Command.Should().Be("MESSAGE");
        frame.GetHeader("destination").Should().Be("/topic/x");
        frame.BodyText.Should().Be("body");
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(2)]
    public async Task Content_length_allows_NUL_in_the_body(int chunk)
    {
        var wire = Encoding.UTF8.GetBytes("MESSAGE\ncontent-length:5\n\n").Concat(new byte[] { 1, 0, 2, 0, 3, 0 }).Concat("MESSAGE\n\nz\0"u8.ToArray()).ToArray();

        var frames = await ReadAll(Reader(wire, chunk));

        frames.Should().HaveCount(2);
        frames[0].Body.ToArray().Should().Equal(1, 0, 2, 0, 3);
        frames[1].BodyText.Should().Be("z");
    }

    [Fact]
    public async Task Content_length_without_trailing_NUL_is_a_protocol_error()
    {
        var act = () => Reader("MESSAGE\ncontent-length:2\n\nabc\0").ReadFrameAsync().AsTask();

        await act.Should().ThrowAsync<StompProtocolException>().WithMessage("*not followed by NUL*");
    }

    [Theory]
    [InlineData("x")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Invalid_content_length_is_a_protocol_error(string length)
    {
        var act = () => Reader($"MESSAGE\ncontent-length:{length}\n\nabc\0").ReadFrameAsync().AsTask();

        await act.Should().ThrowAsync<StompProtocolException>().WithMessage("*content-length*");
    }

    [Fact]
    public async Task Unescapes_header_values()
    {
        var frame = await Reader("MESSAGE\nmessage:a\\cb\\nc\\\\d\\re\\q\nx\\c:y\n\n\0").ReadFrameAsync();

        frame!.GetHeader("message").Should().Be("a:b\nc\\d\re\\q");
        frame.GetHeader("x:").Should().Be("y");
    }

    [Fact]
    public async Task Does_not_unescape_CONNECTED_headers()
    {
        var frame = await Reader("CONNECTED\nserver:a\\cb\n\n\0").ReadFrameAsync();

        frame!.GetHeader("server").Should().Be("a\\cb");
    }

    [Fact]
    public async Task Value_may_contain_colons_and_first_repeated_header_wins()
    {
        var frame = await Reader("MESSAGE\ntime:09:30:00\ntime:10:00\n\n\0").ReadFrameAsync();

        frame!.GetHeader("time").Should().Be("09:30:00");
        frame.Headers.Should().HaveCount(2);
    }

    [Fact]
    public async Task Clean_end_of_stream_returns_null()
    {
        (await Reader(string.Empty).ReadFrameAsync()).Should().BeNull();
        (await Reader("\n\n").ReadFrameAsync()).Should().BeNull();
    }

    [Theory]
    [InlineData("MESS")]
    [InlineData("MESSAGE\ndest")]
    [InlineData("MESSAGE\n\nbody without nul")]
    [InlineData("MESSAGE\ncontent-length:10\n\nshort")]
    public async Task End_of_stream_mid_frame_throws(string wire)
    {
        var act = () => Reader(wire, chunk: 2).ReadFrameAsync().AsTask();

        await act.Should().ThrowAsync<StompProtocolException>().WithMessage("*middle of a frame*");
    }

    [Theory]
    [InlineData("MESSAGE\nno colon here\n\n\0")]
    [InlineData("MESSAGE\n:empty-name\n\n\0")]
    public async Task Malformed_header_line_throws(string wire)
    {
        var act = () => Reader(wire).ReadFrameAsync().AsTask();

        await act.Should().ThrowAsync<StompProtocolException>().WithMessage("*Malformed header*");
    }

    [Fact]
    public async Task Oversized_frames_are_rejected()
    {
        var big = "MESSAGE\n\n" + new string('x', 1000) + "\0";
        var withLength = "MESSAGE\ncontent-length:1000\n\n" + new string('x', 1000) + "\0";

        await Reader(big, chunk: 64, maxFrameBytes: 200).Invoking(r => r.ReadFrameAsync().AsTask())
            .Should().ThrowAsync<StompProtocolException>().WithMessage("*limit*");
        await Reader(withLength, maxFrameBytes: 200).Invoking(r => r.ReadFrameAsync().AsTask())
            .Should().ThrowAsync<StompProtocolException>().WithMessage("*limit*");
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(4096)]
    public async Task Grows_its_buffer_for_large_bodies(int chunk)
    {
        var body = new string('s', 300_000);

        var frames = await ReadAll(Reader($"MESSAGE\n\n{body}\0MESSAGE\ncontent-length:{body.Length}\n\n{body}\0", chunk));

        frames.Should().HaveCount(2);
        frames.Should().AllSatisfy(f => f.Body.Length.Should().Be(body.Length));
    }

    // ---- Writer ----

    [Fact]
    public void Writes_command_headers_content_length_body_and_NUL()
    {
        var bytes = StompFrameWriter.Encode(new StompFrame("SEND", [new("destination", "/topic/SimSig")], "{}"u8.ToArray()));

        Encoding.UTF8.GetString(bytes).Should().Be("SEND\ndestination:/topic/SimSig\ncontent-length:2\n\n{}\0");
    }

    [Fact]
    public void Keeps_an_explicit_content_length_and_omits_it_for_empty_bodies()
    {
        Encoding.UTF8.GetString(StompFrameWriter.Encode(new StompFrame("SEND", [new("content-length", "2")], "{}"u8.ToArray())))
            .Should().Be("SEND\ncontent-length:2\n\n{}\0");
        Encoding.UTF8.GetString(StompFrameWriter.Encode(new StompFrame("DISCONNECT")))
            .Should().Be("DISCONNECT\n\n\0");
    }

    [Fact]
    public void Escapes_headers_except_on_CONNECT()
    {
        Encoding.UTF8.GetString(StompFrameWriter.Encode(new StompFrame("SEND", [new("a:b", "c\\d\ne")])))
            .Should().Be("SEND\na\\cb:c\\\\d\\ne\n\n\0");
        Encoding.UTF8.GetString(StompFrameWriter.Encode(new StompFrame("CONNECT", [new("host", "a:b")])))
            .Should().Be("CONNECT\nhost:a:b\n\n\0");
    }

    [Fact]
    public void CONNECT_headers_must_not_contain_line_breaks()
    {
        var act = () => StompFrameWriter.Encode(new StompFrame("CONNECT", [new("passcode", "a\nb")]));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Round_trips_through_the_reader()
    {
        var original = new StompFrame("MESSAGE", [new("destination", "/topic/TD_ALL_SIG_AREA"), new("odd", "x:y\\z\nw")], "{\"x\":\"é\"}"u8.ToArray());

        var frame = await Reader(StompFrameWriter.Encode(original)).ReadFrameAsync();

        frame!.Command.Should().Be("MESSAGE");
        frame.GetHeader("destination").Should().Be("/topic/TD_ALL_SIG_AREA");
        frame.GetHeader("odd").Should().Be("x:y\\z\nw");
        frame.BodyText.Should().Be("{\"x\":\"é\"}");
        frame.ToString().Should().Contain("MESSAGE");
    }

    [Fact]
    public void Frame_requires_a_command()
    {
        var act = () => new StompFrame(" ");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read, to exercise frames split across reads.</summary>
    private sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], cancellationToken);
    }
}
