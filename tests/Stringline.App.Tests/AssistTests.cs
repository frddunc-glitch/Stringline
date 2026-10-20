using System.Globalization;
using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Stomp;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>
/// Assist (S19, AS-1 and AS-2): off by default, turned on for one live session after a warning that names multiplayer,
/// and every command confirmed before it is sent. Driven against a fake SimSig, which records what reaches it.
/// </summary>
public sealed class AssistTests
{
    private const string Waterloo = "waterloo 2010.WTT";

    /// <summary>Connects to <paramref name="server"/> with the TD topic and Waterloo's WTT, and waits for the plan.</summary>
    private static async Task<MainViewModel> ConnectAsync(TestApp app, FakeStompServer server)
    {
        var vm = app.ViewModel;
        app.Dialogs.Connect = d =>
        {
            d.Host = "127.0.0.1";
            d.Port = server.Port.ToString(CultureInfo.InvariantCulture);
            d.SubscribeTd = true;
            d.TimetablePath = Fixtures.Path(Waterloo);
            return true;
        };
        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
        await server.WaitForSubscribersAsync(GatewayTopics.TrainDescriber);
        await app.Ui.WaitUntil(() => vm.ConnectionLight == StatusLight.Good && vm.Snapshot.Plan.Count > 0, "connected with the WTT");
        return vm;
    }

    private static async Task TurnOnAsync(TestApp app)
    {
        app.Dialogs.Choices.Enqueue(0);
        await app.Ui.Run(() => app.ViewModel.ToggleAssistCommand.Execute(null));
        (await app.Ui.Run(() => app.ViewModel.AssistEnabled)).Should().BeTrue();
    }

    /// <summary>A Waterloo train and one of its calls with a platform and somewhere to go next.</summary>
    private static (TrainPlan Plan, int Index) ACall(MainViewModel vm)
    {
        foreach (var plan in vm.Snapshot.Plan.Trains)
        {
            var points = plan.RunningPoints;
            for (var i = 1; i + 1 < points.Count; i++)
            {
                if (points[i].IsCall && !string.IsNullOrWhiteSpace(points[i].Platform))
                {
                    return (plan, i);
                }
            }
        }

        throw new InvalidOperationException("No call with a platform.");
    }

    /// <summary>The same, on a WTT line in the corridor shown, with a hit on the graph at it.</summary>
    private static (TrainPlan Plan, int Index, GraphHit Hit) ACallOnTheGraph(MainViewModel vm)
    {
        foreach (var path in vm.Scene!.InLayer(GraphLayer.Wtt))
        {
            var plan = vm.Snapshot.Plan.Find(path.Train!.Value)!;
            for (var v = 0; v < path.Vertices.Count; v++)
            {
                if (path.PointAt(v) is { } i && i >= 1 && i + 1 < plan.RunningPoints.Count && plan.RunningPoints[i] is { IsCall: true, Platform: { Length: > 0 } })
                {
                    return (plan, i, new GraphHit(path, 0, 0, v, path.Vertices[v].Time));
                }
            }
        }

        throw new InvalidOperationException("No call with a platform on the graph.");
    }

    private static Task<StompFrame> SentAsync(FakeStompServer server, string type) =>
        server.WaitForFrameAsync(f => f.Command == StompCommands.Send && f.BodyText.Contains(type, StringComparison.Ordinal));

    private static IEnumerable<StompFrame> Sends(FakeStompServer server) =>
        server.ReceivedFrames().Where(f => f.Command == StompCommands.Send && f.GetHeader("destination") == GatewayTopics.TrainDescriber && !f.BodyText.Contains("snapshot", StringComparison.Ordinal));

