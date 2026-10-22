# Developing Stringline

This page is for people who want to build Stringline, change it or make a release. For using it, see the [README](../README.md) and the [user guide](user-guide/README.md).

## Building and testing

You need the .NET 10 SDK (`global.json` pins the feature band).

```sh
dotnet build -warnaserror        # Windows: the full solution, including the WPF app
dotnet test

dotnet build Stringline.Portable.slnf -warnaserror   # Linux or macOS: everything except the WPF app
dotnet test Stringline.Portable.slnf
```

The CI workflow runs both: the full solution on Windows and the portable one on Ubuntu. Warnings are errors.

Run the app on Windows with:

```sh
dotnet run --project src/Stringline.App -- [--wtt t.wtt] [--replay x.sslog --speed 8|max --start 07:30] [--resume s.stringline | --review s.stringline]
```

## Stack

C# on .NET 10 (LTS). The shell is WPF with CommunityToolkit.Mvvm; the graph is drawn with SkiaSharp, so it renders the same in the app, in the CLI and in tests on Linux. Tests use xUnit, FluentAssertions 7.x and Verify. Logging is Serilog; sessions are SQLite (Microsoft.Data.Sqlite); the CLI uses System.CommandLine; the re-plan optimiser uses Google OR-Tools (CP-SAT); the user guide is rendered with Markdig.

## Layout

| Folder | What is in it |
|---|---|
| `src/Stringline.Core` | Sim time (`SimTime`), identifiers, lenient JSON converters |
| `src/Stringline.Gateway` | The STOMP client, message parsing, recorder and replay |
| `src/Stringline.Timetable` | `.wtt` import and matching live trains to their workings |
| `src/Stringline.Network` | Corridors, sections, profiles and the distance axis |
| `src/Stringline.Planning` | The working plan and its commands, forecaster, conflicts, suggestions and re-planning |
| `src/Stringline.Optimiser` | The CP-SAT re-planner |
| `src/Stringline.Persistence` | The SQLite session store |
| `src/Stringline.Graph` | The graph's scene model, Skia renderer and hit testing (no WPF) |
| `src/Stringline.Tools` | The `stringline` command-line tool |
| `src/Stringline.App` | The WPF app (Windows only) |
| `tests/` | One xUnit project per source project, plus `Stringline.Architecture.Tests` |
| `benchmarks/` | BenchmarkDotNet frame timings for the graph |
| `build/`, `installer/` | Publish, signing, upgrade-test and guide scripts; the Inno Setup installer |
| `docs/` | Design documents, the session plan, the progress log and the user guide |

Only `Stringline.App` (and its tests) may reference WPF; everything else targets `net10.0` and must build and test on Linux. `Stringline.Architecture.Tests` enforces it. View-model logic in the app must not touch WPF types either (it goes through `IUiDispatcher` and `IDialogService`), so the app's tests can drive it without a window. A new test project goes in `tests/`, and in `Stringline.Portable.slnf` too unless it references the app.

## Design documents

Read these before changing how something works:

- [`00-brainstorm-and-decisions.md`](00-brainstorm-and-decisions.md): why things are the way they are.
- [`01-requirements.md`](01-requirements.md): the requirement IDs the plan refers to.
- [`02-architecture.md`](02-architecture.md): projects, data flow and threading.
- [`03-simsig-gateway-integration.md`](03-simsig-gateway-integration.md): the gateway's messages, as checked against recordings.
- [`04-timetable-and-domain-model.md`](04-timetable-and-domain-model.md): the `.wtt` format and the domain model.
- [`05-train-graph-ui.md`](05-train-graph-ui.md): the graph and the window.
- [`06-forecasting-conflicts-replanning.md`](06-forecasting-conflicts-replanning.md): the plan, forecast, conflicts and re-planning.
- [`07-implementation-plan.md`](07-implementation-plan.md): the work in sessions, with what each one builds.
- [`08-timetable-editor.md`](08-timetable-editor.md): the planned timetable editor.
- [`progress.md`](progress.md): what each session did, its decisions and open questions.

A few rules run through the code:

- All sim times are `SimTime` (seconds after midnight of sim day 0, possibly past 86 400), never `DateTime`.
- Gateway JSON may send numbers and booleans as strings (use the lenient converters), and snapshot bodies have duplicate keys (parse them with `Utf8JsonReader`, never `JsonDocument`).
- Nothing is sent to SimSig as hand-built JSON: only through the tested command builders, and only when assist is turned on.
- Unknown or malformed messages are logged and skipped, never a crash. Credentials are never logged.
- UI text and docs are in Australian English, with railway terms as SimSig uses them (headcode, TIPLOC, Up and Down).

## The command-line tool

`Stringline.Tools` is the `stringline` CLI, run with `dotnet run --project src/Stringline.Tools -- <command>`. Its commands:

