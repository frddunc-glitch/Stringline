# Stringline

[![CI](https://github.com/frddunc-glitch/Stringline/actions/workflows/ci.yml/badge.svg)](https://github.com/frddunc-glitch/Stringline/actions/workflows/ci.yml)
[![Licence: GPL v3](https://img.shields.io/badge/licence-GPL--3.0--or--later-blue.svg)](LICENSE)

A live electronic train graph (time–distance) for [SimSig](https://www.simsig.co.uk/). It connects to a running simulation through the SimSig Interface Gateway and lets you re-plan late services around delays and disruptions, in the style of ViziRail's ETG.

**Status:** early development. See `docs/07-implementation-plan.md` for the roadmap and `docs/progress.md` for where things stand.

## Building

Requires the .NET 10 SDK.

```sh
dotnet build -warnaserror        # Windows: the full solution, including the WPF app
dotnet test

dotnet build Stringline.Portable.slnf -warnaserror   # Linux/macOS: everything except the WPF app
dotnet test Stringline.Portable.slnf
```

## Recording a SimSig session

Enable the Interface Gateway in SimSig's options before starting the sim, then:

```sh
dotnet run --project src/Stringline.Tools -- record --out my-sim.sslog          # movements and clock
dotnet run --project src/Stringline.Tools -- record --td --out my-sim.sslog     # also TD and signalling (much busier)
dotnet run --project src/Stringline.Tools -- record --user <forum name> --out my-sim.sslog   # payware sims
```

For `--user`, the password comes from the `STRINGLINE_PASSWORD` environment variable, or you are prompted for it. It is never written to the recording or the log. Press Ctrl+C to stop; the command prints message counts and rates per message type. If SimSig is not running yet, the recorder keeps retrying (1 s back-off rising to 30 s).

## Replaying a recording

`replay-server` plays a recording as a stand-in for SimSig on port 51515, so Stringline (or `record`) can connect to it without SimSig running:

```sh
dotnet run --project src/Stringline.Tools -- replay-server tests/fixtures/waterloo-1x.sslog --speed 8
dotnet run --project src/Stringline.Tools -- replay-server my-sim.sslog --start 07:30 --speed 0   # from 07:30, as fast as possible
```

Playback starts when the first client connects. Press Space to pause or resume, `+` or `-` to change speed, and Q or Ctrl+C to stop. Close SimSig first, or use `--port`, because only one program can listen on 51515.

## Layout

- `src/` — `Stringline.Core` (sim time, identifiers, lenient JSON), `Gateway`, `Timetable`, `Network`, `Planning`, `Persistence`, `Graph`, `Tools` (the `stringline` CLI) and `App` (WPF, Windows only).
- `tests/` — xUnit test projects; `tests/fixtures/` holds free-sim data only. Recordings and timetables from payware sims go in the git-ignored `local-fixtures/`.
- `docs/` — design documents and the session-by-session plan.

## Licence

Copyright (C) 2026 Frederick Duncan and Stringline contributors.

Stringline is free software: you can redistribute it and/or modify it under the terms of the GNU General Public License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version. It is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See [`LICENSE`](LICENSE) for the full text.

By contributing, you agree that your contributions are licensed under the same terms.

The licence covers Stringline's source code only. Timetables, recordings and other data derived from SimSig simulations (for example, anything in `tests/fixtures/`) remain subject to their owners' terms and are not covered by the GPL.

## Disclaimer

Stringline is an independent project. It is not affiliated with, endorsed by or supported by SimSig or its developers. SimSig is a trademark of its respective owners.
