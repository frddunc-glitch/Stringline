# Stringline — Brainstorm and Key Decisions

*Working name: **Stringline** — a live electronic train graph (ETG) for SimSig.*
*Status: draft 1, 18 October 2026. Author: Fred (Railnexus Consulting) with Claude.*

## 1. The idea in one paragraph

A Windows desktop app that connects to a running SimSig simulation through the Interface Gateway, loads the simulation's working timetable (WTT), and draws a live time–distance graph for one or more corridors. Planned paths, actual running and forecast running are separate layers with a moving "now" line. Like ViziRail's Electronic Train Graph, the user can drag and click to re-plan late services (retime, hold, change platform/line, cancel, add ad-hoc paths), see the conflicts a re-plan creates or removes, mark disruptions (blocks, speed restrictions, possessions), and get suggested resolutions. Stringline is the *planning brain*; SimSig remains the *railway* — the user carries out the plan by signalling.

## 2. What the SimSig Interface Gateway gives us

Full reference in `03-simsig-gateway-integration.md`. The design-shaping facts:

| Fact | Consequence |
|---|---|
| STOMP server hosted by SimSig on TCP 51515 (default); JSON bodies | Small purpose-built STOMP 1.1 client in C#; no broker |
| Payware sims require SimSig username/password on CONNECT, with an authentication delay | Credentials stored with Windows DPAPI; generous connect timeout |
| `train_location` (TRAIN_MVT_ALL_TOC): headcode, sim-internal uid, action arrive/depart/pass, TIPLOC or signal ID, platform/line, time | The "actual running" feed — backbone of the live graph |
| `train_delay` appears to report expected *entry* delay, not running delay, and is irregular | Compute delay ourselves (actual − plan). Use `train_delay` only to forecast trains not yet entered |
| `clock_msg`: seconds after midnight, `interval` (500 = normal speed; speed = 500/interval), `paused`; sent each sim-minute and on speed change | Interpolate the sim clock locally between messages |
| TD berth steps (CA/CB/CC) and SG state (track circuits, signals, routes, points…) | Optional finer positioning and disruption hints (e.g. track occupied with no train) |
| Commands accepted: TD step/cancel/interpose, route request, signal replace, key points, flags, ground frames, crossings, snapshot | **No timetable-editing command.** Re-plans are advisory; "assist" writes are optional later |
| No track distances published | Distance axis configured or derived (§4.3) |
| Snapshot reply uses duplicate `SG_MSG` keys in one object | Parse with `Utf8JsonReader` token streaming, never a DOM |
| Booleans/integers often sent as strings (`"True"`, `"1523"`) | Lenient converters everywhere |
| Sim `uid` ≠ timetable UID | Match live trains to WTT entries by headcode + time window + next-working links |

Community documentation is as of SimSig 5.21 (2022). Session 2 captures real traffic so we confirm everything before building on it.

## 3. ViziRail ETG features mapped to Stringline

ViziRail's ETG is a live train-control-diagram interface linked to the control system, with drag-and-drop schedule adjustment, point-and-click path changes, and display of trains, possessions and ad-hoc notes; its planning graphs also show possessions and speed restrictions.

| ViziRail capability | Stringline v1 | Later |
|---|---|---|
| Live time–distance train control diagram | ✅ Per-corridor graph; WTT / working plan / actual / forecast layers; now line; auto-scroll | Multiple boards side by side, print/plot |
| Interface with train control | ✅ Read-only from SimSig gateway | Opt-in "assist" (route request, TD interpose) |
| Drag-and-drop schedule adjustment | ✅ Drag whole path or "from this point on" in time | Snap to next free headway slot |
| Point-and-click path changes | ✅ Hold at location, change platform/line, add/skip stop, terminate short, cancel, reinstate | Divert via another corridor |
| Possessions / blocks | ✅ Time–distance rectangles that forecasting respects | Import from scenario file |
| Speed restrictions | ✅ Section running-time multiplier or added minutes | By train category |
| Ad-hoc notes | ✅ Pinned annotations | — |
| Delay monitoring | ✅ Delay at each timing point, worst offenders list, CSV export | Delay attribution codes, reports |
| Ad-hoc trains | ✅ Clone an existing path as a new working | Free-slot path search |
| Planning support | ✅ Conflict detection + rule-based suggestions | Rolling-horizon optimiser |

Out of scope: consists, yards, incidents, train notices distribution, GPS.

## 4. Decisions and reasoning

### 4.1 Stack — C# / .NET 10 LTS / WPF + SkiaSharp
- SimSig is Windows-only and you prefer C#; WPF is mature and Claude Code is strong with it.
- The graph redraws hundreds of polylines while panning, zooming and animating. WPF shapes don't scale well to that, so the graph is a **single SkiaSharp surface** (`SKElement`; `SKGLElement` if GPU needed) with our own hit-testing. Everything around it is ordinary WPF + MVVM (CommunityToolkit.Mvvm).
- Considered: Avalonia (cross-platform not needed), WinUI 3 (weaker tooling), Python/Qt (fine, but not your preference).
- Core libraries target plain `net10.0` so they build and test on Linux too (Claude Code cloud sessions); only the WPF app targets `net10.0-windows`.

