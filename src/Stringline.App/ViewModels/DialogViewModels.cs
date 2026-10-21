using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Gateway;

namespace Stringline.App.ViewModels;

internal static class FileFilters
{
    public const string Timetable = "SimSig timetables (*.wtt)|*.wtt|All files (*.*)|*.*";
    public const string Recording = "Stringline recordings (*.sslog)|*.sslog|All files (*.*)|*.*";
    public const string Png = "PNG images (*.png)|*.png";

    public const string Pdf = "PDF documents (*.pdf)|*.pdf";

    public const string Session = "Stringline sessions (*.stringline)|*.stringline|All files (*.*)|*.*";

    public const string Csv = "CSV files (*.csv)|*.csv|Text files (*.txt)|*.txt|All files (*.*)|*.*";

    public const string Profile = "Stringline profiles (*.json)|*.json|All files (*.*)|*.*";
}

/// <summary>The Connect dialog (CON-1, CON-5): where SimSig is, the optional login, and an optional timetable.</summary>
internal sealed partial class ConnectViewModel(IDialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string Host { get; set; } = "localhost";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string Port { get; set; } = GatewayTopics.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    public partial string UserName { get; set; } = string.Empty;

    /// <summary>Set by the dialog's password box; never bound to the screen or logged.</summary>
    public string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RememberPassword { get; set; }

    /// <summary>True while a password is saved in the settings, so Forget saved password has something to do.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ForgetPasswordCommand))]
    public partial bool HasSavedPassword { get; set; }

    /// <summary>Removes the saved password from the settings at once (S30); set by the main view model.</summary>
    public Action? ForgetSavedPassword { get; init; }

    /// <summary>Raised when the password is forgotten, so the dialog empties its password box.</summary>
    public event EventHandler? PasswordForgotten;

    [ObservableProperty]
    public partial bool SubscribeTd { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string TimetablePath { get; set; } = string.Empty;

    /// <summary>What is wrong with the input, or null if it can be used.</summary>
    public string? Error
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Host))
            {
                return "Enter the host SimSig runs on (usually localhost).";
            }

            if (!int.TryParse(Port, System.Globalization.CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                return "The port is a number from 1 to 65535 (SimSig's default is 51515).";
            }

            return TimetablePath.Length > 0 && !File.Exists(TimetablePath) ? "The timetable file does not exist." : null;
        }
    }

    public int PortNumber => int.Parse(Port, System.Globalization.CultureInfo.InvariantCulture);

    public GatewayClientOptions ToOptions() => new()
    {
        Host = Host.Trim(),
        Port = PortNumber,
        Login = string.IsNullOrWhiteSpace(UserName) ? null : UserName.Trim(),
        Passcode = string.IsNullOrEmpty(Password) ? null : Password,
        Topics = SubscribeTd ? GatewayTopics.WithTrainDescriber : GatewayTopics.Default,
    };

    /// <summary>
    /// Forget saved password (S30): the settings lose the password straight away, whether the dialog is then used to
    /// connect or cancelled, and Remember is unticked.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSavedPassword))]
    private void ForgetPassword()
    {
        ForgetSavedPassword?.Invoke();
        Password = string.Empty;
        RememberPassword = false;
        HasSavedPassword = false;
        PasswordForgotten?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void BrowseTimetable()
    {
        if (dialogs.PickFile("Choose the sim's timetable", FileFilters.Timetable, TimetablePath.Length > 0 ? TimetablePath : Timetable.WttCatalogue.DefaultFolder) is { } path)
        {
            TimetablePath = path;
        }
    }
}

/// <summary>A replay speed choice: a multiple of the recorded speed, or flat out.</summary>
internal sealed record ReplaySpeedOption(double Speed)
{
    public static IReadOnlyList<ReplaySpeedOption> All { get; } =
        [new(1), new(2), new(4), new(8), new(16), new(30), new(60), new(double.PositiveInfinity)];

    public static ReplaySpeedOption Nearest(double speed) =>
        double.IsPositiveInfinity(speed) ? All[^1] : All.Take(All.Count - 1).MinBy(o => Math.Abs(Math.Log(o.Speed / Math.Max(speed, 0.01))))!;

    public override string ToString() => double.IsPositiveInfinity(Speed) ? "Max" : $"{Speed}×";
}

/// <summary>The Replay dialog (CON-7): a recording, an optional timetable, the speed and where to start.</summary>
internal sealed partial class ReplayViewModel(IDialogService dialogs) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string RecordingPath { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string TimetablePath { get; set; } = string.Empty;

    public IReadOnlyList<ReplaySpeedOption> Speeds { get; } = ReplaySpeedOption.All;

    [ObservableProperty]
    public partial ReplaySpeedOption Speed { get; set; } = ReplaySpeedOption.Nearest(8);

    /// <summary>A sim time to start from (HH:mm), or empty for the start of the recording.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial string StartAt { get; set; } = string.Empty;

    public string? Error
    {
        get
        {
            if (RecordingPath.Length == 0 || !File.Exists(RecordingPath))
            {
                return "Choose a recording (.sslog) to replay.";
            }

            if (TimetablePath.Length > 0 && !File.Exists(TimetablePath))
            {
                return "The timetable file does not exist.";
            }

            return StartAt.Trim().Length > 0 && !SimTime.TryParse(StartAt.Trim(), out _) ? "Start at is a sim time such as 07:30, or empty." : null;
        }
    }

    public SimTime? StartTime => SimTime.TryParse(StartAt.Trim(), out var time) ? time : null;

    [RelayCommand]
    private void BrowseRecording()
    {
        if (dialogs.PickFile("Choose a recording to replay", FileFilters.Recording, RecordingPath) is { } path)
        {
            RecordingPath = path;
        }
    }

    [RelayCommand]
    private void BrowseTimetable()
    {
        if (dialogs.PickFile("Choose the sim's timetable", FileFilters.Timetable, TimetablePath.Length > 0 ? TimetablePath : Timetable.WttCatalogue.DefaultFolder) is { } path)
        {
            TimetablePath = path;
        }
    }
}
