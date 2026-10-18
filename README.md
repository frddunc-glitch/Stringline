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