### 4.2 Three plan layers plus forecast
1. **WTT** — immutable, loaded from the `.wtt` file.
2. **Working plan** — WTT + every user re-plan, as an undoable command history ("what control intends").
3. **Actuals** — what SimSig reports.
4. **Forecast** — projection from each train's latest actual along the working plan, respecting minimum running times, dwells, disruptions and other trains.

Drawn as: WTT faint/thin, working plan thin solid, actual bold solid, forecast dashed.

### 4.3 Distance axis — derived by default, real distances optional
SimSig publishes no mileages.
- **Derived (default):** order corridor locations from timetable sequences and space them by the minimum planned running time between adjacent points across all trains. Fast trains then draw as near-straight lines — what a controller wants.
- **Configured:** user enters kilometres per location; used where present.

### 4.4 Corridors
An ordered list of timing locations with axis positions, plus sections between them (number of tracks, direction rules — Up/Down/bi-directional/single line with loops — default headway). Each sim has one or more corridors, each its own graph tab. Auto-generated from the WTT, then hand-tuned in a simple editor; stored in a per-sim profile.

### 4.5 Re-planning is advisory
The gateway can't alter SimSig's timetable, so the working plan never pushes to SimSig. Instead Stringline produces a **regulation list** per location ("1A23 before 6M45 at XYZ; hold 6M45 in loop ~6 min"). An opt-in "assist" mode may later issue route requests — off by default because setting routes unasked is a nasty surprise, especially in multiplayer.

### 4.6 Forecasting, conflicts, suggestions
- Forecast is a fast discrete-event projection, re-run on every relevant event (target < 50 ms for 500 trains).
- v1 conflicts: same-direction headway per section; opposing movements on single-line sections; platform re-occupation; possession/restriction overlaps.
- v1 suggestions: rule-based candidates (hold lower priority at last suitable location, swap order, alternative platform/line), scored by priority-weighted delay; best 2–3 offered.
- Later: rolling-horizon optimisation (e.g. Google OR-Tools CP-SAT).

### 4.7 Record and replay — the key to productive Claude Code sessions
Claude Code can't run SimSig. So early sessions build:
- a **recorder** logging every gateway frame with wall-clock and sim timestamps (`.sslog`, JSON lines), and
- a **replay server** — a fake SimSig speaking STOMP that replays a recording at any speed.
You capture a handful of real sessions once; every later session develops and tests against them.

### 4.8 Train identity matching
Headcodes repeat across a day and change at next-working associations. Matcher: on a new sim uid, candidates = WTT trains with that headcode; pick the one with nearest planned time at that location within a window, honouring next-working links. Unmatched trains are still drawn (actuals only) and flagged for manual linking.

### 4.9 Persistence
- Per-sim **profile** (JSON): corridors, section rules, category colours and priorities, location names.
- Per-run **session** (SQLite): event log, working-plan command history, disruptions, notes. Supports resume and post-run review.
- Exports: PNG/PDF graph, CSV actual-vs-plan. No industry interchange formats (e.g. railML) — this is a tool for a game.

## 5. Open questions (defaults assumed in brackets)

1. **Which sims first?** Ideally one busy two-track main line and one with single-line sections and crossing loops. [One free sim for committed test data plus one payware sim you own, recordings kept local.] **Done for S3:** Waterloo (free, two-track main line, in `tests/fixtures/`) and Penrith (payware, `local-fixtures/`). A single-line sim with crossing loops is still wanted for S6/S11.
2. **Multiplayer:** connect to another player's host? [Host/port configurable; default localhost.]
3. **Distance axis:** derived by default OK? [Yes.]
4. **Assist writes** (route requests / TD interposes): keep on roadmap? [Yes, final optional session.]
5. **Priorities:** source? [Headcode first digit by default, overridable per WTT category.]
6. **Distribution:** personal or community release? **Decided 2026-10-18: open source, public on GitHub, GPL-3.0-or-later.** Recordings from payware sims are not redistributed.

## 6. Risks

| Risk | Mitigation |
|---|---|
| Gateway behaviour differs from community docs | Recorder first; build parsers from real captures. **S3:** checked against real recordings; differences recorded in `03-…md` §6 (e.g. `ttsummary` is an array, flat-out `interval:1`, failures not reported) |
| WTT XML varies between versions | Tolerant parser, fixture tests over several real WTTs |
| Timetable edited in-game after `.wtt` saved | Detect unknown trains, allow re-import and manual linking |
| Auto corridor generation wrong at complex junctions | Editable profile + corridor editor |
| Graph performance | Single Skia surface, viewport culling, cached geometry |
| Sim fast-forward / pause | All logic in sim time; clock interpolation uses `interval` and `paused` |
