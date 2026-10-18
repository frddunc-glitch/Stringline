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
- Only `Stringline.App` may reference WPF or target `net10.0-windows`. Everything else targets `net10.0` and must build and test on Linux.
- All times are `SimTime` (int seconds after midnight of sim day 0, may exceed 86400). Never use `DateTime` for sim time.
- Gateway JSON: numbers and booleans may arrive as strings — use the lenient converters. Snapshot bodies contain duplicate keys — parse with `Utf8JsonReader`, never `JsonDocument`.
- Never send hand-built JSON to SimSig; use the tested command builders. Assist (outbound) features are opt-in and off by default.
- Planning engine is single-writer; UI reads immutable `PlanSnapshot`s.
- Unknown/malformed messages are logged and skipped — never crash.
- Never log credentials.
- Australian English in UI text and docs; railway terms as SimSig uses them (headcode, TIPLOC, Up/Down).
- Payware-sim recordings and WTTs live in git-ignored `local-fixtures/`; only free-sim data goes in `tests/fixtures/`.

## Commands
- Build: `dotnet build -warnaserror`
- Test: `dotnet test`
- On Linux (no WPF): `dotnet build Stringline.Portable.slnf -warnaserror` and `dotnet test Stringline.Portable.slnf`
- New test projects go in `tests/`, and in `Stringline.Portable.slnf` too unless they reference `Stringline.App`.
- Record: `dotnet run --project src/Stringline.Tools -- record --host localhost --port 51515 --out x.sslog`
- Replay server: `dotnet run --project src/Stringline.Tools -- replay-server x.sslog --speed 8`
- Render PNG: `dotnet run --project src/Stringline.Tools -- render-png --wtt t.wtt --log x.sslog --corridor main --at 09:00 --hours 3 out.png`

## End of every session
Build and tests green → update `docs/progress.md` (done, decisions, next, known issues, questions for Fred) → update any design doc you changed → commit.
