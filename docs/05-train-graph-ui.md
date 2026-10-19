# 05 — Train Graph UI and Interaction Design

## 1. Main window

```
┌───────────────────────────────────────────────────────────────────────────────────────┐
│ Menu: File  Session  View  Corridor  Plan  Tools  Help                                 │
│ Toolbar: [Connect▾] [Replay▾] [Rec ●]  Corridor tabs: [Main line] [Branch] [+]          │
│          Layers: ☑WTT ☑Plan ☑Actual ☑Forecast  Colour: [Category▾]  Zoom: [1h][3h][6h]   │
├──────────────┬──────────────────────────────────────────────────────┬─────────────────┤
│ Trains       │                                                      │ Train / Detail  │
│ (filter,     │            TIME–DISTANCE GRAPH (Skia surface)        │ schedule table  │
│  delay sort) │   location labels ▏  paths, now line, blocks, notes  │ plan/act/fcst   │
│              │                                                      │ actions         │
│              │                                                      ├─────────────────┤
│              │                                                      │ Conflicts &     │
│              │                                                      │ suggestions     │
├──────────────┴──────────────────────────────────────────────────────┴─────────────────┤
│ Status: ● Connected Waterloo | Sim 09:14:32 ×4 | 212 trains (3 unmatched) | 2 conflicts │
└───────────────────────────────────────────────────────────────────────────────────────┘
```
Panels are dockable later; v1 uses a fixed three-column layout with a splitter.

## 2. Graph conventions (train control diagram style)
- Time on the horizontal axis by default (ViziRail/TCD style); a toggle swaps axes (S9). **Vertical** (Fred, S9): time runs down, the corridor across with its Up end on the left; times are labelled down the left and location names, rotated to read upwards, along the top.
- The corridor's Up end (its first location) is at the top, so Down trains slope down to the right (Fred, S7). The axis is padded 4% above the first and below the last location.
- Location axis labels: display name + TIPLOC; stations with platforms bold; loops shown as a double tick.
- Minor grid every 5 min, major every 30 min with labels; hour lines heavier.
- Down trains slope one way, Up the other — direction is implicit in the slope.
- Stops draw as horizontal segments at the location; pass points as vertices.

### Line styles
| Layer | Style |
|---|---|
| WTT | 1 px, category colour at 30% opacity |
| Working plan (where differs from WTT) | 1.5 px solid, category colour |
| Actual | 2.5 px solid, category colour (or delay band colour) |
| Forecast | 1.5 px dashed (6–4), same colour |
| Category line style (S14a) | a category styled Dashed draws its WTT, plan and actual lines in long dashes (12–7), Dotted in round dots two widths apart; the forecast keeps its own dash. The selected train is drawn solid |
| Cancelled | thin grey with crosses at ends |
| Unmatched live train | grey actuals |
| Selected train | 4 px with halo; others dim to 50% |
| Conflict | red ring at point + red section highlight |
| Block / possession | hatched rectangle (section × time) |
| Speed restriction | amber translucent band |
| Now line | vertical red line; past area lightly shaded |

### Delay bands (optional colouring)
≤ 0 min early/right time green · 1–5 amber · 6–15 orange · > 15 red.

Built in S9 (Fred: per segment). Each segment of an actual line takes the band of the delay at the timing point it arrives at, in whole minutes rounded towards zero, as the train list shows. Where that point is not booked, the last known delay carries on; segments before the first booked point take the first known delay; a matched train with no booked point yet is grey, as unmatched trains are. WTT lines keep their category colours, and labels take the colour of the segment they sit on. The train list's colour swatch follows the same mode (the current delay's band). The choice is remembered.

## 3. Interaction

