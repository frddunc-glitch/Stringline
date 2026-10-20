using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway;
using Stringline.Gateway.Tests;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>
/// Trains SimSig will not run (S34, #16): the train list's "Not running" group, the detail, Run anyway and its undo,
/// on a small timetable with seed groups at 04:00 and 14:00, started live at 14:00.
/// </summary>
public sealed class NotRunningTests
{
    private const string Timetable = """
        <SimSigTimetable ID="testsim" Version="5.18">
          <Name>Seed test</Name>
          <StartTime>0</StartTime>
          <FinishTime>86400</FinishTime>
          <SeedGroups>
            <SeedGroup><ID>{G1}</ID><StartTime>14400</StartTime><Title>04:00</Title></SeedGroup>
            <SeedGroup><ID>{G2}</ID><StartTime>50400</StartTime><Title>14:00</Title></SeedGroup>
          </SeedGroups>
          <Timetables>
            <Timetable><ID>1A01</ID><SeedPoint>S1</SeedPoint><SeedGroup>{G2}</SeedGroup>
              <Trips><Trip><Location>AAA</Location><DepPassTime>50460</DepPassTime></Trip><Trip><Location>BBB</Location><ArrTime>51000</ArrTime></Trip></Trips></Timetable>
            <Timetable><ID>1A02</ID><SeedPoint>S2</SeedPoint><SeedGroup>{G1}</SeedGroup>
              <Trips><Trip><Location>AAA</Location><DepPassTime>50500</DepPassTime></Trip><Trip><Location>BBB</Location><ArrTime>51100</ArrTime></Trip></Trips></Timetable>
            <Timetable><ID>2B01</ID><EntryPoint>EAAA</EntryPoint><DepartTime>50300</DepartTime>
              <Trips><Trip><Location>AAA</Location><DepPassTime>50520</DepPassTime></Trip><Trip><Location>BBB</Location><ArrTime>51200</ArrTime></Trip></Trips></Timetable>
            <Timetable><ID>2B02</ID><EntryPoint>EAAA</EntryPoint><DepartTime>51000</DepartTime>
              <Trips><Trip><Location>AAA</Location><DepPassTime>51100</DepPassTime></Trip><Trip><Location>BBB</Location><ArrTime>51700</ArrTime></Trip></Trips></Timetable>
          </Timetables>
        </SimSigTimetable>
        """;

    [Fact]
    public async Task The_train_list_groups_trains_that_will_not_run_and_run_anyway_brings_one_back()
    {
        var wtt = Path.Combine(Path.GetTempPath(), $"seed-test-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(wtt, Timetable);
        try
        {
            await using var server = FakeStompServer.Start();
            await using var app = new TestApp();
            var vm = app.ViewModel;
            app.Dialogs.Connect = d =>
            {
                d.Host = "127.0.0.1";
                d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                d.TimetablePath = wtt;
                return true;
            };

            await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
            await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
            await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"testsim","clock":50400,"interval":500}}""");
            await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"1A01","uid":"7","action":"depart","location":"AAA","platform":"","time":50460}}""");
            await app.Ui.WaitUntil(() => vm.Trains.Rows.Count(r => !r.IsLive) == 2 && vm.Trains.Rows.Any(r => r.IsLive), "the live train and two not running");

            await app.Ui.Run(() =>
            {
                vm.Trains.Rows[0].Headcode.Should().Be("1A01");
                vm.Trains.Rows[0].Group.Should().BeEmpty("live trains have no group header");
                vm.Trains.Rows.Skip(1).Select(r => (r.Headcode, r.Group, r.StatusText, r.LastLocation)).Should().Equal(
                    ("2B01", TrainRowViewModel.NotRunningGroup, "Not running", "not seeded"),
                    ("1A02", TrainRowViewModel.NotRunningGroup, "Not running", "seed group 04:00"));
                vm.Trains.Summary.Should().Be("1 trains, 2 not running");
                vm.SideTab = MainViewModel.DelaysTab;
                vm.Delays.Summary.Should().Contain("2 not running");

                vm.Trains.UnmatchedOnly = true;
                vm.Trains.Rows.Should().NotContain(r => !r.IsLive);
                vm.Trains.UnmatchedOnly = false;

                // Selecting one shows why, and offers Run anyway.
                vm.Trains.Selected = vm.Trains.Rows.Single(r => r.Headcode == "1A02");
                vm.Selection.Should().Be(new Graph.GraphSelection(new TrainId("1A02"), null));
                vm.Detail.IsNotRunning.Should().BeTrue();
                vm.Detail.StatusText.Should().StartWith("Not running: seed group 04:00");
                vm.RunAnywaySelectedText.Should().Be("Run anyway");
                vm.RunAnywaySelectedCommand.CanExecute(null).Should().BeTrue();
            });

            await app.Ui.RunAsync(() => vm.RunAnywaySelectedCommand.ExecuteAsync(null));
            await app.Ui.WaitUntil(() => vm.Trains.Rows.Count(r => !r.IsLive) == 1, "1A02 runs anyway");
            await app.Ui.Run(() =>
            {
                vm.Snapshot.Forecast.Find(new TrainId("1A02"))!.Status.Should().Be(TrainForecastStatus.NotStarted);
                vm.Snapshot.History.UndoDescription.Should().Be("Run 1A02 anyway");
                vm.Detail.RunsAnyway.Should().BeTrue();
                vm.Detail.StatusText.Should().Contain("run anyway");
                vm.RunAnywaySelectedText.Should().Be("Leave to start rules");
            });

            await app.Ui.RunAsync(() => vm.UndoCommand.ExecuteAsync(null));
            await app.Ui.WaitUntil(() => vm.Trains.Rows.Count(r => !r.IsLive) == 2, "the undo puts it back");
        }
        finally
        {
            File.Delete(wtt);
        }
    }
}
