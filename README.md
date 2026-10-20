# Stringline

[![CI](https://github.com/frddunc-glitch/Stringline/actions/workflows/ci.yml/badge.svg)](https://github.com/frddunc-glitch/Stringline/actions/workflows/ci.yml)
[![Licence: GPL v3](https://img.shields.io/badge/licence-GPL--3.0--or--later-blue.svg)](LICENSE)

Stringline draws a live **train graph** (time against distance) for the [SimSig](https://www.simsig.co.uk/) sim you are playing. Every train is a line: the timetable, where it actually ran, and where it is forecast to go. Clashes ahead are ringed in red, and you can re-plan late trains by dragging their lines, the way a real train controller does with an Electronic Train Graph.

![Stringline showing Waterloo, with a train selected and a conflict picked](docs/user-guide/images/main-window.png)

It works with free and payware sims. It only reads the sim: it never changes anything in SimSig unless you turn on assist and confirm each command.

**Status:** early development. Expect rough edges, and please [report them](#reporting-a-problem).

## Download and install

You need Windows 10 (version 2004 or later) or Windows 11, 64-bit. Nothing else needs installing.

From the [Releases](https://github.com/frddunc-glitch/Stringline/releases) page, download either:

- **`Stringline-x.y.z-setup.exe`**, the installer. It installs for your Windows account only and adds Stringline to the Start menu; or
- **`Stringline-x.y.z-win-x64.zip`**, the portable version. Unzip it anywhere and run `Stringline.exe`.

The downloads are not code-signed yet, so Windows says "Unknown publisher" and SmartScreen may warn you the first time. Choose **More info ▸ Run anyway**.

## Turn on the Interface Gateway in SimSig

Stringline reads the sim through SimSig's **Interface Gateway**, which is off unless you turn it on.

1. Start SimSig's Loader and pick the sim as usual.
2. In the options for starting the sim, tick **Interface Gateway**. Leave the port at 51515.
3. Start the sim.

The gateway must be ticked *before* the sim starts. If you forgot, save the game, close the sim and start it again from the save.

## Your first graph

1. In Stringline, choose **Session ▸ Connect to SimSig…** (F5). The host is `localhost` and the port 51515.
2. For a payware sim, enter your SimSig forum username and password (SimSig checks them online). Free sims need no login.
3. Press **Connect**. When the sim's name arrives, pick its timetable (`.wtt`) in the bar above the graph.
4. The graph fills in as trains run. Thin lines are the timetable, heavy lines the actual running, and dash-dot lines the forecast. The red vertical line is now.
5. Click a train to see its timings. Drag its line to re-plan it. Ctrl+Z undoes.

No sim running? **Session ▸ Open replay…** plays a recording as if it were live.

## Learn more

The **[user guide](https://github.com/frddunc-glitch/Stringline/wiki)** covers everything else: reading the graph, re-planning, conflicts and suggestions, blocks and speed restrictions, sessions, exports and settings. It is also built into the app: **Help ▸ User guide**, or **F1** for the page you are on, and works offline.

## Privacy

Stringline sends nothing anywhere except to the SimSig gateway you connect to: no telemetry, no update checks. Your SimSig password is kept only if you ask, encrypted for your Windows account. [`PRIVACY.md`](PRIVACY.md) has the details, and how to remove everything.

## Reporting a problem

Please report bugs and ideas on the [issue tracker](https://github.com/frddunc-glitch/Stringline/issues). **Help ▸ About Stringline ▸ Copy version** copies the details worth including, and **Help ▸ Open log folder** shows the logs. The guide's [troubleshooting page](https://github.com/frddunc-glitch/Stringline/wiki/troubleshooting) covers the common snags.

## Contributing

Building, testing, the design documents and how releases are made are in [`docs/developer.md`](docs/developer.md).

## Licence

Copyright (C) 2026 Frederick Duncan and Stringline contributors.

Stringline is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [`LICENSE`](LICENSE) for the full text. By contributing, you agree that your contributions are licensed under the same terms.

Stringline is built with other open-source components, listed with their licences in [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md) and in Help ▸ About Stringline. The graph's labels use [Noto Sans](https://notofonts.github.io/), under the SIL Open Font License 1.1 (`src/Stringline.Graph/Fonts/OFL.txt`).

The licence covers Stringline's source code only. Timetables, recordings and other data from SimSig sims (such as those in `tests/fixtures/`) remain subject to their owners' terms.

Stringline is an independent project. It is not affiliated with, endorsed by or supported by SimSig or its developers. SimSig is a trademark of its respective owners.
