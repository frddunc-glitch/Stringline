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

## Questions for Fred
See `00-brainstorm-and-decisions.md` §5. New from S1:
1. ~~Copyright holder?~~ Resolved: "Frederick Duncan and Stringline contributors".

New from S2:
2. ~~Heart-beats, idle drops, dialogs?~~ Resolved in S3: none needed, no drops, no dialogs; a wrong password gives ERROR (see `03-…md` §6).

New from S3:
3. When convenient: a short **multiplayer** recording (§6.9), and a recording from a sim with **level crossings or AHBs** (to verify those `SG_MSG` fields).
4. ~~A single-line sim with crossing loops?~~ Resolved in S4: Llangollen (`tests/fixtures/llangollen 2012 B.WTT`).

New from S4:
5. Rule `Time` is taken as seconds. If you know otherwise (e.g. from the timetable editor), say so; otherwise S11 will check it against a recording.

New from S6:
6. When convenient, run `stringline corridors` on the sims you play most and say whether the main corridor and branches look right. In particular: are all of Llangollen's sections really single line (LLANGOLLEN–GOODSJNC is hinted too)?

New from S11:
7. The forecaster does not yet use the WTT's **rules** (for example "X appears after Y leaves", 331 at Waterloo). They would hold an entering train until another has gone. Should S13/S14 apply them to entry forecasts? (Question 5, whether rule `Time` is in seconds, is still open: S11 did not need the rules.)
8. Is a **minute's tolerance** right for booked single-line crossings? The forecaster keeps a booked overlap of up to 60 s between opposing trains as rounding, the same as the single-line hint (S6).

New from S14:
9. Is **1 minute** right as the default platform re-occupation margin, and **30 s** as the least conflict shown, once you have tried them on a busy sim? Both are in `ConflictOptions`.

New from S14a:
10. When you have tried line styles: are dashed and dotted categories distinct enough from the forecast's dashes, or should the forecast change instead (e.g. thinner, or dash-dot)?

New from S15:
11. Are the suggestion **penalties** right: 2 min for a platform or line change, 30 min for terminating short, 60 min for cancelling (all weighted like delay)? They are in `SuggestionOptions`.
12. Should suggestions **hold a train at a signal** (e.g. outside a junction), which SimSig can do but the WTT does not time? That would give suggestions for trains already on their way.
