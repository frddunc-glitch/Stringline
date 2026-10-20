# Progress log

## 2026-10-18 — Design
- Design documents 00–07 written. No code yet.
- Next: Session S1 (repo skeleton and core types).

## 2026-10-18 — S1: Repo skeleton and core types

### Done
- Git repo initialised; remote `origin` = https://github.com/frddunc-glitch/Stringline (branch `main`).
- `Stringline.sln` with every project from `02-architecture.md` §1: Core, Gateway, Timetable, Network, Planning, Persistence, Graph, Tools (all `net10.0`) and App (`net10.0-windows`, WPF, placeholder window). Project references follow the data flow (Network → Timetable, Planning → Network, Graph → Planning, etc.).
- `Directory.Build.props` (nullable, warnings as errors, `LangVersion` latest, `AnalysisLevel` latest-recommended, code style enforced in build), `Directory.Packages.props` (central package management, all planned packages pinned), `.editorconfig`, `.gitattributes`, `global.json` (SDK 10.0.100, roll forward to latest feature).
- `Stringline.Portable.slnf` — the solution minus App, for Linux builds.
- GitHub Actions `ci.yml`: Windows builds and tests the full solution; Ubuntu builds and tests the portable filter. Release config, `-warnaserror`, TRX results uploaded.
- `Stringline.Core`:
  - `SimTime` — int seconds; `Day`/`SecondOfDay`/`Hour`/`Minute`/`Second` with floor-day semantics for negative values; arithmetic (`+ int`, `- int`, `time - time → int`), comparisons; `ISpanFormattable`/`ISpanParsable`. Formats `T` `HH:mm:ss` (default), `t` `HH:mm`, `H` `HH:mm½`, each with a `+N`/`-N` day marker when not on day 0. Parses `H:mm`, `HH:mm`, `HH:mm:ss`, `HHmm`, `HHmmss`, a `½`/`H` half-minute suffix, hours ≥ 24, and a day suffix.
  - `Tiploc`, `Headcode` (`IsStandard`, `ClassDigit`), `TrainId` — validated, `TryCreate`, `IsEmpty` for `default`.
  - `Stringline.Core.Json`: `LenientJson` (`TryGet…`/`Get…` helpers working on the current `Utf8JsonReader` token, for the streaming parser in S2), converters for `bool`, `bool?`, `int`, `int?`, `long`, `string` (opt-in per property), `SimTime` (seconds, numeric string or clock text) and the identifier types (also usable as dictionary keys). `LenientJsonOptions.CreateForGateway()`.
- `stringline` CLI (Tools) wired up with System.CommandLine 2.0; no subcommands yet.
- Tests: `Stringline.Core.Tests` (210, about 98% line and 96% branch coverage of Core) and `Stringline.Architecture.Tests` (9: every non-App assembly targets plain `net10.0`, references no WPF/WinForms, and no non-App csproj sets `UseWPF`/`-windows`).
- Verified: `dotnet build -warnaserror` + `dotnet test` green on Windows (full solution), and on Linux in the `mcr.microsoft.com/dotnet/sdk:10.0` container (portable filter).

### Decisions
- **FluentAssertions pinned to 7.2.x** — 8.x is under a commercial licence (Xceed). 7.x is Apache 2.0 and enough for us.
- **xUnit v2** (2.9.3) with VSTest, to match `Verify.Xunit`. Moving to xUnit v3 later is a mechanical change.
- **Identifiers are normalised** (trimmed, upper-cased) for `Tiploc` and `Headcode`, so live and WTT values compare equal regardless of case. `TrainId` is opaque and case-sensitive.
- **Lenient booleans** accept `true`/`false` (any case, native or string) and `0`/`1` (number or string). Other integers, such as Delphi-style `-1`, are rejected so we notice them in recordings rather than guessing.
- **Lenient integers** accept integral decimals (`"12.0"`, `1e3`) but not fractions or thousands separators.
- `SimTime` parsing does **not** accept bare seconds (`"16250"`) because they clash with `HHmm`; the JSON converter handles numeric seconds before trying clock text.
- `SimTime` `H` format **truncates** to the half minute (WTT convention); `t` truncates to the minute.
- Only the test projects that have content exist so far (Core, Architecture). Each later session adds `Stringline.<Project>.Tests` when it first needs one.
- SkiaSharp is referenced by Graph, but there is no native-asset package yet; S7 must add `SkiaSharp.NativeAssets.Linux.NoDependencies` for headless rendering on Linux.

### Next
- S2 — STOMP client, `MessageParser` (use `LenientJson` with `Utf8JsonReader`), `.sslog` recorder, `stringline record`. Then Fred captures recordings (see plan).
- S4 and S6 can start in parallel with S2–S3.

### Known issues
- None. CI is green on Windows and Ubuntu.

## 2026-10-18 — Licence
- Fred chose **GPL-3.0-or-later**: official GNU text in `LICENSE`, `PackageLicenseExpression` and `Copyright` in `Directory.Build.props`, licence and SimSig non-affiliation disclaimer in `README.md`, CI and licence badges. Decision recorded in `00-…md` §5.6; dependency rule added to `CLAUDE.md`.
- Every current dependency is MIT, BSD or Apache-2.0, so all are compatible with GPLv3.
- No per-file licence headers, to keep files short. The `LICENSE` file and README notice cover the repository.
- **For S18 (packaging):** GPLv3 §5(d) requires an interactive program to show "Appropriate Legal Notices". The About box must show the copyright, the no-warranty statement, the licence name and a link to the source. Also ship `LICENSE` with the published build.

## 2026-10-18 — S2: STOMP client, parser, recorder, `stringline record`

### Done
- **`Stringline.Core.Events`**: `GatewayEvent` records (`ClockEvent`, `TrainLocationEvent`, `TrainDelayEvent`, `TdStepEvent`, `SignallingStateEvent`, `UnknownEvent`) and `ISimEventSource`.
- **`Stringline.Gateway.Stomp`**: `StompFrame`, `StompFrameReader` (buffered, NUL or `content-length` bodies, heart-beat EOLs, CR LF, header unescaping, size limit, clean EOF vs mid-frame EOF) and `StompFrameWriter`.
- **`GatewayClient`**: CONNECT → CONNECTED (timeout) → SUBSCRIBE, `idrequest` on connect, TD snapshot on connect when subscribed, `ReadAllAsync` with reconnect and back-off (1 s → 30 s), `SendAsync(GatewayCommand)`, `ConnectionChanged` event, DISCONNECT on cancel. ERROR in reply to CONNECT → `GatewayConnectException`, not retried. Details in `03-…md` §7.
- **`GatewayCommands`**: `Utf8JsonWriter` builders for idrequest, snapshot, ttsummary, TD step/cancel/interpose, route request (by ID or signal pair) and signal replace, with identifier validation.
- **`MessageParser`**: streaming `Utf8JsonReader`, one event per top-level property (handles duplicate `SG_MSG` keys in snapshots), lenient values, never throws: unknown types, missing fields, malformed JSON and invalid UTF-8 all become `UnknownEvent`s and are logged.
- **`LiveGatewaySource`** (`ISimEventSource` = client + parser).
- **`.sslog`**: `SslogWriter` (header, frames stamped with the latest sim clock, connection lines, base64 for non-UTF-8 bodies, flushed every second), `SslogReader` (skips and counts bad lines), `GatewayRecorder` + `RecordingStats` (counts per message kind, area, clock, speed).
- **`stringline record --host --port --user --out [--td] [--ttsummary] [--force] [--verbose]`**: the password comes from `STRINGLINE_PASSWORD` or a hidden prompt. Status every 10 s while connected; summary with per-kind rates on Ctrl+C.
- Tests: `Stringline.Gateway.Tests` (147, using an in-process `FakeStompServer`; Gateway coverage 91% line, 89% branch) and `Stringline.Tools.Tests` (6, running `record` end to end in-process). Totals: 372 tests, green on Windows (Debug and Release) and on Linux (`sdk:10.0` container, portable filter).

