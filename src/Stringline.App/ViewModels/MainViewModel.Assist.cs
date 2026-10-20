using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Stringline.App.Services;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Assist (S19, AS-1 and AS-2): asking SimSig to set a route for a train's move, or interposing a description into a
/// TD berth. Off by default. The user turns it on for one live session (Session ▸ Assist), with a warning that names
/// multiplayer, and it turns itself off when that session ends. Every command is shown, with what it will send, and
/// sent only when the user presses Send. The route asked for a move is remembered in the profile and offered next time.
/// </summary>
internal sealed partial class MainViewModel
{
    public const string AssistTitle = "Assist";

    /// <summary>Shown when assist is turned on.</summary>
    public const string AssistWarning =
        "Assist lets Stringline send commands to SimSig: a route request for a train's move, or a description interposed " +
        "into a TD berth. Each one is sent only when you press Send to SimSig in its dialog, and SimSig acts on it as if " +
        "you had done it on the panel.\n\n" +
        "In a multiplayer game these act on the shared sim. Only set routes and interpose descriptions on your own panel, " +
        "and agree it with the other players first.\n\n" +
        "Assist stays on until this session ends.";

    /// <summary>Under the heading of each command's dialog.</summary>
    public const string AssistNote =
        "Sent to SimSig as soon as you press Send to SimSig. SimSig does it only if it can, and does not say whether it did: " +
        "check the panel. In multiplayer it acts on the shared sim.";

    /// <summary>Stringline may send commands to SimSig (S19). Only ever on during a live session; off again when it ends.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RequestRouteForRegulationCommand), nameof(InterposeForRegulationCommand), nameof(ToggleAssistCommand))]
    public partial bool AssistEnabled { get; private set; }

    /// <summary>The Session menu's text for the switch.</summary>
    public string AssistTip => AssistEnabled
        ? "Assist is on: right-click a train or a regulation row to request a route or interpose a description. Click to turn it off."
        : IsLive ? "Let Stringline send route requests and TD interposes to SimSig, each after you confirm it, until this session ends."
        : "Connect to SimSig to use assist. It never works in a replay or a review.";

    private bool CanAssistRegulation => AssistEnabled && Regulation.Selected is not null;

    private void InitAssist() => Regulation.PropertyChanged += OnRegulationChanged;

