using System.IO.Compression;
using System.Text;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Core;
using Stringline.Gateway.Tests;
using Stringline.Graph;
using Stringline.Network;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>S14a: the Profile menu and the profile editor, driven through the view models on real timetables.</summary>
public sealed class ProfileEditorTests
{
    private const string Waterloo = "waterloo 2010.WTT";
    private const string Llangollen = "llangollen 2012 B.WTT";

    private static async Task<MainViewModel> Load(TestApp app, string path)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.LoadTimetableAsync(path));
        await app.Ui.WaitUntil(() => vm.Scene?.InLayer(GraphLayer.Wtt).Any() == true && vm.Snapshot.Plan.Count > 0, "the WTT on the graph");
        await app.Ui.Run(() => vm.Tick());
        return vm;
    }

    private static Task<MainViewModel> LoadWaterloo(TestApp app) => Load(app, Fixtures.Path(Waterloo));

    /// <summary>Opens the editor from the menu, does <paramref name="edit"/> in it, and returns it.</summary>
    private static async Task<ProfileEditorViewModel> Edit(TestApp app, Action<ProfileEditorViewModel> edit)
    {
        var count = app.Dialogs.ProfileEditors.Count;
        app.Dialogs.ProfileEditor = edit;
        await app.Ui.Run(() => app.ViewModel.EditProfileCommand.Execute(null));
        app.Dialogs.ProfileEditors.Should().HaveCount(count + 1);
        return app.Dialogs.ProfileEditors[^1];
    }

    /// <summary>The sum of the forecast's final delays, a measure of the knock-on the forecast predicts.</summary>
    private static long TotalDelay(PlanSnapshot snapshot) => snapshot.Forecast.Trains.Sum(t => (long)Math.Max(0, t.FinalDelaySeconds ?? 0));

    [Fact]
    public async Task Editing_the_profile_redraws_the_graph_and_keeps_the_plan()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var (main, other) = await app.Ui.Run(() => (vm.SelectedCorridor!.Corridor, vm.Corridors[1].Corridor));
        main.Id.Should().Be("main");
        var first = main.Locations[0].Tiploc;

        // A block late in the morning, so trains are held and the knock-on behind them depends on the headways.
        var block = new Disruption("B1", DisruptionKind.Block, main.Locations[0].Tiploc, main.Locations[2].Tiploc, null, SimTime.Parse("07:00"), SimTime.Parse("07:20"));
        await app.Ui.RunAsync(() => vm.ExecuteAsync(new AddDisruption(block)));
        await app.Ui.WaitUntil(() => vm.Snapshot.Plan.Disruptions.Count == 1 && vm.Snapshot.Forecast.Interaction, "the block forecast");
        var before = await app.Ui.Run(() => TotalDelay(vm.Snapshot));
        await app.Ui.Run(() => vm.SelectPath(new GraphHit(vm.Scene!.InLayer(GraphLayer.Wtt).First(), 0, 0, 0, 0)));
        var selection = await app.Ui.Run(() => vm.Selection);

        var editor = await Edit(app, e =>
        {
            e.Title.Should().Be("Profile — waterloo", "a new profile has no sim name, so the area ID names it");
            e.SelectedCorridor!.Id.Should().Be("main");

            // General: rename the corridor's first location.
            e.LocationNames.Single(r => r.Tiploc == first).Name = "London Waterloo";

            // Corridors: put the other corridor's tab first, then give every main line section a 12 minute headway.
            e.SelectedCorridor = e.Corridors.Single(c => c.Id == other.Id);
            e.MoveCorridorUpCommand.Execute(null);
            e.Corridors.Select(c => c.Id).Should().StartWith(other.Id);
            e.SelectedCorridor = e.Corridors.Single(c => c.Id == "main");
            e.Locations[0].Name.Should().Be("London Waterloo", "names are shared by both tabs");
            e.AxisMarks[0].Label.Should().Be("London Waterloo");
            foreach (var section in e.Sections)
            {
                section.Headway = "12:00";
            }

            e.Sections[0].Headway.Should().Be("12:00");
            e.Error.Should().BeNull();
            e.IsDirty.Should().BeTrue();
            e.OkCommand.Execute(null);
        });

        editor.IsDirty.Should().BeFalse();
        editor.Status.Should().StartWith("Saved to ");

        // Saved, and drawn with the change.
        var saved = new ProfileStore(app.Paths.Profiles).Load("waterloo")!;
        saved.DisplayName(first).Should().Be("London Waterloo");
        saved.Corridors[0].Id.Should().Be(other.Id);
        saved.FindCorridor("main")!.Sections.Should().OnlyContain(s => s.DefaultHeadwaySeconds == 720);
        var (tabs, tab, profile) = await app.Ui.Run(() => (vm.Corridors.Select(c => c.Id).ToList(), vm.SelectedCorridor!.Id, vm.Profile!));
        tabs.Should().StartWith(other.Id);
        tab.Should().Be("main", "the tab shown stays");
        profile.FindCorridor("main")!.Sections[0].DefaultHeadwaySeconds.Should().Be(720);
        await app.Ui.WaitUntil(() => vm.Scene!.Locations[0].Name == "London Waterloo", "the new name on the graph");

        // The forecast uses the new headways: more knock-on behind the block.
        await app.Ui.WaitUntil(() => TotalDelay(vm.Snapshot) > before, "more knock-on with the longer headways", 60);

        // The plan and the selection are kept.
        (await app.Ui.Run(() => vm.Snapshot.Plan.Disruptions.Single().Id)).Should().Be("B1");
        (await app.Ui.Run(() => vm.Selection)).Should().Be(selection);
    }

    [Fact]
    public async Task Headways_show_where_they_come_from_and_a_blank_one_goes_back_to_the_wtt()
    {
        await using var app = new TestApp();
        await LoadWaterloo(app);

        await Edit(app, e =>
        {
            var section = e.Sections[0];
            (section.Label, section.Headway, section.HeadwaySource).Should().Be(("WLOO – INTJN", "2:54", "WTT"));
            section.HeadwayTip.Should().Contain("From the WTT, being less than the global default, 3:00").And.Contain("5th percentile of 1610 pairs");
            e.DefaultHeadway.Should().BeEmpty();
            e.DefaultHeadwayHint.Should().Contain("(3:00)");

            section.Headway = "2:00";
            (section.Headway, section.HeadwaySource).Should().Be(("2:00", "Hand"));
            section.HeadwayTip.Should().StartWith("Set by hand");
            section.Headway = " ";
            (section.Headway, section.HeadwaySource).Should().Be(("2:54", "WTT"), "a blank headway goes back to the WTT's");

            e.SelectedCorridor = e.Corridors.Single(c => c.Id == "wloo-barnes");
            var putney = e.Sections[7];
            (putney.Label, putney.Headway, putney.HeadwaySource).Should().Be(("WANDS – PUTNEY", "3:00", "Default"));
            putney.HeadwayTip.Should().Contain("the WTT books trains here no closer").And.Contain("5:00 apart");

            // The profile's own default: tighter than the WTT's 2:54 at Waterloo, so it takes over there too.
            e.DefaultHeadway = "2:30";
            (putney.Headway, putney.HeadwaySource).Should().Be(("2:30", "Default"));
            (e.Sections[0].Headway, e.Sections[0].HeadwaySource).Should().Be(("2:30", "Default"));
            (e.Sections[2].Headway, e.Sections[2].HeadwaySource).Should().Be(("2:00", "WTT"));
            putney.HeadwayTip.Should().StartWith("The profile's default, 2:30");
            e.DefaultHeadway = "soon";
            e.Error.Should().Contain("default headway");
            e.DefaultHeadway.Should().Be("2:30");
            e.OkCommand.Execute(null);
        });

        new ProfileStore(app.Paths.Profiles).Load("waterloo")!.DefaultHeadwaySeconds.Should().Be(150, "saved");
    }

    [Fact]
    public async Task The_default_headway_in_options_applies_where_the_wtt_books_no_closer()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        app.Dialogs.Settings = s =>
        {
            s.DefaultHeadway.Should().Be("3:00");
            s.DefaultHeadway = "4:00";
            return true;
        };

        await app.Ui.Run(() => vm.ShowSettingsCommand.Execute(null));

        var sections = await app.Ui.Run(() => vm.Profile!.FindCorridor("wloo-barnes")!.Sections);
        sections[0].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 174 && s.HeadwaySource == HeadwaySource.Wtt);
        sections[7].Should().Match<Section>(s => s.DefaultHeadwaySeconds == 240 && s.HeadwaySource == HeadwaySource.Default, "booked 5:00 apart, so the new default");
        app.Settings.Load().Planning.DefaultHeadwaySeconds.Should().Be(240);
        new ProfileStore(app.Paths.Profiles).Load("waterloo")!.DefaultHeadwaySeconds.Should().BeNull("the global default is not saved in the profile");

        // Opened again, the editor shows the new default.
        await Edit(app, e => e.DefaultHeadwayHint.Should().Contain("(4:00)"));

        // A timetable loaded afterwards gets it too.
        await LoadWaterloo(app);
        (await app.Ui.Run(() => vm.Profile!.FindCorridor("wloo-barnes")!.Sections[7].DefaultHeadwaySeconds)).Should().Be(240);
    }

    [Fact]
    public async Task Refused_edits_say_why_and_change_nothing()
    {
        await using var app = new TestApp();
        await LoadWaterloo(app);

        await Edit(app, e =>
        {
            var draft = e.Draft;
            var section = e.Sections[0];
            section.Headway = "soon";
            e.Error.Should().Contain("is not a headway");
            section.Headway.Should().Be("2:54", "the cell shows the old value again");
            section.Tracks = "Up:Sideways";
            e.Error.Should().Contain("not a direction");
            e.Locations[0].Distance = "far";
            e.Error.Should().Contain("not a distance");
            e.Categories[0].Colour = "red";
            e.Error.Should().Contain("not a colour");
            e.Categories[0].Priority = "200";
            e.Error.Should().Contain("0 to 100");
            e.Corridors[0].Name = " ";
            e.Error.Should().Contain("needs a name");
            e.Draft.Should().BeSameAs(draft);
            e.IsDirty.Should().BeFalse();

            // A good edit clears the error; Cancel then asks before throwing it away.
            section.Layout = "Single line";
            e.Error.Should().BeNull();
            section.Tracks.Should().Be("Single:Bi");
            e.IsDirty.Should().BeTrue();
            app.Dialogs.Choices.Enqueue(1);
            e.CancelCommand.Execute(null);
            e.IsDirty.Should().BeTrue("Keep editing was chosen");
            app.Dialogs.Choices.Enqueue(0);
            e.CancelCommand.Execute(null);
        });

        app.Dialogs.Asked.Should().HaveCount(2).And.OnlyContain(q => q.Contains("Discard"));
        new ProfileStore(app.Paths.Profiles).Load("waterloo")!.FindCorridor("main")!.Sections[0].IsSingleLine.Should().BeFalse("nothing was applied");
        (await app.Ui.Run(() => app.ViewModel.Profile!.FindCorridor("main")!.Sections[0].IsSingleLine)).Should().BeFalse();
    }

    [Fact]
    public async Task Corridors_and_locations_are_added_moved_and_removed()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var main = await app.Ui.Run(() => vm.SelectedCorridor!.Corridor);
        var count = main.Locations.Count;

        await Edit(app, e =>
        {
            // Locations: remove the second, put it back, move it.
            var second = e.Locations[1].Tiploc;
            e.SelectedLocation = e.Locations[1];
            e.RemoveLocationCommand.Execute(null);
            e.Locations.Should().HaveCount(count - 1);
            e.Sections.Should().HaveCount(count - 2);
            e.SelectedLocation = e.Locations[0];
            app.Dialogs.Prompts.Enqueue(p =>
            {
                p.Fields[0].Options.Should().Contain(o => o.EndsWith($"({second.Code})", StringComparison.Ordinal) || o == second.Code);
                p.Fields[0].Value = second.Code;
                return true;
            });
            e.AddLocationCommand.Execute(null);
            e.Locations.Select(l => l.Tiploc).Should().Equal(main.Locations.Select(l => l.Tiploc));
            e.SelectedLocation!.Tiploc.Should().Be(second);
            e.MoveLocationDownCommand.Execute(null);
            e.Locations[2].Tiploc.Should().Be(second);
            e.MoveLocationUpCommand.Execute(null);
            e.Locations[1].Tiploc.Should().Be(second);

            // A distance axis.
            e.Locations[0].Distance = "0";
            e.Locations[^1].Distance = "20.5";
            e.UseDistances = true;
            e.Locations[^1].Distance.Should().Be(20.5.ToString("0.###", System.Globalization.CultureInfo.CurrentCulture));
            e.AxisMarks[^1].Position.Should().Be(1);

            // A new corridor from scratch, then the generated one it replaced is offered back once removed.
            app.Dialogs.Prompts.Enqueue(p =>
            {
                p.Fields[0].Value = "Shuttle";
                p.Fields[1].Value = main.Locations[0].Tiploc.Code;
                p.Fields[2].Value = main.Locations[1].Tiploc.Code;
                return true;
            });
            e.NewCorridorCommand.Execute(null);
            e.SelectedCorridor!.Id.Should().Be("shuttle");
            e.Locations.Should().HaveCount(2);
            e.Sections.Should().ContainSingle().Which.Layout.Should().Be("Double line");
            e.SelectedLocation = e.Locations[0];
            e.RemoveLocationCommand.Execute(null);
            e.Error.Should().Contain("at least 2 locations");

            var other = e.Corridors.First(c => c.Id is not "main" and not "shuttle");
            e.SelectedCorridor = other;
            e.RemoveCorridorCommand.Execute(null);
            e.Corridors.Select(c => c.Id).Should().NotContain(other.Id);
            e.Candidates.Select(c => c.Corridor.Id).Should().Contain(other.Id, "a removed generated corridor can be added again");
            e.ApplyCommand.Execute(null);
            e.Error.Should().BeNull();
        });

        var (tabs, profile) = await app.Ui.Run(() => (vm.Corridors.Select(c => c.Id).ToList(), vm.Profile!));
        tabs.Should().Equal(profile.Corridors.Select(c => c.Id));
        tabs.Should().Contain("shuttle").And.Contain("main");
        profile.FindCorridor("main")!.Axis.Should().Be(AxisMode.Distance);
        await app.Ui.Run(() => vm.SelectedCorridor = vm.Corridors.Single(c => c.Id == "shuttle"));
        await app.Ui.WaitUntil(() => vm.Scene?.CorridorId == "shuttle" && vm.Scene.Locations.Count == 2, "the new corridor drawn");
    }

    [Fact]
    public async Task Removing_the_corridor_shown_closes_its_tab()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);

        await Edit(app, e =>
        {
            e.SelectedCorridor = e.Corridors.Single(c => c.Id == "main");
            e.RemoveCorridorCommand.Execute(null);
            e.OkCommand.Execute(null);
        });

        var (tabs, selected) = await app.Ui.Run(() => (vm.Corridors.Select(c => c.Id).ToList(), vm.SelectedCorridor?.Id));
        tabs.Should().NotContain("main");
        selected.Should().Be(tabs[0]);
        await app.Ui.WaitUntil(() => vm.Scene?.CorridorId == tabs[0], "the next corridor drawn");
    }

    [Fact]
    public async Task Category_styles_and_names_are_edited_and_imported()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var csv = Path.Combine(app.Paths.Profiles, "..", "names.csv");
        var first = await app.Ui.Run(() => vm.SelectedCorridor!.Corridor.Locations[0].Tiploc);
        await File.WriteAllTextAsync(csv, $"TIPLOC,Name\n{first},\"Waterloo, Main\"\nNOWHERE,Nowhere\nbad row\n");
        string? category = null;

        await Edit(app, e =>
        {
            app.Dialogs.Files.Enqueue(csv);
            e.ImportNamesCommand.Execute(null);
            e.Status.Should().Contain("Imported 2 names").And.Contain("1 are for locations not in this timetable (NOWHERE)").And.Contain("1 rows were skipped");
            e.LocationNames.Single(r => r.Tiploc == first).Name.Should().Be("Waterloo, Main");

            var row = e.Categories.First(c => c.Trains > 0);
            category = row.Id;
            row.Style = LineStyle.Dashed;
            row.Colour = "#123456";
            row.Priority = "99";
            row.Name = "Renamed";
            e.SimName = "Waterloo (edited)";
            e.Title.Should().Be("Profile — Waterloo (edited)");
            e.OkCommand.Execute(null);
        });

        app.Dialogs.Lists.Should().ContainSingle().Which.Lines.Should().ContainSingle().Which.Should().Contain("expected TIPLOC,name");
        var style = await app.Ui.Run(() => vm.Profile!.StyleFor(category));
        style.Should().Be(new CategoryStyle(category!, "Renamed", 99, Colour.Parse("#123456"), LineStyle.Dashed));
        (await app.Ui.Run(() => vm.Title)).Should().Be("Stringline — Waterloo (edited) – SX May 2010 (Updated - Loader) v1.0.1 (v1.0.2)");
        await app.Ui.WaitUntil(() => vm.Scene!.Locations[0].Name == "Waterloo, Main" && vm.Scene.Paths.Any(p => p.Style == LineStyle.Dashed), "the dashed category drawn");
        vm.Scene!.Paths.Where(p => p.Style == LineStyle.Dashed).Should().OnlyContain(p => p.Colour == Colour.Parse("#123456") && p.Layer != GraphLayer.Forecast);
    }

    [Fact]
    public async Task Reload_rereads_the_file_and_open_shows_it()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var store = new ProfileStore(app.Paths.Profiles);
        store.Save(store.Load("waterloo")!.RenameCorridor("main", "Main (by hand)"));

        await app.Ui.Run(() => vm.ReloadProfileCommand.Execute(null));

        (await app.Ui.Run(() => vm.Corridors.Single(c => c.Id == "main").Name)).Should().Be("Main (by hand)");
        (await app.Ui.Run(() => vm.InfoMessage)).Should().StartWith("Reloaded the profile");
        await app.Ui.Run(() => vm.OpenProfileFileCommand.Execute(null));
        app.Dialogs.Opened.Should().Equal(store.PathFor("waterloo"));

        await File.WriteAllTextAsync(store.PathFor("waterloo"), "{ damaged");
        await app.Ui.Run(() => vm.ReloadProfileCommand.Execute(null));
        var (severity, message, name) = await app.Ui.Run(() => (vm.InfoSeverity, vm.InfoMessage, vm.Corridors.Single(c => c.Id == "main").Name));
        severity.Should().Be(InfoSeverity.Error);
        message.Should().StartWith("Cannot reload the profile");
        name.Should().Be("Main (by hand)", "the profile in use stays");
    }

    [Fact]
    public async Task Regenerating_asks_before_replacing_hand_tuned_corridors()
    {
        await using var app = new TestApp();
        var vm = await LoadWaterloo(app);
        var regenerate = (Func<Task>)(() => app.Ui.RunAsync(() => vm.RegenerateCorridorsCommand.ExecuteAsync(null)));

        // Nothing hand-tuned: no question.
        await regenerate();
        app.Dialogs.Asked.Should().BeEmpty();

        await Edit(app, e =>
        {
            e.SelectedCorridor = e.Corridors.Single(c => c.Id == "main");
            e.Sections[0].Headway = "5:00";
            e.Corridors[0].Name = "Main line";
            e.OkCommand.Execute(null);
        });
        int Headway() => vm.Profile!.FindCorridor("main")!.Sections[0].DefaultHeadwaySeconds;

        app.Dialogs.Choices.Enqueue(2);
        await regenerate();
        app.Dialogs.Asked.Should().ContainSingle().Which.Should().Contain("Main line");
        (await app.Ui.Run(Headway)).Should().Be(300, "cancelled");

        app.Dialogs.Choices.Enqueue(1);
        await regenerate();
        (await app.Ui.Run(Headway)).Should().Be(300, "kept");
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Contain("keeping 1 of yours");

        app.Dialogs.Choices.Enqueue(0);
        await regenerate();
        (await app.Ui.Run(Headway)).Should().Be(174, "replaced by the WTT's headway");
        (await app.Ui.Run(() => vm.Profile!.FindCorridor("main")!.Sections[0].HeadwaySource)).Should().Be(HeadwaySource.Wtt);
        (await app.Ui.Run(() => vm.Corridors.Single(c => c.Id == "main").Name)).Should().NotBe("Main line");
        new ProfileStore(app.Paths.Profiles).Load("waterloo")!.FindCorridor("main")!.Sections[0].DefaultHeadwaySeconds.Should().Be(174, "saved");
    }

    [Fact]
    public async Task A_timetable_without_a_sim_id_is_named_and_found_again()
    {
        await using var app = new TestApp();
        var wtt = WithoutSimId(app);
        var vm = await Load(app, wtt);
        (await app.Ui.Run(() => (vm.Profile!.AreaId, vm.LoadedTimetable!.NeedsSimId, vm.InfoMessage))).Should().Match<(string, bool, string?)>(
            x => x.Item1 == TimetableLoader.UnnamedSim && x.Item2 && x.Item3!.Contains("name the sim"));
        await app.Ui.Run(() => vm.ReloadProfileCommand.Execute(null));
        (await app.Ui.Run(() => vm.InfoMessage)).Should().Contain("not saved yet");

        // OK without a name is refused; with one it saves under that name and remembers it for this file.
        await Edit(app, e =>
        {
            e.NeedsSimId.Should().BeTrue();
            e.SimId.Should().BeEmpty();
            e.OkCommand.Execute(null);
            e.Error.Should().Contain("Name the sim");
            e.SimId = TimetableLoader.UnnamedSim;
            e.OkCommand.Execute(null);
            e.Error.Should().Contain("other than");
            e.Corridors[0].Name = "Railway";
            e.SimId = "My Llangollen";
            e.OkCommand.Execute(null);
            e.Error.Should().BeNull();
            e.NeedsSimId.Should().BeFalse();
        });

        var store = new ProfileStore(app.Paths.Profiles);
        store.Load("My Llangollen")!.Corridors[0].Name.Should().Be("Railway");
        (await app.Ui.Run(() => (vm.Profile!.AreaId, vm.LoadedTimetable!.NeedsSimId))).Should().Be(("My Llangollen", false));
        app.Settings.Load().SimIdFor(wtt).Should().Be("My Llangollen");

        // Loaded again (in a new run of the app): the named profile is used.
        await using var again = new TestApp(_ => app.Settings.Load());
        Directory.CreateDirectory(again.Paths.Profiles);
        File.Copy(store.PathFor("My Llangollen"), new ProfileStore(again.Paths.Profiles).PathFor("My Llangollen"));
        var reloaded = await Load(again, wtt);
        (await again.Ui.Run(() => (reloaded.Profile!.AreaId, reloaded.LoadedTimetable!.NeedsSimId, reloaded.Corridors[0].Name))).Should().Be(("My Llangollen", false, "Railway"));
    }

    [Fact]
    public async Task Naming_a_sim_whose_profile_exists_asks_which_to_use()
    {
        await using var app = new TestApp();
        var store = new ProfileStore(app.Paths.Profiles);
        var vm = await Load(app, Fixtures.Path(Llangollen));
        store.Save(store.Load("llangollen")!.RenameCorridor(vm.Corridors[0].Id, "Saved one"));
        var wtt = WithoutSimId(app);
        await Load(app, wtt);

        await Edit(app, e =>
        {
            e.SimId = "llangollen";
            app.Dialogs.Choices.Enqueue(2);
            e.ApplyCommand.Execute(null);
            e.NeedsSimId.Should().BeTrue("cancelled");
            app.Dialogs.Choices.Enqueue(0);
            e.ApplyCommand.Execute(null);
            e.NeedsSimId.Should().BeFalse();
            e.Corridors[0].Name.Should().Be("Saved one", "the editor now shows the saved profile");
        });

        app.Dialogs.Asked.Should().HaveCount(2).And.OnlyContain(q => q.Contains("saved already"));
        (await app.Ui.Run(() => vm.Corridors[0].Name)).Should().Be("Saved one");
    }

    /// <summary>A copy of the Llangollen timetable with its sim ID removed.</summary>
    private static string WithoutSimId(TestApp app)
    {
        var path = Path.Combine(Path.GetDirectoryName(app.Paths.Profiles)!, "no-id.wtt");
        using var source = ZipFile.OpenRead(Fixtures.Path(Llangollen));
        using var target = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (entry.Name == "SavedTimetable.xml")
            {
                text = text.Replace("ID=\"llangollen\"", "ID=\"\"", StringComparison.Ordinal);
            }

            using var writer = new StreamWriter(target.CreateEntry(entry.FullName).Open(), new UTF8Encoding(false));
            writer.Write(text);
        }

        return path;
    }
}

