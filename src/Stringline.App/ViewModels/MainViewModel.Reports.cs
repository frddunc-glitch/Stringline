using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Stringline.Core;
using Stringline.Graph;
using Stringline.Planning;

namespace Stringline.App.ViewModels;

/// <summary>
/// Reporting and exports in the shell (S17): the Delays tab (SES-2), CSV export of the delay table, and PDF export of
/// the graph (GR-10). The delay table is worked out only while its tab is shown, at most every
/// <see cref="DelaysRefresh"/> for live updates alone.
/// </summary>
internal sealed partial class MainViewModel
{
    /// <summary>The least time between working out the delay table again for live updates alone.</summary>
    private static readonly TimeSpan DelaysRefresh = TimeSpan.FromSeconds(1);

    private long _delaysSequence = -1;
    private long _delaysPlan = -1;
    private DateTimeOffset _delaysAt;

    /// <summary>The delay table (a tab beside the regulation list; Fred).</summary>
    public DelaysViewModel Delays { get; } = new();

    public const int DelaysTab = 2;

    private void InitReports()
    {
        Delays.FilterChanged += (_, _) => UpdateDelays(force: true);
        Delays.PropertyChanged += OnDelaysChanged;
    }

    partial void OnSideTabChanged(int value) => UpdateDelays(force: true);

