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
| Forecast | 1.5 px dashed, same colour |
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

## 4. Train detail panel
Columns: Location · Plat/Line · WTT arr/dep · Plan arr/dep · Actual · Forecast · Delay. Edited cells in the plan column are bold; double-click to edit. Buttons: Reset to WTT, Cancel train, Clone.

## 5. Conflicts & suggestions panel
List: kind icon · time · location · trains · severity (min). Click focuses graph. Expand to see suggestions: "Hold 6M45 at XYZ loop 6 min → total weighted delay +4 min", with Preview (ghost) and Apply.

## 6. Regulation list (S)
Per location, ordered list of upcoming movements in the working-plan order, highlighting where it differs from the WTT order — the controller's crib sheet while signalling.

## 7. Rendering design (Stringline.Graph)

Built in S7 and S9 as below, except that the scene builder takes a `GraphSceneRequest` (profile, corridor, WTT, live trains, now, colour mode) built from a `PlanSnapshot` by `GraphSceneRequest.For`. S10 adds the plan layer: every train whose working plan differs from the WTT, and every clone, as the "working plan" style below; cancelled trains are not drawn there yet (S12 adds their style), and the WTT layer still shows the booking.

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
- **Layout.** Menu (Session, Timetable, View, Help), toolbar (Connect, Replay, Disconnect, Rec, Timetable; corridor tabs; WTT/Actual layer toggles; 1h/3h/6h; Follow), an info bar for messages and timetable suggestions, the train list and graph side by side with a splitter, a replay transport bar (play/pause, speed 1×–60×/Max, seek bar), and the status bar (connection, sim name, sim clock and speed, train counts, recording). The detail and conflicts panels arrive with S12/S14.
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
