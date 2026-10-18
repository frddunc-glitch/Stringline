using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App;

/// <summary>
/// The application: a generic host for DI and logging (Serilog to rolling files, never credentials), the live
/// engine running for the app's lifetime, and the main window.
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = AppPaths.Default;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(Path.Combine(paths.Logs, "stringline-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddSerilog(dispose: false);
        builder.Services.AddSingleton(paths);
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
        Log.Information("Stringline {Version} started", typeof(App).Assembly.GetName().Version);

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
        if (options.Replay is { } replay)
        {
            await viewModel.OpenReplayAsync(replay, options.Timetable, options.Speed, options.Start);
        }
        else if (options.Timetable is { } timetable)
        {
            await viewModel.LoadTimetableAsync(timetable);
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

        Log.Information("Stringline stopped");
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled exception on the UI thread");
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
