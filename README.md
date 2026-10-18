# Stringline

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