| Gesture | Effect |
|---|---|
| Mouse wheel | Zoom time around cursor; Ctrl+wheel zoom distance; Shift+wheel pan time |
| Drag on background | Pan (disables auto-follow; "Follow" button re-enables) |
| Hover path | Tooltip: headcode, description, location, plan/actual/forecast, delay |
| Click path | Select train → detail panel |
| Drag selected path body | Retime whole remaining path (RP-2) — ghost preview, conflicts recomputed live |
| Alt+drag from a vertex | Retime from that location onward |
| Drag right end of a stop segment | Extend/shorten dwell (hold) |
| Right-click path | Context menu: Hold here…, Change platform/line…, Skip stop, Add stop…, Terminate here, Cancel, Clone as ad-hoc…, Link to WTT train… |
| Right-click background | Add block/possession here…, Add speed restriction…, Add note… |
| Drag-select rectangle with B held | Create block over sections × time |
| Ctrl+Z / Ctrl+Y | Undo / redo working-plan edits |
| Space | Toggle follow-now |

During drag: time snaps to 15 s (Shift = 1 s); a path cannot be dragged earlier than its latest actual or faster than minimum running time — it stops at the limit and the status bar explains why.

*Built in S12* (`PlanDrag` in Stringline.Graph; the shell only adapts the mouse). Fred's choices: **only the selected train is dragged** (a click selects it; pressing anything else pans), and **nothing is planned before the now line** as well as the latest report. The rules:
- **Which drag.** A press within 6 px of the right-hand end of a stop on the selected train's WTT or plan line drags the dwell (`SetDwell`); Alt+press retimes from the nearest vertex (`ShiftTrain` from that point; refused for a point already passed); any other press on the line retimes the body (`ShiftTrain` from the first point not yet reached: the origin for a train not started, the next unreported point for one running). Actual and forecast lines pan. Cancelled and finished trains are refused, with the reason in the status bar. Paths carry each vertex's plan point index (`GraphPath.Points`) for this.
- **Limits.** A retime earlier stops where the first point moved would be before max(now, latest report), or where the run into it would be faster than the minimum (`MinRunTable`, with speed restrictions); a dwell stops at the arrival or now. The limit is rounded up to the snapping grid. Clamps further on (a longer dwell pushing the rest of the run) are the plan builder's, and their warnings show in the status bar.
- **Ghost.** Each move applies the command to the snapshot's plan off the engine (Fred: the plan path only, no live re-forecast) and draws the train's resulting plan as a translucent 3 px line with a halo over everything (`SkiaGraphRenderer.Render(…, ghosts)`, not cached). Release sends the command; the ghost stays until a scene drawn from the edited plan replaces it. Esc (or losing the mouse) drops the drag. The time delta is measured in world time, so it stays right while the view follows the now line.
- **Right-click** on a line opens the menu for the train at the nearest timing point (and selects it): Hold here… (a departure time; at a pass it adds a stop), Add stop… (at a pass), Skip stop (at an intermediate call), Change platform…, Change line…, Terminate here, Cancel train / Reinstate, Reset to WTT (if re-planned), Clone as ad-hoc… (headcode and minutes later; the copy is selected so it can be dragged), and for a live train Link to WTT train… (a list, or "do not match") and Match automatically (if linked by hand). An unmatched live train offers only the link. Right-click on the background is S13's (below).
- **Undo/redo** are Ctrl+Z and Ctrl+Y (also Ctrl+Shift+Z), the Plan menu (which names the edit) and the history panel's buttons; they are off while dragging.