public sealed class ProfileEditorTextTests
{
    [Theory]
    [InlineData("3:00", 180)]
    [InlineData("0:45", 45)]
    [InlineData("12:30", 750)]
    [InlineData("3", 180)]
    [InlineData("2.5", 150)]
    [InlineData("90s", 90)]
    [InlineData(" 0 ", 0)]
    public void Reads_durations(string text, int seconds)
    {
        ProfileEditorText.ParseDuration(text, "headway").Should().Be(seconds);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("3:5")]
    [InlineData("3:60")]
    [InlineData("soon")]
    public void Refuses_other_durations(string text)
    {
        var act = () => ProfileEditorText.ParseDuration(text, "headway");
        act.Should().Throw<ProfileEditException>().WithMessage($"*is not a headway*");
    }

    [Fact]
    public void Formats_durations_and_reads_distances_and_counts()
    {
        ProfileEditorText.FormatDuration(185).Should().Be("3:05");
        ProfileEditorText.ParseKm(" ").Should().BeNull();
        ProfileEditorText.ParseKm("12.5 km").Should().Be(12.5);
        ProfileEditorText.ParseCount("4", "platforms").Should().Be(4);
        var act = () => ProfileEditorText.ParseCount("four", "platforms");
        act.Should().Throw<ProfileEditException>().WithMessage("The platforms must be a whole number.");
    }
}

