# 02 — Architecture

## 1. Solution layout

```
Stringline.sln
├─ src/
│  ├─ Stringline.Core/          net10.0   Domain model, sim time, IDs, shared abstractions
│  ├─ Stringline.Gateway/       net10.0   STOMP client, message parsing, recorder, replay source
│  ├─ Stringline.Timetable/     net10.0   .wtt import, train matching
│  ├─ Stringline.Network/       net10.0   Corridors, sections, profile I/O, auto-generation, distance axis
│  ├─ Stringline.Planning/      net10.0   Working plan + commands, forecaster, conflicts, suggestions
│  ├─ Stringline.Persistence/   net10.0   SQLite session store (Microsoft.Data.Sqlite)
│  ├─ Stringline.Graph/         net10.0   Graph scene model + Skia renderer (SkiaSharp), hit testing — no WPF
│  ├─ Stringline.App/           net10.0-windows  WPF shell, MVVM, SKElement host, dialogs
│  └─ Stringline.Tools/         net10.0   CLI: record, replay-server, wtt-dump, render-png
├─ tests/
│  ├─ Stringline.*.Tests/       xUnit + FluentAssertions 7.x (+ Verify for snapshot tests)
│  ├─ Stringline.Architecture.Tests/  guards the WPF/portability rule below
│  └─ fixtures/                 sample .wtt, .sslog recordings, profiles
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

public interface IPlanCommand { PlanCommandResult Apply(WorkingPlanBuilder b); string Describe(); }
```

## 4. Threading
- `GatewayClient`: dedicated read loop on a `TcpClient` stream; writes through a `SemaphoreSlim`; heart-beats if negotiated.
- `PlanningEngine`: `Channel<EngineInput>` (bounded, `DropOldest` never for train events — use unbounded with monitoring).
- UI: subscribes to `IObservable<PlanSnapshot>` (or a simple event) and marshals to the dispatcher; graph invalidates at most at display refresh rate.
- Sim clock between `clock_msg`s: `SimClock` interpolates with `Stopwatch`, speed = 500/interval, frozen when paused. At SimSig's flat-out setting the interval is 1 and the real speed depends on the PC, so measure the speed from successive `clock_msg`s instead (a replay at 8× also arrives 8× faster than its recorded intervals say).

## 5. Persistence
- **Profile** `%APPDATA%\Stringline\profiles\{area_id}.json` — corridors, location names, distances, categories, priorities.
- **Session** `Documents\Stringline\sessions\{area_id}-{yyyyMMdd-HHmm}.stringline` (SQLite): tables `events`, `plan_commands`, `disruptions`, `notes`, `train_links`, `meta`. Working plan is rebuilt by replaying commands over the WTT.
- **Recordings** `.sslog` — JSON Lines, format version 1:
  - line 1, header: `{"sslog":1,"app":"Stringline 0.1.0","created":"…Z","host":"localhost","port":51515,"topics":[…]}`. The sim's identity is not known yet when the header is written; it is in the first `clock_msg`.
  - frames: `{"t":"2026-10-18T09:12:03.123Z","topic":"/topic/TRAIN_MVT_ALL_TOC","sim":15720,"body":"…"}`. `t` is wall-clock UTC to the millisecond; `sim` is the latest `clock_msg` clock seen so far (absent before the first one), for seeking; `body` is the exact body text, or `body64` (base64) if it is not valid UTF-8.
  - connection changes: `{"t":"…","conn":"connecting|connected|disconnected","detail":"…","headers":{…}}` (CONNECTED headers on `connected`).
  - Readers skip malformed lines (e.g. a truncated last line) and count them; unknown properties are ignored.

## 6. Packages
| Purpose | Package |
|---|---|
| MVVM | CommunityToolkit.Mvvm |
| Rendering | SkiaSharp, SkiaSharp.Views.WPF |
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
