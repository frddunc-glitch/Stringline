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
- Time on the horizontal axis by default (ViziRail/TCD style); a toggle swaps axes.
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
- `GraphViewport` (time range, distance range, pixel size, orientation) → transforms.
- `GraphSceneBuilder` turns a `PlanSnapshot` + corridor + viewport into a `GraphScene` of primitives (polylines with style keys, labels, rects). Built off the UI thread; rebuilt only when snapshot, corridor or viewport changes. Polylines are in **world coordinates** (sim seconds, axis position) so panning only changes the transform.
- `SkiaGraphRenderer.Render(SKCanvas, GraphScene, Viewport)` — culls by bounding box; cached `SKPath`s per train per layer; `SKPaint` objects pooled by style key.
- Label placement: greedy placement along each path at the first visible segment, skipping overlaps against an R-tree / grid of placed label rectangles.
- Hit testing: spatial grid over projected segments; tolerance 5 px.
- Now-line animation: a 10 Hz timer invalidates only when the interpolated now moves ≥ 1 px.
- The same renderer writes PNGs in tests (`Verify` snapshot) and `stringline render-png` CLI.
