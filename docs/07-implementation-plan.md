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
Fred went through the open questions on 2026-10-20 (answers in `progress.md`, "Open-question review"). Four sessions came from them. Order: S30 → S31, then Phase 4c (S34–S39), then S32 → S33 (the issue review of 2026-10-21 put Phase 4c first, as #19 asked). S30 and S31 are small and can run in either order. Each session still ends with a green build, tests, `progress.md` and a commit.

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
- *Built in S30:* Fred chose Forget in both the Connect dialog and a new Options ▸ Connection tab, and the uninstaller removes the unpacked files in `%TEMP%\.net\Stringline` too when asked. `test-upgrade.ps1` checks both uninstall answers (copying the user's real data aside first); it was not run locally because Fred's all-users 0.3.0 install was there, so the Release workflow is its first run.

### S31 — Small fixes from the review (Q10, Q17)
- **Q10: forecast dash-dot (Fred).** The forecast layer (`GraphTheme.Forecast`, the renderer's 6–4 dash) becomes **dash-dot** at its current weight, so it is never mistaken for a category's dashed (12–7) or dotted style.
  - Do the same in the PNG and PDF exports and the plot, the legend and the guide's pictures (`build/guide-shots.ps1`).
  - Update `05-…md` (layers) and the guide's reading-the-graph page.
  - Verify snapshot tests change once. Add a test that no category line style the profile can name renders the same pattern as the forecast.
- **Q17: a sim name equal to a WTT title counts as unset (Fred).** A profile made from one timetable keeps that timetable's title as its sim name, so another timetable of the same sim shows "Old WTT – New WTT".
  - In `SimTitle` (or where the sim name is chosen), treat a profile sim name equal (ignoring case and spaces) to **any** WTT title recorded for that sim as unset, and fall back to the gateway's area ID. Keep the titles of timetables the profile was made or refreshed from in the profile (a small `ProfileJson` addition, round-tripped), and include the loaded WTT's title too.
  - A name the user typed in the profile editor is always kept, even if it matches a title. Mark it hand-set, as headways are.
- **Tests:** the formatter with a stale title, a typed name, and no name; `ProfileJson` round trip; the renderer's dash-dot.
- *Built in S31:* the forecast is dash-dot (7–4–dot–4). Fred chose to settle names from before S31 by reading the titles of the sim's other `.wtt` files in the loaded WTT's folder: a match counts as a title, any other name is kept as typed.

### S32 — WTT rules in the forecast (Q5, Q7)
- *Runs after Phase 4c* (S34–S39), as #19 asked. A rule-held train's forecast uses S36–S38's progress between points like any other.
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
- **Its investigation moved to Phase 4c** (issue review, 2026-10-21). S37 places signals, track circuits and berths on the corridor and stores them in the profile. S38 tells "held at a signal" from "late". This session is now the build only, on top of those.
- **Build:**
  - `PlanCommand` gains **HoldAtSignal** (train, signal, until time), shown on the graph as a short flat step at the signal's placed position between its timing points;
  - the forecaster runs it as a stop of that length there;
  - the suggester proposes it for a train already past its last timing point before a conflict (the cost: the held time, weighted like delay), and the optimiser can use it as one more hold place. Only signals placed with good confidence (S37) are offered;
  - **Assist (S19):** a hold at a signal is a reminder only (SimSig has no gateway command to hold at a signal). The regulation list shows "hold 1A23 at WM123 until 09:12".
- **If S37 found placement unreliable** for a sim, its signals are not offered, and the session builds only for the sims where it works (or a hand-entered list in the profile editor's Signals tab).
- **Tests:** the command's round trip, forecast and undo; a suggestion for an on-the-way train; the optimiser using a signal hold; a signal with low confidence is not offered.

## Phase 4c — From GitHub issues #16–#19 (issue review, 2026-10-21)
Fred raised #16–#19 on 2026-10-20 after testing on more sims (#1 and #5 stay where they were: SignPath later, and S29). In the review, Fred chose:
- **#19:** signal detail is **learned from play** (recordings and every live or replayed session), not built from sim files or by AI from schematics. It is stored in the sim's profile, which can be exported and imported so users can share it.
- **#17:** do all four: use the timing locations SimSig reports that a train's WTT leaves out; draw and forecast through signal passes; extend each running train's line to "now"; and optionally show extra points on the corridor axis.
- **Conflicts:** **block headways**. Section headways come from the learned signal blocks, and catching-up is checked at signal positions. Full block occupancy is not planned yet.
- **Track circuits:** placed along with the signals.
- **Learning:** on by default, saved at the session's end, and reviewable in the profile editor (confidence, samples, reset).
- **#16:** trains SimSig will not run are marked **"Not running" automatically**. This can be undone, and it is not a plan edit.
- **#18:** a user-focused README, with the developer content in `docs/developer.md`.
- **Order:** S34 and S35 (small, either order, can share a sitting) → S36 → S37 → S38 → S39, then S32 → S33. S36 needs nothing new. S38 and S39 need S37.

| Issue | Title | Session |
|---|---|---|
| #16 | Starting seeded trains | S34 |
| #18 | Re-write Readme.md | S35 |
| #17 | Locations not on a train's WTT | S36 (today's data), S38 (signals) |
| #19 | Granularity of signalling and network information | S37 (placement), S38 (graph and forecast), S39 (headways and conflicts) |

**Why learning works.** The gateway already sends every signal a train passes as a `train_location` (about two thirds of all of them, with `aspPass`/`aspAppr`; `03-…md` §2). SimSig also reports timing locations a train's own schedule leaves out (`03-…md` §6.2). Today the forecaster drops both, and the graph drops the signal passes (`Forecaster.StartRunning`, `GraphSceneBuilder.AddActual`). The derived corridor axis is measured in run time (`04-…md` §4.5), so a signal's place can be measured the same way: where its pass time falls between the train's times at the timing locations either side. Track circuit `SG_MSG`s (`clear` true/false) and TD berth steps come only with the TD topic (`--td`); signal passes do not need it.

👤 **Before S37:** record 1–2 h, with `--td`, on the sim where #17 showed (an older sim with few timing points), and on one busy sim. Run at 1×–4×, because faster runs give coarser times. Put them in `local-fixtures/` (payware) or `tests/fixtures/` (free). The Waterloo fixtures are short: `waterloo-1x` has 614 `train_location`s, and `waterloo-4x` has no TD topic.

### S34 — Seed groups and trains that will not run 🪟 (core part portable) (#16)
- **Problem.** When a sim starts at a time other than 00:00, Stringline still forecasts trains SimSig will never run:
  - unseeded trains that should already have been part-way through their run at the start;
  - seeded trains of another seed group.
  - The user must cancel each by hand. Today the forecaster skips only trains that were over before the session started (`Forecaster.Run`). The import ignores `SeedGroups` and each train's `SeedGroup` (`04-…md` §1).
- **Import** (`WttReader`, `04-…md`):
  - `SeedGroups/SeedGroup` → `SeedGroup(Id, StartTime, Title)` on the timetable (Penrith: 04:00 and 14:00);
  - each train's `SeedGroup` and `Started`;
  - `wtt-dump` prints the groups, and `--train` prints a train's group.
- **Which trains run** (`Stringline.Planning`, a pure function of the WTT, the session's start clock and live sightings):
  - **The start group** is the seed group whose `StartTime` equals the session start, or the latest before it, else none. The session start is `LiveState.SessionStart`: the sim's first clock, not a replay's `--start` seek. Check how a replay seek sets it.
  - **Will not run:**
    - a seeded train of another group;
    - an unseeded train whose first booked time (entry, or its first timed point) is before the session start, *unless* a train that will run forms it (`FormedFrom`).
    - **Find out first** on Penrith's 14:00 recording, and record it in `04-…md`: does SimSig start an unseeded train booked a minute or so before the start? Set the tolerance to match.
  - **Not seen:** a seeded train of the start group that has not appeared by `PresentAtStartSeconds` after the start is flagged "not seen" (it may have been removed). It is not hidden.
  - A train that is matched live always runs, whatever the rules said.
- **Shown as "Not running":**
  - a new `TrainForecastStatus.NotRunning` with a reason ("not seeded", "seed group 04:00");
  - kept out of the graph's forecast, conflicts, suggestions, the optimiser and the regulation list;
  - the booked line is drawn faintly (or hidden, by the WTT layer's setting);
  - the train list has a "Not running" group;
  - the delay report counts them apart.
  - It is not a plan command: it is worked out again from the session start, so resume and review agree.
  - **Run anyway** (right-click, or the train detail) *is* a plan command (`ExpectToRun`), undoable and saved, for a sim where the rule is wrong.
- **Tests:** seed group import (Penrith, local); the rule on synthetic timetables (each case above, the forming exception, a timetable with no groups, a start at 00:00 changes nothing); Penrith's 14:00 recording (no 04:00-group train forecast, and nothing that ran is marked); `ExpectToRun` round trip and undo; an App test of the train list group.
- Close #16 with `Fixes #16`.
- *Built in S34:* Penrith's 14:00 recording showed no tolerance (W552, entering at 13:58:30, did not run) and that a train formed from a not-running one does not run either (186L). Fred chose to carry "Not running" down formation chains (a train runs if any train forming it runs), and to apply the "booked before the start" rule only at a fresh start (within a minute of the timetable's or a seed group's start), so a late connection or saved game leaves out only other groups' seeded trains. Seeded trains with no group belong to the timetable's start. See `04-…md` §1 and `06-…md` §2.

### S35 — README for users (#18)
- **README.md**, written for someone who plays SimSig and has never seen Stringline (Australian English, plain words):
  - what it is, with one picture of the graph (from `build/guide-shots.ps1`, free-sim data);
  - download and install, and the SmartScreen note (#1);
  - turning on the Interface Gateway in SimSig's Loader;
  - connecting and the first graph, in a few steps;
  - links to the user guide (wiki) for everything else;
  - privacy, licence (GPL-3.0-or-later), and where to report a problem.
  - Keep it short: about one screen before the links.
- **`docs/developer.md`** gets everything for developers:
  - build and test (Windows, Linux `.slnf`), the stack and project layout;
  - the `Stringline.Tools` commands (from `CLAUDE.md`'s list, not copied word for word: link to `CLAUDE.md` for the full set or keep one list);
  - the fixtures policy (free sims in `tests/fixtures`, payware in `local-fixtures`);
  - benchmarks, publishing and releases, the wiki workflow, the design docs (`docs/00…07`), and the licence rule for dependencies.
  - The README links to it under "Contributing". `CONTRIBUTING.md`, if added, is a short pointer to it.
- **Check:** the links that tests or workflows rely on still work: `PrivacyTests` checks the README links the policy, the wiki script, and the guide link check. Extend the guide's link check to `README.md` and `docs/developer.md`.
- Close #18 with `Fixes #18`.
- *Built in S35:* the README is for players (picture, install, gateway, first graph, wiki links, privacy, licence); developer content is in `docs/developer.md`, with `CONTRIBUTING.md` pointing there. The app's command-line options moved into the guide (sessions page). `GuideLinkTests` checks the README, `CONTRIBUTING.md` and `docs/developer.md` links.

### S36 — Progress between booked points, from today's data (#17, part 1)
- **Find out first:** on Waterloo's recordings and the corpus, how often trains report a timing location their WTT leaves out, and how long trains go between reports on corridor locations. A long gap is the problem #17 describes. Record a baseline (median and 95th percentile gap per sim) in `progress.md`, so S38 can show the gain.
- **Unbooked timing locations in the forecast** (`Forecaster.StartRunning` skips them now):
  - A reported location between booked points k and k+1 becomes a progress anchor, provided it lies on the train's corridor route or in the `LocationGraph` between them.
  - The rest of the run to k+1 is forecast from that location, using `MinRunTable` from it to k+1 (and the train's lateness so far). Without a minimum from it, the run is shared in proportion, as `CorridorMeasurement` does.
  - Delay is not measured there, because it has no booked time. The hover says "passed EARLSFD 09:12 (not booked)".
- **The actual line** already draws reported corridor locations (`AddActual`). Check that unbooked ones keep their place, and that delay colouring carries the latest known delay across them (`DelayColours`).
- **Live head:**
  - Each running train's actual line goes on from its last report to "now", along its forecast, as a short segment in the actual style;
  - it ends in a small marker that moves with the clock (the scene builder adds it; the renderer draws it each frame from the clock, without a rebuild);
  - it is held at the last report's place once the forecast says the train should be past the next point but no report has come ("overdue"), so it never runs ahead of reality;
  - it is off in review and exports unless the layer is on.
  - **Live head** is a layer toggle, on by default, next to Forecast.
- **Corridor extra points (first part):** the profile can hold corridor locations shown as faint, narrow rows (`CorridorLocation.Minor`: no label unless zoomed, no grid line). They are for timing locations the corridor generator skipped, and in S38 for placed signals. The profile editor can add and remove them.
- **Tests:** a train reporting an unbooked location between two booked points (the forecast moves to it); no minimum from it (proportional); the live head's position against the clock and its overdue hold (Graph.Tests, portable); minor rows in the axis and editor; the gap baseline on Waterloo.
- *Built in S36:* `stringline gaps` measures the baseline (`progress.md`). Waterloo's trains report a timing location their WTT leaves out about 30 % of the time, which nearly halves the median gap; Kings Cross (where Fred saw #17) and Penrith report none, so there only the live head helps until S38's signals. Fred chose: a minor row is a full corridor location (its sections split and are measured, keeping the split section's tracks); the live head stops 95 % of the way to the next point when overdue; it is in the View menu only (the toolbar has no room at 1280 px), off while reviewing and in `render-png` unless `--live-head`.

### S37 — Learning signal, track circuit and berth places (#19, part 1)
Takes over S33's "find out first".
- **Model** (`Stringline.Network`, portable): `SignallingPlaces` in the profile:
  - **signals:** ID, direction (Up/Down), the timing locations either side (from, to), fraction along (0–1, in run time), and the line if the trains passing it share one;
  - **track circuits:** ID, from, to, start and end fractions, the signals bounding them where known;
  - **berths:** ID, from, to, fraction, and the signal it belongs to where the ID matches (e.g. berth `0457` ↔ `S457`);
  - for each: sample count, spread, last learned (sim day and session), and a hand-set mark that learning never overwrites.
  - `ProfileJson` round trips it (version stays 2 if older Stringlines can ignore it, as S31 did).
- **Learner** (`SignalLearner`, pure; fed a WTT, a profile and an event stream):
  - For each matched train between two reported timing locations a and b (booked or not), with times t_a and t_b and no stop between (no arrival or departure, and t_b − t_a within, say, 1.5 × the minimum), a signal passed at t gives a sample (t − t_a) / (t_b − t_a) for (signal, a→b). The place is the median, and the spread is the interquartile range. Samples from a train stopped by a signal (`aspAppr` red before the pass) are left out.
  - **Track circuits:** an `SG_MSG track … clear:"False"` is put to the train most recently at the signal or place just before it in the right direction, if exactly one train is near. The track circuit's start is that time's fraction, and its end is when it clears. A track circuit occupied with no train near is skipped (that is a failure: see `03-…md` §6). Find out how reliable this is, and record it.
  - **Berths:** a CA step into a berth is placed the same way, by the train whose description it is.
  - A place is **used** only with at least 3 samples and a spread under about 10% of the section; otherwise it is kept as "learning". A section seen in both directions, with signals one way only, is fine: signals are directional.
- **Learning in the app:** on by default (Options ▸ Profile ▸ "Learn signal places", with the sample threshold).
  - Every live or replayed session feeds the learner off the planning thread; it must not slow the engine.
  - The result is merged into the profile when the session ends (or on Save profile), and logged as one line.
  - Review mode does not learn twice from a session it has already learned from: keep the session IDs it has used.
- **Profile editor ▸ Signals tab:** per corridor section, its signals, track circuits and berths in order, with fraction, samples, spread and state (used / learning / hand-set). The user can edit a fraction (marks it hand-set), delete one, add one by hand, or Reset learned places for a section or the whole sim.
- **Sharing:** Tools ▸ Export profile… and Import profile… (a `.json`; on import, the user picks merge, which keeps hand-set and pools samples, or replace). The guide gets a page on sharing a profile for a sim.
- **CLI:** `stringline learn --wtt t.wtt --log x.sslog [--profile p.json] [--out p.json] [--report]`: learns from one or more recordings, and prints per section the places, samples and spread, and what is still learning.
- **Gate:** on Fred's recordings and Waterloo, report the share of signals passed by 3 or more trains that reach "used", and how far the places move between two recordings of the same sim. If places are not stable (they move more than the spread), stop, write why in `progress.md` and `06-…md` §4, and leave S38–S39 and S33 for a rethink.
- **Tests:** the learner on a synthetic stream (a known fraction comes back; a stopped train is left out; too few samples stays learning; a hand-set place is not moved); track circuit attribution with two trains near (skipped); `ProfileJson` round trip; merge on import; Waterloo learning (portable, from `tests/fixtures`); an App test of learning a replay and seeing it in the Signals tab.
- *Built in S37:* Fred recorded Waterloo for 4 h (04:30–08:30, 8–10×, with TD; `tests/fixtures/waterloo-4h.sslog`), and chose to run the gate on Waterloo only until a Kings Cross `--td` recording exists. The recordings showed a signal's share of the run differs between trains running through and trains starting or stopping at either end, so Fred chose to learn a place per **kind of run** (`RunKind`). Starting and stopping runs count (the spread decides), every train counts, matched or not, and a run counts if no slower than 1.5 × the fastest of its kind seen (the WTT's minimums left out half the runs). **The gate passed**: 198 of 202 signals passed by 3 or more trains are used, and between the 4 h and 4× recordings the 151 used in both move at most 0.018; 83 % of track circuit occupations are put to one train (`03-…md` §6).

### S38 — Signals and track circuits in the graph and forecast (#17 part 2, #19)
- **Actual line through signals.** A pass at a placed (used) signal adds a vertex at its place between the two timing locations, so the line bends where the train was checked. Track circuit occupation moves the live head (S36) to the track circuit's start.
- **Forecast re-anchors** at each placed signal, track circuit and berth step: the rest of the run to the next timing point is the remaining fraction of the section's run (the minimum, scaled by the train's running so far in that section). It is cheap enough to do on every pass (GR-9 budget: check with the benchmarks).
- **Held, not late:** a train whose last pass had a red approach aspect (`aspAppr` 0), or whose track circuit stays occupied with the next one clear, while a signal ahead shows red, is **standing at a signal**. The live head stops there; the hover says "standing at S457 since 09:12"; the forecast assumes it moves only after the conflicting train clears, rather than now. Record in `03-…md` §6 which messages show it, and how soon.
- **Extra points on the axis:** placed signals can be shown as minor rows (S36) or as small ticks on the axis (View ▸ Signals: off / ticks / rows), with the signal ID on hover.
- **Hover:** the actual line at a signal says "passed S457 at 09:12:30 (green / single yellow…)", with the aspect names as SimSig uses them.
- **Tests:** a path with signal vertices; the forecast re-anchoring at a signal and a track circuit; the held detection on Waterloo's failure recording (the T1620 failure must not count as a train held); render snapshots with ticks and rows; the benchmark still under 33 ms a frame; the S36 gap baseline again (it should fall).
- *Built in S38:* Fred chose (question 34) the place for the train's kind of run, else any used place of that signal; saving signal and track circuit `SG_MSG`s in session files (so resume and review show held trains), and **ticks** as View ▸ Signals' default. The recordings showed `aspAppr` 0 is mostly a brief check (median 2 s lost), and the nearest placed signal ahead is often a parallel line's, so the learner now records the signal passed next after each one (`SignallingPlace.Next`) and a train is **standing** when the signal ahead on its line is red and it is overdue there. On Waterloo 4 h: next-point forecast error 46.9 s against 55.6 s; 54 of 57 told as standing then passed that signal from red; the gaps between placed reports fall to a median of 24 s (95th percentile 60 s) from 88/150 s. Line-side changes alone publish at most once a second. `learn` reads saved live sessions; Kings Cross needs more play (2 signals used from one session).

### S39 — Block headways and conflicts at signals (#19, part 3)
- **Section headways from signal blocks.** Where a section has used signal places, its headway is the time for a leader to clear the blocks a follower needs clear. That is the next *n* blocks plus the overlap: *n* = 2 for four-aspect signalling, 1 for three-aspect. The aspect count is learned from the highest `aspPass` seen on the line, or set in the profile. It is measured from observed running (the learner's per-block times), at the section's minimum run speed.
  - **Order** (extends S27, `06-…md` §3): hand-set > **blocks (measured; may be above or below the default)** > WTT (only tighten) > profile default > global default.
  - The source shows as "blocks" in the profile editor, `stringline corridors --headways` and the conflict tooltip.
- **Catching up inside a section.** `SectionOccupancy` checks headway only at timing locations. A fast train catching a slow one between them is missed. Check the same-direction separation at each placed signal (forecast times by fraction) as well. A headway conflict there names the signal: "1A23 catches 2B44 at S457 (1 min 10 s)".
- **Suggester and optimiser** use the same headways (through `Section`), so they need no change beyond checking that they still pass. The optimiser's model may need the per-signal check as extra constraints, if catching-up conflicts stay after a re-plan. Measure first.
- **Tests:**
  - block headways on synthetic sections (four- and three-aspect);
  - a catching-up conflict found at a signal and not before;
  - Waterloo's conflicts before and after on the 4× and failure recordings: false headway conflicts should fall, and none of the real ones should be lost (as S27);
  - Llangollen's single line unchanged;
  - `ProfileJson` round trip of the aspect count.
  - Update `06-…md` §3.

## Phase 4d — Timetable editor (GitHub issue #20)
Fred raised #20 on 2026-10-20: SimSig's editor and outside tools handle timetable editing poorly. Stringline can be a better editor, with SimSig still validating and running the result, because the gateway cannot send a timetable to SimSig. The design is in `08-timetable-editor.md`. In the design review (2026-10-21) Fred chose:
- a grid plus a live graph of the draft;
- **lossless** saving: everything the editor does not model is written back untouched;
- every part of the timetable editable;
- a draft made from the working plan;
- all four bulk tools (pattern, auto-fill, multi-train edits, spreadsheet round trip);
- saves as new versions, never overwriting without asking;
- editing on a separate draft while a session is live.

**Order:** the phase runs after Phase 4c, S32 and S33. S40 → S41 → S42 → S43 → S44 → S45 → S46 → S47 → S48. S41 can run alongside S40 in a separate worktree. S44 needs S43's workspace, and S46's UI needs S44's preview board. Each session still ends with a green build, tests, `progress.md` and a commit. S48 closes #20 with `Fixes #20`.

| Part of #20 | Session |
|---|---|
| Lossless open, edit and save | S40 |
| Knowing what the sim allows | S41 |
| Checks before SimSig (the Analyser's, offline) | S42 |
| Editing trains and trips | S43 |
| The draft on the graph | S44 |
| Activities, rules, seed groups, categories, header and the rest | S45 |
| Bulk tools | S46 |
| Spreadsheet round trip, plan → draft | S47 |
| Testing in SimSig, guide | S48 |

**Research notes** (SimSig wiki, *Writing Timetables*, and the forum):
- The XML format page lists every element. The zip has `SavedTimetable.xml` and `TimetableHeader.xml`, an extract of the `[TH]` fields.
- F4 ▸ Tools 1's Timetable Analyser has about 30 named checks. Its CSV import is deprecated, but XML stays.
- Timing notation: `12:30`, `12/34` pass, `d` set down, `t` thru line, `w` dwell wait, `r` request, and `H` for a half minute.
- Seeding rules: no arrival time at the seed start location, and one train per signal per seed group.
- Naming and versions: `Sim YYYY-MM-DD [tags].wtt`, with the version never in the name, and `VMajor.VMinor.VBuild` always rising.
- Spreadsheet writers' loop: import → "no path between timing points" → add timing points → "ambiguous line/path" → set Line.
- SimSig's `.sim` files are encrypted, so path validity can only be learned from the sim's other timetables and from play (`08-…md` §1, §4).

### S40 — Lossless timetable document and writer (portable)
- **`WttDocument`** (`Stringline.Timetable.Editing`, `08-…md` §3):
  - it holds the `XDocument` and the zip's other entries;
  - typed views (`HeaderNode`, `CategoryNode`, `TrainNode`, `TripNode`, `ActivityNode`, `RuleNode`, `SeedGroupNode`) and generic `ListNode`s read and write through to their elements;
  - booleans are written in the file's own style, a value back at its default is removed, and new elements go in SimSig's order (from the XML format page and the corpus);
  - `ToWorkingTimetable()` uses `WttReader`'s import on the in-memory XML.
- **`TimetableEdit`:** set a field, insert, remove or move a node, and composite, each with apply and undo. A renumber edit updates every activity and rule that names the train.
- **`WttWriter`:**
  - writes `SavedTimetable.xml` plus a regenerated `TimetableHeader.xml`, as UTF-8 with no BOM and no declaration;
  - saves atomically;
  - sets the version (`VBuild` + 1, or a release bump);
  - copies a backup to `%LOCALAPPDATA%\Stringline\timetable-backups` before replacing a file (`08-…md` §6).
- **SimSig time notation** (`SimTimeNotation`, in Core if the grid and CSV both need it):
  - parse and format `12:30`, `12/34`, `12d34`, `12t34`, `12w34`, `12r34`, `1234`, `1234H`, `13:45H`, `24:01` and `+3`;
  - day rollover;
  - half minutes as `SimTime` seconds.
- **CLI:** `stringline wtt-save in.wtt out.wtt [--bump build|minor|major] [--shift-train 1A23 +5]`. It is a round-trip and smoke tool, not the editor.
- **Tests:**
  - **the round trip on the whole corpus:** the XML trees are equal element by element, and the `WttReader` results are equal;
  - each node's read and write, in both boolean styles;
  - an edit's undo restoring the exact tree;
  - a renumber keeping the links;
  - the header extract;
  - the notation's parse and format (every code, `H`, past midnight).
- 👤 **Gate:** Fred loads a round-tripped Waterloo and one payware timetable in SimSig and runs the Analyser. The output must be the same as the original's. If SimSig reads them differently, find out why (element order, a default) before S43.

### S41 — Sim knowledge (portable)
- **New project `Stringline.Editing`** (`net10.0`; it references Timetable, Network and Planning). Its tests go in `tests/Stringline.Editing.Tests` and in `Stringline.Portable.slnf`, and the architecture tests learn the new layer.
- **`SimKnowledge`** per area ID (`08-…md` §4), built from:
  - every `.wtt` for the sim in the timetable folders (`WttCatalogue.FindForArea`);
  - recordings and sessions (reported TIPLOCs and entry signals);
  - the profile (S37's signals, track circuits and berths; corridors).
- **What it holds:**
  - locations and their roles (entry, seed, station, key, reversing);
  - the platform, line and path codes seen per location, with counts;
  - booked links A → B, with line and path, counts and direction, plus skip chains from `LocationGraph`;
  - least and usual run times per link, by speed band and freight flag, net of allowances (reusing `CorridorMeasurement`'s rules);
  - usual dwells.
- **Cache:** `{area}.simdata.json` beside the profile, keyed by the sources' paths and timestamps and rebuilt when one changes. The draft being edited is always left out.
- **CLI:** `stringline simdata --area waterloo [--folder f] [--location WIMB] [--link WLOO VAUX]`.
- **Tests:**
  - Waterloo and Llangollen from `tests/fixtures` (codes, links and run times come back);
  - the cache's invalidation;
  - the draft left out;
  - the corpus: every sim builds in a few seconds, and the time per sim is recorded in `progress.md`.

### S42 — Checks before SimSig (portable)
- **`TimetableValidator`** (`08-…md` §5) gives `ValidationIssue(Severity, Code, Train?, PointIndex?, Message, Hint, QuickFix?)`. It covers:
  - the Analyser's offline checks, in SimSig's wording;
  - sim-knowledge checks: unknown codes and TIPLOCs; "no known path" with an insert-chain fix; "ambiguous line" with a set-line fix; a run shorter than any seen; a stop at a non-station; entry and seed points; unsafe activity locations;
  - rule checks: unresolved names, cycles, and a location the train does not call at.
- Quick fixes are `TimetableEdit`s, so S43 applies and undoes them like any edit.
- **Incremental:** after an edit, only the trains it touches and the trains linked to them are checked again. A full check runs on open and save.
- **CLI:** `stringline wtt-check t.wtt [--knowledge-folder f] [--severity warning] [--train 1A23]`.
- **Corpus baseline:** count the issues by code over released timetables and record it in `progress.md`. A check that fires on many released timetables is a false alarm: fix it or demote it to Info. Name the ones left in "Questions for Fred".
- **Tests:**
  - each check on a small synthetic timetable (the firing case and its near miss);
  - each quick fix applied and checked again;
  - incremental and full checks agree;
  - Waterloo and Llangollen have no Errors.

### S43 — Editor workspace, trains and trip grid 🪟 (view-model portable)
- **`TimetableEditor`** (`Stringline.Editing`): the single writer for one draft. It applies edits, keeps undo and redo, and publishes `DraftSnapshot`s (document, `WorkingTimetable`, issues). Drafts autosave to `Documents\Stringline\drafts` (`.wtt` plus `.draft.json` holding the source path and undo history).
- **Timetable menu** (`08-…md` §7): New draft (from the loaded timetable, from a file, or blank for the sim; "from the working plan" comes in S47), Open draft, Save, Save As, Release version, Close draft and Check timetable. Add it to S25's menu test (unique access keys, every command bound).
- **Editor workspace:** a board-style window holding:
  - **Trains:** a list sorted and filtered by headcode, UID, entry, category, seed group and issues, with multi-select;
  - **Train details**, including `$template` with a preview;
  - **Trip grid:**
    - the columns and keys in `08-…md` §7;
    - completion and code drop-downs from `SimKnowledge`;
    - SimSig notation in and out;
    - auto-inserted rows greyed, and issue marks with hover hints;
  - **Issues panel:** click to go to the train and row, and apply a quick fix (one, or all of a kind);
  - **Header tab:** name, description, start and finish, version, template.
- **Saving** follows `08-…md` §6:
  - the first save of a SimSig timetable asks whether to replace it, defaulting to a new file named by the convention;
  - Errors are listed in the save dialog;
  - unsaved changes are asked about on close and on exit.
- **Live:** opening a draft from the loaded timetable copies it, and the session's WTT and plan are never changed (a test checks this).
- **Tests:**
  - App tests driving the view-model on Waterloo: open a draft, change a time by typing `1234H`, insert a point, fill down, undo, save and reopen, and get the same document;
  - the replace-or-new prompt;
  - autosave and restore after a simulated crash;
  - the session's WTT untouched while a draft is edited.

### S44 — The draft on the graph 🪟 (scene portable)
- **Draft board:** a graph board (S21) whose source is a `DraftSnapshot`, with the profile's corridors.
  - It shows the booked layer only.
  - **Booked conflicts** come from the forecaster with no live data (S32's rules included) and S14's detector, so the draft's headway, platform and single-line conflicts show as they will in play.
  - A train off every corridor is listed in the board's status line.
- **Shared selection:** a train selected in the grid selects it on the board, and the other way round. A row selects its point.
- **Grips edit the draft:** abstract S26's drag target, so `PlanDrag` produces plan commands on session boards and `TimetableEdit`s on draft boards.
  - Retime from here, dwell and shift-path each give one composite edit, rounded to half minutes.
  - A drag clamps to the learned minimum run time with a warning, and Shift lets it past.
- **Ghosts** (View ▸ Draft ghosts): the original timetable's path for changed trains, and a recording's actuals (pick a `.sslog`; no session needed).
- **Exports:** PNG, PDF and the plot work on a draft board, titled "Draft – *name* *version*".
- **Tests:**
  - scene tests: the booked layer only, a conflict between two draft trains, ghosts (Graph.Tests);
  - the drag target giving edits on a draft and commands on a session;
  - App tests: drag a draft path, see the grid's times change, undo;
  - the benchmark with a 500-train draft still under 33 ms a frame.

### S45 — Activities, rules, seed groups, categories and the rest 🪟 (edits portable)
- **Activities**, per trip:
  - add, edit, delete and reorder;
  - the associated train chosen from a list at that location and time (by `$UID` where the file uses them);
  - crew change with arrival and duration;
  - a link to go to the associated train;
  - "Formed by" shown on the formed train.
  - Next must stay last: the edit refuses otherwise and says why.
- **Rules tab:**
  - a grid of kind, T, Y, Z (location) and X (minutes, `0H` style);
  - train pickers;
  - unresolved and cyclic rules marked (S42);
  - a filter for "rules about this train".
- **Seed groups tab:** list, new, edit and delete; "referenced by"; "unassigned"; one seed per signal per group, checked as it is typed. Seed points are offered from `SimKnowledge`, with S37's signals and their places named.
- **Categories (train types):**
  - all fields from the XML format page, including the speed class bits as tick boxes, electrification letters, power to weight, caution speed set and dwell times;
  - "used by n trains";
  - a refused delete while trains use the category, unless they are moved to another.
- **Decisions, Class of Service, Scenarios, Driving:** structured list editors over their documented fields. Anything else is shown read-only and preserved.
- **Tests:**
  - each editor's edits and undo, against the XML;
  - Next-last refused;
  - renaming a category updates its trains;
  - deleting a seed group moves its trains to the start group after asking;
  - a round trip of each part on a corpus file that has it (find one with Decisions and with Scenarios).

### S46 — Bulk tools and auto-fill (core portable, UI 🪟)
Each tool in `08-…md` §8 is one composite `TimetableEdit`, previewed as a list of changes and as ghosts on the draft board before it is applied.
- **Repeat as pattern:**
  - every N minutes from–to;
  - headcode and UID rules;
  - next workings paired at a terminus by the least turnaround that meets the dwell, with each pairing shown and editable in the preview.
- **Auto-fill timings:**
  - fill the points between given times from `SimKnowledge` run times for the train's speed band and freight flag, plus allowances and usual dwells;
  - report the shortfall when the given times cannot be met;
  - **insert timing points** (the booked chain between two points, the "no path" fix) and **set line** (the most used line).
- **Multi-train edits:** shift (all, or from a location), renumber, change category, platform, line or path at a location, set or clear allowances, and find and replace. Links are always kept.
- **Adjust times** as SimSig does it.
- **Tests:**
  - each tool on synthetic timetables, with exact expected XML;
  - pattern headcodes and pairing;
  - auto-fill against Waterloo's own booked times (fill a fast train from its first and last times; the result lies within the WTT's own run times);
  - the "no path" fix clearing S42's warning;
  - undo of each tool;
  - App tests of the preview and apply.

### S47 — Spreadsheet round trip and plan → draft (core portable, UI 🪟)
- **CSV** (`08-…md` §9):
  - one row per timing point, in a documented column set;
  - export, and a tolerant import (header aliases, quoting, `H`, SimSig notation);
  - the import merges by UID, else by headcode and first time, or adds trains;
  - a diff preview is applied as one edit, with a report of rows it could not read (line numbers).
  - CLI: `stringline wtt-csv export t.wtt out.csv` and `import t.wtt in.csv out.wtt`.
  - **`.xlsx`** only if the session has time and the library's licence is GPL-compatible (DocumentFormat.OpenXml, MIT). Check it before adding it to `Directory.Packages.props`. Otherwise leave it as a follow-up issue.
- **New draft from the working plan:**
  - copy the loaded WTT;
  - map each `TrainPlan` against its WTT train into edits: times, platform and line, skipped stops as passes, and terminate short truncating with its next working flagged;
  - cancellations: remove the train, or keep it, as the user picks in the dialog;
  - clones become new trains with new UIDs.
  - Disruptions and notes are listed, not converted.
  - The checks run at once.
- **Tests:**
  - CSV round trip on Waterloo (export then import changes nothing);
  - the tolerant cases;
  - merge, with a changed time and a new train;
  - plan → draft on a saved session (`--session`) with a retime, a platform change, a cancellation and a clone, giving the expected XML and the right S42 issues;
  - an App test of the dialog.

### S48 — Testing in SimSig, the guide and closing #20
- **Test in SimSig…** (Timetable menu) gives the steps: save, load in SimSig, run F4 ▸ Tools 1 ▸ Timetable Analyser, start the sim with Stringline connected and recording.
- **Timetable test report** (`Stringline.Editing`; `stringline report --test --wtt t.wtt --log x.sslog [out.csv]`; and in the app when a session using a draft's timetable ends, or from a recording) lists:
  - trains that never appeared (`ttsummary` status, or no sighting by their entry time plus a margin);
  - unmatched workings;
  - sections where trains always ran slower than booked (with the time to add);
  - platform clashes and booked conflicts that happened.
  - It opens in the draft's Issues panel against the trains, and quick fixes add run time where it says so.
- **Guide** (`docs/user-guide`): pages for the timetable editor, bulk tools, CSV format, from plan to timetable, and testing in SimSig. F1 targets for the editor's panels (`HelpTopics`). Pictures from `build/guide-shots.ps1`, free-sim data only. The wiki follows by its workflow.
- **Tests:**
  - the test report on Waterloo's recordings against the WTT (no false "never appeared" for trains of the recording's span), and against a draft with a deliberately too-short run;
  - the guide's link check and F1 pages.
- 👤 **Before closing:** Fred writes or changes a few trains on a sim he plays, round trips it through SimSig's Analyser and a run, and raises issues for what is missing.
- Close #20 with `Fixes #20`.

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
S21 → S30, S31 → S34, S35 → S36 → S37 → S38 → S39 → S32 → S33
                          👤 longer --td recordings → S37 (S38, S39 and S33 need S37's places)
S33 → S40 → S42 → S43 → S44 → S45 → S46 → S47 → S48      (Phase 4d, #20)
      S41 ──┘                                            (S41 alongside S40; 👤 SimSig Analyser gate after S40)
```
S4 and S6 can run in parallel with S2–S3 (separate worktrees/sessions).

## Working agreement for every session
1. Start: read `CLAUDE.md`, the docs named in the session, and the previous session's notes in `docs/progress.md`.
2. Write tests first for core logic; keep WPF code thin.
3. Finish: `dotnet build -warnaserror` and `dotnet test` green; update `docs/progress.md` (what was done, decisions, what's next, known issues); update design docs if anything changed; commit.
4. If a design question blocks progress, write it under "Questions for Fred" in `docs/progress.md` and pick the conservative default rather than stopping.