    [Fact]
    public async Task Is_off_by_default_and_cannot_be_turned_on_without_a_live_session()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(Fixtures.Path(Waterloo)));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Count > 0, "the plan");

        (await app.Ui.Run(() => vm.AssistEnabled)).Should().BeFalse();
        (await app.Ui.Run(() => vm.ToggleAssistCommand.CanExecute(null))).Should().BeFalse("there is no live session");
        (await app.Ui.Run(() => vm.AssistTip)).Should().Contain("Connect to SimSig");

        var (plan, index) = await app.Ui.Run(() => ACall(vm));
        await app.Ui.RunAsync(() => vm.RequestRouteAsync(plan.Id, index));
        app.Dialogs.Prompted.Should().BeEmpty("nothing is asked, let alone sent");
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Be("Assist only works while connected to SimSig.");
    }

    [Fact]
    public async Task Turning_it_on_warns_about_multiplayer_and_lasts_only_for_the_session()
    {
        await using var server = FakeStompServer.Start();
        await using var app = new TestApp();
        var vm = await ConnectAsync(app, server);
        (await app.Ui.Run(() => vm.ToggleAssistCommand.CanExecute(null))).Should().BeTrue();

        app.Dialogs.Choices.Enqueue(1);
        await app.Ui.Run(() => vm.ToggleAssistCommand.Execute(null));
        (await app.Ui.Run(() => vm.AssistEnabled)).Should().BeFalse("Cancel leaves it off");
        app.Dialogs.Asked.Should().ContainSingle().Which.Should().Contain("multiplayer").And.Contain("until this session ends");

        await TurnOnAsync(app);
        (await app.Ui.Run(() => vm.AssistTip)).Should().StartWith("Assist is on");

        await app.Ui.Run(() => vm.ToggleAssistCommand.Execute(null));
        (await app.Ui.Run(() => vm.AssistEnabled)).Should().BeFalse("it turns off again without asking");
        app.Dialogs.Asked.Should().HaveCount(2);

        await TurnOnAsync(app);
        await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
        (await app.Ui.Run(() => vm.AssistEnabled)).Should().BeFalse("it ends with the session");

        await ConnectAsync(app, server);
        (await app.Ui.Run(() => vm.AssistEnabled)).Should().BeFalse("it is never carried into the next session");
    }

    [Fact]
    public async Task A_route_request_is_confirmed_sent_and_remembered_for_the_move()
    {
        await using var server = FakeStompServer.Start();
        await using var app = new TestApp();
        var vm = await ConnectAsync(app, server);
        await server.PublishAsync(GatewayTopics.TrainDescriber, """{"SG_MSG":{"obj_id":"RWL1A","obj_type":"route","is_set":"False"},"SG_MSG":{"obj_id":"SWL1","obj_type":"signal","aspect":"0"}}""");
        await app.Ui.WaitUntil(() => vm.Snapshot.Signalling.Routes.Count == 1 && vm.Snapshot.Signalling.Signals.Count == 1, "the sim's names");
        await TurnOnAsync(app);
        var (plan, index) = await app.Ui.Run(() => ACall(vm));
        var move = AssistMoves.Of(plan, index);

        app.Dialogs.Prompts.Enqueue(p => false);
        await app.Ui.RunAsync(() => vm.RequestRouteAsync(plan.Id, index));
        var first = app.Dialogs.Prompted.Single();
        first.Heading.Should().StartWith($"{plan.Headcode} calls at ").And.Contain($"(platform {move.Platform}").And.Contain("towards");
        first.Note.Should().Contain("multiplayer");
        first.AcceptText.Should().Be("Send to SimSig");
        first.Fields[0].Options.Should().Equal("RWL1A");
        first.Fields[1].Options.Should().Equal("SWL1");
        first.Fields[0].Value.Should().BeEmpty("nothing is remembered yet");

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value = "RWL1A";
            p.Fields[1].Value = "SWL1";
            p.TryAccept().Should().BeFalse();
            p.Error.Should().Be("Give a route ID or the two signals, not both.");
            p.Fields[0].Value = " ";
            p.TryAccept().Should().BeFalse();
            p.Error.Should().StartWith("Give both signals");
            p.Fields[0].Value = "RWL1A";
            p.Fields[1].Value = string.Empty;
            return true;
        });
        await app.Ui.RunAsync(() => vm.RequestRouteAsync(plan.Id, index));

        var sent = await SentAsync(server, "routerequest");
        sent.GetHeader("destination").Should().Be(GatewayTopics.TrainDescriber);
        sent.BodyText.Should().Be("""{"routerequest":{"route":"RWL1A"}}""");
        (await app.Ui.Run(() => vm.InfoMessage)).Should().StartWith($"Sent to SimSig: route RWL1A for {plan.Headcode} at ");
        var saved = new ProfileStore(app.Paths.Profiles).Load("waterloo")!;
        saved.FindAssistRoute(move)!.Route.Should().Be("RWL1A");

        // The next train making the same move is offered the same route; signals are sent as a pair.
        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Value.Should().Be("RWL1A");
            p.Heading.Should().EndWith("Last time: RWL1A.");
            (p.Fields[0].Value, p.Fields[1].Value, p.Fields[2].Value) = (string.Empty, "SWL1", "SWL3");
            return true;
        });
        await app.Ui.RunAsync(() => vm.RequestRouteAsync(plan.Id, index));
        (await SentAsync(server, "fromSignal")).BodyText.Should().Be("""{"routerequest":{"fromSignal":"SWL1","toSignal":"SWL3"}}""");
        (await app.Ui.Run(() => vm.Profile!.FindAssistRoute(move)!.ToString())).Should().Be("SWL1 to SWL3");
        Sends(server).Should().HaveCount(2, "the cancelled dialog sent nothing");
    }

    [Fact]
    public async Task A_description_is_interposed_into_a_berth_the_sim_has_named()
    {
        await using var server = FakeStompServer.Start();
        await using var app = new TestApp();
        var vm = await ConnectAsync(app, server);
        await server.PublishAsync(GatewayTopics.TrainDescriber, """{"CA_MSG":{"area_id":"waterloo","from":"0243","to":"0235","descr":"2R02","msg_type":"CA","time":"16250"}}""");
        await app.Ui.WaitUntil(() => vm.Snapshot.Signalling.Berths.Count == 2, "the berths");
        await TurnOnAsync(app);
        var (plan, _) = await app.Ui.Run(() => ACall(vm));

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Fields[0].Options.Should().Equal("0235", "0243");
            p.Fields[1].Value.Should().Be(plan.Headcode.Value);
            p.Fields[0].Value = "0243";
            return true;
        });
        await app.Ui.RunAsync(() => vm.InterposeAsync(plan.Headcode));

        (await SentAsync(server, "cc_msg")).BodyText.Should().Be($$$"""{"cc_msg":{"to":"0243","descr":"{{{plan.Headcode}}}"}}""");

        app.Dialogs.Prompts.Enqueue(p => true);
        await app.Ui.RunAsync(() => vm.InterposeAsync(new Headcode("2R02")));
        app.Dialogs.Prompted[^1].Heading.Should().Be("Put 2R02 into a TD berth. SimSig shows it in berth 0235 now.");
        app.Dialogs.Prompted[^1].Error.Should().StartWith("Enter the berth");
        Sends(server).Should().ContainSingle("a prompt that does not validate sends nothing");
    }

    [Fact]
    public async Task The_regulation_list_and_the_graph_offer_it_only_while_it_is_on()
    {
        await using var server = FakeStompServer.Start();
        await using var app = new TestApp();
        var vm = await ConnectAsync(app, server);
        var (plan, index, hit) = await app.Ui.Run(() => ACallOnTheGraph(vm));
        var location = plan.RunningPoints[index].Location;
        await app.Ui.Run(() =>
        {
            vm.ShowRegulation(location);
            vm.Regulation.Selected = vm.Regulation.Rows.First(r => r.Train == plan.Id && r.Index == index);
        });

        (await app.Ui.Run(() => vm.RequestRouteForRegulationCommand.CanExecute(null))).Should().BeFalse();
        (await app.Ui.Run(() => vm.InterposeForRegulationCommand.CanExecute(null))).Should().BeFalse();
        (await app.Ui.Run(() => vm.MenuFor(hit))).Should().NotContain(i => i.Header != null && i.Header.Contains("SimSig", StringComparison.Ordinal));

        await TurnOnAsync(app);
        (await app.Ui.Run(() => vm.RequestRouteForRegulationCommand.CanExecute(null))).Should().BeTrue();
        var menu = await app.Ui.Run(() => vm.MenuFor(hit));
        menu.Select(i => i.Header).Should().Contain("Interpose _description in SimSig…");

        app.Dialogs.Prompts.Enqueue(p =>
        {
            p.Heading.Should().StartWith($"{plan.Headcode} calls at ");
            p.Fields[0].Value = "RWL9A";
            return true;
        });
        await app.Ui.RunAsync(() => vm.RequestRouteForRegulationCommand.ExecuteAsync(null));
        (await SentAsync(server, "routerequest")).BodyText.Should().Be("""{"routerequest":{"route":"RWL9A"}}""");
    }

    [Fact]
    public async Task A_replay_never_sends()
    {
        await using var app = new TestApp();
        var vm = app.ViewModel;
        app.Dialogs.Replay = d =>
        {
            d.RecordingPath = Fixtures.Path("waterloo-1x.sslog");
            d.TimetablePath = Fixtures.Path(Waterloo);
            return true;
        };
        await app.Ui.RunAsync(() => vm.OpenReplayCommand.ExecuteAsync(null));
        await app.Ui.WaitUntil(() => vm.HasSession && vm.Snapshot.Plan.Count > 0, "the replay");

        (await app.Ui.Run(() => vm.ToggleAssistCommand.CanExecute(null))).Should().BeFalse();
        (await app.Ui.Run(() => vm.AssistTip)).Should().Contain("never works in a replay");
        await app.Ui.RunAsync(() => vm.InterposeAsync(new Headcode("1A01")));
        app.Dialogs.Prompted.Should().BeEmpty();
    }
}
