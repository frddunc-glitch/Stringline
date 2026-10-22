# 02 — Architecture

## 1. Solution layout

```
Stringline.sln
├─ src/
│  ├─ Stringline.Core/          net10.0   Domain model, sim time, IDs, shared abstractions
│  ├─ Stringline.Gateway/       net10.0   STOMP client, message parsing, recorder, replay source
│  ├─ Stringline.Timetable/     net10.0   .wtt import, train matching; lossless WttDocument, edits and writer (Editing, S40)
│  ├─ Stringline.Network/       net10.0   Corridors, sections, profile I/O, auto-generation, distance axis
│  ├─ Stringline.Planning/      net10.0   Working plan + commands, forecaster, conflicts, suggestions, re-planning (IReplanner, rules)
│  ├─ Stringline.Optimiser/     net10.0   CP-SAT re-planner (Google OR-Tools, native solver for Windows, Linux and macOS)
│  ├─ Stringline.Persistence/   net10.0   SQLite session store (Microsoft.Data.Sqlite)
│  ├─ Stringline.Editing/       net10.0   Timetable editor core: sim knowledge (S41); checks before SimSig (S42); the draft's single writer, autosave and names (Drafts, S43); a drag's minimum run (S44); bulk tools, CSV (S46–S48, planned; 08-…md)
│  ├─ Stringline.Graph/         net10.0   Graph scene model + Skia renderer (SkiaSharp), hit testing, the draft board and drag targets (S44; it uses Timetable's edits) — no WPF
│  ├─ Stringline.App/           net10.0-windows10.0.19041.0  WPF shell, MVVM, SKElement host, dialogs (SkiaSharp.Views.WPF 4 needs the versioned TFM)
│  └─ Stringline.Tools/         net10.0   CLI: record, replay-server, wtt-dump, render-png, report, replan…
├─ tests/
│  ├─ Stringline.*.Tests/       xUnit + FluentAssertions 7.x (+ Verify for snapshot tests)
│  ├─ Stringline.Architecture.Tests/  guards the WPF/portability rule below
│  └─ fixtures/                 sample .wtt, .sslog recordings, profiles
├─ benchmarks/
│  └─ Stringline.Benchmarks/    net10.0   BenchmarkDotNet: graph frame times against the GR-9 budget (S9)
└─ docs/
```

