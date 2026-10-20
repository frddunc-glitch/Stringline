using System.Text;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Extensions.Logging;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway;
using Stringline.Gateway.Stomp;
using Stringline.Gateway.Tests;

namespace Stringline.App.Tests;

/// <summary>
/// The privacy policy (S30, #14): the SimSig password goes only into the STOMP CONNECT frame, never into a log, session,
/// recording or the settings in clear; Forget saved password removes it at once; and the policy is in the guide and
/// linked from Help, About, the Connect dialog and the Options window.
/// </summary>
public sealed class PrivacyTests
{
    /// <summary>A password nothing else would contain, so finding it anywhere means it leaked.</summary>
    private const string Password = "Pr1vacy-Test-9f3c";

    private static readonly string Root = Path.GetFullPath(Fixtures.Path(Path.Combine("..", "..")));

    private static string SavedPassword(string password) => new DpapiSecretProtector().Protect(password);

    [Fact]
    public async Task The_password_goes_only_into_the_connect_frame()
    {
        var root = Directory.CreateTempSubdirectory("stringline-privacy-");
        try
        {
            await using var server = FakeStompServer.Start();
            var paths = AppPaths.Under(root.FullName);
            var logging = new AppLogging(paths.Logs, detailed: true);
            using (var loggerFactory = new SerilogLoggerFactory(logging.CreateLogger(retentionDays: 1), dispose: true))
            {
                await using var app = new TestApp(root: root.FullName, logging: logging, loggerFactory: loggerFactory);
                var vm = app.ViewModel;
                app.Dialogs.Connect = d =>
                {
                    d.Host = "127.0.0.1";
                    d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    d.UserName = "fred";
                    d.Password = Password;
                    d.RememberPassword = true;
                    d.TimetablePath = string.Empty;
                    return true;
                };

                await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
                var connect = await server.WaitForFrameAsync(StompCommands.Connect);
                connect.GetHeader("passcode").Should().Be(Password, "the CONNECT frame is the one place the password goes");
                await server.WaitForSubscribersAsync(GatewayTopics.SimSig);
                await app.Ui.WaitUntil(() => vm.ConnectionLight == StatusLight.Good, "connected");

                await server.PublishAsync(GatewayTopics.SimSig, """{"clock_msg":{"area_id":"waterloo","clock":21600,"interval":500}}""");
                await app.Ui.WaitUntil(() => vm.Snapshot.AreaId == "waterloo", "the sim's clock");
                await app.Ui.Run(() => vm.ToggleRecordingCommand.Execute(null));
                await app.Ui.WaitUntil(() => vm.IsRecording, "recording");
                await server.PublishAsync(GatewayTopics.TrainMovements, """{"train_location":{"headcode":"2O03","uid":"7","action":"depart","location":"WLOO","platform":"5","time":21630}}""");
                await app.Ui.WaitUntil(() => vm.Snapshot.Trains.Count == 1, "the live train");
                await app.Ui.Run(() => vm.ToggleRecordingCommand.Execute(null));
                await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
                app.Settings.PasswordOf(app.Settings.Load()).Should().Be(Password, "it was remembered");
            }

            // The app and its log are closed: everything it wrote is on disk.
            var files = Directory.GetFiles(root.FullName, "*", SearchOption.AllDirectories);
            files.Should().Contain(f => f.EndsWith(".log", StringComparison.Ordinal), "the log")
                .And.Contain(f => f.EndsWith(".sslog", StringComparison.Ordinal), "the recording")
                .And.Contain(f => f.EndsWith(".stringline", StringComparison.Ordinal), "the session")
                .And.Contain(paths.Settings);
            foreach (var file in files)
            {
                var bytes = await File.ReadAllBytesAsync(file);
                foreach (var form in new[] { Encoding.UTF8, Encoding.Unicode })
                {
                    bytes.AsSpan().IndexOf(form.GetBytes(Password)).Should().Be(-1, $"{Path.GetRelativePath(root.FullName, file)} must not hold the password ({form.EncodingName})");
                }

                bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(Password)))).Should().Be(-1, "nor in base64");
            }

            var log = string.Join("\n", files.Where(f => f.EndsWith(".log", StringComparison.Ordinal)).Select(File.ReadAllText));
            log.Should().Contain("login=fred").And.Contain("passcode=***", "the connection is logged with the password masked");
        }
        finally
        {
            try
            {
                root.Delete(recursive: true);
            }
            catch (IOException)
            {
                // A file still open by a background thread; the temp folder is cleaned up eventually.
            }
        }
    }

    [Fact]
    public async Task Unticking_remember_and_connecting_forgets_the_saved_password()
    {
        await using var server = FakeStompServer.Start();
        await using var app = new TestApp(s => s with { UserName = "fred", RememberPassword = true, ProtectedPassword = SavedPassword("old") });
        var vm = app.ViewModel;
        ConnectViewModel? shown = null;
        app.Dialogs.Connect = d =>
        {
            shown = d;
            d.Host = "127.0.0.1";
            d.Port = server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            d.Password = "new";
            d.RememberPassword = false;
            return true;
        };

        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
        await server.WaitForFrameAsync(StompCommands.Connect);

        shown!.HasSavedPassword.Should().BeTrue("the dialog knew a password was saved");
        var saved = app.Settings.Load();
        saved.ProtectedPassword.Should().BeNull();
        saved.RememberPassword.Should().BeFalse();
        saved.UserName.Should().Be("fred");
        (await app.Ui.Run(() => vm.HasSavedPassword)).Should().BeFalse();
        await app.Ui.RunAsync(() => vm.DisconnectCommand.ExecuteAsync(null));
    }

    [Fact]
    public async Task Forget_in_the_connect_dialog_removes_the_password_at_once_even_if_cancelled()
    {
        await using var app = new TestApp(s => s with { Host = "pc2", UserName = "fred", RememberPassword = true, ProtectedPassword = SavedPassword("pw") });
        var vm = app.ViewModel;
        var forgotten = 0;
        app.Dialogs.Connect = d =>
        {
            d.Password.Should().Be("pw", "the saved password fills the box");
            d.ForgetPasswordCommand.CanExecute(null).Should().BeTrue();
            d.PasswordForgotten += (_, _) => forgotten++;
            d.ForgetPasswordCommand.Execute(null);
            d.Password.Should().BeEmpty();
            d.RememberPassword.Should().BeFalse();
            d.ForgetPasswordCommand.CanExecute(null).Should().BeFalse("there is nothing left to forget");
            return false;
        };

        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));

        forgotten.Should().Be(1, "the dialog empties its password box");
        var saved = app.Settings.Load();
        saved.ProtectedPassword.Should().BeNull();
        saved.RememberPassword.Should().BeFalse();
        saved.Host.Should().Be("pc2", "only the password goes");
        saved.UserName.Should().Be("fred");

        app.Dialogs.Connect = d =>
        {
            d.HasSavedPassword.Should().BeFalse();
            d.Password.Should().BeEmpty();
            return false;
        };
        await app.Ui.RunAsync(() => vm.ConnectCommand.ExecuteAsync(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Forget_in_options_removes_the_password_at_once_and_ok_does_not_bring_it_back(bool ok)
    {
        await using var app = new TestApp(s => s with { RememberPassword = true, ProtectedPassword = SavedPassword("pw") });
        var vm = app.ViewModel;
        app.Dialogs.Settings = s =>
        {
            s.HasSavedPassword.Should().BeTrue();
            s.SavedPasswordText.Should().Contain("is saved");
            s.ForgetPasswordCommand.Execute(null);
            s.HasSavedPassword.Should().BeFalse();
            s.SavedPasswordText.Should().Contain("No SimSig password is saved");
            app.Settings.Load().ProtectedPassword.Should().BeNull("it goes before OK or Cancel");
            return ok;
        };

        await app.Ui.Run(() => vm.ShowSettingsCommand.Execute(null));

        var saved = app.Settings.Load();
        saved.ProtectedPassword.Should().BeNull();
        saved.RememberPassword.Should().BeFalse();
        (await app.Ui.Run(() => vm.HasSavedPassword)).Should().BeFalse();
    }

    [Fact]
    public void The_uninstaller_forgets_the_password_and_keeps_the_rest()
    {
        var root = Directory.CreateTempSubdirectory("stringline-forget-");
        try
        {
            var store = new AppSettingsStore(AppPaths.Under(root.FullName), new DpapiSecretProtector(), NullLogger<AppSettingsStore>.Instance);
            store.ForgetSavedPassword().Should().BeFalse("there are no settings");
            File.Exists(store.Path).Should().BeFalse("nothing is written when there is nothing to forget");

            store.Save(store.WithPassword(new AppSettings { Host = "pc2", UserName = "fred", ReplaySpeed = 4 }, "pw", remember: true).WithTimetable("waterloo", @"C:\tt\w.wtt"));
            store.ForgetSavedPassword().Should().BeTrue();

            var saved = store.Load();
            saved.ProtectedPassword.Should().BeNull();
            saved.RememberPassword.Should().BeFalse();
            saved.Host.Should().Be("pc2");
            saved.UserName.Should().Be("fred");
            saved.ReplaySpeed.Should().Be(4);
            saved.TimetableFor("waterloo").Should().Be(@"C:\tt\w.wtt");
            store.ForgetSavedPassword().Should().BeFalse("it is gone already");
            StartupOptions.ForgetPassword.Should().Be("--forget-password", "installer/Stringline.iss runs the app with it");
            File.ReadAllText(Path.Combine(Root, "installer", "Stringline.iss")).Should().Contain($"Parameters: \"{StartupOptions.ForgetPassword}\"");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void The_policy_is_in_the_guide_and_linked_from_the_readme()
    {
        var policy = File.ReadAllText(Path.Combine(Root, "PRIVACY.md"));
        File.ReadAllText(Path.Combine(Root, "docs", "user-guide", "privacy.md")).ReplaceLineEndings().Should().Be(policy.ReplaceLineEndings(), "the guide's privacy page is a copy of PRIVACY.md");
        HelpGuide.ReadText(HelpTopics.Privacy).ReplaceLineEndings().Should().Be(policy.ReplaceLineEndings(), "the copy is embedded in the app");
        HelpGuide.Contents().Select(c => c.File).Should().Contain(HelpTopics.Privacy, "the guide's contents (and so the wiki's sidebar) link to it");
        HelpTopics.All.Should().Contain(HelpTopics.Privacy);
        File.ReadAllText(Path.Combine(Root, "README.md")).Should().Contain("(PRIVACY.md)");
        policy.Should().Contain("Forget saved password").And.Contain("/REMOVEUSERDATA").And.Contain("DPAPI").And.Contain("https://github.com/frddunc-glitch/Stringline/issues");
        Views.Help.TopicFor(null, HelpTopics.Privacy).Should().Be(HelpTopics.Privacy, "a link can name its page");
        Views.Help.TopicFor(null, "not-a-topic.md").Should().Be(HelpTopics.Contents);
    }

    [Fact]
    public async Task Help_about_connect_and_options_open_the_privacy_page()
    {
        await using var app = new TestApp(wpf: true);
        var vm = app.ViewModel;

        await app.Ui.Run(() =>
        {
            var window = MainWindowTests.Show(vm, 1280, 720);
            var about = new Views.AboutWindow { Owner = window, ShowActivated = false };
            var connect = new Views.ConnectDialog(new ConnectViewModel(app.Dialogs)) { Owner = window, ShowActivated = false };
            var options = new Views.SettingsWindow(new SettingsViewModel(new AppSettings(), app.Paths, app.Dialogs)) { Owner = window, ShowActivated = false };
            try
            {
                about.Show();
                connect.Show();
                options.Show();
                var menu = window.MainMenu.Items.OfType<MenuItem>().Single(m => (string)m.Header == "_Help")
                    .Items.OfType<MenuItem>().Single(m => (string)m.Header == "_Privacy");
                menu.InputGestureText.Should().BeEmpty("F1 is the user guide's key, not this item's");
                menu.Command.Execute(menu.CommandParameter);
                Path.GetFileName(app.Dialogs.Opened[^1]).Should().Be("privacy.html");
                foreach (var (link, on) in new (ICommandSource Link, IInputElement On)[] { (about.PrivacyLink, about.PrivacyLink), (connect.PrivacyLink, connect.PrivacyLink), (options.PrivacyLink, options.PrivacyLink) })
                {
                    link.Command.Should().Be(ApplicationCommands.Help);
                    ApplicationCommands.Help.Execute(link.CommandParameter, on);
                    Path.GetFileName(app.Dialogs.Opened[^1]).Should().Be("privacy.html");
                }

                connect.RememberBox.ToolTip.Should().BeOfType<string>().Which.Should().Contain("DPAPI").And.Contain(@"%APPDATA%\Stringline");
            }
            finally
            {
                options.Close();
                connect.Close();
                about.Close();
                window.Close();
            }
        });
    }
}
