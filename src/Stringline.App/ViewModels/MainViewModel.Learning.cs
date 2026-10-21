using Microsoft.Extensions.Logging;
using Stringline.Core.Events;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Learning signal places (S37, #19). Every live or replayed session (and a review) feeds a <see cref="SignalLearner"/>
/// from the session's pump thread, so the engine is not slowed. What it has learned is pooled into the sim's profile
/// and saved when the session stops, and after the profile editor saves; the profile remembers each session and how
/// far it was learned, so a resumed, reviewed or replayed-again session counts nothing twice.
/// </summary>
internal sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _learnGate = new(1, 1);
    private SignalLearner? _learner;
    private string? _learningId;

    /// <summary>What the current session's learner sees; null when not learning.</summary>
    private Action<GatewayEvent>? LearnerFeed => _learner is { } learner ? learner.Observe : null;

    /// <summary>The ID learning keys the current session by, if it is learning.</summary>
    internal string? LearningSessionId => _learner is null ? null : _learningId;

    /// <summary>Start learning for a new session known by <paramref name="id"/> (none when learning is off).</summary>
    private void StartLearning(string? id)
    {
        _learner = _settings.LearnSignalPlaces && id is not null ? new SignalLearner() : null;
        _learningId = id;
    }

    /// <summary>A replay restarting from its start (a seek): the learner starts again with it.</summary>
    private void RestartLearning() => _learner?.Observe(new ReplayRewoundEvent(DateTimeOffset.UtcNow, default));

    /// <summary>
    /// Pools what the session has taught into the profile and saves it, logging one line. Nothing happens when not
    /// learning, with no timetable, for a timetable whose sim has no ID yet, or when the sim is not the timetable's.
    /// </summary>
    internal async Task LearnNowAsync()
    {
        if (_learner is not { } learner || _learningId is not { } id || _timetable is not { NeedsSimId: false } loaded)
        {
            return;
        }

        if (Snapshot.AreaId is { Length: > 0 } sim && !string.Equals(sim, loaded.Profile.AreaId, StringComparison.OrdinalIgnoreCase))
        {
            LogNotLearned(sim, loaded.Profile.AreaId);
            return;
        }

        await _learnGate.WaitAsync();
        try
        {
            var timetable = loaded.Timetable;
            var learned = await Task.Run(() => learner.Learn(timetable));
            if (learned.UpTo is null || _timetable is not { NeedsSimId: false } current || !ReferenceEquals(current.Timetable, timetable))
            {
                return;
            }

            var outcome = SignalLearning.Apply(current.Profile, learned, id, _settings.LearnMinSamples);
            if (outcome.Added == 0)
            {
                return;
            }

            _timetable = current with { Profile = outcome.Profile };
            if (SaveProfile(outcome.Profile) is { Error: { } error })
            {
                LogLearnSaveFailed(error);
            }

            var summary = outcome.Describe(id);
            LogLearned(summary);
        }
        finally
        {
            _learnGate.Release();
        }
    }

    /// <summary>
    /// Learn without waiting, and log rather than throw: when a replay finishes or the connection closes, and after the
    /// profile editor saves (the session's learning goes into the saved profile too).
    /// </summary>
    private async Task LearnQuietlyAsync()
    {
        try
        {
            await LearnNowAsync();
        }
#pragma warning disable CA1031 // Learning must never stop the editor's save.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLearnFailed(ex);
        }
    }

    /// <summary>For a replay of this recording: learning knows it by when it was made.</summary>
    private static string RecordingLearningId(Gateway.Recording.SslogRecording recording) => SignalLearning.RecordingId(recording.Header.Created);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Summary}")]
    private partial void LogLearned(string summary);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Learning signal places failed")]
    private partial void LogLearnFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Learned signal places not saved: {Error}")]
    private partial void LogLearnSaveFailed(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not learning signal places: the sim is {Sim} but the profile is {Profile}")]
    private partial void LogNotLearned(string sim, string profile);
}
