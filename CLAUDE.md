# CLAUDE.md — Stringline

Stringline is a Windows desktop app that shows a live electronic train graph (time–distance) for a running SimSig simulation via the SimSig Interface Gateway (STOMP, port 51515), and lets the user re-plan late services around delays and disruptions, ViziRail-ETG style.

## Read first
- `docs/00-brainstorm-and-decisions.md` — why things are the way they are
- `docs/07-implementation-plan.md` — which session you are doing
- `docs/progress.md` — what previous sessions did (create it if missing)
- Session-specific docs listed in the plan

## Stack
C# / .NET 10 LTS. WPF + CommunityToolkit.Mvvm for the shell; SkiaSharp for the graph. xUnit + FluentAssertions + Verify. Serilog. Microsoft.Data.Sqlite. System.CommandLine for `Stringline.Tools`.

## Rules
- Only `Stringline.App` (and its test project) may reference WPF or target `net10.0-windows…` (the App targets `net10.0-windows10.0.19041.0` for SkiaSharp.Views.WPF 4). Everything else targets `net10.0` and must build and test on Linux.
- All times are `SimTime` (int seconds after midnight of sim day 0, may exceed 86400). Never use `DateTime` for sim time.
- Gateway JSON: numbers and booleans may arrive as strings — use the lenient converters. Snapshot bodies contain duplicate keys — parse with `Utf8JsonReader`, never `JsonDocument`.
- Never send hand-built JSON to SimSig; use the tested command builders. Assist (outbound) features are opt-in and off by default.
- Planning engine is single-writer; UI reads immutable `PlanSnapshot`s.
- Unknown/malformed messages are logged and skipped — never crash.
- Never log credentials.
- Australian English in UI text and docs; railway terms as SimSig uses them (headcode, TIPLOC, Up/Down).
- Payware-sim recordings and WTTs live in git-ignored `local-fixtures/`; only free-sim data goes in `tests/fixtures/`.
- Licence is GPL-3.0-or-later. Every new dependency must be GPLv3-compatible (MIT, BSD, Apache-2.0, LGPL, GPLv3 are fine; GPLv2-only, proprietary or "commercial licence" packages such as FluentAssertions 8.x are not). Check before adding to `Directory.Packages.props`.

## Commands
- Build: `dotnet build -warnaserror`
- Test: `dotnet test`
- On Linux (no WPF): `dotnet build Stringline.Portable.slnf -warnaserror` and `dotnet test Stringline.Portable.slnf`
- New test projects go in `tests/`, and in `Stringline.Portable.slnf` too unless they reference `Stringline.App`.
- View-model logic lives in `Stringline.App` but must not touch WPF types (use `IUiDispatcher`/`IDialogService`), so `Stringline.App.Tests` can drive it without a window.
- Record: `dotnet run --project src/Stringline.Tools -- record --host localhost --port 51515 --out x.sslog`
- Replay server: `dotnet run --project src/Stringline.Tools -- replay-server x.sslog --speed 8`
- Inspect a WTT: `dotnet run --project src/Stringline.Tools -- wtt-dump t.wtt [--train 1A23] [--warnings]`. The WTT corpus test reads every `.wtt` under `STRINGLINE_WTT_CORPUS` (default `C:\Users\Public\Documents\SimSig\Timetables`; skipped if absent).
- Match a recording to its WTT: `dotnet run --project src/Stringline.Tools -- match --wtt t.wtt --log x.sslog [--all]` (lists unmatched workings; `--all` lists every one).
- Propose corridors: `dotnet run --project src/Stringline.Tools -- corridors t.wtt [--edges] [--top 6] [--min-trains 2] [--profile out.json]` (sections, min run times, derived axis, single-line hints; `--profile` writes a starter profile).
- Render PNG or PDF: `dotnet run --project src/Stringline.Tools -- render-png --wtt t.wtt --log x.sslog --corridor main --at 09:00 --hours 3 [--vertical] [--colour delay] [--no-forecast] [--no-knock-on] [--no-conflicts] out.png` (an `out.pdf` writes a PDF; `--page a3|a4|view`, default A3 landscape).
- Delay table CSV: `dotnet run --project src/Stringline.Tools -- report (--log x.sslog --wtt t.wtt | --session s.stringline) [--at 09:00] [--all] [--by-train] out.csv` (a session brings its plan edits).
- Graph benchmarks (GR-9 budget: 500 trains / 6 h < 33 ms a frame): `dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --quick` (seconds) or `-- --filter "*" --job short` (BenchmarkDotNet).
- Publish (Windows): `./build/publish.ps1 [-Version 0.2.0] [-Installer]` → `artifacts/Stringline-<v>-win-x64.zip` (single-file exe) and, with Inno Setup 6, `Stringline-<v>-setup.exe`. A `v*` tag runs the Release workflow (draft GitHub release).
- Redraw the app icon after changing `AppIcon`: `dotnet run --project src/Stringline.Tools -- icon src/Stringline.App/Assets/stringline.ico [--png dir]`.
- Run the app (Windows): `dotnet run --project src/Stringline.App -- [--wtt t.wtt] [--replay x.sslog --speed 8|max --start 07:30] [--resume|--review s.stringline]`. App logs go to `%LOCALAPPDATA%\Stringline\logs` (Debug with Settings ▸ Logging); sessions autosave to `Documents\Stringline\sessions`.

## End of every session
Build and tests green → update `docs/progress.md` (done, decisions, next, known issues, questions for Fred) → update any design doc you changed → commit.