### Decisions
- **Password:** `--user` on the command line; password from `STRINGLINE_PASSWORD`, else a hidden console prompt. No `--password` flag (shell history). `GatewayClientOptions.ToString()` masks it. (Fred's choice.)
- **Logging:** libraries depend on `Microsoft.Extensions.Logging.Abstractions` (`ILogger<T>`, source-generated `[LoggerMessage]`); Tools uses Serilog via `Serilog.Extensions.Logging` + `Serilog.Sinks.Console`, writing to stderr. (Fred's choice.) New packages: MS.Extensions.Logging.Abstractions 10.0.13 (MIT), Serilog.Extensions.Logging 10.0.0 and Serilog.Sinks.Console 6.1.1 (Apache-2.0).
- **Events live in Core**, not Gateway, so Timetable (S5 matcher) can consume them without a Gateway reference.
- **`SignallingStateEvent`** keeps the object-specific fields as a text map with lenient `GetBoolean`/`GetInt32`, rather than one record per object type. Typed views can come in S13/S14 once recordings show the real field sets.
- **No heart-beats requested**, to keep the handshake as plain as possible. Revisit if recordings show idle disconnects.
- **CS1573** (partial `<param>` docs) is suppressed alongside CS1591.
- `UnknownEvent.Raw` holds the message's value JSON. The type is in `MessageType`.
- The fake STOMP server stays in the test project (linked into Tools.Tests). S3's `replay-server` needs its own pacing logic and can lift the session handling from it.

### Next
- 👤 Fred: make the recordings (checklist in `03-…md` §7) and answer §6.
- S3 — `ReplaySource` and `stringline replay-server`. Parser tests from the real recordings; update `03-…md` with verified facts.
- S4 and S6 can still run in parallel.

### Known issues
- Not yet run against a real SimSig. Everything is tested against the fake server only.
- On Windows, a refused connection to `localhost` takes about 4 s to fail, because Windows retries the SYN and tries IPv6 first. That's harmless, but it makes retries slower than the back-off suggests. Use `--host 127.0.0.1` if it matters.

## 2026-10-19 — S3: Replay source, replay server, parser checked against real recordings

### Done
- **Fred's recordings examined** (`tests/fixtures/waterloo-1x|4x|failure.sslog` + `waterloo 2010.WTT`; `local-fixtures/penrith-payware.sslog`). The S2 parser read every message with **zero unknown events**. The one new shape is the `ttsummary` reply, a top-level JSON array. All §6 questions in `03-…md` are answered except multiplayer. Highlights:
  - CONNECTED is `version:1.1`, `server:SimSig`, with no heart-beat, and nothing drops without heart-beats.
  - Every WTT timing point is reported. Entry points never appear as locations.
  - A new working keeps the uid; a divided portion gets a new one. SimSig sometimes suffixes headcodes with `-1`.
  - Flat-out speed reports `interval:1`.
  - Failures are not reported: the track circuit failure shows only as T1620 occupied for 32 min.
- **Core events:** `TimetableSummaryEvent` + `TrainSummary`/`TrainSummaryStatus` (the `ttsummary` reply); `ReplayRewoundEvent`; `ClockEvent.IsMaximumSpeed`.
- **`MessageParser`** turns a top-level array into one `TimetableSummaryEvent`. `RecordingStats` counts it and shows flat-out speed as "max speed" in `record`.
- **`SslogRecording`**: a whole recording in memory, with `FirstSim`/`LastSim`/`Duration` and `IndexOf(SimTime)`.
- **`ReplayTimeline`**: paces frames by their recorded wall-clock gaps × speed (∞ = flat out). Pause, resume, speed change and seek are thread-safe and take effect mid-wait. A forward seek fast-forwards; a backward seek rewinds and flags the frame. `TimeProvider`-based.
- **`ReplaySource : ISimEventSource`** = timeline + parser. It restamps events with replay time by default (`PreserveTimestamps` to keep recorded times) and yields `ReplayRewoundEvent` after a backward seek.
- **`ReplayServer`**, a fake SimSig on STOMP:
  - SimSig's CONNECTED headers, any login accepted.
  - Per-topic fan-out with `content-length`; per-client send queues, so it never waits for slow clients.
  - Waits for the first client to be ready (first SEND, or 1 s after the first SUBSCRIBE).
  - `StartAt` pre-roll before listening.
  - Answers `idrequest`, `snapshot` (rebuilt from the latest `SG_MSG` per object) and `ttsummary` from replayed state.
  - Logs and ignores other or malformed commands. RECEIPT on request. `FlushAsync`.
- **`stringline replay-server file.sslog [--speed 8] [--port] [--host] [--start 09:00] [--no-wait] [--exit-at-end] [--verbose]`**: prints a summary of the recording and status every 10 s. Keys: Space pause, `+`/`-` speed, Q quit.
- **Fix:** `SslogReader` leaked the file handle when the header was invalid.
- Smoke-tested by hand: `replay-server` → `record` round trip of `waterloo-1x.sslog` gave identical message counts per type, and the `--start` pre-roll answered `idrequest` with the right clock and served a rebuilt snapshot.
- **Tests:**
  - `RecordedFixtureTests`: exact counts per message type for each fixture, real examples of every type, and each §6 fact.
  - `ReplayTimelineTests` (`FakeTimeProvider`), `ReplaySourceTests`, and `ReplayServerTests` (real `GatewayClient` and raw STOMP sockets end to end).
  - `ReplayServerCommandTests`, plus new parser and sslog tests.
  - Totals: 444 (Core 210, Architecture 9, Gateway 202, Tools 23). Green on Windows (Debug and Release) and on Linux (`sdk:10.0` container, portable filter). Coverage: MessageParser 100%, ReplayTimeline 98%, ReplayServer 93%; Gateway overall 91% line, 87% branch.

### Decisions
- **Replay is paced by the recorded wall-clock time, not by sim time.** It reproduces the real arrival pattern, including pauses and speed changes in the recording. `--speed` multiplies on top.
- **Replayed events are stamped with the time they are replayed**, so a consumer measuring speed from clock arrivals sees the replay speed. Recorded bodies are not rewritten (`interval` still says the recorded speed). **S5's `SimClock` should therefore measure speed from successive `clock_msg` arrivals** and use `500/interval` only as a first guess. It has to anyway, because flat out reports `interval:1`.
- **Seeking forward fast-forwards** (hands out every skipped frame) rather than skipping, so downstream state is complete. Seeking back restarts and announces it with `ReplayRewoundEvent`; the replay server never sends skipped frames to clients.
- `ttsummary` gets a typed event (it is cheap and S5 may use it as a cross-check), but the WTT stays the plan source: the summary has no times or paths.
- `SignallingStateEvent` stays untyped: the recorded field sets are now documented in `03-…md` §3.5 for S13/S14.
- New test package **Microsoft.Extensions.TimeProvider.Testing 10.9.0** (MIT).

### Next
- S4 (WTT import) and S6 (corridors) — `tests/fixtures/waterloo 2010.WTT` is ready. S5 (live state and matching) can now use `ReplaySource` with the Waterloo fixtures: the WTT has 1 768 trains; the 4× recording has 128 uids.
- For S5: strip `-1` headcode suffixes when matching, follow new workings by uid, and expect divided portions to appear as new uids that start with `depart`.

### Known issues
- Multiplayer behaviour (§6.9) is still unverified.
- Frame, crossing and AHB `SG_MSG` objects are still unverified (Waterloo has none).
- The replay server answers `idrequest` with nothing until the recording's first `clock_msg` (like a sim not yet started, but SimSig would send its placeholder clock).

## 2026-10-19 — S4: WTT import

### Done
- **Format studied first:** Waterloo (free, in git), Penrith (payware, local), and then Fred's whole timetable folder `C:\Users\Public\Documents\SimSig\Timetables`: 284 `.wtt` files from 100+ sims, SimSig 3.247–5.36, 188 901 trains. What was verified is now in `04-…md` §1.
- **New fixture:** `tests/fixtures/llangollen 2012 B.WTT` (Llangollen is a free sim; Fred confirmed). Single line with crossing loops, 24 trains, with joins, engine detaches and next workings. That answers question 4 below for S6/S11.
- **`Stringline.Core.Timetables`:** `WorkingTimetable` (header, categories, trains, rules; `Find`/`Get` by `TrainId`, `WithHeadcode`, `FindCategory`), `TimetableHeader`, `TrainCategory`, `DwellTimes` (`Or` fallback), `Train` (start kind, entry/seed point, performance, origin/destination, dwell, points, `FormedFrom`; derived `Origin`, `Destination`, `FirstTime`, `LastTime`, `NextWorking`), `TimingPoint` (times, pass, platform, line, path, direction, three allowances in seconds, dwell, request stop, set-down, new category, activities), `TrainActivity` + `ActivityKind` (codes 0–10), `TimetableRule` + `RuleKind` (codes 0–14).
- **`Stringline.Timetable.WttReader`:** reads a `.wtt` zip or bare `SavedTimetable.xml`, streamed one train at a time. Tolerant throughout:
  - A bad value, timing point or train becomes an `ImportWarning` (Info/Warning/Error), and the rest of the file is still read.
  - Damaged XML keeps the trains before the damage.
  - Only a non-timetable throws `WttFormatException`.
  - Unknown elements are counted by path; known-but-ignored ones are listed in `Known`.
  - Also handles both boolean styles, half-minute allowances, `$template` descriptions, category fallbacks, duplicate category IDs, and Latin-1 fallback.
- **`TrainLinker`:** assigns `TrainId`s and resolves activities and rules to trains. Several candidates → same location first, then nearest in time. Sets `FormedFrom`.
- **`stringline wtt-dump file.wtt [--summary] [--train X …] [--warnings] [--verbose]`:**
  - Summary: sim, version, times, start kinds, counts, activities, rules, unknown elements, warnings.
  - A per-train table, and the timing points of the chosen trains.
  - Exit 1 if the file is damaged, 2 if unreadable.
- **Tests:**
  - `Stringline.Timetable.Tests` (86): synthetic XML for every tolerant path, the linker, and description templates.
  - Fixture tests with exact counts for Waterloo and Llangollen in CI; Penrith when `local-fixtures/` has it.
  - A **corpus test** that imports every `.wtt` under `STRINGLINE_WTT_CORPUS` (default Fred's folder; skipped when absent). It requires no skipped trains, no unknown elements and no errors: all 284 pass, in about 12 s.
  - Also `TimetableModelTests` (Core, 16) and `WttDumpCommandTests` (Tools, 4).
  - Totals: 550 (Core 226, Architecture 9, Gateway 202, Timetable 86, Tools 27). Green on Windows (Debug and Release) and on Linux (`sdk:10.0` container, portable filter, Release). Timetable coverage is 92% line, 90% branch.

### Decisions
- **`TrainId`** = WTT UID if present, else headcode; repeats get `#2`, `#3`… in file order (Fred's choice). Stable across re-imports of the same file; it changes only if trains with the same UID/headcode are reordered.
- **Allowances are half minutes**, taken in the section after the point. Fred first chose "minutes", based on SimsigTtTools, but the corpus settles it: run times with allowance N are N × 30 s longer in thousands of sections, for N = 1…8. Stored as seconds on `TimingPoint`.
- **Headcode suffixes are real:** `2O03` and `2O03-1` are separate WTT workings (160 suffixed headcodes at Waterloo). This corrects the S3 note: **S5 must match headcodes exactly**, not strip `-1`. `03-…md` §6.4 is updated.
- **UIDs are not unique** (195 of 284 files reuse them for alternative paths), so links resolve by location and time, and S5's matcher must expect several candidates per UID.
- **Domain model lives in Core** (`Stringline.Core.Timetables`), as `04-…md` §2 says, so Network/Planning/Graph can use it without referencing the reader. Category colour and priority are not in the WTT. They become a profile `CategoryStyle` in S6, so `TrainCategory` holds only WTT data.
- WTT booleans accept `-1` (Delphi `True`) with their own parser. The gateway's lenient JSON booleans still reject `-1`, as decided in S1.
- An entry without `DepartTime` is normal (SimSig works it out) and is not warned about. Crew-change activities often name a crew number, so an unresolved one is not a warning either.
- The ignored elements are listed in `WttReader.Known` with reasons. Saved-game state (`Delay`, `Entry…`, `Shunt…`), decisions and caution speeds are ignored for now.
- A pass ignores any `ArrTime` (as SimSig does).

### Next
- S5 — `LiveState` and `TrainMatcher`. Use exact headcodes; follow `FormedFrom`/`NextWorking` for new workings (same live uid) and divided portions (new uid starting with `depart`). Expect several WTT trains per headcode/UID.
- S6 — corridors from `WorkingTimetable` (Waterloo, plus Llangollen for single line with loops). Allowances matter for min run times: subtract `AllowanceSeconds` of the point before when deriving section minimums.
- `wtt-dump` is the quickest way to inspect a timetable when writing S5/S6 fixtures.

### Known issues
- 99 activity links across the corpus name trains that are not in their WTT, so the link is lost. These look like genuine timetable errors (trains deleted in the editor); `wtt-dump --warnings` lists them per file.
- The description template fields (`$originTime`, `$originName`, `$destName`, `$destTime`, `$operator`, `$stock`) are inferred from the templates in the corpus, not from SimSig documentation. Unknown fields are kept as written.
- Rule `Time` is assumed to be seconds (Penrith 480–600, Waterloo 1500). That's plausible but unverified; S11 can check it against a recording.

## 2026-10-19 — S5: Live state and train matching

### Done
- **`Stringline.Core.Live`:**
  - `LiveTrain`: one sim uid, with its `Workings` (one per headcode: a next working keeps the uid) and all its `Actuals` (timing points and signals, each flagged `AtTimingPoint` and tagged with its working). It is immutable; `LiveState` replaces it on each change.
  - `LiveWorking` (`MatchStatus` Pending/Unmatched/Matched, `MatchMethod` HeadcodeAndTime/NextWorking/Formed/Manual, deviation, reason) and `ActualEvent`.
  - **`SimClock`** interpolates between `clock_msg`s from the events' `ReceivedAt`, and measures the speed from clock arrivals (500/interval is only the first guess; flat out holds until measured). It never runs past the end of the sim minute or the latest `train_location` time, which moves it on. Paused freezes it. Implausible measurements, such as a stale `idrequest` clock followed by a real tick, are ignored.
- **`Stringline.Timetable.Live.TrainMatcher`** is pure. It follows next workings and formed trains by uid, scores candidates with the exact headcode by mean |actual − booked|, and applies a 60 min window, a 60 s margin and a "decide after 3 timing points" rule. Penalties separate alternative paths and seeded/unseeded duplicates. The full algorithm is in `04-…md` §3.
- **`LiveState`** is the reducer: `Apply(GatewayEvent)` covers the clock (ignoring the pre-start placeholder clock 0), movements (with matching), TD berth map, `train_delay` hints per headcode, the latest `ttsummary`, and a reset on `ReplayRewoundEvent`.
  - Manual links: `Link(uid, headcode, train|null)` and `ClearLink`, keyed by (uid, headcode). They apply when the working appears, take the train from another working, and survive resets.
  - Also `FindByTrain(TrainId)`, `Summarise()` and `Version`.
- **`stringline match --wtt t.wtt --log x.sslog [--all] [--verbose]`** plays a recording flat out through `LiveState` and prints the match summary and a table of unmatched workings (`--all` for every one).
- **Results on Fred's recordings:** 100% matched. Waterloo 1× 35/35 workings (31 uids), 4× 205/205 (128 uids; 77 next workings), failure 39/39, and Penrith 9/9, where every train in the sim at 14:00 got its seeded `…S` version. Each match is checked to be consistent: same headcode, and mean deviation where the train calls under 20 min (worst 15 min, in the failure recording).
- **Tests:**
  - `SimClockTests` (Core, 18), `TrainMatcherTests` (29), `LiveStateTests` (31) and `LiveFixtureTests` (11, including one end to end through `ReplaySource`) in Timetable, and `MatchCommandTests` (Tools, 4).
  - Totals: 643 (Core 244, Architecture 9, Gateway 202, Timetable 157, Tools 31). Green on Windows (Debug and Release) and on Linux (`sdk:10.0` container, portable filter, Release).
  - Coverage: TrainMatcher 98% line / 93% branch, LiveState 98% / 95%, SimClock 100% / 93%.

### Decisions
- **Placement.** The live model and `SimClock` are in Core (`Stringline.Core.Live`), so Planning/Graph can read them. `TrainMatcher` and `LiveState` are in `Stringline.Timetable.Live`, next to the WTT they match against, as `02-…md` puts "train matching" in Timetable. Neither references Gateway.
- **A live train is a uid split into workings by headcode.** This is not one record per headcode. The graph can draw a uid's whole run and still attribute each stretch to its WTT train.
- **Matching is evidence-based and final.** A working is re-scored on each movement while `Pending`. Once `Matched` or `Unmatched`, it stays that way unless the user links by hand. Matches never flap.
- **New finding: SimSig reports timing locations outside the train's own schedule** (`03-…md` §6.2). The off-path penalty is therefore small (120 s) and only separates alternative paths.
- **An early `arrive` at an origin costs nothing.** Without this, the seeded trains at Waterloo 1× (sitting at WLOO from 04:30, booked away up to 2 h later) were unmatched.
- **Seeded vs unseeded duplicates** (Penrith) are told apart by whether the live train was first seen within 2 min of the session start.
- **Timetable.Tests references Gateway**, so the fixture tests parse recordings with the real parser. Core.Tests gains `Microsoft.Extensions.TimeProvider.Testing` (already used by Gateway.Tests).
- `SimClock` takes wall times from `ReceivedAt`. That is correct for live sources and for `ReplaySource`'s default restamping, but not with `PreserveTimestamps`.

### Next
- S6: corridors, profile, derived axis (independent of S5).
- S7: the graph scene builder can take actual layers from `LiveState`. Use `LiveTrain.ActualsOf(working)` with `AtTimingPoint`, and `FindByTrain` to go from a WTT train to its actuals. Unmatched workings have no `TrainId`, so draw them grey from their actuals.
- S10's `PlanningEngine` should own a `LiveState` and use `Version` to decide when to publish a snapshot.
- S16: persist `LiveState.ManualLinks` in `train_links`, and apply them before replaying session events.

### Known issues
- A working that is `Unmatched` on its first movement (e.g. the window is exceeded because the train is over an hour late) stays unmatched until linked by hand. A train over 60 min late at its first report needs a manual link.
- "Present at start" means "first seen within 2 min of the first clock *this session*". On connecting to a sim already running, trains present then do not tell us which seeded duplicates SimSig used. For Penrith-style timetables the time scores then decide alone, which is usually but not always right.
- `train_delay` and `ttsummary` are stored but not used for matching yet.
- Not run against a live SimSig. Everything was tested against recordings.

## 2026-10-19 — S6: Corridors, profile, derived axis

### Done
- **`Stringline.Network`** (first code in the project). The full algorithm is in `04-…md` §4–5.
  - **Model:** `Corridor`, `CorridorLocation`, `Section` (tracks, headway, min run overall and per direction, samples), `TrackDef`, `TrackDirection`, `AxisMode`. `Corridor` has `IndexOf`/`PositionOf`/`FindSection`, and `DirectionOf(locations)` for S7.
  - **`LocationGraph`:** location sequences per train, with skip edges expanded (WLOO–CJML → WLOO–VAUX–CJML), each train counted once per edge and through move, and out-and-back moves counted separately.
  - **`CorridorGenerator`:** greedy candidates from the busiest uncovered edge, extended along through traffic. Branches follow their trains onto the main line. Each is oriented Up end first from SimSig's Down flags, falling back to the busier end. Short, contained and near-duplicate candidates are dropped; "via X" names tell apart corridors with the same ends.
  - **`CorridorMeasurement`:** per-location calls, stops, platforms, starts and terminations; per-section minimum run times less allowances, with runs past skipped locations shared between sections; single-line hint.
  - **`CorridorBuilder`** (`Create`, `Refresh` keeping user settings) and **`CorridorAxis`** (derived, or km with interpolation and extrapolation).
  - **Profiles:** `StringlineProfile` (corridors, location names, category styles; `Create`, `Refresh`), `CategoryStyle` with defaults by headcode class, `Colour`, `LineStyle`, `ProfileJson` (with validation → `ProfileFormatException`) and `ProfileStore` (`{area_id}.json` under `%APPDATA%\Stringline\profiles`, atomic save).
- **`stringline corridors t.wtt [--top] [--min-trains] [--edges] [--profile out.json] [--force]`** prints each candidate as a table: axis position, platforms, calls/stops, run times each way, runs, opposing overlaps and single-line hint. It can write a starter profile.
- **Results:**
  - Waterloo: Main WLOO–WIMB (8 locations, 1 626 trains) and Windsor WLOO–BARNES (10, 1 156).
  - Llangollen: LLANGOLLEN–CARROG, every section hinted single line.
  - Penrith: BLAKTWN–VLYHGTS (14 locations).
  - Corpus: all 284 timetables give consistent corridors (1 265 in total), the slowest in 0.49 s.
- **Tests:**
  - New `Stringline.Network.Tests` (103): model, axis, measurement, graph, generator, builder, profile, JSON and store. It also has fixture tests for Waterloo, Llangollen and Penrith (local) and a corpus test (skipped without the folder).
  - `CorridorsCommandTests` in Tools (10).
  - Totals: 756 (Core 244, Architecture 9, Gateway 202, Timetable 157, Network 103, Tools 41). Green on Windows (Debug) and on Linux (`sdk:10.0` container, portable filter, Release).
  - Network coverage: 99.9% line, 97% branch.

### Decisions
- **Fred's choices:** corridors run **Up end first** (Down trains ascend), and **a branch follows its trains through** onto the main line, so corridors may overlap. Waterloo's Windsor corridor starts at WLOO.
- **Edge weight = distinct trains**, not runs. Counting runs made looping shunts (e.g. cowlairs `CWLRSWJ–CWLRSW534`) the "main" corridor.
- **Skip-edge chains must agree with each other.** Without that check, contradictory chains expanded each other without end: Tyneside took over 4 min; it now takes milliseconds.
- **Runs past skipped corridor locations are shared** in proportion to direct minimums. Otherwise Waterloo's QTRDBAT–CJML had 4 timed runs out of 1 100 trains.
- **Single line is a hint only.** Sections start double line with a 3 min headway; opposing overlaps of a minute or less are treated as booking rounding.
- **Display names live once per TIPLOC** in `StringlineProfile.LocationNames`, not on `CorridorLocation` (the doc sketch had them there). Renaming a location then renames it in every corridor.
- **Category defaults** come from the headcode class most trains in the category use. All are solid lines, because dashes are reserved for the forecast layer. Colours are a Stringline `Colour` struct, so Network does not depend on SkiaSharp.
- `AxisPosition` is stored in the profile, but always recomputed by `Refresh`. User-editable: order, distances, platforms (if set), tracks, headways, axis mode.
- **No Verify snapshots yet.** Plain assertions were clearer for tables and JSON; S7 introduces Verify for PNGs.

### Next
- S7: `GraphSceneBuilder` takes a `Corridor` from the profile. Use `PositionOf` for each point, and `DirectionOf` to pick Up/Down. Skip points not on the corridor; a train's straight line between non-adjacent corridor locations is right, because it was booked past them. Label locations with `profile.DisplayName(tiploc)`. `render-png --corridor main` can build a profile on the fly with `StringlineProfile.Create(tt)` when none is saved.
- S8: load the profile with `ProfileStore.Load(area_id)`; if there is none, call `StringlineProfile.Create(tt)` and save it. Call `Refresh(tt)` when a WTT is (re-)imported.
- S11: section run times per direction are `Section.MinRunFor(direction)`. They are pass-to-pass minimums less allowances, so starting and stopping need their own allowance.

### Known issues
- Corridor generation is heuristic. Complex areas (London Bridge, Sydney, Kings Cross) give plausible but overlapping candidates such as "KNGX – ROYSTON via BOWESPK". The profile is the source of truth once edited, and there is no corridor editor until the app has one.
- The single-line hint cannot see layouts: it also fires on lightly used double-line sections where opposing trains happen never to overlap.
- The minimum run time comes from the WTT, so it is only as good as the timetable. A section where every train stops at both ends has a slower minimum than a pass-to-pass run would.
- No CSV import for location names yet (mentioned in `04-…md` §1); names are edited in the profile JSON for now.

## 2026-10-19 — S7: Graph scene and Skia renderer (headless)

### Done
- **`Stringline.Graph`** (first code; still portable `net10.0`). The design is in `05-…md` §7.
  - **`GraphViewport`:** time range across, axis range down, surface size and margins. It has `ToScreen`/`ToWorld`, `PanPixels`/`PanTime`, `ZoomTime` (keeps the time under the anchor; held between 5 min and 2 days), `Follow(now)`, `Around(now, span, fraction)` and `Resize`. The Up end is at the top.
  - **`GraphScene`** (per corridor, in world coordinates: sim seconds × axis 0..1): `GraphLocation`s, `GraphPath`s (layer, train, headcode, colour, vertices, unmatched flag, direction) and `Now`.
  - **`GraphSceneBuilder`:** pure, `GraphSceneRequest` → scene. It draws the WTT layer from `Train.Points` and the actual layer from `LiveTrain` workings (`AtTimingPoint` actuals only), coloured by the profile's category styles; unmatched and pending workings are grey. Out-and-back trips off the corridor split the path. The plan and forecast layers exist in the model for S10/S11.
  - **`TimeGrid`:** tick spacing from zoom (5/30 min at a 3 h view), with hour lines heaviest and day-marked labels after midnight.
  - **`SkiaGraphRenderer`:**
    - Draws, in order: background, past shading, grid, location lines, paths by layer (culled by bounds), now line, headcode labels (rotated along the line, greedy overlap check), border, time labels, location labels (bold for stations, TIPLOC under a display name) and title.
    - Paints are pooled by colour and layer. `RenderPng` renders headless.
    - `GraphTheme` holds the line-style table from `05-…md` §2. `GraphFonts` loads the embedded Noto Sans.
- **`stringline render-png --wtt t.wtt [--log x.sslog] [--profile p.json] [--corridor main] [--at 07:00] [--hours 3] [--now-at 0.33] [--width] [--height] [--no-wtt] out.png`:**
  - Plays the recording up to `--at`, through the first event after it.
  - The profile is generated from the WTT unless given.
  - `--at` defaults to the end of the recording, or to the timetable start without one.
- **Tests:**
  - New `Stringline.Graph.Tests` (57): viewport, grid, scene builder, renderer pixels, clipping, the snapshot comparer itself, and **Verify PNG snapshots**: the small synthetic corridor, Waterloo main at 07:00 from the 4× recording, and Llangollen single line.
  - `RenderPngCommandTests` in Tools (14).
  - Totals: 827 (Core 244, Architecture 9, Gateway 202, Timetable 157, Network 103, Graph 57, Tools 55). Green on Windows (Debug) and on Linux (`sdk:10.0` container, portable filter, Release); `render-png` there gives the same output summary as on Windows.
  - Graph coverage: 98% line, 93% branch.

### Decisions
- **Fred's choices:**
  - **Bundle an OFL font** (Noto Sans Regular and Bold, `src/Stringline.Graph/Fonts/`, with `OFL.txt`; noted in the README).
  - **`--at` is the now time**: the window is around it, with now ⅓ across (`--now-at`).
  - **The Up end is at the top.**
- **PNG snapshots compare with a tolerance** (`PngComparer`). Text goes through DirectWrite on Windows and FreeType on Linux, so even with the same font about 0.5–2.5% of pixels differ slightly. Measured: none differ by more than 64 in any channel. The comparer allows 0.01% of pixels beyond 64, and a test checks that it still catches a one-minute move of the now line and an extra train.
  - It decodes copies of the streams, because `SKBitmap.Decode(Stream)` closes Verify's stream. Without the copy, a real mismatch showed up as `ObjectDisposedException`.
- **The scene builder takes a `GraphSceneRequest`, not a `PlanSnapshot`.** S10 can build the request from its snapshot, or add an overload.
- **The scene is whole-day and viewport-independent.** The renderer culls by path bounds; Waterloo builds 1 700+ paths. Path caching and the label grid are left for S9, as planned.
- **SkiaSharp 4 deprecates `SKPath.MoveTo/LineTo`**, so paths are built with `SKPathBuilder`. `SkiaSharp.NativeAssets.Linux.NoDependencies` (MIT) is added for Linux, CI and cloud sessions.
- **The Graph tests reference Timetable and Gateway** to play real recordings. The Graph library itself still references only Core, Network and Planning.

### Next
- S8: host `SkiaGraphRenderer` in an `SKElement`. Keep one renderer per surface. Rebuild the scene with `GraphSceneBuilder` when `LiveState.Version` changes, and change only the `GraphViewport` on pan/zoom (`PanPixels`, `ZoomTime(factor, mouseX)`, `Follow`). Use `SimClock.Now` for `Now`.
- S9: cache `SKPath`s per path and layer; replace the label list with a grid; add hit testing, the vertical orientation (the viewport is horizontal only so far), delay-band colouring and PNG export (`RenderPng` already exists).

### Known issues
- Dense corridors at a 3 h view are busy: actual lines are 2.5 px as the style table says, and labels sit on top of lines. S9's label placement and delay-band colours should help.
- `ActualEvent` has no "at a signal between timing points" position, so actual lines run straight between timing points.
- Titles use the WTT name, so Waterloo shows "SX May 2010 (Updated - Loader) v1.0.1"; the profile's `Name` can be edited.

## 2026-10-19 — S8: WPF shell and live graph (MVP milestone)

### Done
- **The app runs and shows a live graph of plan vs actual.** It was checked by hand by replaying `waterloo-4x.sslog` at 8× with its WTT: corridor tabs, WTT and actual layers, now line, train list with delays, transport bar and status bar (`Sim 06:04:36 ×32`) all work. The window's design is in `05-…md` §8.
- **Portable parts (tested on Linux):**
  - Core: **`SimClockReading`**, a value copy of `SimClock`, so the UI interpolates the now line without touching the clock. `SimClock.SecondsAt` uses it.
  - Planning (first code):
    - **`LiveEngine`**, the single writer. It takes a queue of events plus `SetTimetable`/`Clear`, and publishes immutable **`LiveSnapshot`**s at most every 100 ms, coalescing events in between.
    - It keeps the session's events, so a timetable loaded mid-session rebuilds the state and matches as if it had been loaded from the start.
    - Failing events and handlers are logged and skipped.
    - **`TrainDelays`**: delay at a timing point, and the current delay of a live train.
  - Graph:
    - **`GraphNavigator`**: pan, zoom and follow, independent of the UI toolkit.
    - `GraphViewport.ZoomAxis`, `PanAxisPixels` and `WithFullAxis`, for distance zoom.
    - **`GraphHitTester`**: 5 px tolerance, preferring actual over WTT; a linear scan for now.
    - **`GraphHover`** builds the tooltip text. **`GraphSelection`**: the renderer draws the selected train with a halo and dims the rest. `GraphPath` gains `LiveUid`/`Working`, and there is `GraphSceneRequest.For(snapshot)`.
  - Timetable: **`WttCatalogue`** reads only a `.wtt`'s sim ID and name, and finds a sim's timetables in a folder tree.
  - Gateway: **`SessionRecorder`** records a live connection, can be switched on and off at any time, and is thread-safe. `LiveGatewaySource.MessageParsed` feeds it.
- **App (`Stringline.App`):**
  - Generic host with DI, and Serilog writing rolling files to `%LOCALAPPDATA%\Stringline\logs`.
  - Settings in `%APPDATA%\Stringline\settings.json`, with the password encrypted by DPAPI (`DpapiSecretProtector`).
  - `SimSession` runs a live or replay session. `TimetableLoader` loads the WTT plus its profile (load+refresh, or create, then save).
  - View models: `MainViewModel`, `TrainListViewModel` (rows updated in place), `TransportViewModel`, and the Connect and Replay dialog view models.
  - Views: the main window, Connect, Replay and a list window (import warnings), and the `GraphView` control (`SKElement`, DPI-scaled, mouse, tooltip popup, click to select).
  - Command line: `--wtt`, `--replay`, `--speed`, `--start`.
  - A minimal About box with the GPL notice.
- **Tests:**
  - New `Stringline.Planning.Tests` (22): engine, coalescing with `FakeTimeProvider`, rebuild on timetable, rewind, a Waterloo fixture with the WTT loaded halfway, and delays.
  - New `Stringline.App.Tests` (33, Windows only). They run view models on a test "UI thread" with its own synchronisation context, and cover: replaying Waterloo end to end into a scene, hover and selection; timetable suggestions; the mismatch warning; a live connection to `FakeStompServer` with recording and remembered settings; a refused login; DPAPI settings; status text; the train list; dialogs; transport.
  - Additions to Graph (+40: navigator, axis zoom, hit testing, hover, selection rendering), Gateway (+5 recorder), Timetable (+6 catalogue) and Core (+3).
  - Totals: 936 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 22, Graph 97, Tools 55, App 33). Green on Windows, Debug and Release. The Linux container was not run this session; the new portable code has no Windows dependencies.

### Decisions
- **Fred's choices:**
  - **Timetables are picked and suggested.** A path in the dialogs (remembered per sim), plus suggestions from SimSig's Timetables folder once the area is known.
  - **Replay runs in-process** with a transport bar.
  - **Rec is in S8.**
  - **"Remember password" is an opt-in checkbox.**
- **The App targets `net10.0-windows10.0.19041.0`**, because SkiaSharp.Views.WPF 4.x ships only that build (Windows 10 2004 or later). The architecture test now accepts any `net10.0-windows…` for the App. Its transitive OpenTK/GLWpfControl packages (for `SKGLElement`) are MIT. DPAPI (`ProtectedData`) is part of the Windows Desktop framework, so no package is added.
- **The App sets `InvariantGlobalization=false`.** With the repository-wide `true`, WPF throws on XAML `Language` lookups. The libraries stay invariant.
- **`LiveSnapshot` is a class, not a record,** so `with` cannot copy its lazy lookup caches stale.
- **The engine stands in for S10's `PlanningEngine`.** The UI already reads snapshots only, so S10 can extend `LiveSnapshot` into `PlanSnapshot` (or wrap it) without changing the shell's threading.
- **The WTT layer draws as soon as a timetable is loaded**, even with no session. The now line then sits at the timetable's start.
- **Follow-mode zoom keeps the now line in place.** Panning in time turns follow off; panning only along the corridor does not.
- **Delay minutes round towards zero** (59 s late is right time). They are shown as `+3`/`0`/`−1` in the list, and as "3 min late" in tooltips.
- **View models never touch WPF types**, so they are tested without a window (`IUiDispatcher`, `IDialogService`).

### Next
- S9: path caching, a hit-test grid (replacing `GraphHitTester`'s linear scan; same API), a label grid, the benchmark, colouring by delay band (`TrainDelays` is ready), the vertical orientation, and PNG export (`RenderPng` takes a selection now).
- S10: turn `LiveEngine` into `PlanningEngine`, with commands and undo, publishing a `PlanSnapshot` that carries what `LiveSnapshot` does.
- S12: the train detail panel goes in the right-hand column. `GraphSelection` and `TrainListViewModel.Selected` already keep in step.

### Known issues
- **Not yet run against a live SimSig.** Live mode is tested against `FakeStompServer`, and replay against the real recordings. 👤 Fred: please try Connect against a running sim (with and without a timetable chosen), and Rec.
- Running the app here wrote `settings.json` and `profiles\waterloo.json` under Fred's `%APPDATA%\Stringline` (the replay check). They are harmless; delete them for a clean first run.
- Seeking a replay forward fast-forwards every frame at once, so the speed shown may jump for a minute until it is measured again.
- The tooltip and train list use the WTT time for delays; there is no forecast until S11.
- Hit testing scans every path in view; that is fine for Waterloo, and S9 adds the grid.
- The UI language is the WPF default (en-US) for number and date formatting in controls. Everything we format ourselves is invariant `HH:mm:ss`.

## 2026-10-19 — S9: Graph performance and polish

### Done
- **Performance (GR-9), in `Stringline.Graph`:**
  - **`ProjectedPathCache`.** Paths are projected to `SKPath`s once per zoom and drawn translated, so panning, following and scrolling along the corridor never re-project. A new scene keeps every path whose key, colours and vertices are unchanged, and reports where drawing changed (for a train that moved on, only its last stretch).
  - **`PathTileCache`.** The unselected lines are kept as 256 px tiles at device resolution.
    - A pan blits the tiles, drawing only those new to the view. A live update redraws only the tiles under the changed stretches. A zoom, resize, turn, DPI change or new selection drops them all.
    - The frame that changes the zoom draws directly; the next frame builds the tiles in one pass. The projection is aligned so tiles land on whole device pixels.
    - `RenderPng` draws directly. `SkiaGraphRenderer.CacheTiles` can turn the tiles off.
  - **`LabelGrid`.** Labels are checked for overlap against a grid of 48 px cells, not a list.
  - **`GraphHitIndex`.** A 16 px grid over one scene and viewport, with the same answers as `GraphHitTester`'s linear scan (checked point by point on Waterloo in both orientations). The app's graph keeps one until the scene or view changes.
  - `GraphPath` bounds are worked out once, not with LINQ every frame.
- **Benchmark:** new `benchmarks/Stringline.Benchmarks` (BenchmarkDotNet 0.15.8, MIT). It uses a synthetic corridor of 30 locations with 500 (or 1 000) trains over 06:00–12:00, now at 08:00, and actuals coloured by delay band per segment (the most runs per path), drawn at 1920×1080 on a CPU surface. `--quick` gives rough timings in seconds. Results are below.
- **Vertical orientation (GR-1).**
  - `GraphViewport.Orientation`: time down, with the Up end on the left (Fred). The transforms work in time and axis pixels (`TimeToPixel`, `AxisToPixel`, `PanTimePixels`, `TimePixelOf`…), replacing the X/Y-named members. So the scene, caches, hit testing and navigator gestures work in either orientation.
  - The renderer swaps the grid, past shading and now line. Times are labelled down the left, and location names along the top, rotated to read upwards, with the TIPLOC beside them. `GraphNavigator.SetOrientation` keeps the ranges.
- **Delay-band colouring (GR-7).**
  - `GraphColourMode` and `DelayBands` (≤ 0 / 1–5 / 6–15 / > 15 min, rounded towards zero).
  - `GraphSceneBuilder` colours each actual segment by the band at the point it arrives at (Fred: per segment). An unbooked point carries the last delay; the start takes the first known delay. Matched trains with none are grey.
  - `GraphPath.SegmentColours` is drawn as single-colour runs, and labels take their segment's colour.
- **PNG export (GR-10).** `RenderPng(…, scale)`. In the app, Session ▸ Export graph as PNG… (Ctrl+E), or at 2×, saves the current view (Fred). That is the time and corridor range, orientation, colours, selection and now line, through a save dialog that starts at `Documents\Stringline\exports\{area}-{corridor}-{HHmm}.png`, then at the folder last used.
- **App.** A toolbar Colour box (Category/Delay) and ⇅ Vertical toggle, also in the View menu, remembered in the settings (`ColourByDelay`, `VerticalGraph`, `LastExportFolder`). The train list's swatch follows the colour mode. `AppPaths.Exports` and `IDialogService.PickSaveFile` are added.
- **CLI.** `render-png --vertical --colour category|delay`.
- **Tests:**
  - Graph +46 (143):
    - orientation: transforms, round trips, turning, navigator gestures, hit testing, rendering, and a vertical snapshot;
    - delay bands: thresholds, the carry rules, scene colours and drawn segment colours;
    - caches: no re-projection while panning, re-projection on zoom, only changed paths re-projected, cached frames matching fresh renders, PNG scale, label grid, the hit index against the linear scan;
    - tiles: matching direct drawing at 1×, 1.25× and 1.5× with a selection, reuse while panning, redraws only under moved trains, and no stale tiles after a direct render;
    - Waterloo: a vertical, delay-coloured snapshot, and the hit index against the linear scan in both orientations.
  - Tools +1 (vertical and delay render), App +4 (options remembered, recolouring, export, failed export).
  - Totals: 987 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 22, Graph 143, Tools 56, App 37). Green on Windows (Release; the Debug build could not copy into the App's `bin` while a running Stringline held it) and on Linux (`sdk:10.0` container, portable filter, Release: 950).

### Benchmark (500 trains / 6 h, 1920×1080, CPU raster, this PC)
BenchmarkDotNet 0.15.8, short job, i7-9750H, .NET 10.0.11. Mean frame times in ms:

| | 500 trains, horizontal | 500, vertical | 1 000, horizontal | 1 000, vertical |
|---|---:|---:|---:|---:|
| **Pan frame (tiles)** | **13.7** (median 13.6) | **9.5** (median 7.1) | **9.8** | **7.8** |
| Pan frame, drawn directly (as S8 did) | 36.2 | 34.0 | 58.3 | 57.7 |
| Zoom frame (draws directly) | 39.7 | 37.4 | 62.1 | 69.1 |
| Live update, unchanged scene | 7.4 | 6.6 | 10.0 | 10.7 |
| Hit index build | 8.2 | 10.6 | 25.9 | 30.1 |
| 100 hover tests, grid | 0.08 | 0.12 | 0.17 | 0.28 |
| 100 hover tests, linear scan | 3.7 | 4.1 | 7.0 | 8.5 |

So GR-9 (< 33 ms while panning) holds with a wide margin, even at 1 000 trains. Short-job errors are large on a laptop. `--quick` gives pan frames of 5–9 ms on the same machine.

### Decisions
- **Fred's choices:** vertical has time down and the Up end on the left; delay bands are per segment; export saves the current view through a save dialog, at 1× or 2×.
- **Tiles, not only path caching.** Measured, anti-aliased rasterisation is nearly all of a direct frame: pre-stroked outlines fill as slowly as stroking, and lines drawn one segment at a time are no faster. Projection caching alone saved about 3 ms of 30–37 ms. Panning from tiles takes about 5–7 ms.
- **Tiles use the platform's pixel format** (`SKImageInfo.PlatformColorType`). RGBA tiles on Windows (BGRA) took Skia's slow blitter path and cost three times as much.
- **A zoom frame draws directly,** because building tiles costs about two direct frames. The next frame at that zoom builds them. So continuous zooming runs at direct speed (about 30–35 ms at this size), and a pan or follow tick after it pays once.
- **Pixel snapping.** The projection is aligned so tiles sit on whole device pixels. After a pan by a fraction of a pixel, tiled lines can be up to half a pixel from the exact position. The grid, now line, labels and selected train are not snapped.
- **The hit index is built lazily** on the first hover after the scene or view changes: about 8–12 ms for 500 trains, then 0.003 ms a test.
- **View settings are booleans in `settings.json`** (`ColourByDelay`, `VerticalGraph`), so the JSON stays readable without an enum converter.
- **`render-png` keeps its default look;** `--vertical` and `--colour delay` are opt-in.
- **The benchmark project is in the portable filter**, so the Linux build compiles it. It is not a test project.

### Next
- S10: `PlanningEngine` with commands and undo, publishing a `PlanSnapshot`; the plan layer draws through the same caches.
- S11/S12: forecast lines (dashed) are drawn by the same renderer; the dash pattern is phase-stable under translation.

### Known issues
- **Not yet tried in the app by hand this session for feel** (it builds and the view models are tested). 👤 Fred: please try panning, the Vertical toggle, Colour → Delay and Export on a live or replayed sim, especially on a high-DPI screen.
- Tile memory: up to 1.5× the tiles in view (at least 64), 256 KB each; about 20 MB at 1080p and about 50 MB at 4K at 200%.
- In the vertical view, long location names are cut off at the top margin (170 px).
- A zoom frame still costs a direct draw (about 37–40 ms for 500 trains at 1920×1080 here, 60–70 ms for 1 000); only panning, following and live updates are well within the budget.
- PDF export (GR-10) is not built; PNG only.

## 2026-10-19 — S10: Working plan, commands, undo/redo

### Done
- **Working plan (`Stringline.Planning`).** The rules are written up in `06-…md` §1.
  - `WorkingPlan` is immutable: the WTT with commands applied in order. It holds `TrainPlan`s made of `PlannedPoint`s (WTT trains in file order, then clones), disruptions and manual live links. It offers `Apply`/`ApplyAll`, `Modified`, `NewCloneId` and `FromTimetable`.
  - `WorkingPlanBuilder` is the mutable side that commands work through. It clamps a change that shortens a run below the minimum, or moves a departure before the arrival. Each clamp gives a `PlanWarning` with the message shown to the user. A command that fails or throws is rolled back.
  - `MinRunTable` holds the fastest booked run per location pair (either way, less allowances).
  - `Disruption` (Block / SpeedRestriction) covers runs between calls at its two ends. A speed restriction lengthens the minimum while it is in force.
- **Commands:** every one in `06-…md` §1 — `ShiftTrain`, `SetDwell`, `ChangePlatform`, `ChangeLine`, `SkipStop`, `AddStop`, `TerminateAt`, `CancelTrain`, `ReinstateTrain`, `CloneTrain`, `AddDisruption`/`EditDisruption`/`RemoveDisruption`, `LinkLiveTrain` and `AcceptSuggestion` (all-or-nothing) — plus `UnlinkLiveTrain`.
  - `IPlanCommand.Describe(plan)` gives the history text, e.g. "Shift 1A01 by +5 min from B".
  - `PlanCommandJson` serialises commands with a `type` discriminator, ready for S16's session store.
- **`PlanningEngine`** replaces `LiveEngine`, and `PlanSnapshot` replaces `LiveSnapshot`; the files were renamed and the App now uses the new names.
  - `Execute(command)` returns a task with the `PlanCommandResult`. `Undo()`/`Redo()` return whether anything changed.
  - A plan change publishes straight away. The snapshot carries `Plan`, `History` (`Done`/`Undone`, `UndoDescription`…) and `PlanVersion`.
  - Undo replays the remaining history over the WTT. Reloading the same timetable keeps the history; a different one, or `Clear`, starts a new one.
  - Links: the live state is rebuilt with the plan's links when they change, so a manual link undoes.
- **Graph:** `GraphSceneRequest.Plan` and `ShowPlan`. Trains whose plan differs from the WTT, and clones, draw on the plan layer (1.5 px solid). Cancelled trains are not drawn there. A train terminated short draws to its new end.
- **Tests:**
  - Planning +71 (93):
    - every command, with its errors and clamps (cascade, dwell, origin, speed restrictions in and out of force, blocks);
    - descriptions;
    - the minimum run table;
    - disruption coverage;
    - JSON round trips of every command;
    - determinism: incremental equals all-at-once, and JSON-replayed equals direct;
    - the engine: execute, refuse, a throwing command, warnings, undo/redo, redo cleared, reload kept or dropped, a different timetable, clear, links that rematch and undo;
    - a Waterloo fixture that re-plans every train, is deterministic, takes under 2 s (it is milliseconds) and never leaves a shortened run below the minimum.
  - Graph +2: the plan layer, and termination.
  - Totals: 1 060 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 93, Graph 145, Tools 56, App 37). Green on Windows (Debug) and on Linux (`sdk:10.0` container, portable filter, Release: 1 023; the tree is streamed in with tar, skipping `.vs`, `bin` and `obj`).

### Decisions
- **Fred's choices:**
  - The minimum run time is the **fastest WTT run per location pair**.
  - The **disruption model and its commands are in S10**, and speed restrictions affect clamping.
  - **`LinkLiveTrain` is undoable**: it goes through the plan history.
- **Edits never add or remove points.** Indices are WTT point indices. Skip/add stop convert between stop and pass, and termination keeps the points it no longer runs to, so commands stay valid and plans compare point by point.
- **Only runs and dwells that a command shortens are clamped**, so tight WTT bookings elsewhere are not "fixed" as a side effect. A clamped point pushes the next only if that run is then too short too, so a late train uses up its slack.
- **A pair with no direct booked run has no minimum** (only order is kept), rather than a 60 s default as for corridor sections. That pair is only reachable across untimed points, where the steps are summed.
- **Disruptions do not re-plan existing trains** when added or edited. Their effect on running belongs to the forecaster (S11/S13). They only lengthen the minimum for later edits.
- **`IPlanCommand.Describe` takes the plan**, so the history names trains by headcode (a change to `02-…md`'s sketch).
- **A no-op is refused** ("This makes no change to the plan."), so it never enters the history.
- **Clone IDs** are `{headcode}#c{n}`, chosen by the caller and kept in the command for replay.
- **`LiveEngine.Rebuild` used to carry links over from the live state; the plan now owns them.** `LiveState.Link` is only called by the engine.

### Next
- S11: the forecaster reads `PlanSnapshot.Plan` (`RunningPoints`, `Disruptions`, `MinRuns`) and adds the forecast layer.
- S12: the detail panel and drag edits build commands for `PlanningEngine.Execute`. Show `PlanCommandResult.Warnings`/`Error` in the info bar. Bind Ctrl+Z/Ctrl+Y to `Undo`/`Redo`, and list `History` as the edit history. Give cancelled trains their style (thin grey with crosses).
- S16: store `PlanCommandJson` per history entry, and restore it by replaying.

### Known issues
- **No UI for editing yet** (S12). The App only renames to the new engine; nothing in the shell sends commands.
- Clones are never matched to live trains: the matcher only knows the WTT, and `LinkLiveTrain` accepts only WTT trains.
- The hover tooltip still shows WTT times only; it does not show the plan's.
- An undo replays the whole history over the WTT. For Waterloo with a command per train this takes milliseconds, so there is no caching.

## 2026-10-19 — S11: Forecaster

### Done
- **Forecaster (`Stringline.Planning`).** The rules are written up in `06-…md` §2.
  - `Forecaster.Run(ForecastRequest)` → `Forecast`, which holds a `TrainForecast` per train: `ForecastPoint`s with a basis (Actual, Arrived, Forecast, None) and delay, a status (NotStarted, Running, Finished, Unavailable), `PossiblyStopped` and `KnockOnSeconds`.
  - It is one discrete-event pass, with departures queued by time, priority and plan order. It covers steps 1–3 (start from the latest actual or the planned start plus the `train_delay` hint; minimum runs with recovery; dwells; no time before now), step 4 (the interaction pass, behind `ForecastOptions.Interaction`) and step 5 (possibly stopped after 5 min).
  - It also handles trains formed from another (next working, divide, detach), speed restrictions and blocks.
  - `ForecastNetwork` is built from the profile's corridor sections, with their headway and single-line setting. Shared sections are merged, and the tracks each way are widened to what the WTT uses at once. It also carries the category priorities.
  - `MinRunTable.Along(points, from, to, depart, disruptions, fallback)` is now shared by the plan builder and the forecaster.
- **Engine.** `PlanningEngine.SetForecast(network, options)`. The engine re-forecasts when the live state, the plan or the settings change, and logs a slow forecast (over 100 ms) or failed trains. `PlanSnapshot` gains `Forecast` and `EntryDelays`; `LiveState.EntryDelays` is new.
- **Graph.**
  - `GraphSceneRequest.Forecast` and `ShowForecast` add the forecast layer: `Forecast.Drawn`, dashed. Each running train's line starts at its latest actual, and a dwell it is in draws as a step. The line is in the category colour, or by forecast delay band when colouring by delay.
  - The tooltip adds Forecast (times and delay at the location) and Notes (knock-on, possibly stopped).
- **App.**
  - A Forecast check box on the toolbar and View → "Show forecast".
  - View → "Forecast knock-on from other trains" (remembered, on by default).
  - The network is built when a timetable loads.
- **Tools:** `render-png` draws the forecast with `--log`. `--no-forecast` leaves it out; `--no-knock-on` forecasts without the interaction pass.
- **Benchmarks:** `ForecastBenchmarks` (Waterloo, half way through the 4× recording). `--quick` prints them.
- **Tests:**
  - Planning +47 (140):
    - the forecaster's rules, one by one (lateness and recovery, dwell, early running, now and possibly stopped, signals, unseen trains, the session start, entry delays, cancellation and termination, speed restriction, block, formed trains, finished trains, determinism, the snapshot request);
    - **golden scenarios** worked out by hand: a double line with a loop (late fast behind the stopper; late stopper recovering), a single line with a crossing loop (on time, rounding overlap, a late train holding the opposing one, an unbooked meeting), and a junction (a shared section, priority ties), plus tracks side by side and a formed-train regression;
    - the network (shared sections, tracks, routes and shares, `TracksNeeded`, priorities);
    - the engine (forecast in snapshots, settings, entry delays);
    - Waterloo fixtures: the properties of `06-…md` §6 over all three recordings (about 450 000 forecast points: never before now, never leaving a stop early, never faster than the minimum), determinism, the on-time WTT making little knock-on, and the time taken.
  - Graph +8 (153): the forecast layer, hiding, delay colours, tooltips and a PNG snapshot.
  - Tools +1 (57), App +1 (38).
  - Totals: 1 117 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 140, Graph 153, Tools 57, App 38). Green on Windows (Debug) and on Linux (`sdk:10.0` container, portable filter, Release: 1 079).
- **Measured** (`benchmarks/ --quick`, Release): Waterloo, 1 768 trains / 7 834 points, takes 10 ms independent and 23 ms with the interaction pass (both passes).

### Decisions
- **Fred's choices:**
  - **Forecast lines** go on running trains, and on trains not started whose forecast is off the plan (a late entry or knock-on). On-time future trains show only their plan.
  - The **interaction pass is on by default**, using the profile's corridor sections (headway, single line). Runs off every corridor are not checked.
  - The **minimum dwell** is 30 s when the WTT gives none. A train is **possibly stopped** after 5 min unreported past its forecast.
  - **No horizon:** every train is forecast to the end of its run.
- **The WTT never makes knock-on.** A pair booked through a section in that order is held no further apart than its booked spacing (or the headway, if less). Opposing trains booked to overlap by up to a minute keep it. Without this, Llangollen's booked crossings and Waterloo's close running would all show delay.
- **Tracks each way are at least what the WTT uses at once.** A pair within a minute at either end, or overtaking, needs two tracks. Without this, Waterloo's four-track main line, which the profile calls double, held trains for minutes.
- **"Deviates" means a minute or more**, as railway delay minutes go. Otherwise nearly every future train was drawn, because the forecast's shortest dwells differ from the WTT's half-minute bookings by seconds: Waterloo drew 563 lines, against 126 with the threshold.
- **The first train not started with a headcode takes its `train_delay`.** The sim gives entry delays by headcode only.
- **A block with a track** applies when the train's planned line out of its departure point has that name. S13 will refine this with the corridor's tracks.
- **Forecast times are the plan's running points** (`TrainPlan.RunningPoints`), so a train terminated short is forecast to its new end. Cancelled trains, and trains not seen whose plan ended before the session started, are left out.
- **The engine forecasts on every published change** rather than lazily, so S14/S15 can read the same forecast. It is cheap enough: 23 ms for Waterloo in Release.
- **Found while testing on Waterloo:** a train formed from a one-point train (an ECS arrival, such as 5L45 → 2L45) was started only after every other train. It then queued behind the whole day and showed up to 18 min of false knock-on. Fixed, with a regression test.

### Next
- S12: the detail panel's Forecast column comes from `PlanSnapshot.Forecast.Find(train)`, with points by plan index. Drag previews can call `Forecaster.Run` on the previewed plan.
- S13: draw disruptions, and refine track-specific blocks. The forecaster already honours speed restrictions and blocks.
- S14: conflicts on the plan without the interaction pass (`ForecastOptions { Interaction = false }`), with knock-on from `TrainForecast.KnockOnSeconds`.

### Known issues
- **Not yet tried in the app by hand.** 👤 Fred: please replay or run a sim and check that the dashed lines and the knock-on look sensible. Try toggling View → "Forecast knock-on from other trains", especially on a single-line sim (Llangollen) and on Waterloo.
- The interaction pass does not model platforms or junction conflicts inside a location; it only covers sections. The order is first come, first served; S15 suggests regulation.
- When a section's tracks are not set in the profile, they are inferred from the WTT, which can be too generous: a WTT booked tighter than the headway counts as two tracks.
- Waterloo's on-time WTT still gives 8 trains small knock-ons (4 s to 3 min), where a booking is tighter than the forecast's dwell or the headway.
- In a Debug build, Waterloo takes 80–110 ms with both passes, and the engine logs forecasts over 100 ms. Release is about 23 ms.
- The train list does not show forecast delays or "possibly stopped" yet.

## 2026-10-19 — S12: Interactive re-planning UI

### Done
- **Portable parts (tested on Linux):**
  - Planning:
    - **`ResetTrain`** ("Reset to WTT"): puts a WTT train back as booked. It is in `PlanCommandJson` as `resetTrain`. A clone, or a train already as booked, is refused.
    - **`TrainSchedule`** builds the detail panel's rows (`ScheduleRow`) from a snapshot: WTT, plan, actual, forecast and delay per timed point, with edits marked and points not run after a short termination.
  - Graph:
    - **`PlanDrag`**: drag edits of one train (`05-…md` §3). The body retimes the rest of the run from the next unreached point. Alt+drag retimes from a vertex. The right end of a stop changes the dwell.
      - Time snaps to 15 s (1 s fine). Nothing is planned before now or the latest report, and a retime earlier stops at the minimum run into the first point moved. Each limit is explained in the status text.
      - Each move previews the command on the snapshot's plan (`PlanDragPreview`).
    - `GraphPath.Points` gives each vertex's plan point index; `IsDwellEnd` and `PointAt` use it.
    - **Cancelled trains** are drawn on the plan layer, 1 px grey with crosses at both ends (`IsCancelled`).
    - `GraphSceneBuilder.PlanPaths` builds plan paths for a ghost. `SkiaGraphRenderer.Render(…, ghosts)` draws ghost paths over everything, uncached.
- **App:**
  - **Right-hand column:** the train detail panel (`TrainDetailViewModel`) on top and the edit history (`EditHistoryViewModel`) below, with a splitter between them.
  - **Detail panel:**
    - The heading gives the state, route and description.
    - The schedule grid has editable Plan arr, Plan dep, Plat and Line cells. A stop's arrival is shown over its departure, and passes are italic.
    - Buttons: Reset to WTT, Cancel train/Reinstate, Clone…, Link….
  - **Graph:**
    - Drag edits show a ghost; Esc cancels a drag.
    - The right-click menu offers Hold here…, Add stop…, Skip stop, Change platform…, Change line…, Terminate here, Cancel/Reinstate, Reset to WTT, Clone as ad-hoc…, Link to WTT train… and Match automatically.
    - Releasing Alt after an Alt+drag is swallowed, so the window menu does not open.
  - **Undo and redo:**
    - Ctrl+Z, Ctrl+Y and Ctrl+Shift+Z.
    - A Plan menu that names the edit to undo or redo, plus the selected-train actions.
    - Undo and Redo buttons on the history panel.
  - **Results:**
    - A refused edit is shown in the info bar as an error.
    - Clamp warnings are shown in the info bar as a warning.
    - The status bar shows what a drag does, and the last edit made.
  - **Dialogs:** a generic `PromptDialog`/`PromptViewModel` (fields with optional choices, validated on OK), behind `IDialogService.Prompt`.
  - **Input:** `PlanInput` reads times on the nearest day and minutes. It also formats times compactly: `07:30`, `07:30½` or `07:30:15`.
  - `TrainListViewModel.FormatDelay` is shared with the detail panel.
  - The visibility converter understands booleans.
  - The window now opens at 1600×860.
- **Tests:**
  - Planning +6 (146): reset, its refusals, its JSON and description, and the schedule (as booked, edits and termination, running with actuals and forecast, clones).
  - Graph +17 (170, since one S10 test changed to expect cancellations drawn): point indices, cancelled style and crosses, ghosts drawn without changing the scene, and drags. The drag tests cover the body, snapping, a view panning under the drag, now, a running train's next point and minimum run, Alt from a vertex, a point already passed, dwell and its clamps, a dwell before the arrival, the middle of a stop, actual lines, and cancelled and finished trains.
  - App +21 (59), on Waterloo:
    - the schedule;
    - a cell edit, then undo, redo and reset;
    - a bad time and a refused edit;
    - a drag with its ghost, then commit, then Esc;
    - the menu: cancel, reinstate, hold and clone;
    - a cancelled or invalid prompt;
    - linking a live train from a replay;
    - time and minute parsing.
  - Totals: 1 161 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 146, Graph 170, Tools 57, App 59). Green on Windows (Debug). On Linux (`sdk:10.0` container, portable filter, Release: 1 102) one run had a single failure in S10's `PlanningEnginePlanTests.Clear_starts_a_new_session_with_the_booked_plan`, which this session did not touch; five reruns of the Planning tests there were green (see Known issues).
- **Checked in the running app** (replaying `waterloo-4x.sslog` with its WTT; a train selected through UI Automation and the window screenshotted): the layout, the detail panel with live actuals and forecasts, and the history panel. It starts and closes cleanly with no errors logged. The drag and right-click were not tried by hand; see Known issues.

### Decisions
- **Fred's choices:**
  - A body drag of a running train retimes it **from its next point**, and **nothing is planned before now** or before its latest report.
  - **Only the selected train is dragged**; pressing anything else pans.
  - The **detail panel sits on top of the history**.
  - The **ghost is the plan path only**, with no live re-forecast while dragging.
- **A drag stops cleanly at a limit** rather than sending a command that the builder then clamps. An earlier retime is held at max(now, latest report), and at the minimum run into the first point moved, both rounded up to the snap grid. So it makes a plain shift with no warnings. Clamps later in the run (a longer dwell eating into a tight run) are still the builder's, and the preview shows their warnings.
- **Only WTT and plan lines are dragged.** Actual and forecast lines pan, so dragging the dashes cannot be mistaken for moving the forecast.
- **The ghost stays until the redrawn scene shows the edit**, so the line does not flick back for a frame.
- **Plat and Line are separate columns** (the design said "Plat/Line"), so each can be edited.
- **A plan cell edit:**
  - An arrival edit retimes from that point (`ShiftTrain`).
  - A departure edit sets the dwell at a call, and retimes from that point at an origin or a pass.
  - A typed time takes the day nearest the old time unless a day is given (`00:10+1`).
- **Hold here… asks for a departure time.** It suggests the later of the plan and now, plus 2 min. At a pass it becomes `AddStop`.
- **Clone… defaults to the same headcode and +0 min**, and selects the copy so it can be dragged into place.
- **Cancelled trains are drawn grey with crosses** on the plan layer, over their faint WTT booking. The S10 test that expected them not to be drawn was changed.

### Next
- S13: disruptions (right-click on the background is free for them; the menu returns nothing there yet), B-drag blocks, and notes. Draw disruptions on the graph; the forecaster already honours them.
- S14: conflicts can be shown in the right-hand column, as a tab beside the history or below it. A drag preview could check conflicts for the dragged train against the preview plan.
- S16: the history panel lists `PlanHistory`; the session store keeps it as `PlanCommandJson`, which now includes `resetTrain`.

### Known issues
- **Drag and right-click have not been tried by hand.** 👤 Fred: on a replay, select a train and try:
  - dragging its line, Alt+drag from a timing point, and dragging the right end of a stop (look at the ghost and the status bar);
  - Esc during a drag;
  - the right-click menu, and Ctrl+Z / Ctrl+Y;
  - double-clicking a Plan cell, typing a time and pressing Enter.
  - Also check that releasing Alt after an Alt+drag does not open the menu bar. The key-up is swallowed, but that is untested.
- Edits act on the plan only. SimSig is not told anything (by design; S19 is the opt-in assist).
- The detail panel's columns fit at the default 490 px. A narrower panel scrolls sideways.
- Ad-hoc clones are not matched to live trains (as in S10).
- **An intermittent failure:** `Clear_starts_a_new_session_with_the_booked_plan` (S10) failed once in the Linux container and passed on five reruns. It is probably a timing race in the test's snapshot wait; not investigated.
- In Debug builds the engine still logs "Forecast … took 100+ ms" for Waterloo; Release is about 23 ms (S11).

## 2026-10-19 — S13: Disruptions and notes

### Done
- **Portable parts (tested on Linux):**
  - Planning:
    - **Tracks** (`Disruption.Direction`, `Disruption.AppliesTo`): a block or speed restriction is on all lines, on a corridor track (Up, Down or Single, applying to trains running that way), or on a WTT line code (applying to trains planned on it out of the point the run starts from). The forecaster's blocks, the forecaster's restrictions and the plan's clamping (`MinRunTable.Along`) all use it. Restrictions ignored their track before.
    - **Notes** (DIS-3): `PlanNote` (location, time, text, at most 500 characters), with `AddNote`, `EditNote` and `RemoveNote` commands (`addNote`, `editNote`, `removeNote` in `PlanCommandJson`). `WorkingPlan.Notes`, `FindNote`, `NewNoteId` (`N1`…), and `NewDisruptionId` (`B1`…, `SR1`…).
    - **`DisruptionTracks.For`**: the track choices for a span of a corridor: all lines, the profile's section tracks, then WTT line codes used there, most used first (`TrackChoice`).
    - Disruptions describe their track in the history ("Add block A–C (Down) …").
  - Graph (`GraphAnnotations`):
    - `GraphScene.Disruptions` and `Notes`, built from the plan for the corridor (`GraphSceneRequest.ShowDisruptions`).
    - Drawing: a block is a red hatched rectangle, a restriction an amber band, each labelled in its corner and drawn under the trains; a note is a ring with its first line in a yellow box, over the trains. A B+drag draft is drawn dashed with its times (`Render(…, draft)`).
    - Hit tests (`NoteAt`, `DisruptionAt`), tooltips (`Describe`), `NearestLocation`, and `SpanBetween`: a drag or click widened out to the location lines around it and to 5-minute steps.
- **App:**
  - **Right-click:** a note gives Edit note… and Remove note; the background gives the disruption there (Edit…, Remove), then Add block/possession here…, Add speed restriction here… and Add note here….
  - **B+drag** draws a block (a dashed draft and the span in the status bar); release opens the block dialog filled in, and Esc drops it.
  - **Dialogs** (`PromptDialog`): for a block or restriction, the ends, track, times, running (factor and added minutes) and note; for a note, the location, time and text. Every field is checked.
  - **Tooltips** over notes and disruptions.
  - **Disruptions** check box on the toolbar, and View → "Show disruptions and notes".
- **Fixed:** the intermittent `Clear_starts_a_new_session_with_the_booked_plan` (S12 known issue). It now failed about 1 run in 5 on Windows. The test helper checked `_engine.Current` and then read it again, so it could return a newer snapshot than the one it checked. It now reads it once; `PlanningEngineForecastTests` had the same pattern and is fixed too. The engine was not at fault.
- **Tests:**
  - Planning +15 (161): blocks on all lines, on corridor tracks by direction, on line codes (and the line out of the run's start), at one location; a restriction on a track; track validation and wording; new IDs; notes added, changed, removed, refused and leaving the forecast alone; track choices on double and single lines; JSON for the new commands and a disruption with a direction.
  - Graph +14 (184): the scene's disruptions and notes (off-corridor ones left out), hiding, labels, rectangles in both orientations, one-location bands, hit tests, spans (between sections, at a point, on a line, vertical), tooltips, tinting, the draft, and a PNG snapshot.
  - App +5 (64), on Waterloo: a block from the menu with its dialog, then edited, removed and the removal undone; a restriction refused for not slowing trains, then added on a typed line code; B+drag with its draft, dialog and Esc; notes added, edited, refused when empty and removed; hiding.
  - Totals: 1 195 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 161, Graph 184, Tools 57, App 64). Green on Windows (Debug); the engine tests passed 15 runs in a row. Green on Linux (`sdk:10.0` container, portable filter, Release: 1 131), including the new PNG snapshot.
- **Tried in the app:** it starts with a Waterloo replay and closes cleanly, with nothing logged above Information. The menus, B+drag and dialogs were driven only by the view-model tests, not by hand; see Known issues.

### Decisions
- **Fred's choices:**
  - **Notes are plan commands**, so they undo, show in the history and will be saved with the commands (S16).
  - **Tracks are "direction + line codes"**: all lines, the corridor's tracks by direction, or WTT line codes.
  - **B+drag opens the block dialog filled in.** Disruptions and notes are changed through right-click → Edit…, not by dragging them.
- **A disruption's From is the corridor's Up end.** The dialog swaps the ends if they are entered the other way, so a track's Down and Up are the corridor's own. A disruption made elsewhere (JSON, S15) with the ends reversed reads Down as To → From.
- **A direction at one location applies to every train**, since a run into or out of one place has no way along the disruption.
- **A typed track that is not offered is a WTT line code.** An empty one is all lines.
- **"Here" means the section around the click, from the click's time rounded down to 5 min, for an hour**; a note goes on the nearest location at the nearest minute.
- **A speed restriction must slow trains**: a factor over 1 or added minutes. The default is ×1.5.
- **Only disruptions with both ends on the corridor are drawn**; one partly on a corridor is not shown there. Notes need their location on the corridor.
- **Disruptions draw under the trains and notes over them**, so blocks never hide a train and notes stay readable. Neither dims when a train is selected.

### Next
- S14: a disruption conflict is a plan run through a block in force on its track: `Disruption.AppliesTo` with the time check in `Forecaster.AfterBlocks`. Conflicts can sit in the right-hand column next to the history.
- S16: save notes and disruptions through the command history (they are plan commands); `02-…md`'s separate `disruptions` and `notes` tables are not needed unless wanted for review queries.

### Known issues
- **Not tried by hand.** 👤 Fred: on a replay, please try:
  - right-clicking the background and adding a block, a speed restriction and a note;
  - holding B and dragging a block (does B+drag feel right?), and Esc while drawing;
  - right-clicking a block or note to edit or remove it, then Ctrl+Z;
  - hovering a block or note for its tooltip;
  - whether the hatching and amber are too strong or too faint on your screen, and whether the forecast holds trains as you expect for a block on one track.
- The prompt has one-line text boxes, so a note is typed on one line (a note loaded with several lines shows its first line on the graph and all of it in the tooltip).
- Track choices are worked out from the ends shown when the dialog opens; if the ends are changed, the list is not refreshed (a typed line code still works).
- The Disruptions toggle is not remembered between runs.

## 2026-10-19 — S14: Conflict detection

### Done
- **Portable parts (tested on Linux):**
  - Planning:
    - **`ConflictDetector`** (`ConflictRequest` → `ConflictSet` of `Conflict`s) finds every kind in `06-…md` §3: headway, overtake, single line, platform, disruption and unrealistic run time. The rules are in `06-…md` §3.
    - **`SectionOccupancy<T>`**: the interaction pass's section rules (headway, single line, booked spacing, tracks side by side), moved out of `Forecaster` unchanged so the detector uses exactly the same ones. It also names the train in the way (`Blocker`).
    - **`Forecast.Independent`**: the forecast without the interaction pass, kept alongside the full one. This costs nothing extra; it was already computed.
    - **`TrainForecast.BlockHolds`**: where a block held a train (`BlockHold`).
    - **`MinRunTable.Along(…, ceiling)`**: a train's own booked run caps its minimum.
    - **`PlanningEngine`** detects conflicts after every forecast (it logs if detection fails or is slow) and publishes `PlanSnapshot.Conflicts`. `PlanningEngineOptions.Conflicts` holds the `ConflictOptions`.
  - Graph:
    - `GraphConflicts` and `GraphConflict`: the scene, the highlight and ring geometry, the hit test and the tooltip.
    - `GraphScene.Conflicts`, `GraphSceneRequest.Conflicts`/`ShowConflicts`, `GraphSelection.ConflictId` and `GraphTheme.Conflict`.
    - Highlights draw under the trains and rings over them.
    - `GraphViewport.CentredOnAxis` and `GraphNavigator.CentreOn(time, axis)`.
  - Tools: `render-png` marks conflicts with `--log` (`--no-conflicts` turns them off) and counts them in its output line. The quick benchmark times detection.
- **App:**
  - The **Conflicts tab** beside History (`ConflictsViewModel`). Its rows are updated in place, so the selection and scroll position survive live updates.
  - **Focus**: picking a row or clicking a ring selects train A, switches corridor if needed, stops following and centres the view on the conflict.
  - The ring tooltip comes before the train's, and clicking a ring picks the conflict.
  - A Conflicts box on the toolbar, and View → "Show conflicts".
  - "N conflicts" in the status bar.
- **Fixed** (an S8 bug): closing the window after a replay had finished logged "Cannot … Close … while a Window is closing". The dispose finished at once, so `Close()` ran while the window was still closing. The second close is now deferred.
- **Tests:**
  - Planning +23 (184):
    - Golden scenarios for every kind:
      - loop: on time, headway, overtake, booked spacing allowed, a train already on its way, merged sections, the threshold;
      - single line: crossing as booked, and head-on;
      - junction;
      - platform: trains in turn, a late occupant, a platform change, a next working formed in the platform;
      - block: a hold, and a re-plan that clears it;
      - run time: a restriction added later, and a booked run never unrealistic;
      - determinism.
    - Waterloo fixtures: the on-time day, and two recordings checked half way and at the end. Nothing is in the past, nothing is under 30 s, and each run takes under 2 s.
    - The engine publishing conflicts, and undoing them.
  - Graph +11 (195): the scene, hiding, building from a snapshot, highlights in both orientations, the one-location band, the hit test, the tooltip, colours, selection, and a PNG snapshot.
  - App +6 (70):
    - row wording, in-place sync with selection, and selecting by ID;
    - on Waterloo: a block's conflicts listed, picked, ringed, clicked, hidden and undone;
    - focusing a conflict on another corridor, and one on no corridor.
  - Totals: 1 235 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 103, Planning 184, Graph 195, Tools 57, App 70).
    - Green on Windows (Debug).
    - Green on Linux (`sdk:10.0` container, portable filter, Release: 1 165), including the new PNG snapshot.
    - The new App panel test passed 5 runs in a row.
- **Measured** (Release, `--quick`, Waterloo): independent forecast 11.8 ms, with knock-on 25.1 ms, conflicts 9.1 ms. Forecast and conflicts together take about 34 ms, inside FC-2's 50 ms budget.
- **Tried in the app** (the Waterloo 4× replay played to its end, and screenshotted):
  - The Conflicts tab showed 25 conflicts: headways and platforms at Waterloo, where late trains bunch at the now line.
  - The status bar showed "25 conflicts", and the rings were on the graph.
  - It closed cleanly, with nothing logged above Information.
  - Picking rows and clicking rings were driven only by the view-model tests, not by hand.

### Decisions
- **Fred's choices:**
  - Conflicts are checked on the **forecast without knock-on** (actuals, then the plan with lateness). The full forecast's knock-on is shown with each one.
  - **WTT-booked spacing is allowed**, as in the forecaster.
  - The panel is a **tab beside History**.
  - The list shows **upcoming conflicts on all corridors**; the graph marks those on the corridor shown.
  - Clashes **under 30 s** are hidden.
  - The **platform re-occupation margin is 1 minute**.
- **The train that gives way is the one that enters the section or platform second.** A train already on its way into a section, or already at the platform, is never reported as A, because nothing can be re-planned there. So no conflict lies wholly in the past.
- **A disruption conflict is a forecast block hold**, not "the plan runs through a block". A train late enough to miss the block has no conflict, and holding the train in the plan until the block ends clears it.
- **A pair that clashes section after section is one conflict** (within 10 min), so a late train catching another does not fill the list.
- **Platforms:**
  - A train formed from another holds the platform from the other's arrival, booked and forecast.
  - Trains associated there by any WTT activity are not checked against each other.
  - Without the formed-from rule, Waterloo's on-time day gave 115 false platform clashes; with it, 1.
- **A platform highlight shows the wait**: from when A is due until it could get in. Showing the occupant's whole stay could reach back hours.
- **Severity in the list is in whole minutes, rounded up** ("3 min"), or in seconds under a minute. The exact delay is in the tooltip.

### Next
- **S14a: profile editor** (added to the plan after S14). The app had no way to change a sim's profile (COR-2 and COR-5 were never scheduled), and a timetable with no sim ID got an unsaved profile that could not be changed even by hand. See `07-…md`.
- S15: suggestions per conflict.
  - `Conflict.PointIndex` is A's point where it meets the clash (or the point it runs from into it): the natural place to hold. `SeveritySeconds` is the hold.
  - Re-run the forecaster and `ConflictDetector` for each candidate. That takes about 35 ms on Waterloo in Release; limiting it to a time horizon, or to the trains involved, would make several candidates cheap.
  - The panel's rows can expand to show suggestions.
- The S12 idea still stands: a drag preview could check the dragged train's conflicts against the preview plan.

### Known issues
- **Not tried by hand.** 👤 Fred: on a replay, please:
  - pick rows in the Conflicts tab and click rings on the graph. Does the focus (corridor, centring, selection) feel right?
  - judge whether the rings and red highlights are too strong or too faint, and whether the list is useful or too long on a busy sim;
  - add a block and check its conflicts, then hold a train in the plan and check that its conflict goes.
- **Conflicts bunch at the now line.** On a late-running sim, trains whose forecast is floored to now bunch together, so several headway and platform conflicts appear "at now". They are real (the trains are late), but they stay listed until the trains move.
- **Platform occupation is partly unchecked.** A terminating train with no next working occupies its platform only at the instant it arrives. A train that starts from a platform (not formed from another) occupies it only from its departure. So the time a stabled train stands in a platform is not checked.
- The platform margin and threshold are fixed defaults in `ConflictOptions`. There is no setting for them in the app yet (S18's settings page).
- Picking the row that is already selected does not focus the graph again; pick another row, or click its ring.

## 2026-10-19 — S14a: Profile editor

### Done
- **Portable parts (tested on Linux):**
  - Network (`Stringline.Network.Profiles`):
    - **`ProfileEdits`**: immutable edits on a `StringlineProfile` for everything COR-2 and COR-5 ask for: the sim's name and ID, location names, corridors (add a candidate or a new one, remove, rename, reorder), locations (insert, remove, move, distance, platforms, axis mode), sections (tracks, headway) and category styles. Each is checked, and a refused one throws `ProfileEditException` with its reason. Also `HandTuned` and `RegenerateCorridors`. The rules are in `04-…md` §5.
    - **`ProfileValidator`**: `ProfileJson`'s checks moved out unchanged, so a file and an edit keep the same rules.
    - **`TrackLayouts`**: the track presets, and tracks as text (`Up Fast:Up, Down:Down, Relief:Bi`).
    - **`LocationNamesCsv`**: `TIPLOC,name` import (header, quotes, comments), reporting unknown TIPLOCs and bad rows.
  - Graph: **category line styles** are drawn. `GraphPath.Style` carries the category's style on the WTT, plan and actual layers; Dashed is long dashes (12–7), Dotted round dots. The forecast keeps its 6–4 dash.
- **App:**
  - A **Profile menu**: Edit profile…, Open profile file, Reload profile and Regenerate corridors….
  - The **profile editor** (`ProfileEditorWindow`, `ProfileEditorViewModel` and its rows): General, Corridors (with an axis preview) and Categories tabs, OK/Cancel/Apply. See `05-…md` §9.
  - **Applying** saves the profile, then rebuilds the forecast network, the tabs, the train list's colours and the graph without reloading the WTT. The plan, disruptions and selection stay, and so does the corridor shown, with its view.
  - **No sim ID**: the editor asks for one; it is saved and remembered for that `.wtt` (`AppSettings.SimIdByTimetable`), and `TimetableLoader.Load` takes it next time.
  - `IDialogService` gains `Choose` (a question with buttons, `ChoiceDialog`), `ShowProfileEditor` and `OpenInShell`. Dialogs are now owned by the window in front, so a question from the editor sits over it.
- **Tests:**
  - Network +37 (140): every edit and its refusal, sections kept and rebuilt, distances, hand-tuned detection, regenerating both ways, edits surviving save → load → `Refresh`; track presets and text; CSV import.
  - Graph +4 (199): styles reaching the scene's paths, and dashes and dots leaving gaps when drawn.
  - App +23 (93):
    - on Waterloo: rename a location, move a tab, set 12 min headways and see the forecast's knock-on behind a block grow, with the plan and selection kept;
    - refused edits showing why and restoring the cell, and Cancel asking;
    - locations removed, added back and moved, a distance axis, a new corridor, and a removed one offered again;
    - removing the corridor shown; CSV import and category styles reaching the graph; Reload (and a damaged file); Open; Regenerate with each answer;
    - a Llangollen copy with no sim ID: named, saved, and found again by a fresh app; naming one whose profile exists;
    - the editor's text parsing;
    - **the editor window itself**, shown on an STA thread with every tab laid out and no binding errors. It caught nothing this time, but a broken binding fails it (checked).
  - Totals: 1 299 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 140, Planning 184, Graph 199, Tools 57, App 93).
    - Green on Windows (Debug).
    - Green on Linux (`sdk:10.0` container, portable filter, Release: 1 206).
- **Tried in the app:**
  - Started on Waterloo and opened Profile → Edit profile… through UI Automation. The editor showed its three tabs; Cancel closed it, and the app closed cleanly with nothing logged above Information.
  - The tabs were also rendered to PNG from the window test and checked by eye.
  - No edit was applied by hand, so as not to change the real Waterloo profile; applying was driven only by the view-model tests.

### Decisions
- **Fred's choices:**
  - **No sim ID**: the user names the sim, and the name is remembered per `.wtt` file.
  - **Regenerate** asks if any corridor is hand-tuned: Replace all, Keep mine, or Cancel.
  - **Tracks**: presets plus a custom text list.
  - **Line styles** are edited and drawn; the forecast keeps its own dash.
- **Edits apply to a draft** and only Apply or OK save. A refused edit never reaches the draft, so the editor cannot produce a profile `ProfileJson` would refuse.
- **Editor-only rules**: a corridor needs at least 2 locations, and a priority is 0–100. Files are not refused for these, so an older hand-edited file still loads.
- **A corridor's ID never changes**: renaming changes its tab name only. A new corridor's ID comes from its name.
- **Reload doesn't save.** It shows the file as it is, refreshed against the WTT; the next timetable load saves as before.
- **CSV names for TIPLOCs not in the timetable are imported** (and reported), since another timetable for the sim may use them.
- **Headways are typed as m:ss**, minutes (2.5) or seconds (90s).
- **The App tests run with real cultures** (`System.Globalization.Invariant` false at run time only, as the app has), because WPF text needs them. The build keeps `InvariantGlobalization`, so the analyser rules are unchanged.

### Next
- S15: suggestions (see S14's notes). Category priorities, now editable, weight them.
- The editor could show the single-line hint (S6) beside each section, to help set Llangollen's.

### Known issues
- **Not tried by hand.** 👤 Fred, on a sim you know:
  - rename locations, reorder tabs and change tracks and headways, then press Apply and check the graph;
  - try a distance axis;
  - import a names CSV;
  - change a category to dashed or dotted, and say whether it reads well next to the forecast's dashes;
  - try Regenerate corridors after changing a corridor.
- Grid cells commit when you leave them (Tab, Enter or a click elsewhere), as WPF grids do. A cell still being typed in when Apply is pressed may not be included; press Enter first.
- The selected train is drawn solid whatever its category's line style.
- Changing a corridor's locations re-measures it from the WTT straight away, which takes a moment on large sims.
- A section whose two locations swap order (by moving one past the other) keeps its tracks, as `Refresh` already did, so its Up and Down tracks are then the wrong way round. Set its layout again.

## 2026-10-19 — S15: Suggestion engine and regulation list

### Done
- **Portable parts (tested on Linux)**, in Planning:
  - **`Suggester`**: rule-based resolutions for a conflict (FC-5). Hold the train that gives way; swap (hold the other); another platform; another line off a one-line block; retime a run planned too fast; and, only if allowed, terminate short or cancel for a block. Each is scored by re-forecasting the plan with it; the best 3 are offered. The rules are in `06-…md` §4.
  - **`RegulationList`** (RP-9): the movements still to come at a location in plan order, with each train that is ahead of or behind others against the WTT's order.
  - **`PlanSnapshot.Settings`** (`ForecastSettings`): the network and options the snapshot's forecast and conflicts were made with, so `SuggestionRequest.For(snapshot, conflict)` forecasts exactly as the engine does.
  - `AcceptSuggestion` gains an optional `Title`, so the history reads "Suggestion: Hold 1F01 at A until 08:07 (+2 min)".
- **App:**
  - A **suggestions panel** under the conflicts list for the picked conflict: up to 3 rows, each with its cost, a tooltip of the trains it moves, Preview (ghosts of the changed plans) and Apply (one undoable edit). There is also an **Allow cancelling** box (remembered in `AppSettings.SuggestCancellations`). See `05-…md` §5.
  - A **Regulation tab** between Conflicts and History, with a location box. "Regulation list at …" on the graph's right-click menu opens it there. See `05-…md` §6.
- **Tests:**
  - Planning +21 (205):
    - golden scenarios for each kind: a hold at the origin, and the swap with its cost worked out by hand; a running train that cannot be held; priority weights; holding in the crossing loop; a free platform; a platform change taking the next working, or the train it is formed from; a block's hold, termination and cancellation; a line change; a retime;
    - accepting a suggestion as one edit (and its JSON); determinism;
    - Waterloo part way through both recordings: suggestions apply cleanly and to the half minute, and most resolve;
    - the regulation list: order, out of order, the horizon and actuals, cancelled trains and clones, short termination;
    - the engine's snapshot settings feeding a suggestion that is then applied.
  - App +5 (98): on Waterloo, picking a block's conflict, previewing, applying and undoing; allowing cancelling (remembered); refusing a suggestion worked out on an older plan; the regulation list out of order after a hold; the menu item.
  - Totals: 1 325 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 140, Planning 205, Graph 199, Tools 57, App 98).
    - Green on Windows (Debug).
    - Green on Linux (`sdk:10.0` container, portable filter, Release: 1 227).
- **Measured** (Debug, Waterloo): 120–150 ms for a conflict with a hold and a swap; 450–520 ms for a platform conflict at Waterloo station (3 platforms tried). Release will be about three times faster.
- **Tried in the app** (the Waterloo 4× replay played to its end, driven through UI Automation and screenshotted):
  - Picking a headway conflict between two running trains showed "No suggestion: neither 1B92 nor 2L90 can wait anywhere before it".
  - Picking a platform conflict at Waterloo showed three platform changes with their costs.
  - The Regulation tab listed Waterloo's 311 movements in the next 3 hours.
  - The app closed cleanly.
  - Apply was driven only by the view-model tests.
  - The run in the app found that platform changes were wrong for a train formed in the platform from another. It also found that "resolves" was too strict when the same pair clash again further on. Both are fixed and tested.

### Decisions
- **Fred's choices:**
  - Suggestions are worked out **on selection**, not for every conflict.
  - The regulation list is a **tab beside Conflicts**.
  - **Preview shows the changed trains' plans** only.
  - The **weight is priority / 50**, linear.
- **Delay is measured against the forecast without the suggestion**, with knock-on if it is on: at each train's last point still to come within 90 min. So planning a hold that the knock-on forecast already predicts costs nothing, and trains that gain count against the cost.
- **New platform clashes are added to the score** (weight × severity). The forecast does not hold trains for platforms, so otherwise a platform change into a busy platform looked free. Every new clash is added when the knock-on pass is off.
- **Holds are to the half minute** (the departure time, not the length), and grow by what is still missing, up to 4 times, so a train that makes up time on the way is held enough.
- **A train can wait** at a stop, at a platform it passes, or at its first point (entry included), and only where it has not yet left. A train already on its way past every such point gets no hold, and the panel says so.
- **"Resolves"** means that this pair no longer clash in this way at this place (or the train is no longer held by the block, or no longer planned too fast). Another clash between them further on is its own conflict.
- **Penalties**: 2 min for a platform or line change, 30 min for terminating short, 60 min for cancelling (`SuggestionOptions`).
- **Line changes are offered only for blocks on one WTT line**: section tracks are chosen by the forecast itself, so a line change does not change a headway clash.
- **The regulation list orders by plan time** (departure or pass, arrival for a termination) and shows the forecast beside it. It looks 3 hours ahead.
- **A suggestion worked out on an older plan is not applied** (a shift is relative), and is worked out again.

### Next
- S16: session persistence and review. `AcceptSuggestion` (with its `Title`) already serialises.
- Suggestions could be offered for a whole train (all its conflicts at once), or rolled into S20's optimiser.
- The regulation list could be printed or exported with S17's reports.

### Known issues
- **Not tried by hand.** 👤 Fred, on a replay:
  - pick conflicts and judge whether the suggestions are sensible, especially their order, and whether the "weighted delay" line means anything to you;
  - try Preview and Apply, then Undo;
  - use the Regulation tab while signalling and say whether it helps, what is missing (e.g. routes or lines) and whether 3 hours is right.
- **Few suggestions on a busy sim.** Many conflicts at the now line are between trains already on their way, so nothing can be held, and they show none. Holding at a signal is not modelled.
- **Platform changes often add clashes at Waterloo.** The free-platform check is by the forecast alone. A changed platform can still clash with trains formed or stabled there, so its score includes them and it is often ranked below a hold.
- **Live refresh.** While the sim runs, suggestions are worked out again at most every 2 s, so a row can be up to 2 s out of date. Applying one checks the plan has not changed, not the live state.
- **Not-yet-seen trains look very late in the regulation list.** Trains booked before now but not yet seen are forecast from now (as in S11), so the list shows them late at the top.

## 2026-10-19 — S16: Session persistence and review

### Done
- **Portable parts (tested on Linux):**
  - Planning:
    - **`PlanStep`** (Execute, Undo, Redo, Reset; command, description, sim time, wall time). **`PlanningEngine.PlanStepped`** reports every applied command, undo and redo in order, and a Reset when the history is dropped (`Clear` or a different timetable).
    - **`PlanningEngine.RestorePlan(steps)`** replaces the history and plan with what the steps leave, over the current timetable, dropping commands that no longer apply. It does not report steps.
    - **`PlanLog`**: `Fold` (the done and redo lists the steps leave, from the last reset) and `By` (the steps made by a sim time, for review).
  - Persistence (first code; now references Gateway too):
    - **`SessionWriter`**: thread-safe autosave (every 2 s, on `Flush` and on close) of the live frames the engine keeps, the plan steps and `SessionInfo`. The file is made lazily, on the first such frame or a replay's first edit, and named from the sim ID known then. `End` marks the session ended. A failed save is logged once and retried with nothing lost.
    - **`SavedSession`** (`Load`, `Events`, `ToRecording`, `Edits`, `Problems`), **`SessionCatalogue`** (`List`, `LatestUnfinished`, `ReadInfo`) and `SessionFormatException`. The schema is in `02-…md` §5.
- **App:**
  - Every live session and replay autosaves to `Documents\Stringline\sessions`. Disconnect, or starting another session, ends it; closing the app leaves it running.
  - **At start-up** the info bar offers to Resume or Review the last session if it was left running. The Session menu has **Resume saved session…** and **Review saved session…**, and the command line has `--resume` and `--review`.
  - **Resume:**
    - A live session is rebuilt from its frames and steps; the clock stands at its last report. Connect then asks whether to carry on (same state and file) or start a new session.
    - A replay session reopens its recording, paused where it had got to, with its plan.
    - A timetable or recording that has moved is asked for.
  - **Review:** the session replays from its start, paused. The plan is restored to the steps made by the replay clock as it moves (and seeking back takes edits away again). Edits, drags, B+drag, undo and redo are refused, the title ends "(review)", and nothing is written. See `05-…md` §10.
  - Restarting a finished replay (seeking after the end) now **keeps the plan**. Before, it cleared it.
  - With no session (disconnected, or resumed and not yet connected), the now line stands at the last reported time instead of drifting to the end of the minute.
- **Tests:**
  - Planning +8 (213): steps reported in order with sim times, nothing reported for refused or empty steps, restore with a redo list, restore dropping commands that no longer apply, partial restores for review, and `Fold`/`By`.
  - New `Stringline.Persistence.Tests` (11):
    - lazy file creation;
    - frames, steps and meta read back as saved;
    - end and append;
    - reset handling;
    - replay sessions;
    - autosave on a `FakeTimeProvider` timer;
    - a failed save retried;
    - unreadable steps skipped;
    - non-session and newer files refused;
    - the catalogue.
    - A Waterloo 4× fixture saved half way with edits and an undo, then resumed into a fresh engine: the same trains, matches, session start, actuals, history and redo list.
  - App +6 (104), on Waterloo:
    - a replay session offered after a restart and resumed paused with its edit, then ended by Disconnect;
    - restarting a finished replay keeps the plan;
    - review refusing edits and applying the edit when the replay reaches it, writing nothing;
    - a live session (`FakeStompServer`) rebuilt after a restart and carried on in the same file;
    - a new session started instead;
    - the start-up options.
  - Totals: 1 350 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 140, Planning 213, Persistence 11, Graph 199, Tools 57, App 104).
    - Green on Windows (Debug); the App session tests passed 4 runs in a row.
    - Green on Linux (`sdk:10.0` container, portable filter, Release: 1 246). SQLite's native library loads there.
