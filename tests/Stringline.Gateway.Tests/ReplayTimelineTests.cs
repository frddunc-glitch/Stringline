using Microsoft.Extensions.Time.Testing;
using Stringline.Core;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Replay;

namespace Stringline.Gateway.Tests;

public sealed class ReplayTimelineTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private readonly FakeTimeProvider _time = new(T0.AddHours(1));
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));

    /// <summary>Frames at 0, 1, 3, 6 and 10 s, stamped with sim times 09:00, 09:01, 09:02, 09:03 and 09:04.</summary>
    private static SslogRecording Recording()
    {
        int[] seconds = [0, 1, 3, 6, 10];
        var messages = seconds.Select((s, i) => new SslogMessage(T0.AddSeconds(s), "/topic/x", new byte[] { (byte)('a' + i) }, new SimTime(32400 + (60 * i)))).ToList();
        return new SslogRecording(new SslogHeader(1, "test", T0, null, null, ["/topic/x"]), messages);
    }

    public void Dispose() => _timeout.Dispose();

    private sealed class Reader(IAsyncEnumerator<ReplayFrame> enumerator) : IAsyncDisposable
    {
        private Task<bool>? _pending;

        /// <summary>Starts reading the next frame (if not already) and reports whether it arrived within a moment.</summary>
        public async Task<bool> ArrivesSoon()
        {
            _pending ??= enumerator.MoveNextAsync().AsTask();
            await Task.WhenAny(_pending, Task.Delay(100));
            return _pending.IsCompleted;
        }

        public async Task<ReplayFrame> Next()
        {
            _pending ??= enumerator.MoveNextAsync().AsTask();
            (await _pending.WaitAsync(Wait)).Should().BeTrue("a frame was expected");
            _pending = null;
            return enumerator.Current;
        }

        public async Task<bool> Ended()
        {
            _pending ??= enumerator.MoveNextAsync().AsTask();
            var more = await _pending.WaitAsync(Wait);
            _pending = null;
            return !more;
        }

        public ValueTask DisposeAsync() => enumerator.DisposeAsync();
    }

    private Reader Read(ReplayTimeline timeline) => new(timeline.ReadAllAsync(_timeout.Token).GetAsyncEnumerator(_timeout.Token));

    [Fact]
    public async Task Plays_at_the_recorded_pace()
    {
        var timeline = new ReplayTimeline(Recording(), timeProvider: _time);
        await using var reader = Read(timeline);

        (await reader.Next()).Index.Should().Be(0);
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(999));
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        var frame = await reader.Next();

        frame.Should().BeEquivalentTo(new { Index = 1, FastForward = false, Rewound = false });
        timeline.Position.Should().Be(new SimTime(32460));
        timeline.NextIndex.Should().Be(2);
    }

    [Fact]
    public async Task Speed_scales_the_gaps_and_can_change_mid_wait()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { Speed = 2 }, _time);
        await using var reader = Read(timeline);
        await reader.Next();

        _time.Advance(TimeSpan.FromMilliseconds(500));
        (await reader.Next()).Index.Should().Be(1);

        // 2 s of recording to frame 2. Half a second in (1 s of recording), slow to 1×: 1 s more.
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(500));
        timeline.Speed = 1;
        _time.Advance(TimeSpan.FromMilliseconds(900));
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        (await reader.Next()).Index.Should().Be(2);
    }

    [Fact]
    public async Task Pause_holds_and_resume_carries_on_where_it_left_off()
    {
        var timeline = new ReplayTimeline(Recording(), timeProvider: _time);
        await using var reader = Read(timeline);
        await reader.Next();

        _time.Advance(TimeSpan.FromMilliseconds(600));
        timeline.Pause();
        timeline.IsPaused.Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(5));
        (await reader.ArrivesSoon()).Should().BeFalse();

        timeline.Resume();
        _time.Advance(TimeSpan.FromMilliseconds(399));
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromMilliseconds(1));
        (await reader.Next()).Index.Should().Be(1);
    }

    [Fact]
    public async Task Start_paused_waits_for_resume()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { StartPaused = true }, _time);
        await using var reader = Read(timeline);

        (await reader.ArrivesSoon()).Should().BeFalse();
        timeline.Resume();
        (await reader.Next()).Index.Should().Be(0);
    }

    [Fact]
    public async Task Infinite_speed_plays_everything_without_waiting_then_ends()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { Speed = double.PositiveInfinity }, _time);
        var frames = new List<ReplayFrame>();

        await foreach (var frame in timeline.ReadAllAsync(_timeout.Token))
        {
            frames.Add(frame);
        }

        frames.Select(f => f.Index).Should().Equal(0, 1, 2, 3, 4);
        timeline.IsFinished.Should().BeTrue();
        timeline.Position.Should().Be(new SimTime(32640));
    }

    [Fact]
    public async Task Slowing_down_from_infinite_speed_paces_from_the_playhead()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { Speed = double.PositiveInfinity }, _time);
        await using var reader = Read(timeline);
        await reader.Next();
        await reader.Next();

        timeline.Speed = 1;
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(2));
        (await reader.Next()).Index.Should().Be(2);
    }

    [Fact]
    public async Task Seeking_forward_fast_forwards_then_paces_from_the_target()
    {
        var timeline = new ReplayTimeline(Recording(), timeProvider: _time);
        await using var reader = Read(timeline);
        await reader.Next();

        timeline.SeekTo(new SimTime(32520)); // 09:02, frame 2
        var skipped = await reader.Next();
        var target = await reader.Next();

        skipped.Should().BeEquivalentTo(new { Index = 1, FastForward = true, Rewound = false });
        target.Should().BeEquivalentTo(new { Index = 2, FastForward = false });
        (await reader.ArrivesSoon()).Should().BeFalse();
        _time.Advance(TimeSpan.FromSeconds(3));
        (await reader.Next()).Index.Should().Be(3);
    }

    [Fact]
    public async Task Seeking_back_rewinds_to_the_start_and_flags_it()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { Speed = double.PositiveInfinity }, _time);
        await using var reader = Read(timeline);
        await reader.Next();
        await reader.Next();
        await reader.Next();

        timeline.SeekTo(new SimTime(32460)); // 09:01, frame 1
        var first = await reader.Next();
        var target = await reader.Next();

        first.Should().BeEquivalentTo(new { Index = 0, FastForward = true, RewoundTo = (SimTime?)new SimTime(32460) });
        target.Should().BeEquivalentTo(new { Index = 1, RewoundTo = (SimTime?)null });
    }

    [Fact]
    public async Task Seeking_back_to_the_very_start_flags_the_first_paced_frame()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { Speed = double.PositiveInfinity }, _time);
        await using var reader = Read(timeline);
        await reader.Next();
        await reader.Next();

        timeline.SeekTo(new SimTime(0));

        (await reader.Next()).Should().BeEquivalentTo(new { Index = 0, FastForward = false, Rewound = true });
    }

    [Fact]
    public async Task Start_at_fast_forwards_before_playing()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { StartAt = new SimTime(32580), StartPaused = true }, _time);
        await using var reader = Read(timeline);

        // Catching up happens even while paused, so state is ready before playback starts.
        (await reader.Next()).Should().BeEquivalentTo(new { Index = 0, FastForward = true });
        (await reader.Next()).Index.Should().Be(1);
        (await reader.Next()).Index.Should().Be(2);
        (await reader.ArrivesSoon()).Should().BeFalse();
        timeline.Resume();
        (await reader.Next()).Should().BeEquivalentTo(new { Index = 3, FastForward = false });
    }

    [Fact]
    public async Task Seeking_past_the_end_plays_everything_and_ends()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { StartAt = new SimTime(90000) }, _time);
        await using var reader = Read(timeline);

        for (var i = 0; i < 5; i++)
        {
            (await reader.Next()).FastForward.Should().BeTrue();
        }

        (await reader.Ended()).Should().BeTrue();
    }

    [Fact]
    public async Task Empty_recording_ends_at_once()
    {
        var timeline = new ReplayTimeline(new SslogRecording(Recording().Header, []), timeProvider: _time);
        await using var reader = Read(timeline);

        (await reader.Ended()).Should().BeTrue();
        timeline.IsFinished.Should().BeTrue();
        timeline.Position.Should().BeNull();
    }

    [Fact]
    public async Task Cancelling_stops_a_waiting_reader()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { StartPaused = true }, _time);
        using var stop = new CancellationTokenSource();
        var read = Task.Run(async () =>
        {
            await foreach (var _ in timeline.ReadAllAsync(stop.Token))
            {
            }
        });

        await Task.Delay(50);
        await stop.CancelAsync();

        await read.Invoking(r => r.WaitAsync(Wait)).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Can_only_be_read_once()
    {
        var timeline = new ReplayTimeline(Recording(), new ReplayOptions { Speed = double.PositiveInfinity }, _time);
        await foreach (var _ in timeline.ReadAllAsync())
        {
        }

        var again = async () =>
        {
            await foreach (var _ in timeline.ReadAllAsync())
            {
            }
        };

        await again.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Speed_must_be_positive(double speed)
    {
        var create = () => new ReplayTimeline(Recording(), new ReplayOptions { Speed = speed });
        create.Should().Throw<ArgumentOutOfRangeException>();

        var timeline = new ReplayTimeline(Recording());
        timeline.Invoking(t => t.Speed = speed).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Recording_summary_ignores_the_pre_start_zero_clock()
    {
        var header = Recording().Header;
        var messages = new List<SslogMessage>
        {
            new(T0, "/t", new byte[] { 1 }, null),
            new(T0.AddSeconds(1), "/t", new byte[] { 1 }, new SimTime(0)),
            new(T0.AddSeconds(5), "/t", new byte[] { 1 }, new SimTime(16200)),
            new(T0.AddSeconds(9), "/t", new byte[] { 1 }, new SimTime(16260)),
        };
        var recording = new SslogRecording(header, messages);

        recording.FirstSim.Should().Be(new SimTime(16200));
        recording.LastSim.Should().Be(new SimTime(16260));
        recording.Duration.Should().Be(TimeSpan.FromSeconds(9));
        recording.IndexOf(new SimTime(0)).Should().Be(1);
        recording.IndexOf(new SimTime(16201)).Should().Be(3);
        recording.IndexOf(new SimTime(99999)).Should().Be(4);
    }
}
