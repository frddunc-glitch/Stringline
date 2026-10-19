using System.Globalization;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Core.Live;
using Stringline.Timetable.Live;

namespace Stringline.App.ViewModels;

/// <summary>How the status bar dot is coloured.</summary>
internal enum StatusLight
{
    Off,
    Busy,
    Good,
    Warning,
    Bad,
}

/// <summary>Status bar wording (CON-3), kept out of the view model so it can be tested on its own.</summary>
internal static class StatusText
{
    public static (string Text, StatusLight Light) Connection(SessionKind? kind, SessionStatus? status, string? description)
    {
        if (kind is null || status is null)
        {
            return ("Not connected", StatusLight.Off);
        }

        var what = description is null ? string.Empty : " " + description;
        return status.State switch
        {
            SessionState.Connecting => ($"Connecting to{what}…", StatusLight.Busy),
            SessionState.Connected => ($"Connected{what}", StatusLight.Good),
            SessionState.Reconnecting => (status.RetryIn is { } retry
                ? $"Disconnected — retrying in {Math.Max(1, (int)Math.Round(retry.TotalSeconds))} s"
                : "Disconnected — reconnecting…", StatusLight.Warning),
            SessionState.Playing => ($"Replay{what}", StatusLight.Good),
            SessionState.Paused => ($"Replay{what} (paused)", StatusLight.Warning),
            SessionState.Finished => ($"Replay{what} finished", StatusLight.Off),
            SessionState.Failed => (status.Detail ?? "Failed", StatusLight.Bad),
            _ => (kind == SessionKind.Replay ? "Replay closed" : "Disconnected", StatusLight.Off),
        };
    }

    /// <summary>What a clock shows before the sim's first clock message.</summary>
    public const string NoTime = "—";

    /// <summary>"Sim 09:14:32", or a dash before the first clock.</summary>
    public static string Clock(SimClockReading clock, SimTime now) => "Sim " + ClockTime(clock, now);

    /// <summary>"09:14:32" (a day marker after midnight, as <see cref="SimTime"/> writes it), or a dash before the first clock.</summary>
    public static string ClockTime(SimClockReading clock, SimTime now) =>
        clock.HasTime ? now.ToString("T", CultureInfo.InvariantCulture) : NoTime;

    /// <summary>The toolbar clock's font size for each setting (#13): the toolbar's text is 12.</summary>
    public static double ClockFontSize(ClockSize size) => size switch
    {
        ClockSize.Large => 24,
        ClockSize.Huge => 36,
        _ => 16,
    };

    /// <summary>"×4", "×1.5", "max", "paused", or empty before the first clock.</summary>
    public static string Speed(SimClockReading clock)
    {
        if (!clock.HasTime)
        {
            return string.Empty;
        }

        if (clock.Paused)
        {
            return "paused";
        }

        if (clock.MeasuredSpeed is null && clock.IsMaximumSpeed)
        {
            return "max";
        }

        var speed = clock.Speed;
        if (speed <= 0)
        {
            return string.Empty;
        }

        return speed >= 10 ? $"×{Math.Round(speed).ToString(CultureInfo.InvariantCulture)}" : $"×{Math.Round(speed, 1).ToString("0.#", CultureInfo.InvariantCulture)}";
    }

    /// <summary>"212 trains (3 unmatched)", with pending ones mentioned while matching.</summary>
    public static string Trains(MatchSummary matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var trains = matches.Trains == 1 ? "1 train" : $"{matches.Trains} trains";
        var notes = new List<string>(2);
        if (matches.Unmatched > 0)
        {
            notes.Add($"{matches.Unmatched} unmatched");
        }

        if (matches.Pending > 0)
        {
            notes.Add($"{matches.Pending} matching");
        }

        return notes.Count == 0 ? trains : $"{trains} ({string.Join(", ", notes)})";
    }
}