- **Tried in the app** (driven through UI Automation and screenshotted). A replay session file was made by a script, with one edit (Cancel 2A94 at 06:10) and the position 06:20.
  - Starting the app with no arguments showed the offer with Resume and Review.
  - Resume reopened the Waterloo replay paused at 06:20 with "1 edit".
  - `--review` showed "(review)" in the title. Pressing Play ran the replay to its end and the edit appeared in History, with Undo and Redo greyed out.
  - Nothing was logged above Information. The script's session files were deleted from `Documents\Stringline\sessions` afterwards.

### Decisions
- **Fred's choices:**
  - **Live and replay sessions are both saved.** A replay refers to its `.sslog` rather than copying it.
  - **Resume is offered at start-up** when the last session was left running, and also from the Session menu.
  - **Review shows the plan as it was at each moment.**
  - **Review is read-only.**
- **Raw frames, not events, are stored**, filtered to those the engine keeps. Resuming parses them again with `MessageParser`, so parser fixes reach old sessions, and review replays them through the same `ReplaySource` as a recording.
- **The plan is stored as its step log** (execute/undo/redo/reset), not as the final command list. So the redo list survives a restart, and review can rebuild the plan at any sim time. Commands are pure over the plan, so their order relative to events does not matter.
- **Disruptions, notes and links are not separate tables:** they are plan commands (S13).
- **"Ended" means Disconnect or another session started.** Closing the app leaves the session running, so it is offered next time.
- **Review applies the steps made by the replay clock** (with steps made before any clock first). A step's sim time is the clock's last report when it was made. After a replay was sought backwards, the steps' sim times are not in order, and review applies them by time, not in the order they were made.
- **A session file is not made until there is something to resume**, so a refused login or a replay watched without edits leaves no file.
- **Autosave failures never throw.** Saving runs on a timer thread, so any failure is logged and retried.