`Stringline.Portable.slnf` is the solution minus `Stringline.App`; use it to build and test on Linux (CI's `ubuntu-latest` job and cloud sessions).

Rule: **only `Stringline.App` references WPF.** `Stringline.Architecture.Tests` enforces it. The graph renderer draws to any `SKCanvas`, so it renders PNGs in tests and CLI on Linux. That lets Claude Code sessions verify graph output visually without Windows.

## 2. Runtime data flow

```
SimSig ──STOMP──► GatewayClient ──► MessageParser ──► IGatewayEvent stream
   ▲                                   │                   │
   │ (assist, opt-in)                  ▼                   ▼
   └──────────── CommandSender     Recorder(.sslog)    EventBus (Channel<T>)
                                                           │
       ReplaySource(.sslog) ───────────────────────────────┤
                                                           ▼
                                                  LiveState (actuals, clock, TD)
                                                           │
                         WTT ─► TrainMatcher ◄─────────────┤
                                                           ▼
                       WorkingPlan (commands) ─► Forecaster ─► ConflictDetector ─► Suggester
                                                           │
                                                           ▼
                                                   PlanSnapshot (immutable)
                                                           │
                                          GraphSceneBuilder (per corridor, per viewport)
                                                           │
                                                SkiaRenderer ─► SKElement (WPF)
```

- **Single-writer model:** one background `PlanningEngine` loop consumes the event channel and user commands, and publishes immutable `PlanSnapshot`s. The UI thread only reads snapshots — no locks on the hot path.
- Recomputation is debounced (e.g. coalesce events within 100 ms wall time) and incremental where cheap.
- `ISimEventSource` has two implementations: `LiveGatewaySource` and `ReplaySource`. Everything downstream is identical.
- **CommandSender** is `SimSession.SendAsync` over `GatewayClient.SendAsync` (live sessions only). Apart from `idrequest` when recording starts, only assist (S19, `05-…md` §14) sends, with bodies from `GatewayCommands`. The engine also publishes the route, signal and berth IDs seen (`SignallingIndex`) in each `PlanSnapshot` for its dialogs.

## 3. Key abstractions

```csharp
public readonly record struct SimTime(int Seconds);             // seconds after midnight of sim day 0; may exceed 86400
public readonly record struct Tiploc(string Code);            // trimmed + upper-cased; also holds signal IDs from train_location
public readonly record struct Headcode(string Value);          // trimmed + upper-cased; IsStandard, ClassDigit (default priority)
public readonly record struct TrainId(string Value);           // opaque, case-sensitive identity of one planned train
// SimTime text: "HH:mm:ss" / "HH:mm" / "HH:mm½" plus "+N"/"-N" day marker; parse also accepts HHmm, HHmm½/H, hours ≥ 24.
// Stringline.Core.Json: LenientJson (Utf8JsonReader helpers) + lenient bool/int/long/string/SimTime/ID converters.

// Stringline.Core.Events — in Core so Timetable/Planning can consume events without referencing Gateway.
public interface ISimEventSource { IAsyncEnumerable<GatewayEvent> ReadAllAsync(CancellationToken ct); }

public abstract record GatewayEvent(DateTimeOffset ReceivedAt);
public sealed record ClockEvent(AreaId, Clock, IntervalMs, Paused) : GatewayEvent;   // SpeedRatio = 500 / IntervalMs; IsMaximumSpeed when IntervalMs == 1 (flat out: ratio unknown)
public sealed record TrainLocationEvent(Headcode, Uid, Action, Location, Platform, Time, AspectPassed?, AspectApproach?) : GatewayEvent;
public sealed record TrainDelayEvent(Headcode, Uid, DelaySeconds) : GatewayEvent;
public sealed record TdStepEvent(Kind, AreaId, From, To, Description, Time?) : GatewayEvent;   // CA/CB/CC
public sealed record SignallingStateEvent(AreaId, ObjectType, ObjectTypeText, ObjectId, Time?, Fields) : GatewayEvent;  // SG_MSG; object-specific fields as text + lenient getters
public sealed record TimetableSummaryEvent(Topic, Trains) : GatewayEvent;   // ttsummary reply: TrainSummary(Headcode, Uid, Status, StatusText, DelaySeconds?, Location?, LocationName, Platform)
public sealed record ReplayRewoundEvent(Target) : GatewayEvent;              // not from SimSig: a replay sought backwards; drop state, events restart from the beginning
public sealed record UnknownEvent(Topic, MessageType, Raw, Reason) : GatewayEvent;

// Stringline.Gateway
GatewayClient        // STOMP 1.1 client: ReadAllAsync() yields GatewayMessage(ReceivedAt, Topic, Body bytes); SendAsync(GatewayCommand); ConnectionChanged event
MessageParser        // GatewayMessage → IReadOnlyList<GatewayEvent>, Utf8JsonReader streaming, never throws
GatewayCommands      // the only way to build outbound bodies (Utf8JsonWriter)
LiveGatewaySource    // ISimEventSource = GatewayClient + MessageParser
Recording.SslogWriter / SslogReader / GatewayRecorder / SslogRecording (whole file in memory, IndexOf(SimTime))
Replay.ReplayTimeline // paces recorded frames by their wall-clock gaps × Speed (∞ = flat out); Pause/Resume/SeekTo from any thread
Replay.ReplaySource   // ISimEventSource = ReplayTimeline + MessageParser; ReplayRewoundEvent after a backward seek
Replay.ReplayServer   // fake SimSig STOMP server for `stringline replay-server`; answers idrequest/snapshot/ttsummary from replayed state

// Stringline.Core.Live — LiveTrain (sim uid → workings + actuals), LiveWorking, ActualEvent, MatchStatus/MatchMethod, SimClock
// Stringline.Timetable.Live
TrainMatcher         // pure: MatchRequest (headcode, actuals, taken trains, present at start, previous train) → MatchDecision; 04-…md §3
LiveState            // reducer: Apply(GatewayEvent) → clock, live trains + matches, TD berth map, entry-delay hints; manual Link/ClearLink

// Stringline.Core.Timetables — the WTT domain model: WorkingTimetable(Header, Categories, Trains, Rules), Train, TimingPoint, TrainActivity, TimetableRule
// Stringline.Timetable
WttReader            // .wtt (zip) or SavedTimetable.xml → WttImportResult(WorkingTimetable, Warnings, UnknownElements, SkippedTrains); tolerant, see 04-…md §1

// Stringline.Graph (S7) — portable; draws to any SKCanvas
GraphViewport        // time range × axis range, surface size, margins, Orientation (S9: Horizontal, or Vertical = time down, Up end left); ToScreen/ToWorld, TimeToPixel/AxisToPixel, PanTimePixels, ZoomTime(anchor), Follow(now), Around(now, span, fraction), WithOrientation
GraphScene           // per corridor, world coordinates (sim seconds, axis 0..1): GraphLocation[], GraphPath[] (layer, train, headcode, colour, vertices, S9: per-segment colours, S12: plan point index per vertex, IsCancelled), Now; S13: Disruptions (GraphDisruption), Notes (GraphNote)
GraphSceneBuilder    // pure: GraphSceneRequest(profile, corridor, timetable?, plan?, live trains, forecast?, now, ColourMode) → GraphScene; WTT, plan (S10: modified trains and clones; S12: cancelled ones grey with end crosses; PlanPaths for ghosts), actual and forecast (S11: Forecast.Drawn, on from the latest actual) layers
DelayBands           // S9: GraphColourMode (Category | Delay); band of a delay (≤0 / 1–5 / 6–15 / >15 min) and its colour
TimeGrid             // tick spacing from zoom (5 min / 30 min at a 3 h view), hour lines heaviest
SkiaGraphRenderer    // Render(SKCanvas, scene, viewport, selection?), RenderPng(…, scale), S17: RenderPdf(…, PdfPage (A3/A4 landscape or view size; Fit lays the view out inside its margins), title); pooled paints, culling by path bounds, headcode labels; GraphTheme, GraphFonts (embedded Noto Sans)
  ProjectedPathCache //   S9: SKPaths projected once per zoom, drawn translated while panning; a new scene re-projects only changed paths and reports where drawing changed
  PathTileCache      //   S9: the unselected lines as 256 px device tiles; panning blits, a live update redraws the tiles under changed trains (05-…md §7)
  LabelGrid          //   S9: uniform grid of placed label rectangles for the overlap check
SkiaGraphRenderer.Render(…, GraphSelection?, ghosts?) // S8: the selected train (WTT train and/or live uid) heavy with a halo, the rest dimmed; S12: ghost paths drawn over everything, uncached
PlanDrag             // S12: a drag edit of the selected train (body → ShiftTrain from the next point, Alt+vertex → ShiftTrain from it, stop end → SetDwell); 15 s snap, not before now or the latest report, min-run stop; each move previews the command on the plan → PlanDragPreview (command, train, status)
GraphAnnotations     // S13: disruptions and notes for a scene, where they draw (RectOf, MarkerOf, LabelOf), hit tests (DisruptionAt, NoteAt), the span a B+drag or right-click picks (SpanBetween: widened to location lines, 5 min steps), tooltips; Render(…, ghosts?, draft?) draws a B+drag draft
GraphNavigator       // S8: pan/zoom/follow state for one surface, toolkit-free: Resize, Tick(now), Wheel (Ctrl = corridor, Shift = pan), drag, presets, CentreOn; S9: SetOrientation; S21: MatchTime(other)
GraphTimeLink        // S21: keeps several navigators (boards) on one clock: a change of time range or follow mode on one is matched by the rest; each keeps its corridor range
GraphPlot            // S21: GraphPlotOptions(from, to, SecondsPerPage, PdfPage (A3/A4 portrait or landscape), Orientation; Problem) → GraphPlotPage[] (one scale, last page cut short); SkiaGraphRenderer.RenderPlot writes the multi-page PDF
GraphHitTester       // S8: nearest projected segment within 5 px, actual preferred over WTT; linear scan → GraphHit(path, segment, nearest vertex, time)
GraphHitIndex        // S9: the same answers from a 16 px grid over one scene × viewport; the app keeps one until the view or scene changes
GraphHover           // S8: GraphHit + scene + PlanSnapshot → HoverInfo (location, WTT and actual times, delay, S11: forecast, knock-on, possibly stopped, match, train)

// Stringline.Planning (S8, S10, S11)
PlanningEngine       // single writer: queue of GatewayEvents, SetTimetable/Clear, SetForecast(network, options) and plan Execute/Undo/Redo (Tasks) → LiveState + WorkingPlan → Forecaster → PlanSnapshot at most every 100 ms (plan changes at once); keeps the session's events so a timetable loaded mid-session rebuilds the matches
PlanSnapshot         // immutable: timetable, live trains, SimClockReading (interpolate on any thread), match summary, WorkingPlan, PlanHistory, PlanVersion, EntryDelays, Forecast; FindByTrain/FindByUid; S14: Conflicts; S15: Settings (ForecastSettings: the network, forecast and conflict options it was made with)
ConflictDetector     // S14, pure: ConflictRequest(plan, forecast, network, options) → ConflictSet (06-…md §3)
Suggester            // S15, pure: SuggestionRequest(forecast request, conflict, options; baseline forecast and conflicts) → Suggestion[] (hold, swap, platform, line, retime, terminate, cancel), scored by re-forecasting (06-…md §4); Suggestion.ToCommand() → AcceptSuggestion
IReplanner           // S20: ReplanRequest(forecast request, conflict options, ReplanOptions; baseline) → ReplanResult (commands, steps, conflicts before/after, delay); RuleReplanner (suggestions, earliest conflict first) and, in Stringline.Optimiser, CpSatReplanner (CP-SAT model of the plan builder, forecaster and detector, checked each round); ReplanEvaluator scores candidates for both (06-…md §5)
RegulationList       // S15, pure: plan + forecast + location + now → RegulationRow[] in plan order, with trains ahead of / behind others against the WTT order (RP-9)
Forecaster           // S11, pure: ForecastRequest(plan, live trains, now, session start, entry delays, ForecastNetwork, ForecastOptions) → Forecast; one event queue, independent and interaction passes (06-…md §2)
ForecastNetwork      // S11: the profile's corridor sections as resources (headway, single line, tracks each way widened to what the WTT uses at once), category priorities; built once per timetable and profile
WorkingPlan          // S10, immutable: WTT + commands (06-…md §1). TrainPlan(Id, Headcode, CategoryId, Origin Wtt|Clone, Source, Points, Cancelled, TerminatesAt, IsModified) of PlannedPoints; Disruptions; Notes (S13); LiveLinks; MinRunTable; Apply/ApplyAll(commands), NewCloneId, NewDisruptionId, NewNoteId
WorkingPlanBuilder   // S10: what commands change the plan through; clamps shortened runs to the minimum (× speed restriction) and departures to arrivals, with PlanWarnings; all-or-nothing per command
IPlanCommand         // S10: ShiftTrain, SetDwell, ChangePlatform, ChangeLine, SkipStop, AddStop, TerminateAt, CancelTrain, ReinstateTrain, ResetTrain (S12), CloneTrain, Add/Edit/RemoveDisruption, Add/Edit/RemoveNote (S13), Link/UnlinkLiveTrain, AcceptSuggestion; PlanCommandJson ("type" discriminator)
MinRunTable          // S10: fastest booked run per location pair (either way, less allowances)
TrainSchedule        // S12, pure: PlanSnapshot + train → ScheduleRows (WTT, plan, actual, forecast, delay; edits marked) for the detail panel
PlanHistory          // S10: Done (oldest first) and Undone (next redo first) PlanHistoryEntry(Command, Description, Warnings)
DelayReport          // S17, pure: PlanSnapshot + filter (Seen | All) → DelayReportTrain[] (state, latest, worst, final) of DelayReportPoints (WTT, plan, actual or forecast, delay vs WTT and vs plan) via TrainSchedule; DelayReportCsv writes points or trains as CSV (SES-2)
TrainDelays          // actual − booked at a timing point (arrive vs arrival, else departure); Current(live) = latest booked point of the current working

// Stringline.Planning (S16): PlanStep(Kind Execute|Undo|Redo|Reset, Command?, Description?, SimTime?, At); PlanningEngine.PlanStepped (every history change, in order) and RestorePlan(steps); PlanLog.Fold(steps) → done/undone, PlanLog.By(steps, now) for review
// Stringline.Persistence (S16): SessionWriter (thread-safe autosave of frames, steps and SessionInfo; file made lazily; End marks it ended), SavedSession.Load (Info, Frames, Steps, Problems, Events(parser), ToRecording()), SessionCatalogue (List, LatestUnfinished, ReadInfo), SessionFormatException
// Stringline.Timetable: WttCatalogue (S8) — ReadInfo(path) peeks the sim ID and name; FindForArea(folder, area) for timetable suggestions
// Stringline.Gateway: SessionRecorder (S8) — toggleable, thread-safe .sslog recording of a live connection; LiveGatewaySource.MessageParsed feeds it

public interface IPlanCommand { PlanCommandResult Apply(WorkingPlanBuilder b); string Describe(WorkingPlan plan); }   // S10: Describe takes the plan, to name trains by headcode
```

## 4. Threading
- `GatewayClient`: dedicated read loop on a `TcpClient` stream; writes through a `SemaphoreSlim`; heart-beats if negotiated.
- `PlanningEngine`: `Channel<EngineInput>` (bounded, `DropOldest` never for train events — use unbounded with monitoring).
- UI: subscribes to `IObservable<PlanSnapshot>` (or a simple event) and marshals to the dispatcher; graph invalidates at most at display refresh rate.
- Sim clock between `clock_msg`s (`Stringline.Core.Live.SimClock`, S5): interpolates from the events' `ReceivedAt` wall times, frozen when paused. The speed is **measured** from successive running `clock_msg`s (Δsim / Δwall, over at least 20 sim s), because at SimSig's flat-out setting the interval is 1 and a replay at 8× still reports the recorded interval. 500/interval is only the first guess, and at flat out the clock holds until measured. It never runs past the end of the current sim minute (the next `clock_msg` is due then) or past the latest `train_location` time, which runs up to a minute ahead of the clock and moves it on.

## 5. Persistence
- **Settings** `%APPDATA%\Stringline\settings.json` (`AppSettings`): connection (password only DPAPI-encrypted), view options, timetables by sim, and from S18 the settings window's values: `planning` (forecast, conflict and suggestion numbers), the sessions, recordings and exports folders (`AppPaths.WithFolders`), logging and session housekeeping.
- **What is stored and sent** is listed for users in `PRIVACY.md` (S30, #14). Nothing is sent anywhere but the gateway the user connects to (no telemetry, no update check). The uninstaller always removes a saved password (`Stringline.exe --forget-password`), and asks whether to remove `%APPDATA%\Stringline`, `%LOCALAPPDATA%\Stringline` and `%TEMP%\.net\Stringline` (default No; `/REMOVEUSERDATA` when silent). `Documents\Stringline` is never removed.
- **Profile** `%APPDATA%\Stringline\profiles\{area_id}.json` — corridors, location names, distances, categories, priorities, the sim name (and whether it was typed), the titles of the timetables it was made or refreshed from (S31), and the learned signal, track circuit and berth places with the sessions they came from (S37, `04-…md` §5). Beside it, `{area_id}.simdata.json` caches the sim knowledge (S41, `08-…md` §4), rebuilt when a timetable, recording, session or the profile it came from changes. Shared with Tools ▸ Export profile… and Import profile… (merge or replace). Edited in the app's profile editor (S14a, `ProfileEdits`) or by hand, then Tools ▸ Reload profile.
- **Timetable drafts** `Documents\Stringline\drafts\{name}.wtt` plus `{name}.draft.json` (S43, `08-…md` §7.1): the draft as a timetable, and its source and target, undo history (edits recorded by element path), the `.wtt`'s hash and whether it was open (so a crash's draft is offered back). Timetables the editor replaces are copied first to `%LOCALAPPDATA%\Stringline\timetable-backups\{area}` (S40).
- **Session** `Documents\Stringline\sessions\{area_id}-{yyyyMMdd-HHmm}.stringline` (SQLite, WAL, format 1; S16):
  - `meta` (key/value): `format`, `kind` (live/replay), `area_id`, `sim_name`, `timetable_path`, `timetable_name`, `host`, `port`, `recording_path`, `position` (replay), `last_sim`, `created`, `updated`, `ended` (0 while running, so it is offered for resuming), `app`.
  - `frames` (seq, at in Unix ms, sim, topic, body): a live session's gateway frames, only those whose events the engine keeps (clock, train location and delay, TD, timetable summary). Replay sessions refer to their `.sslog` instead.
  - `steps` (seq, at, sim, kind execute/undo/redo/reset, command as `PlanCommandJson`, description): the plan's history as `PlanStep`s. Disruptions, notes and live links are plan commands, so the sketch's separate `disruptions`, `notes` and `train_links` tables are not needed.
  - The live state is rebuilt by parsing the frames again; the working plan by folding the steps (`PlanLog.Fold`) and replaying the commands over the WTT. A step that cannot be read (e.g. a command from a newer version) is skipped and reported. A newer `format` is refused.
- **Recordings** `.sslog` — JSON Lines, format version 1:
  - line 1, header: `{"sslog":1,"app":"Stringline 0.1.0","created":"…Z","host":"localhost","port":51515,"topics":[…]}`. The sim's identity is not known yet when the header is written; it is in the first `clock_msg`.
  - frames: `{"t":"2026-10-18T09:12:03.123Z","topic":"/topic/TRAIN_MVT_ALL_TOC","sim":15720,"body":"…"}`. `t` is wall-clock UTC to the millisecond; `sim` is the latest `clock_msg` clock seen so far (absent before the first one), for seeking; `body` is the exact body text, or `body64` (base64) if it is not valid UTF-8.
  - connection changes: `{"t":"…","conn":"connecting|connected|disconnected","detail":"…","headers":{…}}` (CONNECTED headers on `connected`).
  - Readers skip malformed lines (e.g. a truncated last line) and count them; unknown properties are ignored.

## 6. Packages
| Purpose | Package |
|---|---|
| MVVM | CommunityToolkit.Mvvm |
| Rendering | SkiaSharp (+ SkiaSharp.NativeAssets.Linux.NoDependencies for Linux), SkiaSharp.Views.WPF; Noto Sans embedded (OFL) |
| DI / hosting | Microsoft.Extensions.Hosting |
| Logging | Microsoft.Extensions.Logging.Abstractions in libraries (`ILogger<T>`, `[LoggerMessage]`); Serilog (+ Console/File sinks, Serilog.Extensions.Logging) as the provider in Tools and App |
| SQLite | Microsoft.Data.Sqlite |
| Tests | xUnit, FluentAssertions, Verify.Xunit, Microsoft.Extensions.TimeProvider.Testing (`FakeTimeProvider`) |
| CLI | System.CommandLine |

No third-party STOMP library: the protocol subset is small (CONNECT, SUBSCRIBE, SEND, DISCONNECT, MESSAGE, ERROR, RECEIPT) and owning it avoids the SimSig quirks other clients have tripped over.

## 7. Error handling principles
- Never crash on a bad message: log, emit `UnknownEvent`, continue.
- Never send malformed JSON to SimSig (it shows an error dialog to the host) — all outbound bodies built with `Utf8JsonWriter` and unit-tested.
- Planning failures for one train must not stop others; mark that train "forecast unavailable".
