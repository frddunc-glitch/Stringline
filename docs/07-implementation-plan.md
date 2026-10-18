# 07 — Implementation Plan (Claude Code sessions)

Each session is sized for one focused Claude Code sitting (roughly 1–3 hours of agent work), ends with a green build, passing tests and a commit, and has a ready-to-paste prompt. Sessions marked 🪟 need Windows (run Claude Code on your PC); the rest can run anywhere, including cloud sessions, because core projects target `net10.0`.

**Fred's manual steps** are marked 👤 — mostly capturing recordings and sample files from SimSig.

## Phase 0 — Foundations

### S1 — Repo skeleton and core types
- Create solution and all projects per `02-architecture.md`; Directory.Build.props (nullable, warnings as errors, LangVersion latest), central package management, .editorconfig, GitHub Actions (build + test on `windows-latest` and `ubuntu-latest`; App only on Windows).
- `Stringline.Core`: `SimTime` (parse/format `HH:mm:ss`, `HH:mm½`, day rollover), `Tiploc`, `Headcode`, `TrainId`, lenient JSON converters (string-or-native bool/int).
- **Done when:** `dotnet test` green; `SimTime` and converters fully tested.
- Prompt: *"Read CLAUDE.md and docs/02-architecture.md. Do Session S1 from docs/07-implementation-plan.md."*

### S2 — STOMP client, parser, recorder + CLI `record` 🪟(test run)
- Minimal STOMP 1.1 client (`GatewayClient`): connect/login, subscribe, send, frame reader with NUL terminator and content-length, reconnect with back-off, cancellation.
- `MessageParser` → `GatewayEvent`s for every message in `03-…md` (§3) using `Utf8JsonReader` streaming (handles duplicate keys).
- `.sslog` writer; `stringline record --host --port --user --out file.sslog [--td]`.
- In-process fake STOMP server for tests.
- 👤 **After this session:** run `stringline record` against 2–3 sims for 30–60 min each (one at 4×+ speed, one with an in-game failure). Save the `.wtt` used. Put free-sim captures in `tests/fixtures/`, payware ones in a git-ignored `local-fixtures/`. Answer §6 questions in `03-…md` (a short notes file is fine).

### S3 — Replay server and replay source
- `ReplaySource : ISimEventSource` (speed factor, pause, seek to sim time).
- `stringline replay-server file.sslog --speed 8` — a fake SimSig on 51515 so the real client can be exercised end to end.
- Update `03-…md` with verified facts from Fred's recordings; add parser tests for every message type seen.

## Phase 1 — Data

### S4 — WTT import
- `WttReader`: unzip, parse `SavedTimetable.xml` tolerantly into the domain model; categories; activities/next-working; warnings list.
- `stringline wtt-dump file.wtt` (summary + per-train table).
- Fixture tests against at least two real `.wtt` files.

### S5 — Live state and train matching
- `LiveState` reducer (actuals per sim uid, clock with interpolation, TD berth map optional).
- `TrainMatcher` per `04-…md` §3; next-working following; manual link API.
- Test: replay a fixture recording + its WTT → ≥ 95% trains matched; report unmatched.

### S6 — Corridors, profile, derived axis
- Auto corridor generation, section min run times, derived axis; profile JSON load/save keyed by `area_id`.
- `stringline corridors file.wtt` prints candidates.
- Tests on synthetic and real WTTs.

## Phase 2 — The graph (MVP visible)

### S7 — Graph scene + Skia renderer (headless)
- `GraphViewport`, `GraphSceneBuilder` (WTT + actual layers first), `SkiaGraphRenderer`, grid, location labels, now line, headcode labels.
- `stringline render-png --wtt --log --corridor --at 09:00 --hours 3 out.png`.
- Verify snapshot tests of PNG output. **This is the first "looks like a train graph" moment.**

### S8 — WPF shell and live graph 🪟
- App shell with DI/hosting, connection dialog (DPAPI credentials), replay dialog, corridor tabs, status bar, SKElement host, pan/zoom/follow, hover tooltip, train list.
- Runs against live SimSig or the replay server.
- **MVP milestone:** live graph of plan vs actual for a real sim.

### S9 — Performance and polish of the graph
- Path caching, culling, label placement grid, hit-test grid; benchmark (BenchmarkDotNet) 500 trains / 6 h at ≥ 30 fps equivalent render time (< 33 ms).
- Colour by category / delay band; vertical orientation toggle; PNG export.

## Phase 3 — Planning

### S10 — Working plan, commands, undo/redo
- `WorkingPlan` + all commands in `06-…md` §1 with validation and clamping; `PlanningEngine` loop publishing `PlanSnapshot`s.
- Unit tests per command; determinism tests.

### S11 — Forecaster
- Independent forecast (steps 1–3), then interaction pass (step 4) behind a toggle.
- Golden scenario fixtures (double line + loop, single line + crossing loop, junction).
- Forecast layer added to scene builder.

### S12 — Interactive re-planning UI 🪟
- Train detail panel with editable plan column; drag path / Alt-drag from vertex / dwell drag with ghost preview and min-run clamping; context menu actions; undo/redo; edit history.

### S13 — Disruptions and notes 🪟 (core part portable)
- Disruption model + commands; forecaster honours them; graph hatched blocks and restriction bands; create by right-click or B-drag; notes.

### S14 — Conflict detection
- All kinds in `06-…md` §3; conflicts panel; red markers; click-to-focus. Tests per kind on golden scenarios.

### S15 — Suggestion engine
- Candidate generation, scoring with forecaster, top-3 with preview/apply as composite command. Regulation list view.

## Phase 4 — Session, reporting, release

### S16 — Session persistence and review
- SQLite session store; autosave; resume after restart (rebuild plan from commands + events); post-run review mode (replay saved session).

### S17 — Reporting and exports
- Delay table per train/location; CSV export; PDF export of graph (SkiaSharp PDF backend); print preset A3 landscape.

### S18 — Packaging 🪟
- Single-file self-contained publish; app icon; settings page; logging; About/credits; README with setup (enabling the gateway in SimSig).

## Phase 5 — Optional extensions
- **S19** Opt-in assist: route-request / TD-interpose for the selected regulation step, with explicit confirmation and per-session enable.
- **S20** Optimiser (`IReplanner` using OR-Tools CP-SAT) for a rolling horizon.
- **S21** Split-view multiple corridors / multi-board layout; vertical TCD print plotting.

## Dependencies
```
S1 → S2 → S3 ─┬→ S5 → S7 → S8 → S9
       S4 ────┤       ↑
       S6 ────┘───────┘
S8 → S10 → S11 → S12 → S13 → S14 → S15 → S16 → S17 → S18 → (S19–S21)
```
S4 and S6 can run in parallel with S2–S3 (separate worktrees/sessions).

## Working agreement for every session
1. Start: read `CLAUDE.md`, the docs named in the session, and the previous session's notes in `docs/progress.md`.
2. Write tests first for core logic; keep WPF code thin.
3. Finish: `dotnet build -warnaserror` and `dotnet test` green; update `docs/progress.md` (what was done, decisions, what's next, known issues); update design docs if anything changed; commit.
4. If a design question blocks progress, write it under "Questions for Fred" in `docs/progress.md` and pick the conservative default rather than stopping.