### Next
- S17: reporting and exports. A saved session's frames and steps are a ready source for a post-run delay table (`SavedSession.Events`).
- A session browser (a list of saved sessions with their sim, time and edits) could replace the file picker; `SessionCatalogue.List` is ready for it.
- S18: `PlanCommandJson` uses reflection-based System.Text.Json. A trimmed or AOT publish would need a source-generated context. Untrimmed single-file publish is unaffected.

### Known issues
- **Not tried live.** 👤 Fred, against a running sim:
  - edit, close the app, reopen it, and Resume; then Connect → Carry on and check the trains carry on;
  - Review a finished session and judge whether the plan changing as the replay runs is useful;
  - check the size of a long live session file (one per sim minute of clock, plus every train report).
- **A sim restarted from a save gives new sim UIDs.** Resuming and carrying on then sees the trains as new, and the old ones stay with their last reports.
- **Sessions are never deleted.** They pile up in `Documents\Stringline\sessions`; delete old ones by hand for now.
- Review starts at speed Max if that was the last replay speed. Pick a slower speed before pressing Play to watch the plan change.
- A session whose timetable was changed mid-session resumes on the last timetable only. Its edits before the change are dropped (Reset).

## 2026-10-19 — S17: Reporting and exports

### Done
- **Portable parts (tested on Linux):**
  - Planning:
    - **`DelayReport`** (SES-2): every train's WTT, plan, and actual or forecast times at each timing point, with the delay against the WTT and against the plan, and per train its state, latest point, worst actual delay and final delay. Built through `TrainSchedule`, so it agrees with the detail panel. Filter: trains seen (the default) or all.
    - **`DelayReportCsv`**: one row per train and point, or one per train. RFC 4180, UTF-8 with a byte order mark.
  - Graph: **`SkiaGraphRenderer.RenderPdf`** (GR-10) and **`PdfPage`** (A3 landscape, A4 landscape, view size). The view's time and corridor range is laid out afresh inside 10 mm margins, giving a one-page vector PDF with the fonts embedded.
  - Tools:
    - **`stringline report`** writes the CSV from a recording, or from a saved session with its plan edits as they stood at `--at` (or where a replay session had got to).
    - `render-png` (now also `render`) writes a PDF when the output ends in `.pdf` (`--page a3|a4|view`).
    - Tools now references Persistence.
