using Stringline.Core.Timetables;
using Stringline.Gateway;
using Stringline.Gateway.Recording;
using Stringline.Gateway.Tests;
using Stringline.Planning;
using Stringline.Timetable;

namespace Stringline.Persistence.Tests;

/// <summary>
/// A Waterloo recording saved as a live session part way through, with plan edits, then resumed into a fresh engine:
/// the same trains, matches and plan as the engine that saw it live (SES-1: "rebuild plan from commands + events").
/// </summary>
public sealed class SessionResumeFixtureTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly Lazy<WorkingTimetable> Waterloo = new(() => new WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable);

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-resume-");

    public void Dispose() => _root.Delete(recursive: true);

    private static async Task<PlanSnapshot> Settled(PlanningEngine engine, Func<PlanSnapshot, bool> until)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!until(engine.Current))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The engine did not settle.");
            }

            await Task.Delay(10);
        }

        return engine.Current;
    }

    [Fact]
    public async Task A_resumed_session_has_the_trains_and_plan_it_was_saved_with()
    {
        var recording = SslogRecording.Load(Fixtures.Path(Fixtures.Waterloo4x));
        var messages = recording.Messages.Select(m => m.ToGatewayMessage()).ToList();
        var parser = new MessageParser();
        string path;
        PlanSnapshot live;

        var engine = new PlanningEngine(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero });
        using (var cts = new CancellationTokenSource())
        using (var writer = SessionWriter.Create(_root.FullName, new SessionInfo { Kind = SavedSessionKind.Live, Host = "localhost", Port = 51515 }, new SessionWriterOptions { AutosaveInterval = TimeSpan.Zero }))
        {
            var loop = engine.RunAsync(cts.Token);
            engine.SetTimetable(Waterloo.Value);
            await Settled(engine, s => s.Timetable is not null);
            engine.PlanStepped += (_, step) => writer.OnStep(step);

            var half = messages.Count / 2;
            void Feed(GatewayMessage m)
            {
                var events = parser.Parse(m);
                writer.OnMessage(m, events);
                foreach (var e in events)
                {
                    engine.Post(e);
                }
            }

            messages.Take(half).ToList().ForEach(Feed);
            var trains = Waterloo.Value.Trains;
            (await engine.Execute(new CancelTrain(trains[0].Id))).Applied.Should().BeTrue();
            (await engine.Execute(new ShiftTrain(trains[1].Id, 0, 300))).Applied.Should().BeTrue();
            (await engine.Execute(new CancelTrain(trains[2].Id))).Applied.Should().BeTrue();
            (await engine.Undo()).Should().BeTrue();
            messages.Skip(half).ToList().ForEach(Feed);

            var last = recording.LastSim;
            live = await Settled(engine, s => s.Clock.LastReported == last && s.History.CanRedo);
            writer.SetTimetable(Fixtures.Path("waterloo 2010.WTT"), Waterloo.Value.Header.Name, Waterloo.Value.AreaId, null);
            writer.Flush();
            path = writer.Path!;
            await cts.CancelAsync();
            await loop;
        }

        var saved = SavedSession.Load(path);
        saved.Info.AreaId.Should().Be("waterloo");
        saved.Edits.Should().Be(2);
        saved.Steps.Select(s => s.SimTime).Should().OnlyContain(t => t != null);

        var resumed = new PlanningEngine(new PlanningEngineOptions { PublishInterval = TimeSpan.Zero });
        using (var cts = new CancellationTokenSource())
        {
            var loop = resumed.RunAsync(cts.Token);
            resumed.SetTimetable(Waterloo.Value);
            foreach (var e in saved.Events())
            {
                resumed.Post(e);
            }

            await resumed.RestorePlan(saved.Steps).WaitAsync(Timeout);
            var snapshot = await Settled(resumed, s => s.Clock.LastReported == live.Clock.LastReported && s.History.CanUndo);
            await cts.CancelAsync();
            await loop;

            snapshot.Trains.Select(t => t.SimUid).Should().Equal(live.Trains.Select(t => t.SimUid));
            snapshot.Matches.Should().BeEquivalentTo(live.Matches);
            snapshot.SessionStart.Should().Be(live.SessionStart);
            snapshot.History.Done.Select(e => e.Description).Should().Equal(live.History.Done.Select(e => e.Description));
            snapshot.History.Undone.Select(e => e.Description).Should().Equal(live.History.Undone.Select(e => e.Description));
            snapshot.Plan.Modified.Select(t => t.Id).Should().Equal(live.Plan.Modified.Select(t => t.Id));
            snapshot.Trains.Sum(t => t.Actuals.Count).Should().Be(live.Trains.Sum(t => t.Actuals.Count));
        }
    }
}
