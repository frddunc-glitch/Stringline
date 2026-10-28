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
- Inspect a WTT: `dotnet run --project src/Stringline.Tools -- wtt-dump t.wtt [--train 1A23] [--warnings]` (its summary says how many rules the forecast applies and lists those it leaves out, S32). The WTT corpus test reads every `.wtt` under `STRINGLINE_WTT_CORPUS` (default `C:\Users\Public\Documents\SimSig\Timetables`; skipped if absent).
- Save a WTT through the lossless writer (S40): `dotnet run --project src/Stringline.Tools -- wtt-save in.wtt out.wtt [--bump build|minor|major|none] [--shift-train 1A23 +5] [--backup-folder dir]` (a file already at `out.wtt` is backed up to `%LOCALAPPDATA%\Stringline\timetable-backups\{area}` first; `--bump none` is a pure round trip, byte for byte for SimSig-saved files). The round-trip test reads the same corpus as the WTT corpus test.
- What Stringline knows about a sim (S41): `dotnet run --project src/Stringline.Tools -- simdata --area waterloo [--folder f] [--sessions dir|--no-sessions] [--log x.sslog] [--profile p.json|--no-profile] [--exclude draft.wtt] [--cache-folder d] [--rebuild] [--location WIMB] [--link WLOO VAUX]` (built from the sim's timetables, saved sessions and profile, cached as `{area}.simdata.json` beside the profile and rebuilt when a source changes).
- Check a timetable before SimSig (S42): `dotnet run --project src/Stringline.Tools -- wtt-check t.wtt [--knowledge-folder f] [--log x.sslog] [--profile p.json|--no-profile] [--include-self] [--no-knowledge] [--severity info|warning|error] [--train 1A23] [--summary]` (the Analyser's offline checks in SimSig's wording, the sim-knowledge checks and the rules; the checked file is left out of the knowledge unless `--include-self`; exit 1 when there are Errors). The corpus baseline is `TimetableValidatorCorpusTests` (`--filter The_corpus_baseline --logger "console;verbosity=detailed"`).
- A timetable as a spreadsheet (S47): `dotnet run --project src/Stringline.Tools -- wtt-csv export t.wtt out.csv` and `wtt-csv import t.wtt in.csv out.wtt` (one row per timing point; the import merges by UID, else headcode and first time, else adds, and saves a new version).
- Match a recording to its WTT: `dotnet run --project src/Stringline.Tools -- match --wtt t.wtt --log x.sslog [--all]` (lists unmatched workings; `--all` lists every one).
- Propose corridors: `dotnet run --project src/Stringline.Tools -- corridors t.wtt [--edges] [--top 6] [--min-trains 2] [--profile out.json] [--headways [--log x.sslog] [--places p.json [--min-samples 3]]]` (sections, min run times, headways and where they come from, derived axis, single-line hints; `--profile` writes a starter profile; `--headways` lists the WTT's booked spacing, with `--log` the spacing trains actually ran at, and with `--places` a learned profile's block headways, signals and aspects, S39, and its learned run times in the Learned column, which space the axis; the summary counts trains that jump across a corridor by a chord, and each corridor lists the off-corridor locations it draws at one of its own, S44a).
- Render PNG or PDF: `dotnet run --project src/Stringline.Tools -- render-png --wtt t.wtt --log x.sslog --corridor main --at 09:00 --hours 3 [--vertical] [--colour delay] [--no-forecast] [--no-knock-on] [--no-conflicts] [--live-head] [--signals off|ticks|rows] out.png` (an `out.pdf` writes a PDF; `--live-head` draws each running train's live head, off by default; `--signals` shows the profile's learned signals on the axis, ticks by default (S38); `--page a3|a4|a3p|a4p|view`, default A3 landscape). A multi-page plot (S21): `... --plot [--from 06:00] [--to 18:00] [--per-page 120] [--horizontal] out.pdf` (time down on A3 portrait by default).
- Re-plan the hour ahead of a point in a recording (S20): `dotnet run --project src/Stringline.Tools -- replan --wtt t.wtt --log x.sslog [--profile p.json] [--at 08:00] [--minutes 60] [--seconds 5] [--rules] [--no-platforms] [--verbose]` (conflicts before and after, and the holds and platform changes; with a learned profile, holds at signals for trains on their way, S33; `--verbose` logs each optimiser round). The optimiser (`Stringline.Optimiser`) uses OR-Tools CP-SAT, whose native solver also runs on Linux.
- Delay table CSV: `dotnet run --project src/Stringline.Tools -- report (--log x.sslog --wtt t.wtt | --session s.stringline) [--at 09:00] [--all] [--by-train] out.csv` (a session brings its plan edits).
- Report gaps (S36, the baseline for #17): `dotnet run --project src/Stringline.Tools -- gaps (--log x.sslog --wtt t.wtt | --session s.stringline) [--profile p.json]` (the share of reports at timing locations a train's WTT leaves out, and the median, 95th percentile and longest gap between reports on the corridors: booked locations only, all timing locations, and with signals).
- Learn signal places (S37, #19): `dotnet run --project src/Stringline.Tools -- learn --wtt t.wtt --log a.sslog [--log b.sslog|s.stringline …] [--profile p.json] [--out p.json] [--report] [--min-samples 3]` (signal, track circuit and berth places between timing locations, from one or more recordings or saved live sessions (S38); how track circuits were put to trains; signals used; with two or more logs, how far places move between the first and each other; `--report` lists each section's places).
- Graph benchmarks (GR-9 budget: 500 trains / 6 h < 33 ms a frame; the draft board too, S44): `dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --quick` (seconds) or `-- --filter "*" --job short` (BenchmarkDotNet).
- Publish (Windows): `./build/publish.ps1 [-Version 0.2.0] [-Installer]` → `artifacts/Stringline-<v>-win-x64.zip` (single-file exe) and, with Inno Setup 6, `Stringline-<v>-setup.exe`. Pushing a `v*` tag, or publishing a release on GitHub, runs the Release workflow, which attaches the zip and installer to that release (a draft is made if none exists). To fill in an existing release: Actions ▸ Release ▸ Run workflow with its tag.
- Check the installer updates an install (Windows, needs Inno Setup 6): `./build/test-upgrade.ps1 [-Old old-setup.exe -New new-setup.exe]`. With no setups given it builds 0.0.1 and 0.0.2. It installs for the current user, so it refuses where Stringline is installed (`-Force` replaces that install). It also checks both uninstall paths: a silent uninstall keeps settings but removes a saved password, and `/REMOVEUSERDATA` removes `%APPDATA%`/`%LOCALAPPDATA%\Stringline` (it copies yours aside and puts them back). The Release workflow runs it from the previous release's setup.
- User guide (`docs/user-guide`, embedded in the app; Help ▸ User guide, F1): `./build/guide-shots.ps1` remakes its images (free-sim data only); `./build/publish-wiki.ps1 -Out dir` previews the wiki, and the Wiki workflow publishes it on push to main. A new F1 page goes in `HelpTopics` and on the panel as `views:Help.Topic`. Update the guide when the UI changes.
- Redraw the app icon after changing `AppIcon`: `dotnet run --project src/Stringline.Tools -- icon src/Stringline.App/Assets/stringline.ico [--png dir]`.
- Run the app (Windows): `dotnet run --project src/Stringline.App -- [--wtt t.wtt] [--replay x.sslog --speed 8|max --start 07:30] [--resume|--review s.stringline]`. App logs go to `%LOCALAPPDATA%\Stringline\logs` (Debug with Tools ▸ Options ▸ Logging); sessions autosave to `Documents\Stringline\sessions`, and timetable drafts (Timetable menu, S43) to `Documents\Stringline\drafts`.

## End of every session
Build and tests green → update `docs/progress.md` (done, decisions, next, known issues, questions for Fred) → update any design doc you changed → commit.