- **App:**
  - A **Delays tab** between Regulation and History. It lists trains, worst offenders first, and can sort five ways or list all trains. Below that are the selected train's points (WTT, plan, actual, forecast, delay against the WTT and the plan). The row and the graph selection follow each other. The table is worked out only while the tab is shown, at most once a second for live updates. See `05-…md` §11.
  - Session menu:
    - Export graph as PDF (A3 landscape, Ctrl+P; A4 landscape; view size).
    - Export delay table as CSV… and Export train delays as CSV… (also as buttons on the tab).
    - All exports share one save path: the last folder, an error report and a name like `{area}-delays-{HHmm}.csv`.
- **Tests:**
  - Planning +16 (229):
    - seen and all;
    - actual then forecast delays;
    - a train standing at a stop;
    - against the WTT and the plan after a re-plan;
    - clones and cancelled trains;
    - short termination;
    - both CSVs (exact text, quoting, day marker, activity);
    - Waterloo 4× at the end: every working that reported at its plan's points is listed, and the CSV is well-formed.
  - Graph +8 (207): A3 by default with the fonts embedded and the title; A4 and view sizes; fitting; page names.
  - Tools +5 (62): `report` from a recording, by train and all trains, from a replay session with an edit before and after its position, and bad arguments; `render` to PDF.
  - App +3 (107), on Waterloo:
    - the Delays tab (not worked out while hidden, order, selection both ways, sorting keeping the selection, all trains);
    - CSV export;
    - PDF export on A3 and A4.
  - Totals: 1 382 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 140, Planning 229, Persistence 11, Graph 207, Tools 62, App 107).
    - Green on Windows (Debug).
    - Green on Linux (`sdk:10.0` container, portable filter, Release). Skia's PDF backend works with the Linux native assets.
- **Tried for real:**
  - `render` to an A3 PDF of Waterloo at 07:00. It looks right: vector lines, text that can be selected, margins.
  - `report` at 07:00: 97 trains, 420 rows. The run found intermediate stops booked with only a departure labelled "start"; this is fixed and tested.
  - The app on the Waterloo 4× replay, driven through UI Automation and screenshotted. The Delays tab listed 202 workings seen, worst first, and the app closed cleanly. Picking a row in the app was only driven by the view-model tests.

### Decisions
- **Fred's choices:**
  - **WTT, plan and forecast** in one table, with actual and forecast marked.
  - **A Delays tab** (not a window).
  - **PDF of the current view, fitted to the page**, one page.
  - **A3 landscape is a PDF page preset**; printing is from the PDF reader, with no Print command.
- **Where a delay is measured:**
  - at the departure or pass, or at the arrival where nothing departs (a destination), the same rule as the forecaster's;
  - a train standing at a stop is measured at its arrival until it leaves, so its delay there is actual, not forecast.
- **Delay is against the WTT** (against the plan for an ad-hoc copy). The delay against the plan sits beside it.
- **"Worst"** is the greatest actual delay. **"Final"** is the delay at the last point the train runs to, actual or forecast.
- **The PDF is laid out at the page size**, one point per view pixel, rather than scaling a screen-sized picture, so text stays at its screen size (about 11 pt) on A3.
- **The CSV gives delays in seconds** and times as `HH:mm:ss`, so a spreadsheet can work with them. It has a byte order mark so Excel reads accented names.
- **Trains are listed per working** (plan train), not per live uid, as the plan has them. So "202 trains seen" at Waterloo counts workings, while the train list shows 128 live trains.

