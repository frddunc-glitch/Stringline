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
- None. CI has not run yet; it starts on the first push to GitHub.

## Questions for Fred
See `00-brainstorm-and-decisions.md` §5. New from S1:
1. Licence for the GitHub repo? (No `LICENSE` file added yet. Personal-first per §5.6.)
