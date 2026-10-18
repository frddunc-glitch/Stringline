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

## Questions for Fred
See `00-brainstorm-and-decisions.md` §5. New from S1:
1. ~~Copyright holder?~~ Resolved: "Frederick Duncan and Stringline contributors".

New from S2:
2. When you record, does SimSig send heart-beats or drop idle clients? Does it show any dialog when Stringline connects or disconnects? (We send no `heart-beat` header and a plain DISCONNECT.)