### Next
- S18: packaging (single-file publish, icon, settings page, About, README).
- The regulation list could be exported or printed the same way (S15's note).
- A multi-page PDF of a time range (e.g. a whole shift, N hours a page) if wanted; `PdfPage.Fit` and `RenderPdf` are the base for it.

### Known issues
- **Not tried by hand.** 👤 Fred:
  - open the Delays tab while signalling and say whether the columns and the worst-offenders order help;
  - open a CSV in Excel;
  - print an A3 PDF and check the text size and line weights on paper.
- The Delays tab is in the lower side panel, which is short by default. Drag the splitter above the tabs to give it room.
- A train that has not reported anywhere on its plan is not "seen", even if it is in the sim at a signal; "All trains" shows it.
- `report --session` uses a profile generated from the WTT unless `--profile` is given, so its forecast sections can differ from the app's saved profile.

## 2026-10-19 — S18: Packaging

### Done
- **Settings window** (View ▸ Settings…, Ctrl+,; `SettingsViewModel`, `Views/SettingsWindow`). Fred chose all four groups:
  - **Forecast and conflicts:** knock-on, default dwell and turnaround, "possibly stopped" time, platform margin, least conflict shown.
  - **Suggestions:** allow cancellations, and the four penalties in minutes (question 11's numbers can now be tried in the app).
  - **Folders:** timetables, sessions, recordings and exports. Blank means the default.
  - **Logging and housekeeping:** Debug logging, days of logs kept, Open log folder, and deleting old saved sessions.
  - Values are checked together on OK and the window stays open with the reason. Restore defaults resets every tab.
- **Applying settings** (`MainViewModel.Settings.cs`):
  - The engine takes new forecast and conflict options at once. `PlanningEngine.SetForecast` gained `ConflictOptions`, which were fixed at construction before.
  - Suggestions are worked out again with the new penalties. Their cache key is now the whole `SuggestionOptions`.
  - Folders apply to the next save (`AppPaths.WithFolders`), and the log level changes at once (`AppLogging`, a Serilog level switch).
- **Session housekeeping** (`SessionCatalogue.DeleteOlderThan`):
  - It runs at start-up when turned on, and asks first when the setting is turned on.
  - It never deletes the newest session, the one being written or one named with `--resume`/`--review`. The WAL side files go too.
- **Logging:**
  - Settings are read before the logger is made, for the level and the retention.
  - Each run's first line gives the version, commit, OS, runtime and whether it is the single-file build.
  - Exceptions are now logged from every thread and from unobserved tasks, not only the UI thread.
  - The app's own lines carry its name.
- **About window** (`Views/AboutWindow`, `AboutInfo`):
  - the icon, version and commit;
  - the GPLv3 §5(d) notices (copyright, no warranty, licence link, source link);
  - View licence (the GPL embedded as a resource) and Copy version;
  - credits for every shipped component, and the SimSig disclaimer.
  - `THIRD-PARTY-NOTICES.md` holds the same credits. Help also gained Getting started (online) and Open log folder.
- **Icon** (Fred: train-graph lines):
  - `AppIcon` in Stringline.Graph draws it at 9 sizes from 16 to 256 px into a PNG-frame `.ico`. `stringline icon out.ico [--png dir]` writes it.
  - It is used for the exe, the main window, the About window and the installer.
  - Below 32 px it shows only the two stepped trains, because with the fast train as well it read as an X in a box.
- **Packaging** (Fred: an installer too):
  - `build/publish.ps1` makes a single-file, self-contained, compressed win-x64 `Stringline.exe` (78 MB, native libraries inside), zipped with LICENSE, notices, OFL and README.
  - `-Installer` adds `Stringline-x.y.z-setup.exe` from Inno Setup 6 (`installer/Stringline.iss`): per user, no admin, Start menu, optional desktop icon, Windows 10 2004+ x64.
  - `.github/workflows/release.yml` runs on `v*` tags: test, publish, draft GitHub release. Run by hand, it uploads the files as artifacts.
- **README:** user-facing first: installing (SmartScreen note), turning on the gateway, connecting (payware login), timetables, reading and re-planning, settings, where files are kept, troubleshooting, command line. The developer sections follow, plus publishing.
- **Tests:**
  - Planning +1 (230): conflict options changed while running.
  - Persistence +1 (12): deleting old sessions.
  - Graph +3 (210): the icon's sizes, PNGs, `.ico` and the committed file.
  - Tools +1 (63): `icon`.
  - App +10 (117):
    - settings applied, saved and in the engine;
    - restart with saved settings;
    - bad values and Cancel;
    - Restore defaults, Browse and Open logs;
    - housekeeping at start-up and on turning it on;
    - the About notices, version and embedded licence;
    - every shipped package credited;
    - the Settings and About windows load every tab with no binding errors. Set `STRINGLINE_WINDOW_SHOTS` to a folder to save them as PNGs.
  - Totals: 1 398 (Core 247, Architecture 9, Gateway 207, Timetable 163, Network 140, Planning 230, Persistence 12, Graph 210, Tools 63, App 117). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 281).
- **Tried for real:**
  - `publish.ps1 -Installer` built the zip and the installer.
  - The published exe started, logged "single-file" with the commit, and closed cleanly.
  - The installer installed silently for the current user into a temporary folder with its Start-menu shortcut, and uninstalled cleanly.
  - The Settings tabs and the About window were rendered to PNG and checked by eye.
  - Inno Setup 6.7.3 was installed on Fred's PC with winget (Fred agreed).

### Decisions
- **Settings are applied on OK, not live.** The view menu's switches (knock-on, cancellations) stay in their places too; both write the same settings.
- **Penalties are shown in minutes**, as the README and question 11 talk about them; they are kept in weighted seconds.
- **Log retention takes effect at the next start**: Serilog's file sink fixes it when the logger is made. The level changes at once.
- **Housekeeping never deletes the newest session.** It also asks before the first delete, because deleting files is not undoable.
- **Only the exe ships in the zip** (fonts are embedded), plus the notices. The `stringline` CLI is not in the release; developers run it from source.
- **The installer is per user and unsigned.** It needs no admin rights. SmartScreen will warn until the files are code-signed (see questions).
- **The icon is drawn in code** so it can be changed by editing `AppIcon`, not a binary. A test checks the committed `.ico` has every size.
- **`AboutInfo.IsSingleFile`** uses the empty `Assembly.Location` of a single-file app on purpose (IL3000 suppressed there).

### Next
- S19 (opt-in assist), S20 (optimiser), S21 (multi-corridor views): optional extensions.
- Code signing, if releases go public (see questions).
- A session browser in place of the file picker (S16's note) would sit well beside the housekeeping setting.

### Known issues
- **Not tried by hand.** 👤 Fred:
  - run the installer normally (not silently) and check the wizard, Start menu entry and first start;
  - open Settings, change a margin or penalty while signalling, and check the conflicts and suggestions follow;
  - push a test tag (e.g. `v0.1.0-rc1`) to check the Release workflow on GitHub's runner. It installs Inno Setup with Chocolatey if the image lacks it; that path has not run yet.
- The README's gateway steps are from the design notes ("turn on Interface Gateway in the options before starting the sim"), not from a fresh look at SimSig's Loader. Check the wording matches SimSig.
- The release draft notes are GitHub's generated ones. Edit them before publishing.

## 2026-10-20 — Plan update: GitHub issues #1–#13

### Done
- Reviewed Fred's 13 open GitHub issues (raised 2026-10-19 after using the S18 build) and added **Phase 4a (S22–S29)** to `07-implementation-plan.md`, with an issue → session table:
  - S22: publisher and sim identity (#1, #6)
  - S23: panel layout, resizing and long names (#2, #3, #9)
  - S24: wording, tooltips and clock (#11, #12, #13)
  - S25: menus against the UI spec (#8)
  - S26: TRIMS-style drag grips (#10)
  - S27: sim-aware headways (#7)
  - S28: user guide and wiki (#4)
  - S29: multiplayer (#5)
- Docs only, so there was no code change, build or test run.

### Decisions
- **The fixes come before the optional S19–S21.**
- **#9 single-view resizing is fixed in S23.** S21 keeps the split and multi-board views.
- **#13 is optional within S24**, because Fred marked it not critical.

### Next
- S22.

### 2026-10-20 — Installer updates an installed Stringline

### Done
- **Tried it first.** I built 0.1.0 and 0.1.1 setups, installed 0.1.0, started it, and ran 0.1.1 silently over it. The installer already updated in place: the same `AppId`, folder and per-user mode, with the Restart Manager closing the app. **But with a session open, the app closed without stopping it.** At a Windows session end (an installer's Restart Manager, logging off, shutting down), WPF closes the window without waiting for `OnClosing`'s async work. The log went straight from "shutting down" to "stopped". The session was not stopped and the layout was not saved.
- **Fix:** `App.OnSessionEnding` calls `MainWindow.ShutDownNow(4 s)`. It does `OnClosing`'s work (tick, layout, `MainViewModel.DisposeAsync`) once, running the dispatcher in a nested frame until that is done. The log says "Windows is closing the app (…); stopping the session" and then "Session stopped for Windows". A window test covers it.
- **Installer** (`installer/Stringline.iss`):
  - comments on how updating works;
  - `CloseApplicationsFilter=Stringline.exe`;
  - the product version on the setup exe;
  - an empty `[InstallDelete]` for files a later version drops;
  - a **downgrade check**: an older setup over a newer install (for this user or all users) asks first. A silent one refuses with exit code 1 unless given `/ALLOWDOWNGRADE`. Pre-release and build suffixes are ignored, so reinstalling the same version is allowed.
- **`build/test-upgrade.ps1`**: it installs an old setup, starts it with the Waterloo replay running, and updates it with `/VERYSILENT … /CLOSEAPPLICATIONS`. It checks:
  - the app was closed and logged that it stopped its session;
  - the version in the Apps list and on the exe;
  - the folder is unchanged;
  - there is no second (all-users) entry;
  - a silent downgrade is refused, and `/ALLOWDOWNGRADE` goes ahead;
  - the uninstall removes the program and its entry, and leaves the logs.

  An old app or setup from before this change has neither the session save nor the downgrade check; for those it warns instead of failing.
- **Release workflow:** a "Test the update" step runs the script from the previous release's setup. When no release has one, it uses a 0.0.1 build of this code.
- **Passed by hand:**
  - 0.0.1 → 0.1.1 built here: every check;
  - the real 0.1.0 setup (from the V0.1.0 Release run's artifact) → 0.1.1: every check, plus the two expected warnings.
- `Directory.Build.props` is now **0.1.1**. Updated the guide's Installing ▸ Updating, the README, `05-…md` §12 and CLAUDE.md.

### Decisions
- **The Restart Manager, not `AppMutex`.** A mutex would only make setup ask the user to close Stringline (and abort a silent update). The Restart Manager closes it, and now the app saves its session first.
- **The downgrade check is in the older setup**, so it only protects from 0.1.1 on. Installing 0.1.0 over a newer version still goes ahead silently.
- **The test is in the Release workflow only.** It publishes and installs, which is too slow for each push.

### Next
- S29, as before.

### Known issues
- **0.1.0 cannot be helped.** Updating from a running 0.1.0 closes it without saving its session or layout. That happens once, and an edited session is still offered for resuming from its last autosave.
- **The V0.1.0 release has no files.** The one Release run was started by hand without a tag, so the zip and setup only went to that run's workflow artifacts, which GitHub deletes after the retention period (90 days by default). Anyone who installed 0.1.0 got it from there.
- **Not seen on the hosted runner yet.** The script passed on Fred's PC; the first tagged release will show whether the Restart Manager behaves the same on `windows-latest`.

### Questions for Fred
- 21. **Release 0.1.1?** It carries the save-on-close fix and the downgrade check. Pushing tag `v0.1.1` builds it, tests the update, and attaches the files. Should V0.1.0 also get its files, with Actions ▸ Release ▸ Run workflow and tag `V0.1.0`? That build would not have this change.

## Questions for Fred
- 15. **#1:** which code-signing route: SignPath Foundation (free for open source, recommended), Azure Trusted Signing or an OV certificate? Without one, Windows keeps saying "Unknown publisher".
- 16. **#5:** a multiplayer recording is needed before S29.

## 2026-10-20 — S22: Publisher and sim identity (#1, #6)

### Done
- **#1 Unknown publisher.**
  - `Directory.Build.props`: `Authors` and a new `Company` are "Frederick Duncan and Stringline contributors", so the exe's properties name the publisher (checked on a real publish).
  - New `build/sign.ps1` signs files with `signtool` when `STRINGLINE_SIGN_PFX` names a `.pfx` (`STRINGLINE_SIGN_PASSWORD`, optional `STRINGLINE_SIGN_TIMESTAMP`). Without one it warns and succeeds.
  - `publish.ps1` signs `Stringline.exe` before zipping. With a certificate it also hands Inno a `stringline` sign tool (`/DSign`), so the installer and its uninstaller are signed (`SignTool=`, `SignedUninstaller=yes` in `Stringline.iss`).
  - `release.yml` decodes the `SIGN_PFX_BASE64` secret to a temporary file (deleted afterwards) and passes `SIGN_PFX_PASSWORD`. Without the secrets the release is built unsigned, as before.
  - README: the SmartScreen note now says why Windows shows "Unknown publisher", and a new "Code signing" section explains the set-up.
- **#6 Sim and timetable shown together.** New `SimTitle` in Core (the Tools need it too): "*sim* – *timetable* (v*version*)", plus " (review)". It is used in:
  - the window title (`Stringline — Waterloo – SX May 2010 (Updated - Loader) v1.0.1 (v1.0.2)`);
  - the status bar (`MainViewModel.SimTitleText`, which replaces `SimName`);
  - the graph's title band (`GraphSceneRequest.SimTitle`, defaulting to the profile and timetable header), so PNG and PDF, and the PDF's document title, in the app and in `render-png`;
  - saved sessions: `SessionInfo.TimetableVersion` (meta key `timetable_version`), `SessionInfo.Title`, and `DisplayName` (resume prompt, messages);
  - the CSV: an optional first line `# Sim – Timetable – as at HH:mm:ss` from `DelayReportCsv.WritePoints/WriteTrains(…, title)`, in the app and in `stringline report`.
- Tests: `SimTitleTests` (fallbacks, tidying, version and review); the title, status bar and graph band on Waterloo (live, replay, review, profile rename); the CSV title line (unit, app and CLI); the session round trip; `SigningScriptTests` (no certificate: warns, exits 0 and leaves the file alone; a missing certificate fails without printing the password). The 8 graph snapshots were re-accepted: only their title band changed.

### Decisions
- **Signing route: a generic `signtool` + PFX** (Fred, this session). It works with an OV certificate now. SignPath or Azure Trusted Signing can be added in `sign.ps1` later.
- **The certificate comes only from environment variables**, read inside `sign.ps1`. Inno calls `sign.ps1` rather than `signtool` directly, so the password is never on a command line that ISCC or the workflow could log.
- **New profiles leave the sim name blank** (Fred: "area ID, tidied"). The sim part shows the area ID tidied: words split at `_`/`-` are capitalised, and an ID with capitals is kept as it is. A saved profile whose name equals the timetable's title (every profile made before S22) is treated as unnamed, so no migration is needed.
- **The gateway's area ID comes before the timetable's** for the sim part, as the plan says. The unnamed-sim placeholder (`unknown`) is never shown as a name.
- **The timetable's version is left out when its name already contains it**, and shown as `(v1.0.2)` otherwise.
- **The CSV title line starts with `#`**, so pandas (`comment='#'`) and similar tools skip it. Excel shows it in A1. It uses " – as at" rather than a comma, so it is quoted only when the timetable's name has a comma.

### Next
- S23: panel layout, resizing and long names (#2, #3, #9).

### Known issues
- **The signed path has not run.** This machine has no Windows SDK (`signtool`), and there is no certificate yet. GitHub's `windows-latest` image has `signtool`. Try it once a certificate exists, e.g. with a test tag.
- `publish.ps1 -Output` takes only a path relative to the repo (as before S22).
- Renaming the sim in the profile editor updates the title at once. A session file that is already saving keeps the old sim name until the timetable is loaded again.

### Questions for Fred
- 15 (from the plan update) is answered for now: `signtool` + PFX. **To finish #1**, a certificate is still needed:
  - SignPath Foundation (free for open source; it would need a small `sign.ps1`/workflow change);
  - Azure Trusted Signing;
  - or an OV certificate, which works with this session's set-up as it is.
  Then set the `SIGN_PFX_BASE64` and `SIGN_PFX_PASSWORD` secrets.
- 17. A profile made from a *different* timetable of the same sim keeps that timetable's title as its name, so the title can read "Old WTT – New WTT". Clear the sim name in Profile ▸ Edit profile if you see this. Is that good enough, or should a name equal to *any* WTT title count as unset?

## 2026-10-20 — S23: Panel layout, resizing and long names (#2, #3, #9)

### Done
- **#2 Columns cut off.** The cause was fixed column widths that, added up, were wider than the panel: the Delays trains list needed 497 px in a 490 px panel, the delay points 553, and the train detail's Delay column (36 px) was narrower than its own header. Every list in the panels now has one star-sized text column, which trims with an ellipsis and has a tooltip, and its numbers sized to their content and right-aligned (`PanelGrid`, `RightCell`, `TrimCell` styles in `MainWindow.xaml`). Regulation and both Delays lists are `DataGrid`s now, not fixed-width `GridView`s. The conflict rows share their Time, Trains and Severity widths (`SharedSizeGroup`).
- **#3 Long location names.** New `GraphLabels` (Graph, portable) sizes the label margin to the widest name or TIPLOC, between 90 and 260 px of room, in both orientations. `GraphNavigator.FitLabels` keeps that room when the graph turns. The renderer shortens a name that is still too long with an ellipsis. Hovering over a label shows the full name, TIPLOC and platforms, and the hint that Profile ▸ Edit profile can shorten it (also in the README). The app's PNG and PDF exports use the navigator's viewport, so they get the same margin; `render-png` works it out too.
- **#9 Resizing.** The three columns are star-sized and keep their shares as the window changes size. Their least widths are set so every list fits (`WindowLayout.Min…`). Double-clicking a splitter, or View ▸ Panels, hides and shows the train list, the side panel, or the tabs under the train detail. The suggestions get their own splitter. View ▸ Reset layout puts everything back. The layout and the window's place, size and maximised state are saved on closing (`AppSettings.Layout`) and restored, back onto a screen that is still there (`WindowPlacement.OnScreen`, with the screens from `EnumDisplayMonitors`). `LayoutViewModel` holds the rules; `MainWindow.Layout.cs` only moves WPF.
- **Tests:**
  - `GraphLabelsTests`: the room, the ellipsis, the hover in both orientations, the navigator, and three snapshots (a 40-character name horizontal and vertical, and a longer one shortened).
  - `LayoutTests`: the switches, Reset, damaged settings, window placement on gone or partly-off screens, and save and load (also through the settings window).
  - `MainWindowTests` drives the real main window over a Waterloo replay, on a WPF test dispatcher (`TestUiThread(wpf: true)`):
    - at the panels' least width, no list in any tab scrolls sideways or squeezes a column below its content;
    - at 1280×720 and 2560×1440 the panels keep their share. The content is laid out at the exact size inside a canvas, because Windows keeps a window no bigger than the screen;
    - hiding, a restart, and Reset layout.
    With `STRINGLINE_WINDOW_SHOTS` set, it saves PNGs and a `lists.txt` of the column widths.
  - The `render-png` vertical test now finds the now line, not the old fixed margin.
- Totals: 1 435 (Core 259, Architecture 9, Gateway 207, Timetable 163, Network 140, Planning 231, Persistence 12, Graph 221, Tools 63, App 130). Green on Windows (Debug). Not run on Linux this session, though the Graph and Tools changes are portable.

### Decisions
- **Keep share (Fred).** The panels grow and shrink with the window in proportion, not at a fixed pixel width.
- **A double-click on the train detail's splitter hides the tabs (Fred).** The train detail takes their room.
- **Suggestions get a splitter (Fred)**, in place of the 240 px cap.
- **Least widths come from measurement**, not guesses: the train list needs 265 px and the train detail 440 px with a vertical scroll bar. So the least widths are 270 and 450 px, and the window is at least 1050 × 520 px (it was 800 × 480).
- **A hidden panel's room goes to the graph**, and a splitter next to a hidden panel does not drag. Hidden panels keep their share at the current scale, so showing one brings it back as it was.
- **DataGrid squeezes Auto columns to their MinWidth before it scrolls**: that is what clipped "08:03:58" to "08:03:5". The window test therefore checks every column against its content as well as the scroll extent.
- **The label room is clamped to 90–260 px.** A forty-character name in the bold station font measures 255 px, so it shows in full.
- **Unsafe code is allowed in the App only**, for the `EnumDisplayMonitors` callback (`LibraryImport` with a function pointer).

### Next
- S24: wording, tooltips and the clock (#11, #12, #13).

### Known issues
- **Not tried by hand.** 👤 Fred:
  - drag the splitters and double-click them;
  - unplug a second monitor between runs;
  - check that maximised comes back on the right screen.
  Mixed-DPI monitors are scaled by the system DPI, so a window saved on a screen with a different scale may come back slightly offset.
- The corridor tabs in the toolbar still wrap into two rows at 1280 px (as before S23). S25 reviews the toolbar.
- The View menu repeats some access keys (W, F, A), as before; S25 checks them.

## 2026-10-20 — S24: Wording, tooltips and the clock (#11, #12, #13)

### Done
- **#11 Early / On time / Late.**
  - `DelayBand.RightTime` is now `DelayBand.OnTime` (and `DelayBands.OnTime`). The colour is the same.
  - `GraphHover.FormatDelay` says "On time" within the minute, and "*n* min late" or "*n* min early" otherwise. This reaches the hover, the forecast line and the train detail's heading.
  - The toolbar's colour tooltip, README, `05-…md` and `06-…md`, and code comments are reworded.
  - The lists keep their signed minutes (`+3`, `0`, `−1`), and every delay cell now has a tooltip in words: the train list, the train detail, Regulation, and both Delays lists including Final. This is a `DelayCell` style with `DelayWordsConverter` → `TrainListViewModel.DelayWords`.
  - New `WordingTests` (Architecture, portable) fails if "right time" (any spacing or case) is in `src/**/*.cs|xaml`, the README or the design docs. `progress.md` and the plan are left out because they record the history.
- **#12 Tooltips for the red boxes.**
  - New `GraphHitTester.TargetAt` (Graph, portable) finds what is under the mouse, in this order: a conflict's ring, a note, a train line (using the hit grid if given), a conflict's highlight, a block or speed restriction, then a location label.
  - The result is a `GraphTarget`. When it is a line, the target also carries the highlight and disruption the line is inside.
  - `GraphConflicts.HighlightAt` picks the smallest highlight under a point.
  - `GraphHover.Describe(GraphTarget, …, suggestionFor)` builds every tooltip. `GraphView` now has one describer and one click handler (`ConflictClicker(GraphTarget?)`), in place of three describers.
  - **Conflict tooltip:** kind and summary, then Trains ("2B02 gives way to 1A01", "1A01 held by block B1"), Where (from–to), When (with the clash's span), Resolve, Knock-on, and Suggest. Suggest is the top row of the suggestions panel when it is showing that conflict; otherwise a tip says "Click to focus it and see suggestions".
  - Clicking a highlight away from any line focuses the conflict, as clicking the ring does.
  - **Disruption tooltip:** now adds Effect for a block ("trains running into it are held until it ends") and "Tip: Right-click to edit or remove it". A restriction still shows its slower running.
  - **A line inside a box:** its tooltip adds a "Conflict" row and a "Disruption" row. A held train's line runs right along its own 8 px hold band, so without this the band's conflict could never be read there (found by the App test).
- **#13 Sim clock** (built: Fred chose the toolbar clock with a size setting, and no separate window).
  - The sim time is shown in bold Consolas, with the speed smaller and grey, at the right of the **menu row**.
  - Settings has a new **Display** tab with "Sim clock size": Normal, Large or Huge (16, 24 or 36 px). It is `AppSettings.ClockSize`, saved by name.
  - The status bar keeps its clock.
- **Tests:**
  - `GraphTargetTests`: the order of targets, plain and through the hit grid, in both orientations. The tooltip is the same horizontal and vertical. A line inside a highlight and a block names both. The label is found last.
  - `GraphConflictTests`: the new tooltip rows, the suggestion, the block and lone-train wording, and highlight hit testing (smallest first).
  - Disruption tooltip; delay words; the toolbar clock's sizes and `ClockTime`.
  - The Display setting round trip and Restore defaults; the property-change notifications.
  - The ring's tooltip in the app names the trains. The held train's band names its conflict. The tooltip carries the panel's top suggestion once it is worked out.
  - `MainWindowTests`: at 1280×720 with the Normal and Huge clock, the clock and the toolbar's last button are both on screen, and the clock sits above the toolbar.
- Totals: 1 464 (Core 259, Architecture 10, Gateway 207, Timetable 163, Network 140, Planning 231, Persistence 12, Graph 236, Tools 63, App 143). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 321).

### Decisions
- **Lists keep their numbers, and the words go in tooltips (Fred).** "On time" would not fit a 32 px column, and signed minutes sort and scan well.
- **The CSV is unchanged (Fred).** It never had the old wording: delays there are signed seconds. There is nothing to put in the release notes.
- **Toolbar clock with a size setting, and no floating window (Fred).**
- **The clock sits at the right of the menu row, not on the toolbar.** On the toolbar it pushed "Follow" off the window at 1280 px, but the menu row is empty on the right at any width. A Huge clock makes the menu row taller, which is what the user chose.
- **Hover and click order:** ring, note, line, highlight, disruption, label.
  - A line wins over the box it is in, so hovering and clicking a train still work inside red boxes. The line's tooltip names the box instead.
  - Clicking a note still selects the line under it, as before.
- **The top suggestion is shown only when it has already been worked out** (the conflict picked in the panel). Hovering does not start the suggester, which can take a while.
- **The headway used is not in the conflict tooltip yet.** Conflicts do not record it; S27 adds that with sim-aware headways.

### Next
- S25: menus against the UI spec (#8). The new Display tab is a candidate for Tools ▸ Options.

### Known issues
- **Not tried by hand.** 👤 Fred:
  - hover the red boxes on a live sim, in both orientations;
  - try Settings ▸ Display ▸ Huge from across the room.
- The corridor tabs still squeeze into two rows at 1280 px (as before; S25 reviews the toolbar).

## 2026-10-20 — S25: Menus against the UI spec (#8)

### Done
- **Review.** The shell was checked against `05-…md` §1, §8 and §12 and Windows conventions. The findings, and the menus as now built, are in a new `05-…md` §1 "Menus, toolbar and shortcuts", with a "spec vs build" table. Before S25:
  - the menu bar was Session · Timetable · Profile · View · Plan · Help, where the spec has File · Session · View · Corridor · Plan · Tools · Help;
  - Settings was in View (#8);
  - there was no Corridor menu;
  - seven export items sat in Session;
  - access keys repeated in Session (R, V, D), View (W, F, D) and the train's right-click menu (L: Link and Regulation list);
  - the corridor tabs wrapped onto two rows at 1280 px. Measured, the row had 79 px, and neither of Waterloo's tabs (101 and 112 px) fitted whole.
- **Menu bar (Fred: the spec's).**
  - **File:** timetable, saved sessions, Export ▸ (PNG, PDF and CSV) and Exit.
  - **Session:** Connect, Open replay, Disconnect and Record.
  - **View:** display switches only.
  - **Corridor:** every corridor, ticked when shown, with Ctrl+1–9; and Whole corridor.
  - **Plan:** the edit menu (Fred). Undo and Redo, the train edits, Clear selection (Esc) and Forecast knock-on.
  - **Tools:** Options… (Ctrl+,) and the profile's items (Fred).
  - **Help:** unchanged.
  - Exit is `ApplicationCommands.Close` and About is a routed command, so every item has a command. The Settings window is now titled **Options**. Every "Settings ▸", "Profile →" and "Session ▸" path in the messages, tooltips, the label hover, README, `CLAUDE.md`, `02-…md`, `05-…md` and `06-…md` now names the new place.
- **Access keys.** `MenuText.AccessKey` and `Escape`:
  - corridor names, locations and the edit named in Undo and Redo are escaped, so an underscore in them is shown rather than taken as a key;
  - the right-click menus' "Regulation list" became Re**g**ulation (key G);
  - `PlanMenuItem` gained a shortcut and a tick, and `Views.MenuBuilder` turns it into a WPF item for the graph's menu, the Corridor menu and ».
- **Toolbar (Fred: one row + overflow).**
  - The tabs are a single row. A » button lists the tabs that do not fit (`MainWindow.HiddenCorridorTabs`), and picking one scrolls its tab into view.
  - To give the tabs room, the Colour box became a **By delay** toggle (the same switch as View ▸ Colour by delay), and the padding is tighter. Both Waterloo tabs now fit at 1280 px.
  - Every toolbar control has a tooltip that names its menu item or shortcut.
- **Tests:** `MenuTests` (11):
  - the bar's order;
  - every item bound to a command (or a bound tick);
  - no access key repeated in any menu, including the graph's train and background menus over a Waterloo replay;
  - every shortcut shown is a key binding for the same command: Space and Esc on the graph, Ctrl+n picking that corridor, and no binding repeated;
  - the Corridor menu and Ctrl+2;
  - the tabs on one row at 1280 px, the » list when squeezed, scrolling a picked tab into view, and all of them under » at the least width;
  - `MenuText` units.
  Two existing tests follow the new Re_gulation header.
- Totals: 1 475 (Core 259, Architecture 10, Gateway 207, Timetable 163, Network 140, Planning 231, Persistence 12, Graph 236, Tools 63, App 154). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 321).

### Decisions
- **The spec's menu bar, Undo and Redo in Plan, and one row of tabs with overflow (Fred).**
- **Open replay is under Session, not File.** It starts a session, as Connect does. File holds what reads or writes files without starting one.
- **Forecast knock-on moved from View to Plan.** It changes the forecast and the conflicts, not only what is drawn, and View is for display switches only.
- **Clear selection moved to Plan**, beside the commands that act on the selected train.
- **Whole corridor moved to the Corridor menu.**
- **The Colour box became a toggle.** It had only two choices, and the room was needed for the tabs.
- **No recent-file ▾ menus on Connect and Replay** (spec §1). The dialogs already remember the last host and file. This is noted in the spec table.

### Next
- S26: TRIMS-style grips for drag re-planning (#10).

### Known issues
- **Not tried by hand.** 👤 Fred:
  - walk the menus with Alt and the access keys;
  - try Ctrl+1/Ctrl+2;
  - narrow the window until » appears, and pick a tab from it.
- **A possible flaky test.** `MenuTests.Every_shortcut_shown_is_bound_…` failed once, in the first full run after the build, and its message was not captured. It then passed in 10 full runs, one of them after a clean rebuild. If it fails again, the message will say which item.

### Questions for Fred
- 18. **Naming changes from S25.** Do these read well? Each is easy to change back.
  - "Settings" is now **Options** (Tools ▸ Options…, and the window's title).
  - "Show import warnings" is now **File ▸ Timetable import warnings**.
  - The Colour box is now a **By delay** toggle.
  - Forecast knock-on and Clear selection are now in **Plan**.

## 2026-10-20 — S26: TRIMS-style grips for drag re-planning (#10)

### Done
- **Why dragging "did not work as expected"** (found in the code and shown by `GraphGripTests.The_selected_line_is_found_under_its_forecast`):
  - **The main cause.** Over a running train, its forecast line is drawn on top of its WTT (or plan) line wherever it runs to time. The hit test prefers any non-WTT layer by 2 px, so a press there hit the forecast. `PlanDrag.Begin` refuses forecast lines *with no reason*, so the graph panned and nothing was said. On an on-time train, no part of the run still to go could be dragged.
  - **The tolerance.** 5 px on a 1 px (WTT) or 2 px (selected WTT) line made the line hard to catch.
  - **Alt.** An Alt+press that did not start a drag (wrong train, a refused point) left Alt to open the menu bar on release, and the next keys went to the menu.
  - **Clamps.** The now line, latest report and minimum run stopped a drag with the reason only in the status bar, so the line seemed to stick.
- **Grips** (`GraphGrips`, Graph, portable). On the selected train's plan line, or its WTT line if not re-planned:
  - a square at each timing point (drag = `ShiftTrain` from there; no Alt needed);
  - a bar at each stop's departure end (drag = `SetDwell`; none at the end of the run);
  - a round handle on the first stretch of the run still to go (drag = `ShiftTrain` from the next point not reached).
  Movable grips are filled in the line's colour; locked ones are hollow grey, with the reason `PlanDrag` would give (passed, standing at the platform, left, cancelled, finished). The grip under the mouse is larger, and the picked one has a blue ring. Grips are hit first (7 px), then the selected train's own line (6 px), even under its forecast.
- **`PlanDrag`:**
  - `Begin(GraphGrip, …)` and `Start(snapshot, train, kind, index, now)`, so grips, nudges and the lock reasons share one set of rules;
  - `Nudge(seconds)`;
  - the preview's `Time` and `Label` ("07:15:30 (+3 min) · stopped: now").
  An arrived point now says "drag its departure instead".
- **Renderer:** `Render(…, grips)` draws the grips over everything on the plot. While dragging it draws only the dragged grip, where it has got to, with its label; both turn red at a limit.
- **Shell:**
  - The cursor changes over a grip or the selected line (↔, ↕ when vertical, ⊘ when locked). The status bar names the action (`MainViewModel.GripHint`, shown over `EditStatus` as `EditStatusText`), and the tooltip says what the grip does or why it is locked.
  - A click on a grip picks it, and a grip dragged stays picked. The arrow keys along the time axis nudge it by 15 s (Shift: 1 min).
  - A drag on the line's body gets the time label too, at the press.
  - Alt+press never opens the menu.
- **Docs:** `05-…md` §3 (gestures and "Built in S26"), the plan, README.
- **Tests:**
  - `GraphGripTests` (22): which grips and where; plan versus WTT line; locks and their reasons; hit testing in both orientations and out of view; the cause above; each grip kind's drag; the clamp label; nudges and their limits; the tooltip; drawing (filled versus hollow, the dragged grip and label, the ring).
  - `GripEditingTests` (6, App, Waterloo): select → hover hint → grip drag → `ShiftTrain` from the grip's point → undo; a bar → `SetDwell`; click and nudge twice (+15 s, +1 min) → two edits → undo; a cancelled train's grips locked with their reason; a press on the selected line drags its body with a label.
- Totals: 1 503 (Core 259, Architecture 10, Gateway 207, Timetable 163, Network 140, Planning 231, Persistence 12, Graph 258, Tools 63, App 160). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 343).

### Decisions
- **Fred:**
  - the handle and the whole line drag the body;
  - a click picks a grip, and each nudge is one edit;
  - passed points are hollow grey squares.
- **Grips use the scene's lines.** With the WTT layer off, a train not yet re-planned has no grips, because its editable line is not drawn. Grips are not drawn on a line that is not there.
- **The forecast and actual lines still pan.** A press on the selected train's own line wins over its forecast. Elsewhere, a forecast that has drifted from the plan is not an edit handle, because moving the plan does not move the forecast one for one.
- **Nudges use the engine's latest plan** (`PlanningEngine.Current`), not the UI's snapshot, so a quick run of `SetDwell` nudges (absolute times) adds up. Nudges pressed while one is being made are added together into the next edit.
- **A locked grip neither drags nor pans** when pressed, because the user aimed at it. Its reason goes in the status bar.
- **No bar at the end of the run.** A WTT may time a departure at a train's last point (it forms its next working), but that bar would only ever be locked.

### Next
- S27: sim-aware headways (#7). S28's guide can now describe the grips.

### Known issues
- **Not tried by hand.** 👤 Fred:
  - on a live sim, select a running train and drag its squares, bars and handle, including near the now line;
  - nudge with the arrow keys, in both orientations;
  - hold an arrow key down: each repeat is an edit, so the history fills quickly. Say if a held key should be one edit.
- Grips on a train that leaves the corridor and comes back are on each visit's line. The handle is on the first stretch in view.

## 2026-10-20 — S27: Sim-aware headways (#7)

### Done
- **The problem, measured.** Every section had 3 min unless set by hand. On Waterloo's 4× recording, the conflicts at the end included 16 headway conflicts. Waterloo's main line is booked tighter than 3 min (WLOO–INTJN 2:54 down to EARLSFD–WIMB 1:30). The WTT already exempts booked pairs, so the false ones are late trains held to 3 min where the timetable runs them closer.
- **Headway per section** (`06-…md` §3; `Section.ResolveHeadway`), in this order:
  1. set by hand;
  2. the WTT's, if it is less than the default;
  3. the profile's default, then the global one.
  - **From the WTT** (`BookedHeadway.Derive`, Network). Booked windows through each section, each direction, go on tracks as the forecaster sees them: in entry order, on the track free longest, as many tracks as the WTT needs at once. A pair's spacing is the lesser of its spacing at the two ends. The figure is the 5th percentile, at least 1 min, from at least 10 pairs; pairs over 30 min apart are not counted. `CorridorMeasurement` measures it with the run times (`SectionStats.BookedHeadway`), and `CorridorBuilder` stores it on the section (`BookedHeadwaySeconds`, `BookedHeadwayPairs`) and resolves the headway.
  - **Section** gained `HeadwaySource` (Default, Wtt, Hand). `DefaultHeadwaySeconds` keeps its name (it is in every saved profile) but is now always the headway used.
  - **Profile:** `DefaultHeadwaySeconds` (profile-wide; null = global), `GlobalHeadwaySeconds` (from Options, not saved), `WithGlobalHeadway`, `ResolveHeadways`. `Refresh`, `RegenerateCorridors`, `AddCorridor` and location edits resolve against the profile's fallback. `WithHeadway(…, null)` goes back to automatic; `WithDefaultHeadway`.
  - **Hand-tuned:** only a hand-set headway makes a corridor hand-tuned now. A WTT or default headway is not a setting.
  - **Profile version 2.** In a version 1 profile, a headway other than 3:00 was set by hand and is marked Hand; a 3:00 one is taken as the default, so the WTT's can apply.
- **Planning:** `ForecastSection.HeadwaySource`; `Conflict.HeadwaySeconds` and `HeadwaySource` on headway, overtake and single-line conflicts (the worst section's, when merged). `ForecastNetwork.TracksNeeded` now calls the Network one, so both count tracks the same way.
- **Shown:**
  - the conflict tooltip has a **Headway** row ("2 min 30 s (from the WTT)") between When and Resolve;
  - the profile editor's sections have a **From** column (Hand, WTT or Default), whose tooltip says why and what the WTT books ("…5th percentile of 1610 pairs"). Clearing a headway goes back to automatic. General has the profile's **Default headway**, with the global one in its hint;
  - Tools ▸ Options ▸ Forecast and conflicts has **Default headway** (3:00). Changing it re-resolves the loaded profile and rebuilds the forecast network;
  - `stringline corridors` prints Headway and From columns.
- **Observed spacing** (Fred: build it). `stringline corridors --headways` lists each section's headway, source, WTT figure and pairs. `--log x.sslog` (which implies `--headways`) plays the recording through `LiveState`, turns its trains into a timetable (`ObservedTimetable`, Timetable.Live) and measures it the same way: observed 5th percentile, pairs and closest. It is reported only, never applied. Waterloo 4×: the main line ran 1:26–2:01 apart at the 5th percentile, over 55–165 pairs.
- **Results** (`ConflictFixtureTests.Wtt_headways_drop_false_conflicts_and_keep_the_real_ones`, at the end of each recording, against every section at 3:00):
  - 4×: 16 → 12 headway conflicts, none new; 6L49/6G15 and 2C85/2S91 (180 s each) stay;
  - failure: 3 → 2; the two clashes behind the failure (5S06/5O05, 2O05/2U07) stay, smaller (42 and 52 s);
  - Waterloo's whole day on time: 3 → 1 headway conflict;
  - Llangollen: too few trains follow each other, so every section stays Default 3:00 and its single line is unchanged.
- **Corpus** (284 WTTs, 12 749 sections on their generated corridors): 10 821 sections have enough pairs, and about 1 100 (10%) are under 3 min. So the global default stays 3 min.
- **Docs:** `06-…md` §3 (the rule and the results), `04-…md`, `05-…md` (the editor), the plan, README, `CLAUDE.md` (`corridors --headways --log`).
- **Tests:**
  - `HeadwayTests` (20, Network): the derivation on synthetic runs (two minutes apart; one tight pair; too few pairs; side by side on two tracks; the floor; an hour apart is not a pair; both directions), the resolution order, corridor create and refresh, clearing a hand-set headway, the profile and global defaults, hand-tuned and regenerating, the JSON round trip with the source, a version 1 profile, Waterloo's headways, and Llangollen unchanged;
  - `ConflictScenarioTests`: the conflict names its headway, and a WTT headway shrinks the clash. `ConflictFixtureTests`: before and after on both recordings;
  - `GraphConflictTests`: the tooltip's Headway row;
  - `ObservedTimetableTests` (3);
  - `CorridorsCommandTests`: the columns, `--headways`, `--log`, and a bad recording;
  - `ProfileEditorTests`: the From column and its tooltip, hand then blank, the profile's default; the Options default re-resolving the loaded profile (not saved in it), the editor hint, and a timetable loaded afterwards.
  - Existing tests changed to match: the profile version (now 2), a hand-set headway now needing its source, and Waterloo's first section now 2:54 (not 3:00).
- Totals: 1 536 (Core 259, Architecture 10, Gateway 207, Timetable 166, Network 160, Planning 234, Persistence 12, Graph 259, Tools 67, App 162). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 374).

### Decisions
- **Only tighten (Fred).** A WTT figure is used only where it is less than the default. The plan's 60–300 s range let the WTT raise a headway, and on Waterloo's Windsor lines that gave 18 headway conflicts in place of 16, with new ones at WANDS–PUTNEY (booked 5:00 apart). Trains booked far apart on a quiet line say nothing about the signalling.
- **Tracks by "free longest".** A section with more tracks than the WTT needs one way puts its runs on the track free longest. First-fit would pack them onto one track at the 1 min side-by-side limit, giving 1 min on four-track lines.
- **The global default is applied, not saved.** The profile file holds the sections' resolved headways and the profile's own default, but not the global one. The app applies the global default to every profile it loads or uses (`MainViewModel.WithGlobalHeadway`), so the file never goes stale.
- **A blank headway cell means automatic.** There is no separate "reset" button.
- **`DefaultHeadwaySeconds` keeps its name** in the JSON and in code, though it is now the headway used whatever its source.
- **Merged conflicts** carry the headway of their worst section.

### Next
- S28: user guide, in-app help and wiki (#4). It can describe the headway sources.

### Known issues
- **Not tried by hand.** 👤 Fred:
  - open Tools ▸ Edit profile… on a sim you play and look at the From column and its tooltips;
  - change Tools ▸ Options ▸ Default headway and see the conflicts change;
  - hover a headway conflict and check the Headway row.
- **A hand-set 3:00 in an old profile** is read as the default, so the WTT's figure may replace it. Type it in again if you meant it.
- **Observed spacing counts every train in the recording** on each corridor's sections, as the WTT's figure does. On shared sections (WLOO–VAUX) both corridors show the same figure.

### Questions for Fred
- 19. **Headways on your sims.** Run `stringline corridors your.wtt --headways` on the sims you play (and with `--log` for a recording if you have one). Do the WTT figures look like the signalling? Any section you know runs closer, or needs more room, can be set by hand.

## 2026-10-20 — S28: User guide, in-app help and wiki (#4)

### Done
- **The guide** (`docs/user-guide`, 17 pages, Australian English, SimSig's railway terms). Its pages are a contents page (`README.md`, with a five-minute start) and:
  - installing;
  - turning on the gateway;
  - connecting (single player, payware, train describer, dropped connections, a provisional multiplayer section, replays);
  - timetables and profiles (loading, matching, corridors, the profile editor, headways and where they come from);
  - reading the graph (layout, axes, lines, delay colours, moving around, hovering, the train list, status bar and clock);
  - re-planning (selecting, grips and their limits, nudges, the right-click menu, the train detail panel, undo and history);
  - disruptions and notes;
  - conflicts and suggestions (the kinds, headways, suggestions, knock-on);
  - the regulation list;
  - sessions, recordings and review (including replays);
  - exports and reports (the Delays tab, PNG, PDF, CSV);
  - settings (every Options tab, and the window layout);
  - troubleshooting and logs;
  - a glossary;
  - keyboard and mouse;
  - further reading.
- **Pictures** (`docs/user-guide/images`, 8 PNGs, about 2.8 MB), from Waterloo only. `build/guide-shots.ps1` makes them again:
  - `render-png` makes `graph.png` and `graph-by-delay.png`;
  - `GuideScreenshotTests` (App), run with `STRINGLINE_WINDOW_SHOTS` set, makes the main window (a running train selected, an hour across), its grips, and the Connect, Open replay, Options and profile editor windows.
  - The shots draw through a `VisualBrush`, so a dialog's margin no longer shifts and crops it. The dialogs show plain file names, not this machine's folders.
- **In-app help, offline** (Fred: render at run time). `HelpGuide` (App/Services, no WPF):
  - The guide's Markdown and images are embedded in the exe (`Stringline.Guide/…`).
  - On first use in a run, it renders them with **Markdig 1.4.0** (BSD-2-Clause; added to `Directory.Packages.props`, `THIRD-PARTY-NOTICES.md` and About's credits) into `%LOCALAPPDATA%\Stringline\help` (`AppPaths.Help`). The pages have a contents sidebar, light and dark styles, GitHub heading IDs, links pointed at the `.html`, and a footer linking the page on GitHub.
  - A heading is opened through a small `open.html`, because Windows drops the `#heading` when it hands a file to the browser.
  - **Help ▸ User guide** (F1 shown) opens the contents. **Getting started (online)** stays.
  - **F1** is `ApplicationCommands.Help` with one class binding on `Window` (`Views.Help`), so it works in dialogs too. The page is the inherited `views:Help.Topic` of the focused element (`HelpTopics`):
    - graph, train list, train detail, the four tabs and the replay transport;
    - Connect, Open replay, Options and the profile editor.
  - `MainViewModel.OpenGuide(topic)` writes the guide and opens it; a write failure is logged and shown.
- **Wiki** (Fred: workflow and script).
  - `build/publish-wiki.ps1 -Out dir` writes the wiki's pages: README becomes Home, `.md` links become wiki links with their headings kept, and the script adds images, a `_Sidebar` in contents order and a `_Footer` saying where to edit.
  - `-Push` clones, commits and pushes. The token goes to git as a header, never in a URL or the log. With no wiki repository yet, it warns and passes.
  - `.github/workflows/wiki.yml` runs it on pushes to `main` that touch the guide, and by hand.
- **Other sources.** I reviewed the SimSig wiki's [Interface Gateway](https://www.simsig.co.uk/Wiki/Show?page=usertrack:interface_gateway) page and the forum's [Interface Gateway board](https://www.simsig.co.uk/Forum/BoardView/266), and linked both from Further reading.
  - From the forum: SimSig fails with "cannot open a TCP port for hosting the information gateway" when 51515 is taken or blocked. That is now in Troubleshooting and on the gateway page.
- **Fixed on the way:** the Connect dialog's "Remember the password (encrypted for your Windows account)" ran off the dialog's right edge. Both check boxes now span the Browse column and wrap.
- **Docs:** `05-…md` §1 (Help menu) and new §13, the plan, README (a link to the guide, and a developers' section on keeping it), `CLAUDE.md`.
- **Tests:**
  - `GuideLinkTests` (3, Architecture, portable): every relative link, image and heading resolves (Markdig's GitHub IDs); links stay inside the guide; every page is in the contents; every image is used. `WordingTests` already covers the guide.
  - `HelpTests` (6, App): every guide file is embedded; every F1 topic's page and heading is rendered; the HTML's links, images and contents; `open.html`; an unknown page opens the contents; F1 on the graph, lists, detail and history; Help ▸ User guide gives the contents; F1 in the Connect dialog.
  - `GuideScreenshotTests` (2, App).
  - `WikiScriptTests` (1, App).
  - `MenuTests`: F1 is the Help command, not a window key binding.
- Totals: 1 548 (Core 259, Architecture 13, Gateway 207, Timetable 166, Network 160, Planning 234, Persistence 12, Graph 259, Tools 67, App 171). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 377).

### Decisions
- **Fred:**
  - embed the Markdown and render it at run time, with no build-time step;
  - the wiki by a workflow plus a script;
  - a provisional multiplayer section;
  - commit the pictures.
- **Markdig 1.4.0**, not 1.4.1 or 1.4.2: both are under two weeks old.
- **No F1 key binding on the window.** WPF's Help command already has F1, and a window binding would swallow it before the focused panel's page could be picked. The menu item sends "contents".
- **The guide is written again in each run.** That costs about 20 small files, and a new version's guide always replaces the last, even between builds of one version.
- **Links stay inside the guide.** Anything else, such as the README or the design docs, is linked on GitHub, so the bundled help and the wiki have no broken links.
- **Follow-up issues not raised (Fred).** To raise if wanted:
  - check the gateway page's Loader steps against SimSig's current Loader (also question 14);
  - have Stringline say why when port 51515 is already taken (by its own replay-server, say).

### Next
- S29: the multiplayer investigation (#5), when Fred's recording is ready. It replaces the provisional section in `connecting.md`.
- Keep the guide in step with UI changes. `guide-shots.ps1` remakes the pictures.

### Known issues
- **Not tried by hand.** 👤 Fred:
  - press F1 in a few panels and dialogs in the app, and use Help ▸ User guide;
  - read the guide for anything that does not match SimSig as you play it.
- **👤 The wiki needs its first page.** The repository's wiki is turned on but empty. Create any first page on GitHub, then run Actions ▸ Wiki ▸ Run workflow. Until then the workflow warns and passes.
- The guide's pictures are Waterloo at 08:05 on the 4× recording. They show WPF's off-screen rendering, which is close to the app on screen but not exact.
- The guide lists the Options defaults. If a default changes, the guide must change too: no test checks this.

### Questions for Fred
- 20. **The guide's gateway steps.** Do "tick **Interface Gateway** in the options for starting the sim" and the payware login match what you see in SimSig's Loader? This is question 14 again; the guide repeats the README.

## 2026-10-20 — S19: Opt-in assist (AS-1, AS-2)

### Done
- **Fred's choices** (asked at the start):
  - the route for a step is **asked for, then remembered** in the profile per move;
  - interpose acts on the **picked train**, into a berth the user chooses;
  - a **Session menu switch**, off again at each new session;
  - **live connections only** (`replay-server` counts; file replays never send).
- **Core:** `SignallingIndex` (Core.Live) holds every route, signal and TD berth ID seen, and what each berth holds now. It is immutable and returns the same instance when an event adds nothing. The engine keeps it outside `LiveState` (names, not state: kept through a rewind or a timetable rebuild, dropped on Clear) and publishes it as `PlanSnapshot.Signalling`. A new name triggers a publish; repeats do not.
- **Gateway:** `GatewayCommands.IsValidId`, the check the builders already made, now public for the dialogs. `SimSession.SendAsync` returns whether the frame went.
- **Profile:** `AssistMove` (location, platform, line, from, towards; tidied by `Of`) and `AssistRoute` (a route ID or two signals). `StringlineProfile.AssistRoutes` (JSON `assistRoutes`), `FindAssistRoute`, `WithAssistRoute` (one per move). The format version stays 2: an older Stringline ignores the new property.
- **Planning:** `AssistMoves.Of(plan, index)`: the move at a plan point, with the nearest other locations before and after it on the running path.
- **App** (`MainViewModel.Assist`, `05-…md` §14):
  - **Session ▸ Assist: send commands to SimSig…**, enabled only while live. Turning it on warns (what it does; **in multiplayer it acts on the shared sim**; until the session ends) with **Allow for this session** / Cancel. Choosing it again turns it off. It turns off when the session stops, fails or is replaced.
  - With assist on, the graph's train menu adds Re**q**uest route in SimSig… and Interpose **d**escription in SimSig…. The regulation list now has a selection and a right-click menu with both (enabled only while assist is on).
  - **Request route:** the heading describes the move ("2S01 calls at Bletchley (platform 2) at 08:06, from … towards …"). The user gives a route ID or both signals. The lists offer what is remembered at the location, then SimSig's IDs from the snapshot. "Last time: …" is shown, and the remembered route filled in.
  - **Interpose:** a berth from those seen, the headcode as description, and which berth SimSig shows the train in now, if any.
  - Every dialog has a note (sent at once; SimSig does not say whether it did it, so check the panel; multiplayer) and a **Send to SimSig** button (`PromptViewModel.Note`, `AcceptText`). Each command, and assist turning on and off, is logged.
- **Docs:** new guide page `assist.md` (and contents, glossary, keyboard and mouse, regulation, re-planning, gateway and connecting pages, which said Stringline only reads); `05-…md` §1 and new §14; `03-…md` §4; `01-…md`; `02-…md`; `00-…md` §5 question 4; the plan.
- **Tests:**
  - `SignallingIndexTests` (4, Core);
  - `GatewayCommandsTests`: `IsValidId`;
  - `PlanningEngineTests`: the names are published, kept through a rewind and dropped on Clear;
  - `AssistMovesTests` (3, Planning), including the profile keeping one route per move;
  - `ProfileTests`: the JSON round trip of `assistRoutes`;
  - `AssistTests` (6, App, against `FakeStompServer`):
    - off by default, with nothing asked or sent without a live session;
    - the warning names multiplayer, Cancel leaves it off, it turns off on disconnect and stays off on reconnecting;
    - a route request's dialog, its validation, the exact body sent, the route remembered in the saved profile and offered again, and the signal-pair form;
    - an interpose's berths, body, the berth SimSig shows the train in, and a failed validation sending nothing;
    - the regulation and graph menus only while it is on;
    - a replay cannot turn it on;
  - `WindowTests`: the assist dialog shows its note and Send button with no binding errors.
- Totals: 1 569 (Core 263, Architecture 13, Gateway 211, Timetable 166, Network 161, Planning 238, Persistence 12, Graph 259, Tools 67, App 179). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 390).

### Decisions
- **Nothing is guessed.** The WTT and profile name no signals, so the user gives the route the first time. Inferring it from the signals trains passed (`train_location` at signals) was offered and not chosen.
- **The move is the key**: location, platform, line, and where the train comes from and heads for. A different platform or direction is a different route, and the user is asked again.
- **The signalling names are not state.** A rewind or a timetable change keeps them; they would only come back with the next snapshot, which SimSig sends only on connect.
- **The graph shows the assist items only while assist is on**, so its menu is unchanged for everyone else. The regulation list shows them disabled, with a tooltip saying how to turn assist on.
- **Only route requests and interposes.** TD step and cancel and signal replacement have builders but are not offered.

### Next
- S29: the multiplayer investigation (#5), when Fred's recording is ready. It should check what assist does there, if Fred wants to try it.
- S20 (optimiser) and S21 (multi-corridor views) remain optional.

### Known issues
- **Never tried against a real SimSig.** 👤 Fred: connect with the TD topic ticked, turn on assist, and on a quiet part of the panel:
  - request a route by its ID, and by its two signals, and check SimSig sets it;
  - request one that cannot be set (against a set route) and see what SimSig does: whether it queues it, refuses quietly or shows anything;
  - interpose a headcode into an empty berth.
- SimSig does not answer a command, so Stringline cannot say whether it worked; the dialogs and the guide say to check the panel.
- Without the TD topic the lists are empty: the user must type IDs as SimSig names them. The dialogs say so.

### Questions for Fred
- 22. **Assist on a real sim.** Do route requests and interposes work as above? Is the route ID or the signal pair easier to find in SimSig (the panel's hover, or the F2 route list)? If signals are easier, the dialog could put them first.

## 2026-10-20 — S20: Optimiser (FC-6)

### Done
- **Fred's choices** (asked at the start):
  - OR-Tools CP-SAT, as planned;
  - holds **and** platform changes;
  - Plan ▸ Re-plan over the whole horizon, shown in the suggestions panel;
  - 60 min and 5 s by default, both in Options.
- **Planning** (`Replanning.cs`, `RuleReplanner.cs`; `06-…md` §5):
  - `IReplanner` → `ReplanResult`: the commands, the steps (`ReplanStep`), the conflicts in the horizon before and after, the delay, impacts and changed trains, and the plan with it. `ToCommand()` is one `AcceptSuggestion` ("Re-plan to 09:05: 4 holds, 1 platform change").
  - `ReplanEvaluator` checks and scores a candidate as the engine would. Better means fewer conflicts in the horizon, then less weighted severity, then less weighted delay plus penalty.
  - `RuleReplanner`: the suggestions, the earliest clash between trains first.
- **Optimiser** (new project `Stringline.Optimiser`, net10.0; Google.OrTools 9.15.6755, Apache-2.0; `CpSatReplanner`, `ReplanModel`):
  - The model's trains follow the plan builder and forecaster exactly (tested): run times, dwells, formations, and how a hold moves the plan on while keeping dwells.
  - Holds are where suggestions hold, to the half minute. Platform moves bring the train forming or formed from it.
  - Either-or orders come from the detector's spacing rules, for each clashing pair and its trains' neighbours.
  - Rounds check the plan with the forecaster and add what it still finds. A last pass drops steps that do not help.
  - Debug logging per round.
- **App** (`05-…md` §15):
  - Plan ▸ Re-plan the next 60 min… (Ctrl+Shift+R) works off the UI thread and shows one row in the suggestions panel: title, cost line, each change, tooltip, Preview and Apply, Close.
  - It stays while the sim moves on, until a conflict is picked. Apply refuses it once the plan has changed.
  - Options ▸ Suggestions: optimiser or rules, platform changes, minutes ahead (10–240), time limit (1–60 s).
  - The suggestion rows are now general (`SuggestionRowViewModel` carries the command, plan and changed trains; `Suggestion` and `Replan` are nullable).
- **Tools:** `stringline replan --wtt --log [--at] [--minutes] [--seconds] [--rules] [--no-platforms] [--no-knock-on] [--verbose]`.
- **Licences:** OR-Tools and what it bundles (Protocol Buffers, Abseil, RE2, SCIP, HiGHS, zlib, bzip2; BSD-3, Apache-2.0, MIT, zlib, bzip2) are in About and `THIRD-PARTY-NOTICES.md`. The win-x64 zip grows from 69 to 92 MB; the single-file exe is 98 MB with the solver inside.
- **Fixed on the way:** King's Cross 2020 (added to the WTT corpus today) has `ShuntInterpose` on its trains, a saved-game field. It is now ignored with the other Shunt… fields, so the corpus test passes.
- **Docs:** `06-…md` §5; `05-…md` §1 and new §15; the plan; `01-…md`; `02-…md`; `CLAUDE.md`; README; the guide's conflicts and suggestions (new "Re-planning the next hour"), settings, and keyboard and mouse pages.
- **Tests:**
  - `Stringline.Optimiser.Tests` (new, portable, 22):
    - scenarios for both re-planners: hold at origin; priorities make the swap; single-line crossing; another platform; platforms only if allowed; nothing in the horizon; several conflicts; the command;
    - the model against the forecaster;
    - Waterloo fixtures (the 4× recording half way and at its end, the failure recording), each checked by applying the result.
  - `ReplanCommandTests` (3, Tools).
  - `ReplanPanelTests` (2, App): preview, apply, kept while the sim moves, conflict pick, close; a stale re-plan refused.
  - `SettingsTests`: the new settings round trip.
- Totals: 1 597 (Core 263, Architecture 14, Gateway 211, Timetable 166, Network 161, Planning 238, Persistence 12, Graph 259, Optimiser 22, Tools 70, App 181). Green on Windows (Debug), and on Linux (`sdk:10.0` container, portable filter, Release: 1 416, the native solver included).

### Decisions
- **The optimiser is checked by the forecaster and detector**, which have the last word. The model makes candidates; it never sets the result on its own. Pairs are added lazily, from what the detector still finds, so the model stays small: 125–151 trains and 3–200 pairs at Waterloo.
- **Clashes under the least conflict reported (30 s) are allowed**, as the detector allows them. Without this the model paid for spacing nobody sees, and lost to the rules.
- **Multi-track sections:** a pair's order needs spacing only once the detector finds them clashing that way round, as the train behind may take another track.
- **A held second costs a little** (0.2 weighted s) beyond the delay at the train's last point. A train that recovers by then is still late on the way, so the shortest, latest hold wins.
- **Settings on the Suggestions tab**, not Forecast and conflicts as my question said: they sit beside the penalties they use.
- **Not worked out again as the sim moves on** (it takes seconds), unlike suggestions; Apply refuses it once the plan has changed.
- **Blocks and runs planned too fast** stay with their suggestions: they are about one train, not the order of trains.

### Next
- S21 (multi-corridor views) remains optional; S29 when Fred's multiplayer recording is ready. *(S21 done next.)*
- Perhaps: polish the optimiser's plan with the rules for what is left; line changes; a deterministic time limit for repeatable plans.

### Known issues
- **Not tried by hand.** 👤 Fred: in the app, on a late-running sim, use Plan ▸ Re-plan, preview it and apply it; try the published exe (the solver unpacks from it on first use).
- On a busy horizon, the plan found in the time limit varies from run to run and with the machine's load. On Waterloo's 4× recording at its end (21 conflicts), the optimiser left 12 to 18, the rules 16. Most of those are trains arriving at Waterloo's platforms now, which nothing can clear.
- The optimiser can make a plan with more weighted delay than the rules' when it clears more conflicts: fewer conflicts come first.

### Questions for Fred
- 23. **Re-plan on your sims.** Do the re-plans look like what a signaller would do? Is clearing more conflicts at the cost of more delay the right way round, or should delay weigh more?
- 24. **Download size.** The solver adds about 23 MB to the zip. Acceptable, or should the optimiser be an optional download?

## 2026-10-20 — S21: Boards and plots (GR-8, GR-10)

### Done
- **Fred's choices** (asked at the start):
  - split panes **and** board windows;
  - locked in time;
  - full editing on every board;
  - a multi-page plot, time running down.
- **Graph** (portable):
  - `GraphTimeLink` with `GraphNavigator.MatchTime`: a change of time range or follow mode on one navigator is matched by the rest. Each keeps its own corridor range, size and orientation.
  - `GraphPlot`, `GraphPlotOptions` and `GraphPlotPage`: a time range at a fixed number of minutes a page, laid out on pages at one scale. The last page is cut short. Problems are reported (end before start, under 5 min a page, more than 100 pages).
  - `SkiaGraphRenderer.RenderPlot` writes the multi-page PDF, each page headed "… — 08:00–10:00 — page 2 of 3".
  - `PdfPage.A3Portrait`, `A4Portrait` and `Paper`.
- **App** (`05-…md` §16):
  - `GraphBoardViewModel`: a board's corridor, navigator, scene, ghosts, grips, draft, selection and placeholder. `MainViewModel.Boards` holds them: `MainBoard`, `SplitBoard`, `BoardWindows`, `ActiveBoard`.
  - `Scene`, `Navigator`, `Ghosts`, `Grips` and `DraftDisruption` now pass through to the main board. The editing code works on the active board through a per-board `BoardEditor`.
  - Ghosts are plans (`GhostPlan`) drawn on every board whose corridor the train runs on. Grips are worked out per board.
  - View ▸ Split view ▸ Off / One above the other / Side by side (`LayoutViewModel.Split`). The second board has a corridor box and a ✕; the splitter between the two keeps their shares.
  - View ▸ New board window and Close board windows (`BoardWindow`, `BoardView`). Board windows have undo, redo and export shortcuts.
  - Remembered: the split, its shares and corridor, and each board window's corridor and place (`WindowLayout`, now with value equality). Board windows reopen when the main window is shown.
  - File ▸ Export ▸ Plot (multi-page PDF)… asks for the corridor, times, minutes a page, paper and direction, and remembers the choices (`AppSettings.Plot`).
  - Picking a conflict centres the active board, else another board that shows it, else the main one switches corridor. Exports and Whole corridor act on the active board.
- **Tools:** `render-png --plot [--from] [--to] [--per-page] [--horizontal]`. `--page` takes `a3p` and `a4p`, and a vertical plot defaults to A3 portrait.
- **Docs:**
  - the guide: "Boards: split view and board windows" (also its F1 topic), "Plotting a train control diagram", keyboard and mouse, glossary (Board, Plot) and contents;
  - design docs: `05-…md` §1 and new §16; `01-…md` GR-8 and GR-10; `02-…md`; `00-…md` §3; the plan;
  - `CLAUDE.md` and the README.
- **Tests:**
  - `GraphTimeLinkTests` (4) and `GraphPlotTests` (8), both in Graph;
  - `RenderPngCommandTests`: the plot;
  - `BoardTests` (5, App):
    - the split view: corridor, time lock, orientation, close and come back;
    - board windows: shared selection, remembered and restored;
    - a grip drag on the split board, with its ghost on both boards;
    - exports and Whole corridor on the active board;
    - the plot and its refusal;
  - `BoardWindowTests` (1, WPF): the split laid out both ways, and a board window opened and closed, with no binding errors.
- Totals: 1 616 (Core 263, Architecture 14, Gateway 211, Timetable 166, Network 161, Planning 238, Persistence 12, Graph 271, Optimiser 22, Tools 71, App 187). Green on Windows (Debug). Linux was not run this session; nothing portable uses Windows.

### Decisions
- **One active board.** Editing state (a drag, a B+drag, the picked grip) belongs to the board it started on. The board last pressed, wheeled or typed on is the one exports and Whole corridor use; it goes back to the main board when its window closes.
- **The main board keeps the toolbar's tabs.** The other boards each have their own corridor box. A new board opens on the first corridor no board shows.
- **Board windows are owned by the main window**, so they close and minimise with it. They can still go on any monitor.
- **A plot has no selection** (nothing is dimmed), and it shows the layers that are on. The now line is drawn where it falls.
- **Reset layout keeps the split and board windows**, and gives the split equal shares.

### Next
- S29: the multiplayer investigation, when Fred's recording is ready.
- Perhaps: guide pictures of the split view and a plot page (`guide-shots.ps1`).

### Known issues
- **Not tried by hand.** 👤 Fred: on a replay, turn on the split view both ways, open a board window on a second monitor, drag a train on it, close and restart the app, and print a plot of an afternoon on A3.
- Every board builds its own scene on each snapshot. Three or four boards at Waterloo should be fine, but this is not measured.

### Questions for Fred
- 25. **Plot scale.** Is two hours a page on A3 portrait (about 3 mm a minute) a good default? Would you rather choose millimetres a minute, or have the plot fit the time onto a set number of pages?
- 26. **Boards.** Do the board windows need their own toolbar (layers, 1h/3h/6h, Follow), or are the main window's enough, since the boards are locked in time?

## 2026-10-20 — Open-question review

### Done
- Went through every open question (1–26) and open GitHub issues #1, #5 and #14 with Fred. Four new sessions went into `07-implementation-plan.md` as **Phase 4b (S30–S33)**.
- **Answered and closed:**
  - 13/15 (#1, code signing): signing with `signtool` and a PFX is built (S22). **Move to SignPath** once downloads grow enough to support an application (Fred's comment on #1, confirmed in the review). Until then #1 stays open and the README's SmartScreen note stays.
  - 14/20 (Loader wording): the README and guide match SimSig's Loader.
  - 18 (S25 naming): all kept.
  - 21 (release 0.1.1): closed (Fred). v0.2.0 replaces it, and V0.1.0 stays as it is.
  - 23 (re-plan): conflicts first stays.
  - 24 (download size): the 23 MB solver stays bundled.
  - 25 (plot scale): two hours a page stays; `--per-page` is enough.
  - 26 (boards): the main window's toolbar is enough.
- **Answered, with work to do:**
  - #14 (privacy) → **S30**: a policy, an audit of what is stored, Forget saved password, and an uninstall choice.
  - 10 (line styles) → **S31**: the forecast becomes dash-dot.
  - 17 (sim name) → **S31**: a sim name equal to any of the sim's WTT titles counts as unset.
  - 5 and 7 (WTT rules) → **S32**: apply the rules to entry and departure forecasts, after checking `Time` is in seconds.
  - 12 (holds at signals) → **S33**: investigate placing signals or berths on the corridor first, then build if that works.
- **Still open** (Fred has not tried them yet): 3 and 16 (multiplayer and level-crossing recordings; S29 stays blocked), 6 and 19 (corridors and headways on Fred's sims), 8, 9 and 11 (crossing tolerance, margins, penalties: keep the defaults for now), 22 (Assist: route ID or signal pair).

### Next
- S30 (or S31), then S32 and S33. S29 when the multiplayer recording exists.

## 2026-10-20 — S30: Privacy policy and stored data (#14)

### Done
- **Fred's choices** (asked at the start): Forget saved password in **both** the Connect dialog and a new Options ▸ Connection tab; the uninstaller also removes the unpacked files in `%TEMP%\.net\Stringline` when asked to remove data.
- **Audit** (written into the policy):
  - roaming `%APPDATA%\Stringline`: `settings.json` (host, port, user name, the password only when remembered, DPAPI for the current user) and `profiles`;
  - local `%LOCALAPPDATA%\Stringline`: `logs` and `help`; the single-file exe unpacks native libraries to `%TEMP%\.net\Stringline`;
  - `Documents\Stringline`: sessions, recordings, exports;
  - network: only `GatewayClient`'s `TcpClient` (and the developer `replay-server`'s listener). No telemetry, no update check; links open only when clicked;
  - the passcode goes only in the STOMP CONNECT frame. The connection's log line has the user name and `passcode=***`. `.sslog` headers hold host, port and topics; sessions hold host and port.
- **`PRIVACY.md`** at the root, copied to `docs/user-guide/privacy.md` (so the wiki gets it from the contents); linked from the README (new Privacy section), the guide's contents, connecting, settings and installing pages.
- **App:**
  - `AppSettingsStore.HasPassword`, `WithoutPassword` and `ForgetSavedPassword` (load, clear, save; writes nothing if there is nothing to forget);
  - `MainViewModel.ForgetSavedPassword`, used by `ConnectViewModel.ForgetPasswordCommand` (clears the box too) and `SettingsViewModel.ForgetPasswordCommand` (OK afterwards does not bring it back);
  - Connect dialog: the Remember tooltip says how it is stored, a Forget button and a Privacy link; Options ▸ Connection tab; About: a privacy line and link; Help ▸ Privacy (`OpenGuideCommand`, so it does not show F1);
  - `HelpTopics.Privacy`; `Help.TopicFor` opens a topic the command names;
  - `Stringline.exe --forget-password` on its own removes a saved password and exits, with no window or log.
- **Installer:** `[UninstallRun]` runs `--forget-password` before the files go; after them, `/REMOVEUSERDATA` (or Yes to the question, default No) removes `%APPDATA%\Stringline`, `%LOCALAPPDATA%\Stringline` and `%TEMP%\.net\Stringline`. Documents is never touched. Compiled with ISCC here.
- **`build/test-upgrade.ps1`:** after the upgrade checks, copies the user's data aside, uninstalls silently (settings kept, password gone, profiles and logs kept), reinstalls, uninstalls with `/REMOVEUSERDATA` (all gone, Documents untouched), and puts the data back in `finally`.
- **Docs:** `02-…md` §5, `05-…md` (credentials, command line), the plan, `CLAUDE.md`, README, guide.
- **Tests:** `PrivacyTests` (8, App): a connected, recorded session with Debug logging leaves the password in no file (UTF-8, UTF-16 or base64) and only in the CONNECT frame; unticking Remember and connecting forgets it; Forget in the Connect dialog (cancelled) and in Options (OK and Cancel); the uninstaller's forget; the policy is the guide's copy, embedded, in the contents and the README; the Help, About, Connect and Options links open `privacy.html`.
- Totals: 1 624 (Core 263, Architecture 14, Gateway 211, Timetable 166, Network 161, Planning 238, Persistence 12, Graph 271, Optimiser 22, Tools 71, App 195). Green on Windows (Debug). Linux not run; nothing portable changed.

### Decisions
- **Forget acts at once**, not on OK: the plan asked for that, and it means Cancel cannot leave the password behind.
- **The uninstaller asks the app to forget** (`--forget-password`) rather than editing the JSON in Pascal, so the tested C# code does it.
- **The guide's page is an exact copy** of `PRIVACY.md`, with only absolute links, and a test keeps them equal.
- **The user name is logged** with each connection, as before; the policy says so.

### Next
- S31 (forecast dash-dot, Q17 sim names), then S32 and S33.
- Perhaps: `guide-shots.ps1` again for the Options picture (it now has a Connection tab).

### Known issues
- **`test-upgrade.ps1` was not run here**: Fred's all-users 0.3.0 install was in place, and Fred chose to leave it, so the Release workflow is its first run. The `.iss` compiles and the script parses.
- 👤 Fred: try Forget in both places, Help ▸ Privacy, and an uninstall answering Yes and No.
- The guide's Options picture does not show the new Connection tab yet.

### Questions for Fred
- 27. **Privacy wording.** Is `PRIVACY.md` what you want people to read? In particular the note that a multiplayer login crosses the network unencrypted (SimSig's gateway has no TLS).

## 2026-10-21 — S31: Small fixes from the review (Q10, Q17)

### Done
- **Fred's choice** (asked at the start): a sim name in a profile saved before S31 is settled by reading the titles of the sim's other `.wtt` files in the loaded WTT's folder.
- **Q10: the forecast is dash-dot.** `LayerStyle.Dashed` became `DashDot`; `GraphTheme.Forecast` is 1.5 px dash-dot. `SkiaGraphRenderer.ForecastPattern` (7, 4, dot, 4) and `CategoryPattern(style, width)` (dashed 12–7, dotted round caps two widths apart) are public so a test can compare them. The B+drag draft keeps the old 6–4 dash. PNG, PDF and plot use the same renderer, so they changed with it.
  - Text: the Forecast layer tooltip, the profile editor's category hint, README, the guide's reading-the-graph table and glossary, `05-…md` §2 and `00-…md`.
  - `build/guide-shots.ps1` run: every picture remade (graph pictures show dash-dot; Options now shows the Connection tab from S30).
- **Q17: a sim name that is only a WTT title counts as unset.**
  - `StringlineProfile.TimetableTitles` (titles of the WTTs it was made or refreshed from; `Create` and `Refresh` add them) and `NameByHand` (written only when true). Older Stringlines ignore both, so the version stays 2.
  - `SimName(header)` drops a name equal, ignoring case and spaces (`SimTitle.SameTitle`), to the loaded WTT's title or any remembered title, unless `NameByHand`. `Title(areaId, header, review)` builds the `SimTitle` (with the new `keepName`, so a typed name equal to the current title is kept).
  - `ProfileEdits.WithName` (the editor) marks a name hand-set; blank clears both.
  - Pre-S31 names: `HasUnsettledName` (set, not hand-set, not a known title). `SimNames.Settle` (Network) reads the headers of the sim's `.wtt` files in the loaded WTT's folder and below (`WttCatalogue.FindForArea`): a match is remembered as a title, otherwise the name is marked hand-set. So each profile is searched at most once. `TimetableLoader` settles after refreshing a saved profile.
  - Callers: the main window title and status bar, the graph title band, sessions (they now save the resolved name) and `report` use the profile's `Title`/`SimName`.
- **Tests:** Core +7 (`SameTitle`, a spaced title, `keepName`); Network +9 (`SimNameTests`: titles remembered, stale title, loaded title, typed name, no name, JSON round trip, a pre-S31 file, settling, reading a folder); Graph +4 (no category pattern equals the forecast's at any layer width; forecast drawn dash-dot, dashed and dotted not, by measuring ink runs) and the forecast snapshot changed once; App +1 (a pre-S22 profile named after timetable A, loading B from a folder holding both, then typing B's title in the editor).
- Totals: 1 645 (Core 270, Architecture 14, Gateway 211, Timetable 166, Network 170, Planning 238, Persistence 12, Graph 275, Optimiser 22, Tools 71, App 196). Green on Windows (Debug), see the known issue below. Linux not run.

### Decisions
- **Dash-dot pattern** 7–4–dot–4 at 1.5 px with round caps: a 8.5 px dash and a 1.5 px dot, 2.5 px gaps. Shorter than a dashed category's 12–7, and unlike dots it has dashes.
- **The draft's 6–4 dash stays**: it is a box border, not a train line.
- **A name that matches no sibling title becomes hand-set**, so the folder is not searched again on every load. If the old WTT has been deleted or is in another folder, that name stays; clearing it in the profile editor fixes it.
- **A saved session's own sim name** still goes through `SimTitle`'s old check against its timetable's title (sessions from before S31 may hold a stale name). So a typed name that is exactly the session's timetable title shows as the area ID when reviewing it from the session list or the `report --session` title. Rare; not worth a session-format change.

### Next
- S32 (WTT rules in the forecast), then S33.

### Known issues
- `ReplannerFixtureTests.Re_plan_the_next_hour_part_way_through_a_recording` (Waterloo, part 0.5, optimiser) failed once in the full parallel run and passed three times alone. It is time-limited, so it seems to be machine load; nothing in S31 touches the optimiser. Watch it on CI.
- 👤 Fred: look at the forecast's dash-dot on a replay, next to a dashed and a dotted category; and load another timetable of a sim whose profile still has an old title as its name.

### Questions for Fred
- 28. **Dash-dot.** Is 7–4–dot–4 at 1.5 px clear enough on your screen, or would you like a longer dash?

## 2026-10-21 — Issue review (#16–#19)

### Done
- Reviewed open issues #16, #17, #18 and #19 with Fred (#1 and #5 left as they were). Six sessions went into `07-implementation-plan.md` as **Phase 4c (S34–S39)**. They run before S32, as #19 asked, and S33 is now its build only.
- **Findings from the code:**
  - The gateway already sends signal passes. They are about two thirds of `train_location`s, with aspects. SimSig also reports timing locations that a train's WTT leaves out. `Forecaster.StartRunning` skips both, and `GraphSceneBuilder.AddActual` draws only timing locations. So a train on a sim with few timing points shows no progress for long stretches (#17), and headways and conflicts are only checked at timing points (#19).
  - The WTT's `SeedGroups` (Penrith: 04:00, 14:00) and each train's `SeedGroup` are ignored on import. The forecaster skips only trains that were over before the session started, so unseeded trains that should have been part-way through their run, and other groups' seeded trains, are still forecast (#16).
  - The fixtures are short for learning: `waterloo-1x` has 614 `train_location`s, and `waterloo-4x` has no TD topic.
- **Fred's choices:**
  - learn signal places from play (recordings and every session), shareable through profile export and import; not AI from schematics;
  - all four of: unbooked locations, signal passes on the line, a live head to now, and extra corridor points;
  - block headways and catching-up checks at signals (not full block occupancy yet);
  - track circuits placed with the signals;
  - learning on by default, reviewable in the profile editor;
  - automatic "Not running" for trains SimSig will not run, with Run anyway;
  - README for users, and developer content in `docs/developer.md`.

### Next
- S34 (#16) and S35 (#18), then S36 → S37 → S38 → S39, then S32 → S33.
- 👤 Fred, before S37: record 1–2 h with `--td` on the sim where #17 showed and on a busy sim, at 1×–4×.

### Questions for Fred
- 29. **Which sim showed #17?** Name it in the recording's notes, so S36's baseline and S37's gate use it.

## Questions for Fred
See `00-brainstorm-and-decisions.md` §5. New from S1:
1. ~~Copyright holder?~~ Resolved: "Frederick Duncan and Stringline contributors".

New from S2:
2. ~~Heart-beats, idle drops, dialogs?~~ Resolved in S3: none needed, no drops, no dialogs; a wrong password gives ERROR (see `03-…md` §6).

New from S3:
3. When convenient: a short **multiplayer** recording (§6.9), and a recording from a sim with **level crossings or AHBs** (to verify those `SG_MSG` fields).
4. ~~A single-line sim with crossing loops?~~ Resolved in S4: Llangollen (`tests/fixtures/llangollen 2012 B.WTT`).

New from S4:
5. ~~Rule `Time` is taken as seconds. If you know otherwise (e.g. from the timetable editor), say so; otherwise S11 will check it against a recording.~~ → S32 checks it.

New from S6:
6. When convenient, run `stringline corridors` on the sims you play most and say whether the main corridor and branches look right. In particular: are all of Llangollen's sections really single line (LLANGOLLEN–GOODSJNC is hinted too)?

New from S11:
7. ~~The forecaster does not yet use the WTT's **rules** (for example "X appears after Y leaves", 331 at Waterloo). They would hold an entering train until another has gone. Should S13/S14 apply them to entry forecasts? (Question 5, whether rule `Time` is in seconds, is still open: S11 did not need the rules.)~~ Yes (Fred, 2026-10-20) → S32.
8. Is a **minute's tolerance** right for booked single-line crossings? The forecaster keeps a booked overlap of up to 60 s between opposing trains as rounding, the same as the single-line hint (S6).

New from S14:
9. Is **1 minute** right as the default platform re-occupation margin, and **30 s** as the least conflict shown, once you have tried them on a busy sim? Both are in `ConflictOptions`.

New from S14a:
10. ~~When you have tried line styles: are dashed and dotted categories distinct enough from the forecast's dashes, or should the forecast change instead (e.g. thinner, or dash-dot)?~~ Change the forecast to dash-dot (Fred, 2026-10-20) → S31.

New from S15:
11. Are the suggestion **penalties** right: 2 min for a platform or line change, 30 min for terminating short, 60 min for cancelling (all weighted like delay)? They are in `SuggestionOptions`.
12. ~~Should suggestions **hold a train at a signal** (e.g. outside a junction), which SimSig can do but the WTT does not time? That would give suggestions for trains already on their way.~~ Yes (Fred, 2026-10-20) → S33, investigation first.

New from S18:
13. ~~**Code signing:** unsigned downloads get a SmartScreen warning. Is that acceptable for now, or should releases be signed (a certificate costs money; SignPath offers free signing for open-source projects)?~~ Signtool + PFX built in S22; SignPath once downloads grow (Fred, #1).
14. ~~Does the README's "Turn on the Interface Gateway" step match what SimSig's Loader calls it now?~~ Matches (Fred, 2026-10-20).

Since S22 the questions are numbered in each session's notes (15–28); see "Open-question review" above for which are still open.