*Built in S13* (`GraphAnnotations` in Stringline.Graph; DIS-1 to DIS-3). Disruptions and notes are plan commands, so they undo and appear in the edit history (Fred: notes too).
- **Drawn.** A block is a red hatched rectangle (section × time), a speed restriction an amber band, each with a thin border and its label in the top-left corner ("Block · Down", "SR ×1.5 +30 s · UF"). Both draw under the trains, from the plan's disruptions whose two ends are on the corridor; a disruption at one location is an 8 px band along it. A note is a small ring at its time and location with the first line of its text (up to 32 characters) in a pale yellow box beside it, over the trains. Hovering shows the whole of either in the tooltip. The toolbar's Disruptions box and View → "Show disruptions and notes" hide them (`GraphSceneRequest.ShowDisruptions`).
- **Right-click.** On a note: Edit note…, Remove note. On a train line: the train's menu. On the background: a disruption there offers Edit…, Remove; then always Add block/possession here…, Add speed restriction here… (the section around the click, from the click's time rounded down to 5 min, for an hour) and Add note here… (the nearest location, at the minute).
- **B+drag** (Fred: the dialog, filled in). Holding B and dragging draws a dashed draft over the graph, widened out to the location lines around it (a point within 4 px of a line is on it, so a drag along one line is that location) and to 5-minute steps; the status bar gives its span. Release opens the block dialog filled in from the draft; Esc drops it.
- **Dialogs.** Block or restriction: From and To (the corridor's locations; From is stored as the end nearer the corridor's Up end, whichever way round they are entered), Track, From time, Until, for a restriction the running-time factor (default 1.5) and added minutes, and a note. A restriction must make running slower. Track (Fred) offers "All lines", the profile's tracks for the sections between the ends ("Down (Down trains)", "Up (Up trains)", or "Single (both ways)"), and the WTT line codes trains use between them, most used first; a code typed in is a WTT line. Note: location, time and text (at most 500 characters).
- **Not built:** dragging a disruption or note to move or resize it (Fred: edit through the dialog).

## 4. Train detail panel
Columns: Location · Plat/Line · WTT arr/dep · Plan arr/dep · Actual · Forecast · Delay. Edited cells in the plan column are bold; double-click to edit. Buttons: Reset to WTT, Cancel train, Clone.

*Built in S12* (`TrainSchedule` in Stringline.Planning, `TrainDetailViewModel` in the App), at the top of the right-hand column with the edit history below it (Fred). The heading gives the headcode, state (running and its delay, not started, finished, cancelled, terminated short, the forecast delay at the end, knock-on, possibly stopped, re-planned) and the route. Plat and Line are separate columns, so each can be edited. A stop with a dwell shows its arrival over its departure in every column, like a timetable; passes are italic; points after a short termination are grey. Times are as short as they can be without losing anything (`07:30`, `07:30½`, `07:30:15`). Editing: a plan arrival retimes the train from there (`ShiftTrain`); a plan departure at a call sets the dwell (`SetDwell`), at an origin or pass retimes from there; typed times take the day nearest the old one (`00:10` for a train due at `23:55` is the next day). Buttons: Reset to WTT (a new `ResetTrain` command), Cancel train/Reinstate, Clone…, Link… (a live train). A selected live train that is not matched shows its state and the Link button. The edit history lists every edit, oldest first, with undone ones greyed and a ⚠ with the clamp warnings as a tooltip.

## 5. Conflicts & suggestions panel
List: kind icon · time · location · trains · severity (min). Click focuses graph. Expand to see suggestions: "Hold 6M45 at XYZ loop 6 min → total weighted delay +4 min", with Preview (ghost) and Apply.

