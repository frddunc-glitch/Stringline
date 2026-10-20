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

The downloads are not yet code-signed, so Windows shows "Unknown publisher" and SmartScreen may warn you the first time. Choose **More info ▸ Run anyway**. Only a code-signing certificate makes Windows show the publisher; the build signs the files as soon as one is set up (see [Code signing](#code-signing)). The first start takes a few seconds longer while Windows unpacks the app.

To update, run the new version's installer. It updates Stringline where it is installed, closing it first if it is running (from 0.1.1 Stringline saves its session as it closes), and keeps your settings, profiles and sessions.

Uninstalling always removes a saved SimSig password, and asks whether to remove your settings, profiles and logs too (No unless you choose Yes; a silent uninstall needs `/REMOVEUSERDATA`). Sessions, recordings and exports in Documents are never removed (see [Where your files are kept](#where-your-files-are-kept)).

## Getting started

The **[user guide](docs/user-guide/README.md)** covers everything below in more detail. In the app, **Help ▸ User guide** opens it, and **F1** opens the page for the panel or window you are in; both work offline. It is also on the [wiki](https://github.com/frddunc-glitch/Stringline/wiki).

1. **Turn on the Interface Gateway in SimSig.** In SimSig's options for starting a sim, turn on **Interface Gateway** before you start the sim. It uses port 51515. The [SimSig wiki](https://www.simsig.co.uk/) explains the option on its "Interface Gateway" page.
2. **Start the sim, then connect.** In Stringline, choose **Session ▸ Connect to SimSig…** (F5). The host is `localhost` and the port 51515, unless you changed them in SimSig.
   - For a **payware sim**, enter your SimSig forum username and password: SimSig checks them online. Tick **Remember the password** to keep it, encrypted for your Windows account. Stringline never writes it to its logs.
   - Tick **Also subscribe to train describer and signalling messages** only if you need them. It is much busier.
3. **Load the timetable.** Pick the sim's `.wtt` in the Connect dialog, or wait for the sim's name to arrive: the bar above the graph then offers the timetables for that sim from SimSig's Timetables folder (`C:\Users\Public\Documents\SimSig\Timetables`). Stringline remembers the one you used for each sim.
4. **Read the graph.** Time runs across, and each corridor is a tab (or Corridor menu, Ctrl+1 to Ctrl+9; » lists the tabs that do not fit). Thin lines are the working timetable, heavy lines the actual running, and dash-dot lines the forecast. The vertical line is now.
   - Mouse wheel zooms in time, Ctrl+wheel zooms along the corridor, and dragging pans. Space follows now.
   - Click a train to select it. The panel on the right shows its timings.
   - Hover over anything for its details: a train shows its booked, actual and forecast times and whether it is early, on time or late; a red conflict ring or box shows the trains, where, when, the delay needed and the best suggestion; a block or speed restriction shows its section, tracks, times and effect (right-click to edit it).
   - The sim time is at the top right. Tools ▸ Options ▸ Display makes it Large or Huge.
5. **Re-plan.**
   - Click a train to select it and show its grips: drag a square to retime from that timing point, a bar to change a dwell, or the round handle (or the line itself) to retime the rest of the run. Click a grip and use the arrow keys to nudge it by 15 s (Shift: 1 min). Grey hollow grips are points the train has passed. Right-click a train to hold it, change its platform, cancel it or clone it.
   - Right-click the graph's background, or B+drag, to add a block, a speed restriction or a note.
   - Everything can be undone (Plan ▸ Undo, Ctrl+Z).
   - The **Conflicts** tab lists the clashes the forecast finds, with suggested fixes. **Plan ▸ Re-plan the next 60 min…** (Ctrl+Shift+R) clears the clashes ahead all at once with an optimiser, for you to preview and apply. **Regulation** lists the movements at a location in their planned order. **Delays** lists each train's lateness.
6. **Keep and share.** Sessions save themselves as you go, and Stringline offers to resume one left running; File ▸ Resume saved session… and Review saved session… open any of them. File ▸ Export saves the graph as PNG or PDF (A3 landscape for printing), and the delay table as CSV.

**No sim running?** Session ▸ Open replay… (Ctrl+O) plays a recording (`.sslog`) as if it were live. Try `tests/fixtures/waterloo-4x.sslog` with `waterloo 2010.WTT` from this repository.

**Profiles.** The first time you load a sim's timetable, Stringline works out its corridors (the line diagrams the graph is drawn along). Tools ▸ Edit profile… lets you rename locations (handy for a long name the graph has to shorten; hover over a label for its full name), reorder or add corridors, set distances, headways and line layouts, and choose category colours. Each section's headway is worked out from the timetable where its trains are booked closer than the default (3 min, in Tools ▸ Options), and the editor shows where each one comes from. A headway you type in is kept.

## Options

**Tools ▸ Options…** (Ctrl+,) has six tabs:

- **Forecast and conflicts:**
  - knock-on delay from other trains;
  - the default dwell and turnaround when the WTT gives none;
  - how long without a report before a train may have stopped;
  - the platform margin;
  - the smallest conflict worth showing.
- **Suggestions:** whether suggestions may terminate short or cancel a train, what each kind of re-plan costs when suggestions are ranked, and how Plan ▸ Re-plan works (the optimiser or the suggestion rules, platform changes, how far ahead, how long it may think).
- **Folders:** where to look for timetables, and where sessions, recordings and exports go.
- **Logging and housekeeping:** detailed (Debug) logging, how many days of logs to keep, and deleting saved sessions after a number of days (off unless you turn it on; the newest is always kept).
- **Connection:** whether a SimSig password is saved, and **Forget saved password**.
- **Display:** the size of the sim clock at the top right (Normal, Large or Huge).

Connection details are in the Connect dialog. The graph's colours, layers and orientation are in the View menu, and the corridor shown in the Corridor menu. Stringline remembers all of them.

## Where your files are kept

| What | Where |
|---|---|
| Settings | `%APPDATA%\Stringline\settings.json` |
| Sim profiles (corridors, names, colours) | `%APPDATA%\Stringline\profiles` |
| Logs (Help ▸ Open log folder) | `%LOCALAPPDATA%\Stringline\logs` |
| Saved sessions | `Documents\Stringline\sessions` |
| Recordings (Session ▸ Record to file) | `Documents\Stringline\recordings` |
| Exports | `Documents\Stringline\exports` |

The last three can be moved in Tools ▸ Options ▸ Folders.

## Privacy

Stringline sends nothing anywhere except to the SimSig gateway you connect to: no telemetry, no update check. Your SimSig password is stored only if you tick **Remember the password**, encrypted with Windows (DPAPI) for your account, and never written to logs, sessions or recordings. [`PRIVACY.md`](PRIVACY.md) says what is stored, where, and how to remove it all (also **Help ▸ Privacy** in the app).

## Troubleshooting

- **The status bar stays on "Connecting to…" or "Disconnected — retrying".**
  - Check that the sim is running and that Interface Gateway was turned on before it started.
  - Check that nothing else is using port 51515 (another Stringline, or `stringline replay-server`).
  - Stringline keeps retrying, so it connects as soon as SimSig is ready.
- **"SimSig refused the connection".** A payware sim did not accept your forum username or password. Stringline does not retry, so that the forum login is not hammered. Correct the details and connect again.
- **Something else went wrong.**
  - Help ▸ Open log folder shows the logs. Turn on detailed logging in Tools ▸ Options ▸ Logging and housekeeping to get more.
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

The version comes from `Directory.Build.props` unless you pass `-Version`. Pushing a tag such as `v0.2.0` runs the **Release** workflow, which tests, publishes and makes a draft GitHub release with both files. The installer script is `installer/Stringline.iss`. `./build/test-upgrade.ps1` checks that a setup updates an installed Stringline, including one that is running. The Release workflow runs it from the previous release. The app icon is drawn by code (`AppIcon` in `Stringline.Graph`); after changing it, run `dotnet run --project src/Stringline.Tools -- icon src/Stringline.App/Assets/stringline.ico`.

### Code signing

Windows names the publisher only for Authenticode-signed files, so until there is a certificate it says "Unknown publisher". `build/sign.ps1` signs `Stringline.exe`, the installer and its uninstaller with `signtool` when `STRINGLINE_SIGN_PFX` names a `.pfx` file (and `STRINGLINE_SIGN_PASSWORD` gives its password). Without it the files are left unsigned and the build warns. In the Release workflow, set the repository secrets `SIGN_PFX_BASE64` (the `.pfx`, base64-encoded) and `SIGN_PFX_PASSWORD`. The certificate never appears on a command line or in the log.

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
- `--colour delay` colours the actual running by delay band: green on time or early, amber 1–5 min late, orange 6–15, red over 15.

An output ending in `.pdf` writes a PDF (`--page a3|a4|a3p|a4p|view`); with `--plot` it is a multi-page plot of `--from` to `--to` at `--per-page` minutes a page, time running down A3 portrait pages unless `--horizontal`. `report` writes the delay table as CSV. `replan` re-plans the hour ahead of a point in a recording, with the optimiser or (`--rules`) the suggestion rules, and lists the conflicts before and after and what it changes.

### The user guide

The guide is Markdown in `docs/user-guide` (Australian English, railway terms as SimSig uses them). The app embeds it and renders it to HTML with Markdig when Help ▸ User guide or F1 is used. A test checks every link, image and heading in it, and that every F1 page exists.

```powershell
./build/guide-shots.ps1                     # make docs/user-guide/images again from free-sim data (render-png and the App window tests)
./build/publish-wiki.ps1 -Out wiki-preview  # the wiki's pages, to look at
```

The **Wiki** workflow copies the guide to the GitHub wiki whenever it changes on `main` (`publish-wiki.ps1 -Push`). Edit the guide, never the wiki. The wiki needs its first page made by hand on GitHub before it can be pushed to.

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
