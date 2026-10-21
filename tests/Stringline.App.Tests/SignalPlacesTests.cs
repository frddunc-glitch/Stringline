using Stringline.App.ViewModels;
using Stringline.Gateway.Tests;
using Stringline.Network.Profiles;

namespace Stringline.App.Tests;

/// <summary>S37 in the shell: a replay is learned from into the profile, shown on the Signals tab, and shared.</summary>
public sealed class SignalPlacesTests : IDisposable
{
    private const string Waterloo = "waterloo 2010.WTT";

    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("stringline-places-app-");

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Cleaned up eventually.
        }
    }

    /// <summary>Replays Waterloo 1× (with TD) flat out to its end; the finished replay is learned from.</summary>
    private static async Task<MainViewModel> ReplayToEnd(TestApp app, string recording = Fixtures.Waterloo1x)
    {
        var vm = app.ViewModel;
        await app.Ui.RunAsync(() => vm.OpenReplayAsync(Fixtures.Path(recording), Fixtures.Path(Waterloo), double.PositiveInfinity, null));
        await app.Ui.WaitUntil(
            () =>
            {
                vm.Tick();
                return vm.Transport?.IsFinished == true;
            },
            "the replay played to its end");
        return vm;
    }

    private static SignallingPlaces Places(MainViewModel vm) => vm.LoadedTimetable!.Profile.Places;

    private static ProfileEditorViewModel Edit(TestApp app, Action<ProfileEditorViewModel> edit)
    {
        app.Dialogs.ProfileEditor = edit;
        app.ViewModel.EditProfileCommand.Execute(null);
        return app.Dialogs.ProfileEditors[^1];
    }

    [Fact]
    public async Task A_replay_is_learned_once_and_shown_on_the_signals_tab()
    {
        await using var app = new TestApp(root: _root.FullName);
        var vm = await ReplayToEnd(app);
        await app.Ui.WaitUntil(() => Places(vm).Signals.Count > 0, "the learned places", seconds: 30);

        var (signals, tracks, berths, sessions) = await app.Ui.Run(() => (Places(vm).Signals.Count, Places(vm).TrackCircuits.Count, Places(vm).Berths.Count, Places(vm).Sessions));
        signals.Should().BeGreaterThan(50);
        tracks.Should().BeGreaterThan(50, "the 1× recording has the TD topic");
        berths.Should().BeGreaterThan(50);
        sessions.Should().ContainSingle().Which.Id.Should().StartWith("recording ");
        await app.Ui.Run(() => vm.LearningSessionId.Should().Be(sessions[0].Id));
        var saved = ProfileJson.Deserialize(await File.ReadAllTextAsync(Path.Combine(app.Paths.Profiles, "waterloo.json")));
        saved.Places.Signals.Should().HaveCount(signals, "the profile was saved");

        // Stopping the finished replay learns again, but nothing is new.
        var samples = await app.Ui.Run(() => Places(vm).Signals.Sum(p => p.SampleCount));
        await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
        (await app.Ui.Run(() => Places(vm).Signals.Sum(p => p.SampleCount))).Should().Be(samples);

        // The Signals tab: the sections with places, a section's places in order, and a place set by hand.
        await app.Ui.Run(() =>
        {
            var editor = Edit(app, e =>
            {
                e.PlacesSummary.Should().StartWith("Signals: ").And.Contain("used").And.Contain("Learned from 1 session");
                var section = e.PlaceSections.First(c => c.Text.Contains('('));
                e.SelectedPlaceSection = section;
                e.Places.Should().NotBeEmpty();
                var row = e.Places.First(r => r.Kind == SignallingKind.Signal);
                row.State.Should().BeOneOf("Used", "Learning");
                row.Fraction = "0.5";
                e.Error.Should().BeNull();
                row.State.Should().Be("Hand-set");
                row.Fraction.Should().Be(0.5.ToString("0.000", System.Globalization.CultureInfo.CurrentCulture));
                row.Fraction = "1.5";
                e.Error.Should().Contain("not a place");
                e.ApplyCommand.Execute(null);
            });
            editor.IsDirty.Should().BeFalse();
        });
        await app.Ui.WaitUntil(() => Places(vm).Signals.Any(p => p.HandSet), "the hand-set place in use");
    }

    [Fact]
    public async Task Learned_blocks_give_headways_in_the_editor_and_aspects_can_be_set_by_hand()
    {
        await using var app = new TestApp(root: _root.FullName);
        var vm = await ReplayToEnd(app);
        await app.Ui.WaitUntil(() => vm.LoadedTimetable!.Profile.Corridors.SelectMany(c => c.Sections).Any(s => s.HeadwaySource == Network.HeadwaySource.Blocks), "a block headway learned", seconds: 30);

        await app.Ui.Run(() => Edit(app, e =>
        {
            var section = e.Sections.First(s => s.HeadwaySource == "Blocks");
            section.HeadwayTip.Should().StartWith("From the signal blocks learned from play").And.Contain("-aspect");
            section.Aspects.Should().BeEmpty("learned, not set by hand");
            section.AspectsTip.Should().StartWith("Learned: ");
            var learned = section.Headway;

            section.Aspects = "3";
            e.Error.Should().BeNull();
            section.Aspects.Should().Be("3");
            section.AspectsTip.Should().StartWith("Set by hand: 3-aspect");
            section.HeadwaySource.Should().Be("Blocks");
            section.Aspects = "";
            section.Headway.Should().Be(learned, "back to what play showed");
            section.Aspects = "7";
            e.Error.Should().Contain("Aspects must be 2, 3 or 4");
            e.CancelCommand.Execute(null);
        }));
    }

    [Fact]
    public async Task Reviewing_a_session_already_learned_from_learns_nothing_new()
    {
        string file;
        int samples;
        await using (var first = new TestApp(root: _root.FullName))
        {
            var vm = await ReplayToEnd(first);
            await first.Ui.WaitUntil(() => vm.Snapshot.Timetable is not null, "the plan's snapshot");
            var train = await first.Ui.Run(() => vm.Snapshot.Timetable!.Trains[0].Id);
            (await (await first.Ui.Run(() => vm.ExecuteAsync(new Planning.CancelTrain(train))))).Applied.Should().BeTrue();
            await first.Ui.WaitUntil(() => vm.SessionPath is not null && Places(vm).Signals.Count > 0, "the session file and the learned places", seconds: 30);
            file = await first.Ui.Run(() => vm.SessionPath!);
            await first.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
            samples = await first.Ui.Run(() => Places(vm).All.Sum(p => p.Place.SampleCount));
        }

        await using var second = new TestApp(root: _root.FullName);
        var app = second.ViewModel;
        await second.Ui.RunAsync(() => app.ReviewSessionAsync(file));
        await second.Ui.WaitUntil(() => app.Transport is not null && app.IsReviewing, "the review loaded");
        await second.Ui.Run(() => app.Transport!.PlayPauseCommand.Execute(null));
        await second.Ui.WaitUntil(
            () =>
            {
                app.Tick();
                return app.Transport?.IsFinished == true;
            },
            "the review played to its end");
        await second.Ui.RunAsync(() => app.DisconnectCommand.ExecuteAsync(null));

        await second.Ui.Run(() =>
        {
            Places(app).Sessions.Should().ContainSingle("the review is the same recording");
            Places(app).All.Sum(p => p.Place.SampleCount).Should().Be(samples);
        });
    }

    [Fact]
    public async Task The_signals_tab_adds_deletes_and_resets()
    {
        await using var app = new TestApp(root: _root.FullName);
        var vm = await ReplayToEnd(app);
        await app.Ui.WaitUntil(() => Places(vm).Signals.Count > 0, "the learned places", seconds: 30);

        await app.Ui.Run(() =>
        {
            Edit(app, e =>
            {
                var section = e.PlaceSections.First(c => c.Text.Contains('('));
                e.SelectedPlaceSection = section;
                var before = e.Places.Count;

                app.Dialogs.Prompts.Enqueue(p =>
                {
                    p.Fields[1].Value = "S9999";
                    p.Fields[4].Value = "40%";
                    return true;
                });
                e.AddPlaceCommand.Execute(null);
                e.Places.Should().HaveCount(before + 1);
                e.SelectedPlace!.Should().Match<SignallingPlaceRow>(r => r.Id == "S9999" && r.State == "Hand-set" && r.Place.Fraction == 0.4);

                e.DeletePlaceCommand.Execute(null);
                e.Places.Should().HaveCount(before);

                e.SelectedPlace = e.Places[0];
                e.SelectedPlace.Fraction = "0.25";
                app.Dialogs.Choices.Enqueue(0);
                e.ResetSectionPlacesCommand.Execute(null);
                e.Places.Should().ContainSingle("only the hand-set place stays").Which.State.Should().Be("Hand-set");

                app.Dialogs.Choices.Enqueue(0);
                e.ResetAllPlacesCommand.Execute(null);
                e.Draft.Places.All.Should().ContainSingle();
                e.Draft.Places.Sessions.Should().NotBeEmpty("the replay is still known, so it is not learned again");
                e.OkCommand.Execute(null);
            });
        });
        await app.Ui.WaitUntil(() => Places(vm).All.Count() == 1, "the reset profile in use");
    }

    [Fact]
    public async Task Nothing_is_learned_when_learning_is_off()
    {
        await using var app = new TestApp(settings: s => s with { LearnSignalPlaces = false }, root: _root.FullName);
        var vm = await ReplayToEnd(app);
        await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));

        await app.Ui.Run(() =>
        {
            vm.LearningSessionId.Should().BeNull();
            Places(vm).IsEmpty.Should().BeTrue();
        });
    }

    [Fact]
    public async Task A_profile_is_exported_and_imported_by_merging_or_replacing()
    {
        string exported;
        int signals;
        await using (var first = new TestApp(root: Path.Combine(_root.FullName, "first")))
        {
            var vm = await ReplayToEnd(first);
            await first.Ui.WaitUntil(() => Places(vm).Signals.Count > 0, "the learned places", seconds: 30);
            exported = Path.Combine(_root.FullName, "shared.json");
            first.Dialogs.SaveFiles.Enqueue(exported);
            await first.Ui.RunAsync(() => vm.ExportProfileCommand.ExecuteAsync(null));
            signals = await first.Ui.Run(() => Places(vm).Signals.Count);
            first.Dialogs.SaveSuggestions.Should().ContainSingle().Which.Should().EndWith("waterloo-profile.json");
        }

        ProfileJson.Deserialize(await File.ReadAllTextAsync(exported)).Places.Signals.Should().HaveCount(signals);

        await using var second = new TestApp(root: Path.Combine(_root.FullName, "second"));
        var app = second.ViewModel;
        await second.Ui.RunAsync(() => app.LoadTimetableAsync(Fixtures.Path(Waterloo)));

        // Merge: keeps this profile, takes the places.
        await second.Ui.Run(() => Edit(second, e =>
        {
            e.SelectedCorridor!.Name = "My main";
            e.ApplyCommand.Execute(null);
        }));
        second.Dialogs.Files.Enqueue(exported);
        second.Dialogs.Choices.Enqueue(0);
        await second.Ui.RunAsync(() => app.ImportProfileCommand.ExecuteAsync(null));
        await second.Ui.Run(() =>
        {
            Places(app).Signals.Should().HaveCount(signals);
            app.LoadedTimetable!.Profile.Corridors[0].Name.Should().Be("My main");
            app.InfoMessage.Should().StartWith("Merged the learned places of shared.json");
        });

        // Merging again pools the samples; replacing takes the shared profile whole.
        var once = await second.Ui.Run(() => Places(app).Signals.Sum(p => p.SampleCount));
        second.Dialogs.Files.Enqueue(exported);
        second.Dialogs.Choices.Enqueue(0);
        await second.Ui.RunAsync(() => app.ImportProfileCommand.ExecuteAsync(null));
        (await second.Ui.Run(() => Places(app).Signals.Sum(p => p.SampleCount))).Should().Be(2 * once);

        second.Dialogs.Files.Enqueue(exported);
        second.Dialogs.Choices.Enqueue(1);
        await second.Ui.RunAsync(() => app.ImportProfileCommand.ExecuteAsync(null));
        await second.Ui.Run(() =>
        {
            app.LoadedTimetable!.Profile.Corridors[0].Name.Should().NotBe("My main");
            Places(app).Signals.Sum(p => p.SampleCount).Should().Be(once);
        });

        // Another sim's profile is refused.
        var other = Path.Combine(_root.FullName, "other.json");
        await File.WriteAllTextAsync(other, ProfileJson.Serialize(new StringlineProfile { AreaId = "penrith" }));
        second.Dialogs.Files.Enqueue(other);
        await second.Ui.RunAsync(() => app.ImportProfileCommand.ExecuteAsync(null));
        await second.Ui.Run(() => app.InfoMessage.Should().Contain("is the profile for 'penrith'"));
    }

    [Fact]
    public async Task Learned_signals_show_on_the_axis_and_bend_the_actual_lines()
    {
        await using var app = new TestApp(root: _root.FullName);
        var vm = await ReplayToEnd(app);
        await app.Ui.WaitUntil(() => Places(vm).Signals.Count > 0, "the learned places", seconds: 30);

        // Learning hands the places to the forecast and the graph (S38): ticks by default.
        await app.Ui.WaitUntil(() => vm.Scene?.Signals.Count > 0 && vm.Scene.InLayer(Graph.GraphLayer.Actual).Any(p => p.Signals != null), "signal ticks on the axis, and actual lines bent at signals", seconds: 30);
        await app.Ui.Run(() =>
        {
            vm.SignalDisplay.Should().Be(Graph.SignalDisplay.Ticks);
            vm.IsSignalTicks.Should().BeTrue();
            vm.Scene!.SignalDisplay.Should().Be(Graph.SignalDisplay.Ticks);
            vm.SetSignalDisplayCommand.Execute("Rows");
        });
        await app.Ui.WaitUntil(() => vm.Scene?.SignalDisplay == Graph.SignalDisplay.Rows, "signal rows", seconds: 30);
        await app.Ui.Run(() => vm.SetSignalDisplayCommand.Execute("Off"));
        await app.Ui.WaitUntil(() => vm.Scene?.Signals.Count == 0, "no signals", seconds: 30);
        app.Settings.Load().SignalDisplay.Should().Be(Graph.SignalDisplay.Off, "remembered");
    }
}