| Command | What it does |
|---|---|
| `record` | Records a live gateway session to an `.sslog` (`--td` adds the train describer and signalling; `--user` for payware sims, with the password from `STRINGLINE_PASSWORD` or a prompt) |
| `replay-server` | Plays a recording on port 51515 as a stand-in for SimSig (`--speed`, `--start`; Space pauses, `+`/`-` change speed) |
| `wtt-dump` | Prints a timetable, one train (`--train`) or its import warnings; the rules the forecast applies and those it leaves out (S32) |
| `wtt-save` | Opens a timetable losslessly and saves it as a new version (`--bump`), optionally shifting trains' times (`--shift-train 1A23 +5`); a round-trip and smoke tool for the S40 writer, not the editor |
| `match` | Matches a recording's trains to a timetable and lists the ones it could not |
| `corridors` | Proposes corridors, sections, run times and headways from a timetable, and can write a starter profile; `--places` takes a learned profile's signal blocks for each section's block headway (S39) |
| `render-png` | Draws a corridor's graph to a PNG or PDF with the app's renderer, including multi-page plots (`--plot`) and learned signals on the axis (`--signals`) |
| `replan` | Re-plans the hour ahead of a point in a recording and lists the conflicts before and after; with a learned profile (`--profile`) it can hold trains on their way at signals (S33) |
| `report` | Writes the delay table as CSV, from a recording or a saved session |
| `gaps` | Measures how long trains go between reports on the corridors, and how often they report a timing location their WTT leaves out (the S36 baseline; with a profile's learned places, the gaps between placed signals too, S38) |
| `learn` | Learns signal, track circuit and berth places from recordings or saved live sessions into a profile, and compares the places between recordings (S37) |
| `icon` | Redraws the app icon after a change to `AppIcon` |

[`CLAUDE.md`](../CLAUDE.md) keeps every command with its options; each command's `--help` lists them too. Example:

```sh
dotnet run --project src/Stringline.Tools -- replay-server tests/fixtures/waterloo-1x.sslog --speed 8
dotnet run --project src/Stringline.Tools -- render-png --wtt "tests/fixtures/waterloo 2010.WTT" --log tests/fixtures/waterloo-4x.sslog --corridor main --at 07:00 --hours 3 waterloo.png
```

Close SimSig first, or use `--port`, before running `replay-server`: only one program can listen on 51515.

## Fixtures

- `tests/fixtures/` holds **free-sim data only** (Waterloo and Llangollen timetables, and Waterloo recordings). These are checked in.
- Recordings and timetables from **payware sims** go in `local-fixtures/`, which git ignores. Tests that need them pass without checking anything when they are missing.
- The WTT corpus test reads every `.wtt` under `STRINGLINE_WTT_CORPUS` (default `C:\Users\Public\Documents\SimSig\Timetables`) and skips if there is none.

Timetables and recordings remain subject to their owners' terms; the GPL covers only Stringline's code.

## Benchmarks

`benchmarks/Stringline.Benchmarks` times graph frames against the budget of 500 trains over 6 hours in under 33 ms (30 fps) while panning:

```sh
dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --quick                     # a few seconds, rough timings
dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --filter "*" --job short    # BenchmarkDotNet
```

## Publishing a release

```powershell
./build/publish.ps1                 # artifacts/Stringline-<version>-win-x64.zip (single-file, self-contained)
./build/publish.ps1 -Installer      # also artifacts/Stringline-<version>-setup.exe (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
```

The version comes from `Directory.Build.props` unless you pass `-Version`. Pushing a `v*` tag, or publishing a release on GitHub, runs the **Release** workflow: it tests, publishes, and attaches the zip and installer to that release (making a draft if there is none). To fill in an existing release, run Actions ▸ Release ▸ Run workflow with its tag. The installer script is `installer/Stringline.iss`.

`./build/test-upgrade.ps1 [-Old old-setup.exe -New new-setup.exe]` checks that a setup updates an installed Stringline, including a running one, and that both uninstall paths keep or remove the user's files as they should. With no setups given it builds 0.0.1 and 0.0.2. It installs for the current user, so it refuses to run where Stringline is installed unless given `-Force`. The Release workflow runs it from the previous release's setup.

### Code signing

Windows names the publisher only for Authenticode-signed files. `build/sign.ps1` signs `Stringline.exe`, the installer and its uninstaller with `signtool` when `STRINGLINE_SIGN_PFX` names a `.pfx` file (and `STRINGLINE_SIGN_PASSWORD` gives its password). Without it the files are left unsigned and the build warns. In the Release workflow, set the repository secrets `SIGN_PFX_BASE64` (the `.pfx`, base64-encoded) and `SIGN_PFX_PASSWORD`. The certificate never appears on a command line or in the log.

## The user guide and wiki

The guide is Markdown in [`docs/user-guide`](user-guide/README.md). The app embeds it and renders it with Markdig for Help ▸ User guide and F1. A test checks every link, image and heading in it (and in this page and the README), and that every F1 page exists. A new F1 page goes in `HelpTopics` and on its panel as `views:Help.Topic`. Update the guide whenever the UI changes.

```powershell
./build/guide-shots.ps1                     # remake docs/user-guide/images from free-sim data
./build/publish-wiki.ps1 -Out wiki-preview  # the wiki's pages, to look at
```

The **Wiki** workflow copies the guide to the [GitHub wiki](https://github.com/frddunc-glitch/Stringline/wiki) whenever it changes on `main`. Edit the guide, never the wiki. Links inside the guide stay inside it; link to anything else on GitHub.

`PRIVACY.md` at the root is copied word for word to `docs/user-guide/privacy.md`; a test checks they match.

## Dependencies and licence

Stringline is GPL-3.0-or-later. Every dependency must be GPLv3-compatible: MIT, BSD, Apache-2.0, LGPL and GPLv3 are fine; GPLv2-only, proprietary or "commercial licence" packages (FluentAssertions 8.x, for one) are not. Check before adding a package to `Directory.Packages.props`, and add it to [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

By contributing, you agree that your contributions are licensed under the same terms. Report problems and ideas on the [issue tracker](https://github.com/frddunc-glitch/Stringline/issues).