    private void OnDelaysChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DelaysViewModel.Selected) && !_syncingSelection && Delays.Selected is { } row)
        {
            SetSelection(row.Train, row.Report?.LiveUid ?? Snapshot.FindByTrain(row.Train)?.Train.SimUid);
        }
    }

    /// <summary>Work out the delay table again if its tab is shown and it is out of date.</summary>
    private void UpdateDelays(bool force = false)
    {
        if (SideTab != DelaysTab)
        {
            return;
        }

        var snapshot = Snapshot;
        var now = _time.GetUtcNow();
        if (!force && (snapshot.Sequence == _delaysSequence || (snapshot.PlanVersion == _delaysPlan && now - _delaysAt < DelaysRefresh)))
        {
            return;
        }

        _delaysSequence = snapshot.Sequence;
        _delaysPlan = snapshot.PlanVersion;
        _delaysAt = now;
        _syncingSelection = true;
        try
        {
            Delays.Update(snapshot, Profile);
            Delays.Selected = Selection?.Train is { } train ? Delays.Find(train) : Delays.Selected;
        }
        finally
        {
            _syncingSelection = false;
        }
    }

    /// <summary>
    /// Save the delay table as CSV (SES-2): one row per train and timing point, or with the parameter <c>trains</c>
    /// one per train. The trains are those the Delays tab lists (seen, or all).
    /// </summary>
    [RelayCommand]
    private void ExportDelays(string? kind)
    {
        if (Snapshot.Plan.Count == 0)
        {
            ShowInfo(InfoSeverity.Warning, "There is no timetable to report on yet.");
            return;
        }

        var byTrain = string.Equals(kind, "trains", StringComparison.OrdinalIgnoreCase);
        var report = DelayReport.For(Snapshot, Delays.Filter);
        var profile = Profile;
        var title = CurrentSimTitle();
        SaveExport(
            byTrain ? "Export train delays as CSV" : "Export the delay table as CSV",
            FileFilters.Csv,
            ExportName(byTrain ? "train-delays" : "delays", report.Now, ".csv"),
            "the delay table",
            path =>
            {
                using var writer = new StreamWriter(path, append: false, DelayReportCsv.Encoding);
                if (byTrain)
                {
                    DelayReportCsv.WriteTrains(writer, report, title);
                }
                else
                {
                    DelayReportCsv.WritePoints(writer, report, profile, title);
                }
            });
    }

    /// <summary>
    /// Save the graph as a one-page vector PDF (GR-10, S17): the current view's time and corridor range, orientation,
    /// colours and selection, laid out to fill the page. The parameter is the page: <c>a3</c> (landscape, the default;
    /// Fred), <c>a4</c> or <c>view</c>. The graph is the board last used (S21).
    /// </summary>
    [RelayCommand]
    private void ExportPdf(string? page)
    {
        if (ActiveBoard.Scene is not { } scene)
        {
            ShowInfo(InfoSeverity.Warning, "There is no graph to export yet.");
            return;
        }

        var viewport = ActiveBoard.Navigator.Viewport;
        var pdfPage = PdfPage.Parse(page, viewport) ?? PdfPage.A3Landscape;
        var now = NowSeconds is { } seconds ? new SimTime((int)Math.Floor(seconds)) : scene.Now;
        var selection = Selection;
        var corridor = Corridors.FirstOrDefault(c => c.Id == scene.CorridorId)?.Name ?? scene.CorridorId;
        var title = $"{CurrentSimTitle()?.ToString() ?? "Stringline"} — {corridor}{(now is { } t ? " – " + t.ToString("t", CultureInfo.InvariantCulture) : string.Empty)}";
        SaveExport("Export the graph as PDF", FileFilters.Pdf, ExportName(scene.CorridorId, now, ".pdf"), $"the graph ({pdfPage.Name})", path =>
        {
            using var renderer = new SkiaGraphRenderer();
            File.WriteAllBytes(path, renderer.RenderPdf(scene with { Now = now }, viewport, selection, pdfPage, title));
        });
    }

    /// <summary>
    /// File ▸ Export ▸ Plot (multi-page PDF)… (S21, Fred: a multi-page plot): a corridor over a time range at a fixed
    /// number of minutes a page, time running down the page by default as on a printed train control diagram. Asks
    /// for the corridor (the board last used's at first), the times, the minutes a page, the paper and which way time
    /// runs, remembers those choices, and writes one PDF page for each stretch of time, with no train selected.
    /// </summary>
    [RelayCommand]
    private async Task ExportPlotAsync()
    {
        if (Profile is not { } profile || Corridors.Count == 0)
        {
            ShowInfo(InfoSeverity.Warning, "There is no graph to plot yet: load a timetable first.");
            return;
        }

        var tab = ActiveBoard.Corridor ?? SelectedCorridor ?? Corridors[0];
        var now = NowTime ?? _timetable?.Timetable.Header.StartTime ?? SimTime.Zero;
        if (PlotPrompt.Ask(_dialogs, Corridors, tab, now, _settings.Plot) is not var (plot, corridor, remember))
        {
            return;
        }

        _settings = _settings with { Plot = remember };
        _settingsStore.Save(_settings);

        // The board's own scene if it shows that corridor; else one built for it.
        var shown = Boards.FirstOrDefault(b => b.Corridor?.Id == corridor.Id && b.Scene?.CorridorId == corridor.Id)?.Scene;
        var request = SceneRequest(profile, corridor);
        var scene = shown ?? await Task.Run(() => GraphSceneBuilder.Build(request));
        var nowShown = NowTime ?? scene.Now;
        var title = $"{CurrentSimTitle()?.ToString() ?? "Stringline"} — {corridor.Name} — plot {PlanInput.Format(plot.From)}–{PlanInput.Format(plot.To)}";
        SaveExport("Export a plot as PDF", FileFilters.Pdf, ExportName($"{corridor.Id}-plot", plot.From, ".pdf"), $"the plot ({plot.PageCount} pages, {plot.Page.Name})", path =>
        {
            using var renderer = new SkiaGraphRenderer();
            File.WriteAllBytes(path, renderer.RenderPlot(scene with { Now = nowShown }, plot, title));
        });
    }

    /// <summary>
    /// Asks where to save an export (starting in the folder last used), writes it, remembers the folder and reports the
    /// outcome in the info bar.
    /// </summary>
    private void SaveExport(string title, string filter, string name, string what, Action<string> write)
    {
        var folder = _settings.LastExportFolder is { Length: > 0 } last && Directory.Exists(last) ? last : _paths.Exports;
        if (_dialogs.PickSaveFile(title, filter, Path.Combine(folder, name)) is not { } path)
        {
            return;
        }

        try
        {
            write(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoSeverity.Error, $"Cannot export {what}: {ex.Message}");
            return;
        }

        _settings = _settings with { LastExportFolder = Path.GetDirectoryName(path) };
        _settingsStore.Save(_settings);
        ShowInfo(InfoSeverity.Info, $"Exported {what} to {path}.");
    }

    /// <summary>A file name for an export, e.g. <c>waterloo-main-0700.png</c> or <c>waterloo-delays-0700.csv</c>.</summary>
    private string ExportName(string what, SimTime? now, string extension)
    {
        var area = Profile?.AreaId is { Length: > 0 } a ? a : "graph";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string([.. $"{area}-{what}".Select(c => invalid.Contains(c) || c == ' ' ? '_' : c)]);
        if (now is not { } t)
        {
            return safe + extension;
        }

        var day = ((t.Seconds % SimTime.SecondsPerDay) + SimTime.SecondsPerDay) % SimTime.SecondsPerDay;
        return string.Create(CultureInfo.InvariantCulture, $"{safe}-{day / SimTime.SecondsPerHour:00}{day % SimTime.SecondsPerHour / SimTime.SecondsPerMinute:00}{extension}");
    }
}
