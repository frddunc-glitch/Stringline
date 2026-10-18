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
- Time on the horizontal axis by default (ViziRail/TCD style); a toggle swaps axes (S9).
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

Built in S7 as below, except: the scene builder takes a `GraphSceneRequest` (profile, corridor, WTT, live trains, now) until S10 provides `PlanSnapshot`; paths are not cached between frames yet, and labels are placed against a simple list of rectangles (both S9).

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
