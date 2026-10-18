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
