using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Services;
using Stringline.Core;

namespace Stringline.App.ViewModels;

/// <summary>
/// Replay controls (CON-7): play/pause, speed and a seek bar over the recording's sim time. A seek after the replay has
/// finished asks the owner to start it again from there, because a finished timeline cannot be read again.
/// </summary>
internal sealed partial class TransportViewModel : ObservableObject
{
    private readonly SimSession _session;
    private readonly Action<SimTime> _restartAt;

    public TransportViewModel(SimSession session, double speed, Action<SimTime> restartAt)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _restartAt = restartAt;
        var recording = session.Replay?.Recording ?? throw new ArgumentException("Not a replay session.", nameof(session));
        Minimum = recording.FirstSim?.Seconds ?? 0;
        Maximum = Math.Max(Minimum + 1, recording.LastSim?.Seconds ?? Minimum + 1);
        Speed = ReplaySpeedOption.Nearest(speed);
        Position = Minimum;
        Tick();
    }

    public IReadOnlyList<ReplaySpeedOption> Speeds { get; } = ReplaySpeedOption.All;

    public double Minimum { get; }

    public double Maximum { get; }

    public string RangeText => $"{Format(Minimum)}–{Format(Maximum)}";

    [ObservableProperty]
    public partial ReplaySpeedOption Speed { get; set; }

    /// <summary>The seek bar position in sim seconds; follows the replay unless the user is dragging it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    public partial double Position { get; set; }

    public string PositionText => Format(Position);

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    public partial bool IsFinished { get; set; }

    /// <summary>True while the user drags the seek bar.</summary>
    public bool IsSeeking { get; set; }

    partial void OnSpeedChanged(ReplaySpeedOption value)
    {
        if (_session.Timeline is { } timeline)
        {
            timeline.Speed = value.Speed;
        }
    }

    /// <summary>Catch up with the replay (called a few times a second).</summary>
    public void Tick()
    {
        var state = _session.Status.State;
        IsPlaying = state == SessionState.Playing;
        IsFinished = state == SessionState.Finished;
        if (!IsSeeking && _session.Timeline?.Position is { } position)
        {
            Position = Math.Clamp(position.Seconds, Minimum, Maximum);
        }
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (IsFinished)
        {
            _restartAt(new SimTime((int)Minimum));
        }
        else if (_session.Status.State == SessionState.Playing)
        {
            _session.Pause();
        }
        else
        {
            _session.Resume();
        }

        Tick();
    }

    /// <summary>The user let go of the seek bar: go to <see cref="Position"/>.</summary>
    public void CommitSeek()
    {
        IsSeeking = false;
        var target = new SimTime((int)Math.Round(Position));
        if (IsFinished || _session.Timeline is null)
        {
            _restartAt(target);
        }
        else
        {
            _session.Timeline.SeekTo(target);
        }
    }

    private static string Format(double seconds) => new SimTime((int)Math.Round(seconds)).ToString("t", CultureInfo.InvariantCulture);
}
