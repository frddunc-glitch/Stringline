using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Stringline.App.Services;
using Stringline.App.ViewModels;
using Stringline.Gateway.Recording;
using Stringline.Network.Profiles;
using Stringline.Planning;

namespace Stringline.App.Tests;

/// <summary>
/// A stand-in UI thread: one thread with its own synchronisation context, so view-model code (and its awaits) runs
/// there exactly as it would on the WPF dispatcher. Tests drive it with <see cref="Run{T}(Func{T})"/>.
/// </summary>
internal sealed class TestUiThread : SynchronizationContext, IUiDispatcher, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public TestUiThread()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "Test UI" };
        _thread.Start();
    }

    public void Post(Action action) => Post(_ => action(), null);

    public override void Post(SendOrPostCallback d, object? state)
    {
        try
        {
            _queue.Add((d, state));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Shut down: late posts from background work are dropped, as the real dispatcher drops them.
        }
    }

    public override void Send(SendOrPostCallback d, object? state) => Run(() =>
    {
        d(state);
        return 0;
    }).GetAwaiter().GetResult();

    /// <summary>Run on the UI thread and wait for the result.</summary>
    public Task<T> Run<T>(Func<T> func)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            try
            {
                done.SetResult(func());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        });
        return done.Task;
    }

    public Task Run(Action action) => Run(() =>
    {
        action();
        return 0;
    });

    /// <summary>Run an async view-model method on the UI thread (its continuations come back here) and wait for it.</summary>
    public async Task RunAsync(Func<Task> func) => await (await Run(func));

    /// <summary>Poll a condition on the UI thread until it holds.</summary>
    public async Task WaitUntil(Func<bool> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await Run(condition))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }

            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private void Loop()
    {
        SetSynchronizationContext(this);
        foreach (var (callback, state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}

/// <summary>Dialogs that answer from a script instead of showing anything.</summary>
internal sealed class ScriptedDialogs : IDialogService
{
    public Func<ConnectViewModel, bool> Connect { get; set; } = _ => false;

    public Func<ReplayViewModel, bool> Replay { get; set; } = _ => false;

    public Queue<string?> Files { get; } = new();

    public List<string> Errors { get; } = [];

    public List<(string Title, IReadOnlyList<string> Lines)> Lists { get; } = [];

    public bool ShowConnect(ConnectViewModel viewModel) => Connect(viewModel);

    public bool ShowReplay(ReplayViewModel viewModel) => Replay(viewModel);

    public string? PickFile(string title, string filter, string? initialPath) => Files.Count > 0 ? Files.Dequeue() : null;

    /// <summary>Answers to save dialogs, in order; none left means cancelled.</summary>
    public Queue<string?> SaveFiles { get; } = new();

    /// <summary>The path each save dialog suggested.</summary>
    public List<string> SaveSuggestions { get; } = [];

    public string? PickSaveFile(string title, string filter, string initialPath)
    {
        SaveSuggestions.Add(initialPath);
        return SaveFiles.Count > 0 ? SaveFiles.Dequeue() : null;
    }

    public void ShowError(string title, string message) => Errors.Add(message);

    /// <summary>Answers to prompts, in order: each fills in the values and returns OK (true) or Cancel; none left means cancelled.</summary>
    public Queue<Func<PromptViewModel, bool>> Prompts { get; } = new();

    /// <summary>Every prompt shown.</summary>
    public List<PromptViewModel> Prompted { get; } = [];

    public bool Prompt(PromptViewModel viewModel)
    {
        Prompted.Add(viewModel);
        return Prompts.Count > 0 && Prompts.Dequeue()(viewModel) && viewModel.TryAccept();
    }

    public void ShowList(string title, string heading, IReadOnlyList<string> lines) => Lists.Add((title, lines));

    /// <summary>Answers to questions, in order (the index of the choice); none left means the window was closed.</summary>
    public Queue<int?> Choices { get; } = new();

    /// <summary>Every question asked.</summary>
    public List<string> Asked { get; } = [];

    public int? Choose(string title, string message, IReadOnlyList<string> choices)
    {
        Asked.Add(message);
        return Choices.Count > 0 ? Choices.Dequeue() : null;
    }

    /// <summary>What the user does in the profile editor while it is open.</summary>
    public Action<ProfileEditorViewModel>? ProfileEditor { get; set; }

    public List<ProfileEditorViewModel> ProfileEditors { get; } = [];

    public void ShowProfileEditor(ProfileEditorViewModel viewModel)
    {
        ProfileEditors.Add(viewModel);
        ProfileEditor?.Invoke(viewModel);
    }

    /// <summary>Files opened in their Windows program.</summary>
    public List<string> Opened { get; } = [];

    public string? OpenInShell(string path)
    {
        Opened.Add(path);
        return null;
    }
}

/// <summary>A main view model wired as the app wires it, with files under a temporary folder.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    private readonly DirectoryInfo _root;
    private readonly bool _ownsRoot;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _engineLoop;

    /// <param name="root">Another app's folder, to start the app again over its files (S16: resume); that app cleans it up.</param>
    public TestApp(Func<AppSettings, AppSettings>? settings = null, string? root = null)
    {
        _ownsRoot = root is null;
        _root = root is null ? Directory.CreateTempSubdirectory("stringline-app-") : new DirectoryInfo(root);
        Root = _root.FullName;
        Paths = AppPaths.Under(_root.FullName);
        Settings = new AppSettingsStore(Paths, new DpapiSecretProtector(), NullLogger<AppSettingsStore>.Instance);
        if (settings is not null)
        {
            Settings.Save(settings(new AppSettings()));
        }

        Engine = new PlanningEngine(new PlanningEngineOptions { PublishInterval = TimeSpan.FromMilliseconds(20) });
        _engineLoop = Engine.RunAsync(_stop.Token);
        Recorder = new SessionRecorder();
        ViewModel = Ui.Run(() => new MainViewModel(
            Engine,
            Recorder,
            new TimetableLoader(new ProfileStore(Paths.Profiles), NullLoggerFactory.Instance),
            Settings,
            Dialogs,
            Ui,
            Paths,
            TimeProvider.System,
            NullLoggerFactory.Instance)).GetAwaiter().GetResult();
    }

    public TestUiThread Ui { get; } = new();

    /// <summary>The folder everything is kept under.</summary>
    public string Root { get; }

    public ScriptedDialogs Dialogs { get; } = new();

    public AppPaths Paths { get; }

    public AppSettingsStore Settings { get; }

    public PlanningEngine Engine { get; }

    public SessionRecorder Recorder { get; }

    public MainViewModel ViewModel { get; }

    public async ValueTask DisposeAsync()
    {
        await Ui.RunAsync(async () => await ViewModel.DisposeAsync());
        await _stop.CancelAsync();
        await _engineLoop;
        Ui.Dispose();
        Recorder.Dispose();
        _stop.Dispose();
        if (!_ownsRoot)
        {
            return;
        }

        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // A file still open by a background thread; the temp folder is cleaned up eventually.
        }
    }
}
