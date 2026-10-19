# Stringline

[![CI](https://github.com/frddunc-glitch/Stringline/actions/workflows/ci.yml/badge.svg)](https://github.com/frddunc-glitch/Stringline/actions/workflows/ci.yml)
[![Licence: GPL v3](https://img.shields.io/badge/licence-GPL--3.0--or--later-blue.svg)](LICENSE)

A live electronic train graph (time–distance) for [SimSig](https://www.simsig.co.uk/). It connects to a running simulation through the SimSig Interface Gateway and lets you re-plan late services around delays and disruptions, in the style of an Electronic Train Graph.

**Status:** early development (version 0.1.0).

## Installing

You need Windows 10 (version 2004 or later) or Windows 11, 64-bit. Nothing else needs installing: .NET is built in.

Download one of these from the [Releases](https://github.com/frddunc-glitch/Stringline/releases) page:

- **`Stringline-x.y.z-setup.exe`**: the installer. It installs for your Windows account only, without asking for administrator rights, and adds Stringline to the Start menu. To uninstall, use Settings ▸ Apps.
- **`Stringline-x.y.z-win-x64.zip`**: the portable version. Unzip it anywhere and run `Stringline.exe`.

The downloads are not code-signed, so Windows SmartScreen may warn you the first time. Choose **More info ▸ Run anyway**. The first start takes a few seconds longer while Windows unpacks the app.

Uninstalling leaves your settings, profiles, sessions and logs in place (see [Where your files are kept](#where-your-files-are-kept)). Delete those folders by hand if you want them gone too.

## Getting started

1. **Turn on the Interface Gateway in SimSig.** In SimSig's options for starting a sim, turn on **Interface Gateway** before you start the sim. It uses port 51515. The [SimSig wiki](https://www.simsig.co.uk/) explains the option on its "Interface Gateway" page.
2. **Start the sim, then connect.** In Stringline, choose **Session ▸ Connect to SimSig…** (F5). The host is `localhost` and the port 51515, unless you changed them in SimSig.
   - For a **payware sim**, enter your SimSig forum username and password: SimSig checks them online. Tick **Remember the password** to keep it, encrypted for your Windows account. Stringline never writes it to its logs.
   - Tick **Also subscribe to train describer and signalling messages** only if you need them. It is much busier.
3. **Load the timetable.** Pick the sim's `.wtt` in the Connect dialog, or wait for the sim's name to arrive: the bar above the graph then offers the timetables for that sim from SimSig's Timetables folder (`C:\Users\Public\Documents\SimSig\Timetables`). Stringline remembers the one you used for each sim.
4. **Read the graph.** Time runs across, and each corridor is a tab. Thin lines are the working timetable, heavy lines the actual running, and dashed lines the forecast. The vertical line is now.
   - Mouse wheel zooms in time, Ctrl+wheel zooms along the corridor, and dragging pans. Space follows now.
   - Click a train to select it. The panel on the right shows its timings.
5. **Re-plan.**
   - Drag a train's path, or right-click it to hold it, change its platform, cancel it or clone it.
   - Right-click the graph's background, or B+drag, to add a block, a speed restriction or a note.
   - Everything can be undone (Ctrl+Z).
   - The **Conflicts** tab lists the clashes the forecast finds, with suggested fixes. **Regulation** lists the movements at a location in their planned order. **Delays** lists each train's lateness.
6. **Keep and share.** Sessions save themselves as you go, and Stringline offers to resume one left running. Session ▸ Export saves the graph as PNG or PDF (A3 landscape for printing), and the delay table as CSV.

**No sim running?** Session ▸ Open replay… (Ctrl+O) plays a recording (`.sslog`) as if it were live. Try `tests/fixtures/waterloo-4x.sslog` with `waterloo 2010.WTT` from this repository.

**Profiles.** The first time you load a sim's timetable, Stringline works out its corridors (the line diagrams the graph is drawn along). Profile ▸ Edit profile… lets you rename locations, reorder or add corridors, set distances, headways and line layouts, and choose category colours.

## Settings

**View ▸ Settings…** (Ctrl+,) has four tabs:

- **Forecast and conflicts:**
  - knock-on delay from other trains;
  - the default dwell and turnaround when the WTT gives none;
  - how long without a report before a train may have stopped;
  - the platform margin;
  - the smallest conflict worth showing.
- **Suggestions:** whether suggestions may terminate short or cancel a train, and what each kind of re-plan costs when suggestions are ranked.
- **Folders:** where to look for timetables, and where sessions, recordings and exports go.
- **Logging and housekeeping:** detailed (Debug) logging, how many days of logs to keep, and deleting saved sessions after a number of days (off unless you turn it on; the newest is always kept).

Connection details are in the Connect dialog. The graph's colours, layers and orientation are in the View menu. Stringline remembers all of them.

## Where your files are kept

| What | Where |
|---|---|
| Settings | `%APPDATA%\Stringline\settings.json` |
| Sim profiles (corridors, names, colours) | `%APPDATA%\Stringline\profiles` |
| Logs (Help ▸ Open log folder) | `%LOCALAPPDATA%\Stringline\logs` |
| Saved sessions | `Documents\Stringline\sessions` |
| Recordings (Session ▸ Record to file) | `Documents\Stringline\recordings` |
| Exports | `Documents\Stringline\exports` |

The last three can be moved in Settings ▸ Folders.

## Troubleshooting

- **The status bar stays on "Connecting to…" or "Disconnected — retrying".**
  - Check that the sim is running and that Interface Gateway was turned on before it started.
  - Check that nothing else is using port 51515 (another Stringline, or `stringline replay-server`).
  - Stringline keeps retrying, so it connects as soon as SimSig is ready.
- **"SimSig refused the connection".** A payware sim did not accept your forum username or password. Stringline does not retry, so that the forum login is not hammered. Correct the details and connect again.
- **Something else went wrong.**
  - Help ▸ Open log folder shows the logs. Turn on detailed logging in Settings ▸ Logging and housekeeping to get more.
  - Help ▸ About Stringline ▸ Copy version copies the version and system details for a bug report.
  - Report problems on the [issue tracker](https://github.com/frddunc-glitch/Stringline/issues).

**Command line.** `Stringline.exe [--wtt t.wtt] [--replay x.sslog [--speed 8|max] [--start 07:30]] [--resume s.stringline | --review s.stringline]`

## For developers

### Building

Requires the .NET 10 SDK.

```sh
dotnet build -warnaserror        # Windows: the full solution, including the WPF app
dotnet test

dotnet build Stringline.Portable.slnf -warnaserror   # Linux/macOS: everything except the WPF app
dotnet test Stringline.Portable.slnf
```

### Publishing a release

```powershell
./build/publish.ps1                 # artifacts/Stringline-<version>-win-x64.zip (single-file, self-contained)
./build/publish.ps1 -Installer      # also artifacts/Stringline-<version>-setup.exe (needs Inno Setup 6: winget install JRSoftware.InnoSetup)
```

The version comes from `Directory.Build.props` unless you pass `-Version`. Pushing a tag such as `v0.2.0` runs the **Release** workflow, which tests, publishes and makes a draft GitHub release with both files. The installer script is `installer/Stringline.iss`. The app icon is drawn by code (`AppIcon` in `Stringline.Graph`); after changing it, run `dotnet run --project src/Stringline.Tools -- icon src/Stringline.App/Assets/stringline.ico`.

### Recording a SimSig session

Turn on the Interface Gateway in SimSig before starting the sim, then:

```sh
dotnet run --project src/Stringline.Tools -- record --out my-sim.sslog          # movements and clock
dotnet run --project src/Stringline.Tools -- record --td --out my-sim.sslog     # also TD and signalling (much busier)
dotnet run --project src/Stringline.Tools -- record --user <forum name> --out my-sim.sslog   # payware sims
```

For `--user`, the password comes from the `STRINGLINE_PASSWORD` environment variable, or you are prompted for it. It is never written to the recording or the log. Press Ctrl+C to stop; the command prints message counts and rates per message type. If SimSig is not running yet, the recorder keeps retrying (1 s back-off rising to 30 s).

### Replaying a recording

`replay-server` plays a recording as a stand-in for SimSig on port 51515, so Stringline (or `record`) can connect to it without SimSig running:

```sh
dotnet run --project src/Stringline.Tools -- replay-server tests/fixtures/waterloo-1x.sslog --speed 8
dotnet run --project src/Stringline.Tools -- replay-server my-sim.sslog --start 07:30 --speed 0   # from 07:30, as fast as possible
```

Playback starts when the first client connects. Press Space to pause or resume, `+` or `-` to change speed, and Q or Ctrl+C to stop. Close SimSig first, or use `--port`, because only one program can listen on 51515.

### Drawing a train graph

`render-png` draws one corridor's graph to a PNG with the renderer the app uses. It draws the WTT as thin lines and, given a recording, the actual running up to `--at` as heavy lines. The now line is a third of the way across:

```sh
dotnet run --project src/Stringline.Tools -- render-png --wtt "tests/fixtures/waterloo 2010.WTT" --log tests/fixtures/waterloo-4x.sslog --corridor main --at 07:00 --hours 3 waterloo.png
```

Without `--profile`, corridors come from the WTT (`corridors` lists them).
- `--now-at`, `--width`, `--height` and `--no-wtt` change the view.
- `--vertical` runs time down the image.
- `--colour delay` colours the actual running by delay band: green right time, amber 1–5 min late, orange 6–15, red over 15.

An output ending in `.pdf` writes a PDF (`--page a3|a4|view`). `report` writes the delay table as CSV.

### Benchmarks

`benchmarks/Stringline.Benchmarks` times graph frames against the budget of 500 trains over 6 hours in under 33 ms (30 fps) while panning:

```sh
dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --quick                     # a few seconds, rough timings
dotnet run -c Release --project benchmarks/Stringline.Benchmarks -- --filter "*" --job short    # BenchmarkDotNet
```

### Layout

- `src/` — `Stringline.Core` (sim time, identifiers, lenient JSON), `Gateway`, `Timetable`, `Network`, `Planning`, `Persistence`, `Graph`, `Tools` (the `stringline` CLI) and `App` (WPF, Windows only).
- `tests/` — xUnit test projects; `tests/fixtures/` holds free-sim data only. Recordings and timetables from payware sims go in the git-ignored `local-fixtures/`.
- `benchmarks/` — BenchmarkDotNet frame timings for the graph.
- `build/`, `installer/` — the publish script and the Inno Setup installer.
- `docs/` — design documents and the session-by-session plan.

## Licence

Copyright (C) 2026 Frederick Duncan and Stringline contributors.

Stringline is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [`LICENSE`](LICENSE) for the full text.

By contributing, you agree that your contributions are licensed under the same terms.

Stringline is built with other open-source components, listed with their licences in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) and in Help ▸ About Stringline. The graph's labels use [Noto Sans](https://notofonts.github.io/), embedded in `Stringline.Graph` under the SIL Open Font License 1.1 (`src/Stringline.Graph/Fonts/OFL.txt`); the font remains under that licence.

The licence covers Stringline's source code only. Timetables, recordings and other data derived from SimSig simulations (for example, anything in `tests/fixtures/`) remain subject to their owners' terms and are not covered by the GPL.

## Disclaimer

Stringline is an independent project. It is not affiliated with, endorsed by or supported by SimSig or its developers. SimSig is a trademark of its respective owners.
