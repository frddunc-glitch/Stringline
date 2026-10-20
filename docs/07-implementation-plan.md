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

### S14a — Profile editor 🪟 (edit logic portable)
Covers COR-2 and COR-5, which S6 left to "a simple editor" (`00-…md` §4.4) that no session had built: until now a profile could only be changed by hand-editing its JSON.
- **Edit model (portable, `Stringline.Network.Profiles`):** immutable edits on `StringlineProfile`. They cover:
  - the sim name and location display names;
  - corridors: add from a generated candidate or from scratch, remove, rename, reorder tabs;
  - each corridor's locations: add, remove, reorder, distance (km, or derived), platform overrides;
  - sections: tracks, direction (Up/Down/bi-directional/single line), default headway;
  - category styles: colour, line style, priority.
  - Every edit is validated with the same rules as `ProfileJson` (no duplicate IDs or locations, sections match locations, every section has a track), so an invalid profile is never saved. "Regenerate corridors" uses `CorridorGenerator` and asks before replacing hand-tuned ones.
- **Location names CSV import** (`04-…md` §1): `TIPLOC,name`, tolerant of a header row and quoting; unknown TIPLOCs are reported, not fatal.
- **App:** a Profile menu with Edit profile…, Open profile file, Reload profile (re-read and redraw without reloading the WTT) and Regenerate corridors. The editor window has tabs for General (name, location names, CSV import), Corridors (locations and sections) and Categories, with a preview of the selected corridor's axis. Apply or OK saves through `ProfileStore`, then rebuilds the forecast network, corridor tabs and scene through the existing load path. The working plan, disruptions and selection are kept, and a removed corridor's tab closes. The view-model must not touch WPF types.
- **No sim ID:** a timetable without an area ID currently gets an unsaved `"unknown"` profile that cannot be changed at all. Let the user name the sim, then save its profile under that name.
- **Tests:** each edit and its validation; CSV import; round trip edit → save → load → `Refresh` keeps the edits; App tests driving the editor view-model on Waterloo (rename a location, reorder a corridor, change a headway and see the forecast use it).
- **Done when:** a profile can be changed fully in the app without touching its JSON, and the graph redraws with the change.

### S15 — Suggestion engine
- Candidate generation, scoring with forecaster, top-3 with preview/apply as composite command. Regulation list view.

## Phase 4 — Session, reporting, release

### S16 — Session persistence and review
- SQLite session store; autosave; resume after restart (rebuild plan from commands + events); post-run review mode (replay saved session).

### S17 — Reporting and exports
- Delay table per train/location; CSV export; PDF export of graph (SkiaSharp PDF backend); print preset A3 landscape.

### S18 — Packaging 🪟
- Single-file self-contained publish; app icon; settings page; logging; About/credits; README with setup (enabling the gateway in SimSig).

## Phase 4a — Fixes from Fred's first use (GitHub issues #1–#13)
Fred raised 13 issues on 2026-10-19 after using the S18 build. These sessions fix them before the optional extensions. Each session closes its issues with `Fixes #n` in the commit. Order: S22 → S23 → S24 → S25 → S26 → S27. S28 and S29 can run at any point, and S29 waits on Fred's recording. Each session still ends with a green build, tests, `progress.md` and a commit.

| Issue | Title | Session |
|---|---|---|
| #1 | Correct Unknown Publisher | S22 |
| #2 | Delay column display bug | S23 |
| #3 | Long graph location names clipped | S23 |
| #4 | Create user guide | S28 |
| #5 | Investigate multiplayer functionality | S29 |
| #6 | Sim/timetable identification | S22 |
| #7 | Review headway conflict calculations | S27 |
| #8 | Settings menu | S25 |
| #9 | Window resizing behaviour | S23 (split views: S21) |
| #10 | Dragging a train path to re-plan | S26 |
| #11 | Right time wording | S24 |
| #12 | Infrastructure and conflict red boxes (tooltips) | S24 |
| #13 | Make SimTime more prominent (low priority, waiting on feedback) | S24 (optional part) |