*Built in S14* (`ConflictsViewModel`, `GraphConflicts`), suggestions to come in S15:
- **Panel** (Fred: a tab beside the edit history, "Conflicts (N)"): every conflict still to come **on every corridor** (Fred), by time. A row is a red kind badge (HW headway, OT overtake, SL single line, PL platform, BK block, RT run time), the time, the place (section, or location and platform), the trains (A / B, or A / block ID) and the delay to resolve it in minutes rounded up; its tooltip gives the exact delay and the knock-on. Rows are kept in step in place, so the selection and scroll survive live updates.
- **Focus.** Picking a row (or clicking a ring) selects train A with the conflict, switches to a corridor that shows it if the one shown does not (else says it is on no corridor), stops following and centres the view on it (along the corridor too when zoomed in, `GraphNavigator.CentreOn(time, axis)`).
- **Graph.** A red ring (with a white halo) where A meets the clash, over the trains; a translucent red highlight over the section (or an 8 px band at a platform's location) for the time the clash covers, under the trains. The selected conflict is stronger (`GraphSelection.ConflictId`). Hovering a ring shows its tooltip before the train's; clicking it picks the conflict rather than the line. Toolbar Conflicts box and View → "Show conflicts" hide the marks (`GraphSceneRequest.ShowConflicts`, not remembered). `render-png --log` draws them too (`--no-conflicts`).
- **Status bar**: "N conflicts".

## 6. Regulation list (S)
Per location, ordered list of upcoming movements in the working-plan order, highlighting where it differs from the WTT order — the controller's crib sheet while signalling.

## 7. Rendering design (Stringline.Graph)

Built in S7 and S9 as below, except that the scene builder takes a `GraphSceneRequest` (profile, corridor, WTT, live trains, now, colour mode) built from a `PlanSnapshot` by `GraphSceneRequest.For`. S10 adds the plan layer: every train whose working plan differs from the WTT, and every clone, as the "working plan" style below; from S12 a cancelled train is drawn there thin (1 px) and grey with crosses at both ends (drawn into its cached path, inside the tiles' bleed), and the WTT layer still shows the booking. S11 adds the forecast layer (`GraphSceneRequest.Forecast`, `ShowForecast`): each running train from its latest actual time (so the dashes carry on from the end of its actual line, including a dwell it is in), and each train not started whose forecast is a minute or more off the plan (Fred). It is in the category colour, or by forecast delay band per segment when colouring by delay. The tooltip adds the forecast times and delay at the location, any knock-on of a minute or more, and "possibly stopped". The shell has a Forecast layer check box and View → "Forecast knock-on from other trains" (remembered, on by default); `render-png` draws it with `--log` (`--no-forecast`, `--no-knock-on`).

- **Performance (S9, GR-9).** Anti-aliased stroking is most of a frame's cost, so the renderer avoids doing it again:
  - **Projected paths** (`ProjectedPathCache`). Each path is projected to an `SKPath` once per zoom, relative to a reference time, and drawn with the canvas translated. Panning, following and scrolling along the corridor are therefore translations. A new scene keeps the paths whose key, colours and vertices are unchanged, and reports the world rectangles that draw differently (for a train that moved on, only its last stretch).
  - **Tiles** (`PathTileCache`). The unselected lines are kept as 256 px tiles at device resolution. A pan blits them, drawing only tiles new to the view. A live update redraws only the tiles under the changed stretches. A zoom, resize, turn, DPI change or new selection drops them all. The frame that changes the zoom draws directly, and the next frame builds the tiles in one pass over the box around the missing ones, so a zoom gesture costs no more than drawing directly.
  - **Pixel alignment.** The projection is aligned so tiles land on whole device pixels, exactly where a direct draw puts the lines; a later pan by a fraction of a pixel places them on the nearest whole pixel. The selected train is drawn over the tiles each frame.
  - **Other parts.** `RenderPng` (tests, CLI, export) draws directly. Tiles use the platform's native pixel format: RGBA tiles on Windows (BGRA) took Skia's slow blitter path and cost three times as much. Labels are checked for overlap against a `LabelGrid` of 48 px cells. Hit testing uses a `GraphHitIndex`, a 16 px grid built per scene and viewport, which gives exactly the linear scan's answers.
  - **Measured** (`benchmarks/`, 500 trains / 6 h, 1920×1080, CPU): a pan frame takes 7–14 ms, against 32–36 ms drawing directly (at 1 000 trains, 8–10 ms against 58 ms); a zoom frame costs a direct draw (37–40 ms); 100 hover tests take about 0.1 ms with the grid, against about 4 ms by linear scan. See `progress.md` S9 for the BenchmarkDotNet table.
- **Vertical orientation (S9).** `GraphViewport.Orientation`: the transforms work in "time pixels" and "axis pixels" (X and Y, or Y and X), so scene, cache, tiles, hit testing and navigation are orientation-free. The renderer swaps only the grid, past shading, now line and the margins' labels.

- **Scene rules.** Only timed points on the corridor become vertices; a pass is one vertex, a stop with both times two (a level segment). A train that leaves the corridor and comes back to the *same* location starts a new path, so time on a branch does not draw as a stop; rejoining elsewhere draws straight across. A path needs two vertices, so a train that only crosses the corridor at one point draws nothing. Each working of a live uid is its own actual path; only `AtTimingPoint` actuals are used (signals have no axis position). Matched actuals take the WTT train's category colour; pending and unmatched ones are grey (`#8A8A8A`).
- **Labels.** One headcode per train, preferring its actual path over its WTT path, rotated along the first visible sloping segment long enough for it, at ½, ¼ or ¾ of the way along, skipped if it would overlap a label already placed. Labels are the line colour, opaque and 25% darker.
- **Grid.** Minor spacing is the first of 1/2/5/10/15/30/60… min at least 20 px apart; labelled lines are the first step at least six minor lines and 64 px apart. That gives 5/30 min at a 3 h view, 10/60 at 6 h. Times past midnight are labelled with the day (`00:00+1`).
- **Text.** Noto Sans Regular and Bold are embedded (SIL OFL), unhinted and anti-aliased, so text is the same on every machine. Glyph edges still differ slightly between DirectWrite (Windows) and FreeType (Linux), so PNG snapshots compare with a tolerance: no channel more than 64 apart on more than 0.01% of pixels. Measured, Windows and Linux renders differ on no pixel by that much.

- `GraphViewport` (time range, distance range, pixel size, orientation) → transforms.
- `GraphSceneBuilder` turns a `PlanSnapshot` + corridor + viewport into a `GraphScene` of primitives (polylines with style keys, labels, rects). Built off the UI thread; rebuilt only when snapshot, corridor or viewport changes. Polylines are in **world coordinates** (sim seconds, axis position) so panning only changes the transform.
- `SkiaGraphRenderer.Render(SKCanvas, GraphScene, Viewport)` — culls by bounding box; cached `SKPath`s per train per layer; `SKPaint` objects pooled by style key.
- Label placement: greedy placement along each path at the first visible segment, skipping overlaps against an R-tree / grid of placed label rectangles.
- Hit testing: spatial grid over projected segments; tolerance 5 px.
- Now-line animation: a 10 Hz timer invalidates only when the interpolated now moves ≥ 1 px.
- The same renderer writes PNGs in tests (`Verify` snapshot) and `stringline render-png` CLI.

## 8. The WPF shell (S8)

Built as §1 describes, with these specifics:
- **Layout.** Menu (Session, Timetable, View, Help), toolbar (Connect, Replay, Disconnect, Rec, Timetable; corridor tabs; WTT/Actual layer toggles; 1h/3h/6h; Follow), an info bar for messages and timetable suggestions, the train list and graph side by side with a splitter, a replay transport bar (play/pause, speed 1×–60×/Max, seek bar), and the status bar (connection, sim name, sim clock and speed, train counts, recording). S12 adds a Plan menu, a third column (train detail over edit history, with a splitter), and the edit status in the status bar; S13 a Disruptions layer box (also in the View menu); S14 the conflicts panel as a tab beside the history, a Conflicts layer box (also in the View menu) and the count in the status bar; S14a a Profile menu (§9).
- **Sessions.** One `LiveEngine` runs for the app's lifetime; each Connect or Replay clears it and pumps a new source into it. A replay is read in-process through `ReplaySource` (Fred's choice); the external `replay-server` still works through Connect. Seeking a finished replay starts it again from the chosen time.
- **Timetables (Fred's choice: pick + suggest).** The Connect and Replay dialogs take an optional `.wtt`, remembered per sim. When the sim's `area_id` arrives and no timetable is loaded, the info bar offers the timetable last used for that sim plus every `.wtt` for that area in SimSig's Timetables folder (`WttCatalogue`). A timetable for a different sim gets a warning and suggestions. Loading one mid-session rebuilds the matches from the session's events. The profile is loaded and refreshed, or created, and saved (`%APPDATA%\Stringline\profiles`); a damaged profile is not overwritten.
- **Credentials (CON-5, Fred's choice: opt-in).** The username is always remembered; the password only with "Remember the password", encrypted with DPAPI for the current Windows user in `%APPDATA%\Stringline\settings.json`. `GatewayClientOptions` is logged only through its masked `ToString`.
- **Recording (CON-6).** Rec writes the live connection to `Documents\Stringline\recordings\{area}-{yyyyMMdd-HHmmss}.sslog` and sends `idrequest` so the file starts with a clock. Frames are stamped with the last clock even if recording began mid-minute.
- **Graph host.** An `SKElement` drawn at device pixels with the canvas scaled by the DPI, so viewport units are WPF device-independent pixels. A 10 Hz timer moves the clock and now line, redrawing only when the line moves half a pixel or follow mode pans. The now line is interpolated from the snapshot's `SimClockReading`, and frozen while a replay is paused.
- **Mouse and keys.** Wheel zooms time about the cursor (about the now line while following), Ctrl+wheel zooms along the corridor, Shift+wheel pans time. Dragging pans; dragging across in time stops following. A click selects the train under the cursor (both its WTT and live paths), and a click on the background clears it. Space toggles follow and Esc clears the selection, when the graph has focus. Hovering shows a tooltip for the timing point nearest the cursor.
- **Train list.** One row per live train (its current working): colour, headcode, delay in minutes at the latest booked point, last timing point and time, and the WTT train or match state. It can be sorted by delay (the default), headcode or last report, filtered by text, and limited to "not matched". Rows are updated in place, so the selection and scroll position survive updates. Selecting a row selects the train on the graph, and double-clicking it centres the graph on its last report.
- **Command line.** `Stringline.exe [--wtt t.wtt] [--replay x.sslog [--speed 8|max] [--start 07:30]]`.
- **View options (S9).** The toolbar's Colour box (Category or Delay) and ⇅ Vertical toggle, also in the View menu, are remembered in the settings. Turning the graph keeps the time and corridor ranges and follow mode.
- **Export (S9, GR-10; Fred: the current view).** Session ▸ Export graph as PNG… (Ctrl+E), or at 2× size. It saves the graph as shown: the same time and corridor range, orientation, colours, selection and now line, at the graph's size in device-independent pixels times 1 or 2. A save dialog offers `Documents\Stringline\exports\{area}-{corridor}-{HHmm}.png` at first, then the folder last used. PDF export (GR-10) is not built.

## 9. Profile editor (S14a)

- **Profile menu.** Edit profile… opens the editor. Open profile file opens `{area_id}.json` in the program Windows uses for JSON. Reload profile re-reads it (after a hand edit) and redraws without reloading the WTT; a damaged file is reported and the profile in use stays. Regenerate corridors… replaces the corridors with generated ones; if any are hand-tuned it asks first: Replace all, Keep mine (beside the generated ones), or Cancel (Fred's choice).
- **Editor window** (modal), with OK, Cancel and Apply. Tabs:
  - **General:** the sim ID (editable only when the timetable has none), the sim name, and every location's display name (with the corridors it is on), plus Import names from CSV….
  - **Corridors:** the corridors in tab order (rename in place, ▲/▼ to reorder, New… from two ends, Remove, and Add for a generated corridor not in the profile). For the selected one: an axis preview (Up end on the left, stations heavier), Space by distance, the locations (name, distance, platforms; Add location… after the selected one, Remove, ▲/▼), and the sections (a layout preset or tracks as text, headway as m:ss, the WTT's minimum run).
  - **Categories:** name, colour (`#RRGGBB` with a swatch), line style, priority and the number of trains.
- Every cell edit is a `ProfileEdits` edit on a draft. One that is refused shows why under the tabs, and the cell goes back to its old value. Cancel or closing with unapplied changes asks before discarding them.
- **Apply** saves through `ProfileStore`, then rebuilds the forecast network, the corridor tabs, the train list's colours and the graph from the loaded WTT. The working plan, disruptions, notes and selection are kept. The corridor shown stays, with its view, if it is still there; a removed corridor's tab closes and the first is shown.
- **No sim ID** (Fred's choice: remember per file). A timetable without an area ID gets an unsaved `unknown` profile and says so. The editor asks for a sim ID, offering the live sim's `area_id` if connected. On Apply the profile is saved under it, and the settings remember it for that `.wtt` path (`SimIdByTimetable`), so the next load finds it. If a profile with that ID is already saved, it asks whether to use the saved one or replace it.