    private void OnRegulationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RegulationViewModel.Selected))
        {
            RequestRouteForRegulationCommand.NotifyCanExecuteChanged();
            InterposeForRegulationCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnAssistEnabledChanged(bool value) => OnPropertyChanged(nameof(AssistTip));

    partial void OnIsLiveChanged(bool value)
    {
        if (!value)
        {
            EndAssist();
        }

        OnPropertyChanged(nameof(AssistTip));
    }

    /// <summary>Session ▸ Assist: turn assist on (after the warning) or off.</summary>
    [RelayCommand(CanExecute = nameof(CanToggleAssist))]
    private void ToggleAssist()
    {
        if (AssistEnabled)
        {
            AssistEnabled = false;
            LogAssistOff("by hand");
            ShowInfo(InfoSeverity.Info, "Assist is off: Stringline will not send commands to SimSig.");
            return;
        }

        if (_session is not { Kind: SessionKind.Live } session)
        {
            return;
        }

        if (_dialogs.Choose(AssistTitle, AssistWarning, ["Allow for this session", "Cancel"]) != 0 || !ReferenceEquals(session, _session))
        {
            return;
        }

        AssistEnabled = true;
        LogAssistOn(session.Description);
        ShowInfo(InfoSeverity.Info, "Assist is on until this session ends. Right-click a train or a regulation row to request a route or interpose a description.");
    }

    private bool CanToggleAssist() => IsLive || AssistEnabled;

    /// <summary>Turns assist off when its session ends; it is never carried into the next one.</summary>
    private void EndAssist()
    {
        if (AssistEnabled)
        {
            AssistEnabled = false;
            LogAssistOff("the session ended");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAssistRegulation))]
    private Task RequestRouteForRegulationAsync() =>
        Regulation.Selected is { } row ? RequestRouteAsync(row.Train, row.Index) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(CanAssistRegulation))]
    private Task InterposeForRegulationAsync() =>
        Regulation.Selected is { } row ? InterposeAsync(new Headcode(row.Headcode)) : Task.CompletedTask;

    /// <summary>The graph's right-click items for a train at a plan point, when assist is on.</summary>
    private IEnumerable<PlanMenuItem> AssistMenuItems(TrainId train, Headcode headcode, int? index)
    {
        if (!AssistEnabled)
        {
            yield break;
        }

        yield return PlanMenuItem.Separator;
        if (index is { } at)
        {
            yield return new PlanMenuItem("Re_quest route in SimSig…", new AsyncRelayCommand(() => RequestRouteAsync(train, at)));
        }

        yield return new PlanMenuItem("Interpose _description in SimSig…", new AsyncRelayCommand(() => InterposeAsync(headcode)));
    }

    /// <summary>
    /// AS-1: asks for the route a train's move needs (offering the one remembered for the move, and the sim's route and
    /// signal IDs), then sends the route request and remembers it in the profile.
    /// </summary>
    public async Task RequestRouteAsync(TrainId train, int index)
    {
        if (AssistRefusal is { } refusal)
        {
            ShowInfo(InfoSeverity.Warning, refusal);
            return;
        }

        if (Snapshot.Plan.Find(train) is not { } plan || index < 0 || index >= plan.RunningPoints.Count)
        {
            ShowInfo(InfoSeverity.Warning, "That train or stop is no longer in the plan.");
            return;
        }

        var move = AssistMoves.Of(plan, index);
        var remembered = Profile?.FindAssistRoute(move);
        var signalling = Snapshot.Signalling;
        var nearby = Profile?.AssistRoutes.Where(r => r.Move.Location == move.Location).Reverse().ToList() ?? [];
        var routes = nearby.Select(r => r.Route).OfType<string>().Concat(signalling.Routes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var signals = nearby.SelectMany(r => new[] { r.FromSignal, r.ToSignal }).OfType<string>().Concat(signalling.Signals).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var known = signalling.Routes.IsEmpty && signalling.Signals.IsEmpty
            ? "SimSig's route and signal IDs are listed once Stringline has had its snapshot: connect with the TD topic ticked."
            : null;
        var prompt = new PromptViewModel(
            "Request a route in SimSig",
            DescribeMove(plan, index) + (remembered is null ? string.Empty : $" Last time: {remembered}."),
            new PromptField("Route ID", remembered?.Route ?? string.Empty, routes, "As SimSig names it, e.g. RWL123A. Or leave it empty and give the signals."),
            new PromptField("From signal", remembered?.FromSignal ?? string.Empty, signals),
            new PromptField("To signal", remembered?.ToSignal ?? string.Empty, signals, known))
        {
            Note = AssistNote,
            AcceptText = "Send to SimSig",
            Validate = ValidateRoute,
        };
        if (!_dialogs.Prompt(prompt))
        {
            return;
        }

        var route = new AssistRoute(move, Optional(prompt[0]), Optional(prompt[1]), Optional(prompt[2]));
        var command = route.Route is { } id ? GatewayCommands.RouteRequest(id) : GatewayCommands.RouteRequest(route.FromSignal!, route.ToSignal!);
        if (await SendAssistAsync(command, $"route {route} for {plan.Headcode} at {Name(move.Location)}"))
        {
            RememberAssistRoute(route);
        }
    }

    /// <summary>AS-2: asks for a TD berth (offering those seen) and interposes the train's description into it.</summary>
    public async Task InterposeAsync(Headcode headcode)
    {
        if (AssistRefusal is { } refusal)
        {
            ShowInfo(InfoSeverity.Warning, refusal);
            return;
        }

        var signalling = Snapshot.Signalling;
        var already = signalling.BerthsOf(headcode.Value).ToList();
        var heading = already.Count switch
        {
            0 => $"Put {headcode} into a TD berth.",
            1 => $"Put {headcode} into a TD berth. SimSig shows it in berth {already[0]} now.",
            _ => $"Put {headcode} into a TD berth. SimSig shows it in berths {string.Join(", ", already)} now.",
        };
        var prompt = new PromptViewModel(
            "Interpose a description in SimSig",
            heading,
            new PromptField(
                "Berth",
                string.Empty,
                signalling.Berths,
                signalling.Berths.IsEmpty
                    ? "The berth as SimSig names it, e.g. 0235. Berths are listed once descriptions have stepped through them: connect with the TD topic ticked."
                    : "The berth as SimSig names it, e.g. 0235."),
            new PromptField("Description", headcode.Value, null, "Usually the headcode."))
        {
            Note = AssistNote,
            AcceptText = "Send to SimSig",
            Validate = p => !GatewayCommands.IsValidId(p[0]) ? "Enter the berth: a code with no spaces, such as 0235."
                : !GatewayCommands.IsValidId(p[1]) ? "Enter the description: a code with no spaces, such as 1A23."
                : null,
        };
        if (!_dialogs.Prompt(prompt))
        {
            return;
        }

        await SendAssistAsync(GatewayCommands.TdInterpose(prompt[0], prompt[1]), $"{prompt[1]} interposed into berth {prompt[0]}");
    }

    /// <summary>Why no command can be sent now, or null if one can.</summary>
    private string? AssistRefusal =>
        _session is not { Kind: SessionKind.Live } ? "Assist only works while connected to SimSig."
        : !AssistEnabled ? "Assist is off. Turn it on with Session ▸ Assist to send commands to SimSig."
        : null;

    private async Task<bool> SendAssistAsync(GatewayCommand command, string what)
    {
        if (_session is not { Kind: SessionKind.Live } session || !AssistEnabled)
        {
            return false;
        }

        LogAssistSending(command);
        if (!await session.SendAsync(command))
        {
            ShowInfo(InfoSeverity.Error, $"Could not send to SimSig ({what}): it is not connected. The log has the details.");
            return false;
        }

        EditStatus = $"Sent to SimSig: {what}";
        ShowInfo(InfoSeverity.Info, $"Sent to SimSig: {what}. Check the panel: SimSig does not say whether it was done.");
        return true;
    }

    /// <summary>Keeps the route for its move in the profile, saving it unless the sim is not yet known.</summary>
    private void RememberAssistRoute(AssistRoute route)
    {
        if (_timetable is not { Profile: { } profile } loaded)
        {
            return;
        }

        profile = profile.WithAssistRoute(route);
        _timetable = loaded with { Profile = profile };
        if (!loaded.NeedsSimId && SaveProfile(profile) is { Error: { } error })
        {
            ShowInfo(InfoSeverity.Warning, $"Sent to SimSig, but {char.ToLowerInvariant(error[0])}{error[1..]}");
        }
    }

    private static string? ValidateRoute(PromptViewModel p)
    {
        var (route, from, to) = (p[0], p[1], p[2]);
        if (route.Length > 0)
        {
            return !GatewayCommands.IsValidId(route) ? "A route ID is a code with no spaces, such as RWL123A."
                : from.Length > 0 || to.Length > 0 ? "Give a route ID or the two signals, not both."
                : null;
        }

        return from.Length == 0 && to.Length == 0 ? "Give a route ID, or the signals at each end of the route."
            : !GatewayCommands.IsValidId(from) || !GatewayCommands.IsValidId(to) ? "Give both signals, as SimSig names them (e.g. SWL12), with no spaces."
            : null;
    }

    private static string? Optional(string text) => text.Length == 0 ? null : text;

    /// <summary>"2S01 calls at Bletchley (platform 2) at 08:06, from Denbigh Hall towards Milton Keynes."</summary>
    private string DescribeMove(TrainPlan plan, int index)
    {
        var point = plan.RunningPoints[index];
        var move = AssistMoves.Of(plan, index);
        var does = point.IsPass ? "passes" : point.Depart is null ? "arrives at" : point.Arrive is null ? "starts from" : "calls at";
        var where = new List<string>();
        if (move.Platform is { } platform)
        {
            where.Add($"platform {platform}");
        }

        if (move.Line is { } line)
        {
            where.Add($"line {line}");
        }

        var at = point.Depart ?? point.Arrive;
        var text = $"{plan.Headcode} {does} {Name(point.Location)}" +
            (where.Count > 0 ? $" ({string.Join(", ", where)})" : string.Empty) +
            (at is { } t ? $" at {PlanInput.Format(t)}" : string.Empty);
        var route = (move.From, move.Towards) switch
        {
            ({ } f, { } to) => $", from {Name(f)} towards {Name(to)}",
            ({ } f, null) => $", from {Name(f)}",
            (null, { } to) => $", towards {Name(to)}",
            _ => string.Empty,
        };
        return text + route + ".";
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Assist on for {Session}")]
    private partial void LogAssistOn(string session);

    [LoggerMessage(Level = LogLevel.Information, Message = "Assist off: {Reason}")]
    private partial void LogAssistOff(string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Assist: sending {Command}")]
    private partial void LogAssistSending(GatewayCommand command);
}