### S22 — Publisher and sim identity 🪟 (#1, #6)
- **#1 Unknown publisher.** Windows says "Unknown publisher" because the exe and installer are not Authenticode-signed (S18 question 13). Metadata alone cannot fix that, but it is still part of the fix:
  - Set `Company`, `Authors` and `Copyright` in `Directory.Build.props` to "Frederick Duncan and Stringline contributors". Today `Authors` is "Fred" and there is no `Company`. Inno's `AppPublisher` already matches.
  - Add an optional signing step to `build/publish.ps1` and `release.yml`: `signtool` (or the signing service's action) signs `Stringline.exe` before it is zipped, and then the installer (Inno `SignTool=`). The step is skipped, with a warning, when no certificate is set up. Credentials only ever come from GitHub secrets and are never logged.
  - 👤 Fred picks a route and applies:
    - **SignPath Foundation**, free for open-source projects (the recommended route; it needs the GitHub repo and an OSI licence, which GPL-3.0 is);
    - **Azure Trusted Signing** (about US$10 a month);
    - or an OV certificate.
  - Until a certificate exists, the README's SmartScreen note stays and says why.
- **#6 Sim and timetable shown together.** Show "*sim name* – *timetable*" everywhere the app now shows only one of them:
  - the window title;
  - the status bar;
  - the session list and saved session (`SimName` plus the timetable's name and version);
  - the PNG and PDF title band;
  - the CSV header.
  - The sim name comes from the profile, then the gateway's area ID, then "Unknown sim". The timetable comes from the WTT header (name and version). Review mode adds "(review)".
  - Add a `SimTitle` formatter in `Stringline.App` (or Core, if the Tools need it too) so every place formats it the same way.
- **Tests:** the formatter's fallbacks; the title, status bar and PDF band on Waterloo; publish.ps1 skips signing cleanly when there is no certificate.

### S23 — Panel layout, resizing and long names 🪟 (#2, #3, #9)
- **#2 Columns cut off at the right edge.** Check every list and grid in the side panels: the train list, the train detail plan, Conflicts, Suggestions, Regulation, Delays (trains and points) and History.
  - Fixed pixel widths, with the last column (Delay/Late/Final) past the panel's edge, are the likely cause.
  - Give each list one star-sized text column (headcode or description), with the numeric columns right-aligned and sized to their content.
  - Add a horizontal scroll bar only below the panel's least width, and `TextTrimming` with a tooltip for long text.
  - A UI test sets each panel to its least width and checks the last column is fully visible (column right edge ≤ viewport width).
- **#3 Long location names clipped.** The horizontal graph has a fixed 150 px left margin, and the labels are right-aligned into it (`GraphMargins.Default`, `SkiaGraphRenderer.DrawLocationLabels`). The vertical graph has the same problem at the top (170 px).
  - Work the margin out from the widest measured label (name or TIPLOC, station font), between a least and a most width (e.g. 90–260 px).
  - Past the most width, shorten the name with an ellipsis. The full name and TIPLOC show in the hover.
  - Use the same margin in the PNG and PDF exports.
  - The profile's display names (S14a) let users shorten a name for good. Say so in the hover and the guide.
  - Verify snapshot tests: a corridor with a 40-character name, in both orientations.
- **#9 Resizing does what users expect.** Splitters between the train list, graph and side panel, and between the train detail, tabs and suggestions:
  - **Star sizing.** Use proportions, so growing the window grows the graph first and the panels keep their share. Now the side columns are fixed at 330 and 490 px.
  - **Least sizes.** A splitter cannot push a panel below its least size, or off the window.
  - **Hide panels.** A double-click on a splitter, or View ▸ Panels, collapses or restores the panel beside it.
  - **Remembered layout.** Splitter positions, the window's size and place, and maximised state are saved in settings and restored, back onto a visible monitor if a screen has gone.
  - Add View ▸ Reset layout.
  - S21's split and multi-board views stay in S21. This session fixes the single view only.
- **Tests:** layout save and restore (view-model); the least-width column test above; a window-shot check at 1280×720 and 2560×1440.

### S24 — Wording, tooltips and the clock 🪟 (core part portable) (#11, #12, #13)
- **#11 Early / On time / Late.** Replace "Right time" with railway punctuality wording everywhere: hover, train detail, Delays tab, Regulation, CSV, the colour legend and tooltips, the README and the docs.
  - The wording is "On time" within the minute (as now), "*n* min late" and "*n* min early".
  - `DelayBand.RightTime` becomes `DelayBand.OnTime`. The colour stays the same.
  - The CSV column values change, so note it in the release notes.
  - Add a test that no UI string or resource contains "right time".
- **#12 Tooltips for conflict and disruption boxes.** Extend the hit testing (`GraphHitTester`/`GraphHitIndex`) to conflict markers and disruption blocks or restriction bands, and `GraphHover` to describe them.
  - **Conflict:** the kind, the trains, where (from–to), when, how bad it is (minutes), and the top suggestion if there is one. A click still focuses it.
  - **Disruption:** the kind (block/possession or speed restriction), the section and tracks, from–to times, the speed or effect, and its note. A hint says "right-click to edit".
  - The hover is the same in both orientations. Portable tests go in Graph.Tests.
- **#13 Prominent sim time (optional; Fred: not critical, waiting on feedback).** Build it only if the session has time:
  - a large sim clock (and speed) at the toolbar's right, sized by a setting (Normal/Large/Huge);
  - optionally, a small always-on-top clock window for a second monitor.
  - Otherwise, leave it for user feedback.
  - *Built in S24:* the clock went at the right of the menu row, because the toolbar has no room at 1280 px. There is no floating window (Fred). The CSV needed no change: it never had the wording.
- **Tests:** the hover text per kind; the wording test; App tests for the clock setting if built.

### S25 — Menus against the UI spec 🪟 (#8)
- Review the whole shell against `05-…md` §1, §8 and §12 and Windows conventions, then fix what differs:
  - **Settings.** Move Settings… out of View into a **Tools** menu, as Tools ▸ Options… (Ctrl+, stays). Profile editing could go there too; decide when the review is done.
  - **Every menu.** Check that each item is where users look for it: Session versus File naming, export placement, and the View menu holding only display switches.
  - **Accelerators and shortcuts.** Check they are unique.
  - **Toolbar and context menus.** Check they match the menus.
- Write the review's findings into `05-…md` (a short "spec vs build" table) and update the spec where the build is better. Add a "Questions for Fred" entry for any change of naming.
- **Tests:** an App test that every menu command is bound, and that no access key repeats within a menu.
- *Built in S25:* the spec's bar, File · Session · View · Corridor · Plan · Tools · Help (Fred). The profile's items went to Tools, Undo and Redo stay in Plan, and the corridor tabs are one row with a » overflow. The review is in `05-…md` §1.

### S26 — TRIMS-style grips for drag re-planning 🪟 (portable drag model) (#10)
- **Find out first.** Reproduce why dragging "does not work as expected" (`PlanDrag`, `GraphView.xaml.cs`):
  - the hit tolerance on a thin line;
  - drag starting a pan instead;
  - Alt being taken by the WPF menu;
  - the now-line and latest-report clamps stopping the drag with no feedback.
  - Write the cause in `progress.md`.
- **Grips on the selected train** (TRIMS style; `05-…md` §3 to be updated):
  - Selecting a train draws grips:
    - a square at each timing point it has not yet passed (drag = retime from here, now `ShiftFrom` without Alt);
    - a bar at each stop's departure end (drag = dwell);
    - a handle on the line's body (drag = shift the rest of the run).
  - Grips are only drawn on the selected train, so they never clutter the graph. They are hit first, with a tolerance of 6 px or more.
  - The cursor changes over a grip, and the status bar names the action.
  - Points that cannot move (past, or the train's latest report) show as hollow or locked, and the tooltip says why.
  - While dragging, the ghost and a time label at the grip show the new time. A clamp is shown on the grip, not only in the status bar.
  - Arrow keys nudge the selected grip by 15 s (Shift: 1 min). Esc cancels a drag.
  - Alt+drag stays as a shortcut.
- **Tests:** grip hit testing and the drag preview per grip kind (Graph.Tests, portable); App tests driving select → grip drag → command → undo on Waterloo.
- *Built in S26:* the cause was the forecast line winning the hit test over an on-time train's own line (see `05-…md` §3). Fred: the handle and the whole line drag the body, a click picks a grip for one edit per nudge, and passed points are hollow grey squares.

### S27 — Sim-aware headways (#7)
- **Problem.** Every section gets `Section.StandardHeadwaySeconds` = 180 s unless its profile says otherwise. That is too much for busy metro sims and gives false headway conflicts. SimSig's WTT has no headways: SimSig works by signalling, and its timetable writers book trains to it.
- **Work out a headway for each section, in order of preference:**
  1. **The profile's section headway**, if set by hand (S14a; "hand-set" is marked so regenerating keeps it).
  2. **From the WTT.** The least booked spacing between same-direction trains on the same track, taken as a low percentile (e.g. the 5th) of booked spacings so one tight pair does not set it. It must lie between a floor and a ceiling (e.g. 60–300 s), with at least N pairs, else fall back. This follows the existing rule that no pair need be further apart than the WTT books them.
  3. **A profile-wide default**, then a global one in Settings. The global default stays 3 min unless the WTT corpus shows otherwise.
- **Observed spacing (optional; gated on the work above).** Spacing between actual trains in recordings (or live, from TD berth steps) could tighten or confirm the WTT figure. Report it in `stringline corridors --headways`, and do not apply it on its own.
- **Show where a value comes from.** The profile editor shows each section's headway and source (hand / WTT / default), and `stringline corridors` prints them. A conflict's tooltip (S24) names the headway used.
- **Tests:**
  - derivation on synthetic sections;
  - the corpus: Waterloo's headway conflicts on the 4× recording before and after, which should fall without losing the real ones in the failure recording;
  - Llangollen single line unchanged;
  - `ProfileJson` round trip with the source.
- Update `06-…md` §3 with the rule.
- *Built in S27:* Fred chose "only tighten": a WTT figure is used only where it is less than the default, as one above it added false conflicts on Waterloo's Windsor lines. The observed spacing is built (`corridors --headways --log`). See `06-…md` §3.

### S28 — User guide, in-app help and wiki (#4)
- **Guide in the repo** (`docs/user-guide/`, Australian English, railway terms as SimSig uses them). Its parts:
  - installing;
  - turning on the gateway;
  - connecting (single player and multiplayer, after S29);
  - timetables and profiles;
  - reading the graph;
  - re-planning (grips, after S26);
  - disruptions and notes;
  - conflicts and suggestions;
  - the regulation list;
  - sessions and review;
  - exports and reports;
  - settings;
  - troubleshooting and logs;
  - a glossary;
  - keyboard and mouse reference.
- **Screenshots** come from the `STRINGLINE_WINDOW_SHOTS` hook and `render-png` with free-sim data only, so the guide's images can be made again.
- **In-app help** (offline, works without a network):
  - Help ▸ User guide opens the guide bundled with the app as HTML, made from the Markdown at build time with a GPL-compatible library such as Markdig (BSD-2); check before adding it.
  - F1 opens the page for the focused panel or window.
  - "Getting started (online)" stays.
- **GitHub wiki.** A workflow (or `build/publish-wiki.ps1`) copies `docs/user-guide/` into the repo's wiki, so the repo stays the source and the wiki is never edited by hand.
  - 👤 Fred turns on the wiki and creates its first page. GitHub needs that first page before the wiki can be pushed to.
- **Other sources.** Review the SimSig wiki and forum pages on the Interface Gateway, and link them from the guide's "Further reading". Raise follow-up issues for gaps found (the issue expects this may need another issue).
- **Tests:** link check over the guide (no broken relative links or images); the bundled help resource exists and every F1 target page exists.
- *Built in S28:* Fred chose to embed the Markdown and render it at run time (Markdig 1.4), a workflow plus script for the wiki, a provisional multiplayer section (S29 confirms it), and committed pictures. See `05-…md` §13.

### S29 — Multiplayer investigation 🪟 (#5)
- 👤 **Before this session**, Fred records:
  - a short multiplayer session (host plus one client, 20–30 min), with `stringline record` on the host and on a client if the client's gateway can be reached;
  - notes on which machine has the gateway turned on, and any login asked for.
- **Answer `03-…md` §6.9**:
  - does each player's SimSig run its own gateway, or only the host's?
  - does a client see the whole sim's messages or only its own panel's?
  - are the clock messages the same?
  - do any message shapes differ?
- Add parser tests for whatever the recording shows.
- **App:** the connect dialog already takes a host and port.
  - Add a "multiplayer" note to it.
  - Check that the reconnect and back-off cope with the host pausing or dropping out.
  - Check that a client sees a partial area correctly: trains are not marked missing because they are on another player's panel.
  - Assist (S19) stays off. Its confirmation must name multiplayer if S19 is built later.
- **Write-up.** Record the outcome in `00-…md` §5 question 2, and add the guide's multiplayer page (S28). If a real gap needs code, raise it as an issue sized for a session.

## Phase 4b — From the open-question review (2026-10-20)
Fred went through the open questions on 2026-10-20 (answers in `progress.md`, "Open-question review"). Four sessions came from them. Order: S30 → S31 → S32 → S33. S30 and S31 are small and can run in either order. Each session still ends with a green build, tests, `progress.md` and a commit.

| Source | Topic | Session |
|---|---|---|
| Issue #14 | Privacy policy (stored SimSig password) | S30 |
| Q17 | Sim name copied from an older WTT's title | S31 |
| Q10 | Forecast line looks like a dashed category | S31 |
| Q5, Q7 | WTT rules in entry forecasts | S32 |
| Q12 | Holding trains at signals | S33 |

### S30 — Privacy policy and stored data 🪟 (#14)
- **Audit first.** List everything the app writes and where, and write it into the policy:
  - roaming settings (`AppPaths`, `AppSettings`): host, port, user name, and the password only when "Remember password" is ticked, encrypted with DPAPI for the current Windows user;
  - profiles (`%APPDATA%\Stringline\profiles`);
  - logs and help (`%LOCALAPPDATA%\Stringline`);
  - sessions and recordings (`Documents\Stringline`).
  - Confirm nothing is sent anywhere but the SimSig gateway the user connects to. There is no telemetry and no update check. Confirm that no log line, session file, recording or crash report holds the password. A test greps a recorded connect, its log and its `.sslog` for a known password. The STOMP `CONNECT` frame is the one place it legitimately goes.
- **`PRIVACY.md`** at the repo root (Australian English, plain words): what is stored, where, and how; what is sent and to whom (only the gateway); how to remove everything; and a contact (the GitHub issues). Add a copy to the user guide (`privacy.md`) and the wiki, and link it from the README and About.
- **UI:**
  - a "Privacy" link in Help, in About, and next to "Remember password" in the Connect dialog (a tooltip says how it is stored);
  - **Forget saved password** (Tools ▸ Options ▸ Connection, or the Connect dialog). It clears `ProtectedPassword` and unticks Remember at once;
  - unticking Remember and connecting already clears it (keep that tested).
- **Uninstall.** The installer asks whether to remove settings, profiles and logs (default: keep), and always removes the saved password. Sessions in Documents are never removed. `build/test-upgrade.ps1` checks both answers silently (`/REMOVEUSERDATA` or a similar switch).
- **Tests:** settings round trip and Forget; no password in logs, sessions or recordings; the guide's privacy page is embedded and linked; the installer's uninstall paths (`test-upgrade.ps1`).
- Close #14 with `Fixes #14`.

### S31 — Small fixes from the review (Q10, Q17)
- **Q10: forecast dash-dot (Fred).** The forecast layer (`GraphTheme.Forecast`, the renderer's 6–4 dash) becomes **dash-dot** at its current weight, so it is never mistaken for a category's dashed (12–7) or dotted style.
  - Do the same in the PNG and PDF exports and the plot, the legend and the guide's pictures (`build/guide-shots.ps1`).
  - Update `05-…md` (layers) and the guide's reading-the-graph page.
  - Verify snapshot tests change once. Add a test that no category line style the profile can name renders the same pattern as the forecast.
- **Q17: a sim name equal to a WTT title counts as unset (Fred).** A profile made from one timetable keeps that timetable's title as its sim name, so another timetable of the same sim shows "Old WTT – New WTT".
  - In `SimTitle` (or where the sim name is chosen), treat a profile sim name equal (ignoring case and spaces) to **any** WTT title recorded for that sim as unset, and fall back to the gateway's area ID. Keep the titles of timetables the profile was made or refreshed from in the profile (a small `ProfileJson` addition, round-tripped), and include the loaded WTT's title too.
  - A name the user typed in the profile editor is always kept, even if it matches a title. Mark it hand-set, as headways are.
- **Tests:** the formatter with a stale title, a typed name, and no name; `ProfileJson` round trip; the renderer's dash-dot.

### S32 — WTT rules in the forecast (Q5, Q7)
- **Q5 first:** check that rule `Time` is in seconds. Find a recording where a ruled train enters (Waterloo 331, "X appears after Y leaves") and compare the entry with Y's event. Write the result in `04-…md` and `03-…md` §6.
- **Apply the rules** (`TimetableRule`, `04-…md`) in `Forecaster` (`06-…md` §2):
  - **Appearance rules** (0, 1, 2, 11–13: X appears after Y enters/leaves/arrives/joins/divides/forms): an entering train's forecast entry is no earlier than Y's forecast or actual event plus `Time`.
  - **Departure rules** (4–9: X departs after Y …), at the rule's `Location` or X's first stop: X's forecast departure is held the same way.
  - **Exclusion rules** (3 X not if Y, 10 mutually exclusive, 14 alternatives) are not forecast. They show on the train detail as a note.
  - Unresolved rules (a name matching no train, or several) are skipped and logged once, as the import already notes them.
  - A chain of rules is followed in order. A cycle is broken and logged, never looped.
- **Conflicts and suggestions.** A rule-held train is not a conflict in itself. The train detail and hover say "held by rule: after 2B44 leaves (+60 s)". The suggester and optimiser treat a rule as a constraint (a hold cannot release X before the rule allows).
- **Option:** Options ▸ Forecast ▸ "Apply timetable rules" (on by default), so a user can compare.
- **Tests:** each rule kind on synthetic timetables; Waterloo 331 against its recording (entry no longer forecast early); a cycle; an unresolved rule; the optimiser respects a rule. Update `06-…md` §2.

### S33 — Holding trains at signals (Q12)
- **Problem.** Suggestions and the optimiser only hold trains at timing points the WTT times, so a train already on its way to a junction gets no suggestion. SimSig can hold a train at any signal.
- **Find out first** (write the results in `progress.md` and `06-…md` §4):
  - Where can a train be held, as a place on the corridor? Signals are not in the WTT, but TD berth steps and `SG_MSG` aspects are in the recordings. Can a berth be placed between two timing points (by the time a train steps into it, relative to its timing points, across many trains)? Store the result per sim in the profile (`signals`: berth, signal ID if known, between which TIPLOCs, fraction along).
  - Which messages say a train is standing at a signal (berth plus a red aspect ahead), so the forecaster can tell "held" from "late"?
- **Build** (if the investigation gives usable places):
  - `PlanCommand` gains **HoldAtSignal** (train, berth or signal, until time), shown on the graph as a short flat step between the timing points at the signal's place;
  - the forecaster runs it as a stop of that length there;
  - the suggester proposes it for a train already past its last timing point before a conflict (the cost: the held time, weighted like delay), and the optimiser can use it as one more hold place;
  - **Assist (S19):** a hold at a signal is a reminder only (SimSig has no gateway command to hold at a signal). The regulation list shows "hold 1A23 at WM123 until 09:12".
- **If places cannot be found reliably**, stop after the investigation, record why, and leave the build to a later session or a hand-entered list of signals in the profile editor.
- **Tests:** berth placement on Waterloo's recordings (portable); the command's round trip, forecast and undo; a suggestion for an on-the-way train; the optimiser using a signal hold.

## Phase 5 — Optional extensions
- **S19** Opt-in assist: route-request / TD-interpose for the selected regulation step, with explicit confirmation and per-session enable.
  - *Built in S19:* Fred chose to ask for the route and remember it per move in the profile, to interpose the picked train's description into a chosen berth, a Session menu switch that is off at each new session, and live connections only (`replay-server` counts; file replays never send). The warning names multiplayer, as S29 asked. See `05-…md` §14.
- **S20** Optimiser (`IReplanner` using OR-Tools CP-SAT) for a rolling horizon.
  - *Built in S20:* Fred chose OR-Tools CP-SAT in its own project (`Stringline.Optimiser`, about 20 MB more in the download), holds and platform changes, Plan ▸ Re-plan over the whole horizon shown in the suggestions panel, and 60 min and 5 s in Options. The rules (one conflict at a time) are the other `IReplanner`. `stringline replan` tries either on a recording. See `06-…md` §5 and `05-…md` §15.
- **S21** Split-view multiple corridors / multi-board layout; vertical TCD print plotting.
  - *Built in S21:* Fred chose split panes (one above the other or side by side) **and** board windows, all locked in time, full editing on every board, and a multi-page plot (File ▸ Export ▸ Plot…; `render-png --plot`), time down on A3 portrait by default. See `05-…md` §16.

## Dependencies
```
S1 → S2 → S3 ─┬→ S5 → S7 → S8 → S9
       S4 ────┤       ↑
       S6 ────┘───────┘
S8 → S10 → S11 → S12 → S13 → S14 → S14a → S15 → S16 → S17 → S18 → S22 → S23 → S24 → S25 → S26 → S27 → (S19–S21)
                                                                    S18 → S28 (after S26 for grips, S29 for multiplayer)
                                                                    👤 recording → S29
S21 → S30, S31 → S32 → S33
```
S4 and S6 can run in parallel with S2–S3 (separate worktrees/sessions).

## Working agreement for every session
1. Start: read `CLAUDE.md`, the docs named in the session, and the previous session's notes in `docs/progress.md`.
2. Write tests first for core logic; keep WPF code thin.
3. Finish: `dotnet build -warnaserror` and `dotnet test` green; update `docs/progress.md` (what was done, decisions, what's next, known issues); update design docs if anything changed; commit.
4. If a design question blocks progress, write it under "Questions for Fred" in `docs/progress.md` and pick the conservative default rather than stopping.