/// <summary>The editor window itself: its XAML loads and lays out with a real profile, so a markup mistake fails here rather than in the app.</summary>
public sealed class ProfileEditorWindowTests
{
    [Fact]
    public void The_window_loads_and_lays_out_every_tab()
    {
        var timetable = new Timetable.WttReader().Read(Fixtures.Path("waterloo 2010.WTT")).Timetable;
        var profile = StringlineProfile.Create(timetable);
        Exception? failure = null;
        var bindingErrors = new List<string>();
        var thread = new Thread(() =>
        {
            try
            {
                using var listener = new BindingErrorListener(bindingErrors);
                var vm = new ProfileEditorViewModel(profile, timetable, new ScriptedDialogs(), p => new ProfileApplyResult(p, null));
                var window = new Views.ProfileEditorWindow(vm) { ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
                window.Show();
                var tabs = FindChild<System.Windows.Controls.TabControl>(window)!;
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    window.UpdateLayout();
                    System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                }

                vm.SelectedCorridor = vm.Corridors[^1];
                window.UpdateLayout();
                window.Close();
            }
#pragma warning disable CA1031 // Reported on the test thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(60)).Should().BeTrue();

        failure.Should().BeNull();
        bindingErrors.Should().BeEmpty();
    }

    private static T? FindChild<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found || FindChild<T>(child) is { } deeper && (found = deeper) is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Collects WPF binding errors (a mistyped path, say) while it is alive.</summary>
    internal sealed class BindingErrorListener : System.Diagnostics.TraceListener
    {
        private readonly List<string> _errors;

        public BindingErrorListener(List<string> errors)
        {
            _errors = errors;
            System.Diagnostics.PresentationTraceSources.Refresh();
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(this);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
            {
                _errors.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            base.Dispose(disposing);
        }
    }
}
