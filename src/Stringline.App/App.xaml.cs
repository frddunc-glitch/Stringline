using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App;

/// <summary>
/// The application: a generic host for DI and logging (Serilog to rolling files at the level the settings ask for,
/// never credentials), the live engine running for the app's lifetime, and the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    /// <summary>The app's own log lines, under its name; read each time, as <see cref="Log.Logger"/> is set at start-up.</summary>
    private static Serilog.ILogger AppLog => Log.ForContext<App>();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Default;
        if (e.Args is [var only] && string.Equals(only, StartupOptions.ForgetPassword, StringComparison.OrdinalIgnoreCase))
        {
            // The uninstaller (S30): remove a saved password and stop, with no window and no log.
            new AppSettingsStore(paths, new DpapiSecretProtector(), NullLogger<AppSettingsStore>.Instance).ForgetSavedPassword();
            Shutdown(0);
            return;
        }

        // Read before the host is built, for the log level and how many log files to keep.
        var settings = new AppSettingsStore(paths, new DpapiSecretProtector(), NullLogger<AppSettingsStore>.Instance).Load();
        var logging = new AppLogging(paths.Logs, settings.DetailedLogging);
        Log.Logger = logging.CreateLogger(settings.LogRetentionDays);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppLog.Fatal(args.ExceptionObject as Exception, "Unhandled exception; the app is closing");
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error(args.Exception, "Unobserved background task exception");
            args.SetObserved();
        };

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSerilog(dispose: false);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(logging);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        builder.Services.AddSingleton<AppSettingsStore>();
        builder.Services.AddSingleton(new ProfileStore(paths.Profiles));
        builder.Services.AddSingleton<TimetableLoader>();
        builder.Services.AddSingleton(sp => new PlanningEngine(timeProvider: sp.GetRequiredService<TimeProvider>(), loggerFactory: sp.GetRequiredService<ILoggerFactory>()));
        builder.Services.AddSingleton(sp => new SessionRecorder(sp.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<IUiDispatcher>(new WpfUiDispatcher(Dispatcher));
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton(sp => new MainWindow(sp.GetRequiredService<MainViewModel>()));
        builder.Services.AddHostedService<EngineService>();

        _host = builder.Build();
        _host.Start();
        AppLog.Information("Started: {Environment}", AppLogging.Environment());

        var window = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();

        var (options, error) = StartupOptions.Parse(e.Args);
        if (error is not null)
        {
            MessageBox.Show(window, error, "Stringline", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            _ = StartAsync(_host.Services.GetRequiredService<MainViewModel>(), options);
        }
    }

    private static async Task StartAsync(MainViewModel viewModel, StartupOptions options)
    {
        await viewModel.TidySessionsAsync(keep: options.Resume ?? options.Review);
        if (options.Resume is { } resume)
        {
            await viewModel.ResumeSessionAsync(resume);
        }
        else if (options.Review is { } review)
        {
            await viewModel.ReviewSessionAsync(review);
        }
        else if (options.Replay is { } replay)
        {
            await viewModel.OpenReplayAsync(replay, options.Timetable, options.Speed, options.Start);
        }
        else
        {
            if (options.Timetable is { } timetable)
            {
                await viewModel.LoadTimetableAsync(timetable);
            }

            await viewModel.OfferResumeAsync();
        }
    }

    /// <summary>
    /// Windows is logging off or shutting down, or an installer is closing the app to update it (<c>05-…md</c> §12).
    /// WPF will close the window without waiting for it, so the session is stopped and saved here first.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel || MainWindow is not MainWindow window)
        {
            return;
        }

        AppLog.Information("Windows is closing the app ({Reason}); stopping the session", e.ReasonSessionEnding);
        if (window.ShutDownNow(TimeSpan.FromSeconds(4)))
        {
            AppLog.Information("Session stopped for Windows");
        }
        else
        {
            AppLog.Warning("The session did not stop within 4 s; closing anyway");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is { } host)
        {
            // The window has already stopped the session; this stops the engine loop.
            Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(5))).Wait(TimeSpan.FromSeconds(6));
            host.Dispose();
        }

        AppLog.Information("Stringline stopped");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Error(e.Exception, "Unhandled exception on the UI thread");
        var message = $"Something went wrong: {e.Exception.Message}\n\nDetails are in the log in {AppPaths.Default.Logs}.";
        e.Handled = true;
        if (MainWindow is { IsLoaded: true } owner)
        {
            MessageBox.Show(owner, message, "Stringline", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            // Failed before the window was up: there is nothing to go back to.
            MessageBox.Show(message, "Stringline", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>Runs the live engine's single-writer loop for the app's lifetime.</summary>
    private sealed class EngineService(PlanningEngine engine) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => engine.RunAsync(stoppingToken);
    }
}
