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
Panels are dockable later; v1 uses three columns with splitters.

**Resizing (S23, #9; Fred: keep share).** The train list, graph and side panel are star-sized: the window keeps their proportions as it changes size, so the graph (the biggest share) grows most. Each has a least width (train list 270 px, graph 300, side panel 450, `WindowLayout.Min…`), at which every list in it still fits (#2), so a splitter cannot push a panel below it or off the window (window at least 1050 × 520). The side panel splits into the train detail and the tabs (2 : 1), and the Conflicts tab into the list and its suggestions, with their own splitter (Fred; 200 px at first, at least 60).
- **Hiding.** A double-click on the splitter beside the train list or the side panel hides that panel, and again shows it; on the splitter under the train detail it hides the tabs (Fred), and the train detail takes their room. View ▸ Panels has the same three switches. A hidden panel's room goes to the graph, and it comes back at its old share. A splitter beside a hidden panel does not drag.
- **Remembered.** The shares (as pixel sizes last seen, used as star weights), the switches, the suggestions' height and the window's place, size and maximised state are saved in settings (`AppSettings.Layout`) when the window closes, and restored at the next start. A window whose screen has gone is centred on the primary screen, shrunk to fit; one partly off its screen is pulled back so its title bar can be grabbed (`WindowPlacement.OnScreen`).
- **View ▸ Reset layout** shows every panel at its first share, leaving the window where it is.
- **Lists (#2).** Every list and grid in the panels (train list, train detail, Conflicts, Regulation, Delays, History) has one star-sized text column (location, "last at", order) that trims with an ellipsis and shows the whole text in a tooltip; numbers and times are sized to their content, and delays right-aligned. A horizontal scroll bar appears only below the least width. The Regulation and Delays lists are `DataGrid`s now (they were `GridView`s with fixed widths, whose last column fell past the panel's edge).
- S21's split and multi-board views will extend this; S23 does the single view only.

### Menus, toolbar and shortcuts (S25, #8)
Reviewed against this spec and Windows conventions in S25. The bar is the spec's (Fred): **File · Session · View · Corridor · Plan · Tools · Help**.

| Menu | Items (access key underlined in the app; shortcut) |
|---|---|
| File | Load timetable… (Ctrl+T), Timetable import warnings · Resume saved session…, Review saved session… · Export ▸ (Graph as PNG… Ctrl+E, PNG at 2× size…, PDF A3 landscape… Ctrl+P, PDF A4 landscape…, PDF view size… · Delay table as CSV…, Train delays as CSV…) · Exit (Alt+F4) |
| Session | Connect to SimSig… (F5), Open replay… (Ctrl+O), Disconnect · Record to file (ticked while recording) |
| View | Follow now (Space) · 1 hour, 3 hours, 6 hours · Show WTT, actual, forecast, disruptions and notes, conflicts · Colour by delay, Vertical · Panels ▸ (Train list, Side panel, Tabs), Reset layout |
| Corridor | each corridor in tab order, the one shown ticked, the first nine numbered (Ctrl+1 … Ctrl+9) · Whole corridor |
| Plan | Undo *edit* (Ctrl+Z), Redo *edit* (Ctrl+Y, Ctrl+Shift+Z) · Reset selected train to WTT, Cancel or reinstate, Clone as ad-hoc…, Link live train…, Clear selection (Esc) · Forecast knock-on from other trains |
| Tools | Options… (Ctrl+,) · Edit profile…, Open profile file, Reload profile, Regenerate corridors… |
| Help | Getting started (online), Open log folder · About Stringline |

- **File is things on disk, Session is the link to the sim** (Fred). Opening a replay starts a session, so it is under Session with Connect.
- **View holds display switches only.** Settings moved to Tools ▸ Options… (#8). Whole corridor went to the Corridor menu. Clear selection went to Plan, next to the commands that act on the selection. Forecast knock-on also went to Plan, because it changes the forecast and conflicts, not only what is drawn.
- **Plan is the edit menu** (Fred: no separate Edit menu). Its Undo and Redo name the edit.
- **The profile is under Tools** (Fred), beside Options. The Profile and Timetable menus are gone.
- **Access keys** are unique within every menu, including the graph's right-click menus (Regulation list is now Re**g**ulation, because **L** is Link). Text from data (corridor names, locations, the edit in Undo) is escaped, so an underscore in a name is shown and is not taken as a key. **Shortcuts** shown in a menu are key bindings for the same command. Space and Esc belong to the graph, so they work while it has focus. `MenuTests` checks all of this.
- **Toolbar** (it matches the menus): Connect…, Replay…, Disconnect, Rec, Timetable… · corridor tabs · WTT, Actual, Forecast, Disruptions, Conflicts, By delay, ⇅ Vertical · 1h, 3h, 6h, Follow. Every control has a tooltip naming its menu item or shortcut. **The corridor tabs stay on one row** (Fred). Tabs that do not fit are listed under a » button at the row's end, and picking one there scrolls its tab into view. At the window's least width the row has no room, so every corridor is under ». The Colour box became a **By delay** toggle, the same switch as View ▸ Colour by delay. That, and tighter padding, gives Waterloo's two tabs room at 1280 px.

**Spec vs build (S25 review).**

| Spec (§1, §8, §12) | Built before S25 | Now |
|---|---|---|
| Menu bar File · Session · View · Corridor · Plan · Tools · Help | Session · Timetable · Profile · View · Plan · Help | As the spec |
| Settings (§12: View ▸ Settings…) | View ▸ Settings… | Tools ▸ Options… (Ctrl+,); the window is titled Options |
| Corridor menu | none (tabs only) | Every corridor with Ctrl+1–9, and Whole corridor |
| Exports under Session (§8, §11) | Session ▸ Export… (seven items) | File ▸ Export ▸ |
| Toolbar: Connect▾ Replay▾ Rec, tabs, layers, Colour▾, zoom | Plus Disconnect, Timetable…, Vertical, Follow; tabs wrapped onto two rows at 1280 px | The same, on one row: tabs overflow to », and Colour is a toggle. The spec's ▾ recent-file menus on Connect and Replay are not built: the dialogs remember the last host and file |
| Access keys | Repeats in Session (R, V, D), View (W, F, D) and the train menu (L) | Unique in every menu (tested) |
| Right-click menus (§3) | As the spec | Unchanged, except Re**g**ulation list |

## 2. Graph conventions (train control diagram style)
- Time on the horizontal axis by default (ViziRail/TCD style); a toggle swaps axes (S9). **Vertical** (Fred, S9): time runs down, the corridor across with its Up end on the left; times are labelled down the left and location names, rotated to read upwards, along the top.
- The corridor's Up end (its first location) is at the top, so Down trains slope down to the right (Fred, S7). The axis is padded 4% above the first and below the last location.
- Location axis labels: display name + TIPLOC; stations with platforms bold; loops shown as a double tick.
- **Label margin (S23, #3).** The left margin (the top one when vertical) is sized to the widest label, its name or TIPLOC, between 90 and 260 px of room (`GraphLabels`); a forty-character name fits. A longer one is shortened with an ellipsis. Hovering over a label shows its full name, TIPLOC and platforms, and a reminder that Tools ▸ Edit profile can give it a shorter name for good. PNG and PDF exports (the app's and `render-png`'s) use the same margin.
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
≤ 0 min (early or on time) green · 1–5 amber · 6–15 orange · > 15 red.

**Punctuality wording (S24, #11).** As the railway says it: "*n* min early", "On time" (within the minute either way, whole minutes rounded towards zero) and "*n* min late", in every tooltip and the train detail (`GraphHover.FormatDelay`; the band is `DelayBand.OnTime`). The lists keep their compact signed minutes (`+3`, `0`, `−1`) so they fit and sort, and each delay cell's tooltip says it in words. The CSV keeps signed seconds. The old wording is gone, and an Architecture test (`WordingTests`) keeps it out of the source, XAML, README and design docs.

Built in S9 (Fred: per segment). Each segment of an actual line takes the band of the delay at the timing point it arrives at, in whole minutes rounded towards zero, as the train list shows. Where that point is not booked, the last known delay carries on; segments before the first booked point take the first known delay; a matched train with no booked point yet is grey, as unmatched trains are. WTT lines keep their category colours, and labels take the colour of the segment they sit on. The train list's colour swatch follows the same mode (the current delay's band). The choice is remembered.

## 3. Interaction

| Gesture | Effect |
|---|---|
| Mouse wheel | Zoom time around cursor; Ctrl+wheel zoom distance; Shift+wheel pan time |
| Drag on background | Pan (disables auto-follow; "Follow" button re-enables) |
| Hover path | Tooltip: headcode, description, location, plan/actual/forecast, delay ("3 min late", "On time", "1 min early"); inside a conflict's highlight or a disruption, that too (#12) |
| Hover conflict ring or highlight | Tooltip (#12): kind and summary, trains ("2B02 gives way to 1A01", "1A01 held by block B1"), where (from–to), when (and the clash's span), the delay to resolve it, knock-on, and the top suggestion once worked out (else "Click to focus it and see suggestions") |
| Hover block / speed restriction | Tooltip (#12): kind and ID, where, when, track, effect (held until it ends) or the slower running, note, and "Right-click to edit or remove it" |
| Click path | Select train → detail panel, and its grips (S26) |
| Drag a grip's square (selected train) | Retime from that timing point onward (RP-2) — ghost preview, new time beside the grip |
| Drag a grip's bar (end of a stop) | Extend/shorten dwell (hold) |
| Drag the round handle, or the selected line's body | Retime whole remaining path (RP-2) |
| Click a grip, then ←/→ (↑/↓ when vertical) | Nudge it by 15 s (Shift: 1 min); each nudge is one edit |
| Alt+drag from a vertex | Retime from that location onward (kept as a shortcut) |
| Hover a grip | Cursor shows it moves (or ⊘ if locked); the status bar names the action; tooltip says what it does or why it is locked |
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

*Built in S26* (#10; `GraphGrips` and `PlanDrag` in Stringline.Graph, portable). TRIMS-style grips on the selected train. Fred's choices: **a handle and the whole line** drag the body, **click a grip, one edit per nudge**, and **hollow grey squares** for points passed.
- **Why dragging "did not work".** Over a running train its forecast line lies on its WTT line, and the hit test prefers the forecast (by 2 px), which pans with nothing said. So on an on-time train, a press on the run still to go never dragged. As well: the 5 px tolerance on a 1–2 px line; Alt+press that did not start a drag let Alt open the menu bar on release; and a clamp showed only in the status bar.
- **Grips.** Only on the selected train, on its plan line if it has been re-planned, else its WTT line (none if neither is drawn, e.g. with the WTT layer off and no edit yet). A square at each timing point (drag = `ShiftTrain` from that point, as Alt+drag was); a bar across the location line at each stop's departure end (drag = `SetDwell`; none at the end of the run, though the WTT may time a departure there); a round handle at the middle of the first stretch of the run still to go that is at least 30 px on screen (drag = `ShiftTrain` from the next point not reached). Movable grips are filled in the line's colour with a white edge; locked ones are hollow grey; the one under the mouse is drawn larger and the one picked has a blue ring.
- **Locks.** A grip is locked when `PlanDrag.Start` would refuse it, so the reason is the same one a drag gives: a point passed ("1A01 has already passed B."), the arrival at a platform the train is standing at ("…has arrived at C; drag its departure instead." — its bar stays movable), a stop already left, or a cancelled or finished train. Pressing a locked grip neither drags nor pans; its reason goes in the status bar.
- **Hit order.** Grips first, within 7 px (the nearest; a square or bar before the handle), then the selected train's own WTT or plan line within 6 px even under its forecast, then the hit test as before (§3 above).
- **While dragging** only the dragged grip is drawn, where it has got to, with a label: the new time and the change, "07:15:30 (+3 min)", and "· stopped: now" (or last report, minimum run, arrival) when held at a limit, when the grip and the label's border turn red. A drag on the line's body puts the label at the press.
- **Nudges.** Clicking a grip (no drag) picks it, and a grip dragged stays picked. The arrow keys along the time axis move it by 15 s, Shift by a minute, under the same limits. Each nudge is made on the engine's latest plan, so a quick run adds up; nudges pressed while one is being made go together as the next edit. Esc still clears the selection (and so the grip); during a drag it cancels the drag.
- **Alt+press** on the graph never opens the menu bar on release now, whether or not it started a drag.

*Built in S13* (`GraphAnnotations` in Stringline.Graph; DIS-1 to DIS-3). Disruptions and notes are plan commands, so they undo and appear in the edit history (Fred: notes too).
- **Drawn.** A block is a red hatched rectangle (section × time), a speed restriction an amber band, each with a thin border and its label in the top-left corner ("Block · Down", "SR ×1.5 +30 s · UF"). Both draw under the trains, from the plan's disruptions whose two ends are on the corridor; a disruption at one location is an 8 px band along it. A note is a small ring at its time and location with the first line of its text (up to 32 characters) in a pale yellow box beside it, over the trains. Hovering shows the whole of either in the tooltip. The toolbar's Disruptions box and View ▸ "Show disruptions and notes" hide them (`GraphSceneRequest.ShowDisruptions`).
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
- **Graph.** A red ring (with a white halo) where A meets the clash, over the trains; a translucent red highlight over the section (or an 8 px band at a platform's location) for the time the clash covers, under the trains. The selected conflict is stronger (`GraphSelection.ConflictId`). Hovering a ring shows its tooltip before the train's; clicking it picks the conflict rather than the line. *S24 (#12):* the highlight has the same tooltip where no line is over it, and a click there picks the conflict too; a line over a highlight adds a "Conflict" row naming it (a held train's line covers its own band). What is under the mouse is found in one place, `GraphHitTester.TargetAt` (ring, note, line, highlight, disruption, label), and described by `GraphHover.Describe(GraphTarget, …)`, so the hover is the same in both orientations. Toolbar Conflicts box and View ▸ "Show conflicts" hide the marks (`GraphSceneRequest.ShowConflicts`, not remembered). `render-png --log` draws them too (`--no-conflicts`).
- **Status bar**: "N conflicts".

*Suggestions, built in S15* (`SuggestionsViewModel`, `MainViewModel.Suggestions`): picking a conflict opens a panel under the list, "Resolve: 1F01 too close behind 2S01", with up to three suggestions (rules in `06-…md` §4). Each row gives what it does ("Hold 1F01 at Clapham Junction until 08:07 (+2 min)"), a line of cost ("Weighted delay +2 min · clears 2 conflicts · 1 new", or "Does not fully clear it · …"), a tooltip listing the trains made later or sooner (with their weights) and what the penalties add, and **Preview** (a toggle: the changed trains' new plans as ghosts on the corridor shown) and **Apply** (one undoable edit; its ghost stays until the graph shows it). "Working out suggestions…" shows while they are worked out, and "No suggestion: neither … can wait anywhere before it" when there are none. An **Allow cancelling** box adds terminating short and cancelling for blocks (remembered, off by default).

## 6. Regulation list (S)
Per location, ordered list of upcoming movements in the working-plan order, highlighting where it differs from the WTT order — the controller's crib sheet while signalling.

*Built in S15* (`RegulationList` in Stringline.Planning, `RegulationViewModel`), as a **Regulation** tab between Conflicts and History (Fred). A location box (every corridor location, in corridor order) and a list of the movements there not yet made by the forecast, planned or forecast within the next 3 hours, in plan order: plan time, train, what it does (starts, calls 07:24–, passes, terminates), platform, forecast, minutes late (a minute or more), and Order: "Ahead of 1A23" or "Behind 2B45" where the plan has it the other way round from the WTT (those rows are bold amber), or "Added" for a clone. The tooltip gives the booked and planned times, forecast and line out. Cancelled trains, and trains finished or left out of the forecast, are not listed. "Regulation list at …" on the graph's right-click menu (on a train, at its point; or on the background, at the nearest location) opens it there.

## 7. Rendering design (Stringline.Graph)

Built in S7 and S9 as below, except that the scene builder takes a `GraphSceneRequest` (profile, corridor, WTT, live trains, now, colour mode) built from a `PlanSnapshot` by `GraphSceneRequest.For`. S10 adds the plan layer: every train whose working plan differs from the WTT, and every clone, as the "working plan" style below; from S12 a cancelled train is drawn there thin (1 px) and grey with crosses at both ends (drawn into its cached path, inside the tiles' bleed), and the WTT layer still shows the booking. S11 adds the forecast layer (`GraphSceneRequest.Forecast`, `ShowForecast`): each running train from its latest actual time (so the dashes carry on from the end of its actual line, including a dwell it is in), and each train not started whose forecast is a minute or more off the plan (Fred). It is in the category colour, or by forecast delay band per segment when colouring by delay. The tooltip adds the forecast times and delay at the location, any knock-on of a minute or more, and "possibly stopped". The shell has a Forecast layer check box and Plan ▸ "Forecast knock-on from other trains" (View until S25; remembered, on by default); `render-png` draws it with `--log` (`--no-forecast`, `--no-knock-on`).

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
- **Layout.** Menu (Session, Timetable, View, Help; S25 reorganised it, see §1), toolbar (Connect, Replay, Disconnect, Rec, Timetable; corridor tabs; WTT/Actual layer toggles; 1h/3h/6h; Follow), an info bar for messages and timetable suggestions, the train list and graph side by side with a splitter, a replay transport bar (play/pause, speed 1×–60×/Max, seek bar), and the status bar (connection, sim name, sim clock and speed, train counts, recording). S12 adds a Plan menu, a third column (train detail over edit history, with a splitter), and the edit status in the status bar; S13 a Disruptions layer box (also in the View menu); S14 the conflicts panel as a tab beside the history, a Conflicts layer box (also in the View menu) and the count in the status bar; S14a a Profile menu (§9); S15 the suggestions panel under the conflicts list and a Regulation tab (§5, §6).
- **Sim and timetable names (S22, #6).** The window title (`Stringline — …`), the status bar, saved sessions, the graph's title band (PNG and PDF) and the CSV title line all show `SimTitle`: "*sim* – *timetable* (v*version*)", plus "(review)" when reviewing. The sim is the profile's name, then the gateway's area ID, then the timetable's, tidied (`waterloo` → `Waterloo`), then "Unknown sim". A profile name that is just the timetable's title (profiles made before S22) counts as none.
- **Sessions.** One `PlanningEngine` runs for the app's lifetime; each Connect or Replay clears it and pumps a new source into it. A replay is read in-process through `ReplaySource` (Fred's choice); the external `replay-server` still works through Connect. Seeking a finished replay starts it again from the chosen time, keeping the plan (S16). Saving, resuming and review are in §10.
- **Timetables (Fred's choice: pick + suggest).** The Connect and Replay dialogs take an optional `.wtt`, remembered per sim. When the sim's `area_id` arrives and no timetable is loaded, the info bar offers the timetable last used for that sim plus every `.wtt` for that area in SimSig's Timetables folder (`WttCatalogue`). A timetable for a different sim gets a warning and suggestions. Loading one mid-session rebuilds the matches from the session's events. The profile is loaded and refreshed, or created, and saved (`%APPDATA%\Stringline\profiles`); a damaged profile is not overwritten.
- **Credentials (CON-5, Fred's choice: opt-in).** The username is always remembered; the password only with "Remember the password", encrypted with DPAPI for the current Windows user in `%APPDATA%\Stringline\settings.json`. `GatewayClientOptions` is logged only through its masked `ToString`.
- **Recording (CON-6).** Rec writes the live connection to `Documents\Stringline\recordings\{area}-{yyyyMMdd-HHmmss}.sslog` and sends `idrequest` so the file starts with a clock. Frames are stamped with the last clock even if recording began mid-minute.
- **Graph host.** An `SKElement` drawn at device pixels with the canvas scaled by the DPI, so viewport units are WPF device-independent pixels. A 10 Hz timer moves the clock and now line, redrawing only when the line moves half a pixel or follow mode pans. The now line is interpolated from the snapshot's `SimClockReading`, and frozen while a replay is paused.
- **Mouse and keys.** Wheel zooms time about the cursor (about the now line while following), Ctrl+wheel zooms along the corridor, Shift+wheel pans time. Dragging pans; dragging across in time stops following. A click selects the train under the cursor (both its WTT and live paths), and a click on the background clears it. Space toggles follow and Esc clears the selection, when the graph has focus. Hovering shows a tooltip for the timing point nearest the cursor.
- **Train list.** One row per live train (its current working): colour, headcode, delay in minutes at the latest booked point, last timing point and time, and the WTT train or match state. It can be sorted by delay (the default), headcode or last report, filtered by text, and limited to "not matched". Rows are updated in place, so the selection and scroll position survive updates. Selecting a row selects the train on the graph, and double-clicking it centres the graph on its last report.
- **Command line.** `Stringline.exe [--wtt t.wtt] [--replay x.sslog [--speed 8|max] [--start 07:30]] [--resume s.stringline | --review s.stringline]`.
- **View options (S9).** The toolbar's Colour box (Category or Delay; a By delay toggle since S25) and ⇅ Vertical toggle, also in the View menu, are remembered in the settings. Turning the graph keeps the time and corridor ranges and follow mode.
- **Export (S9, GR-10; Fred: the current view).** File ▸ Export ▸ Graph as PNG… (Ctrl+E), or at 2× size (Session ▸ Export until S25). It saves the graph as shown: the same time and corridor range, orientation, colours, selection and now line, at the graph's size in device-independent pixels times 1 or 2. A save dialog offers `Documents\Stringline\exports\{area}-{corridor}-{HHmm}.png` at first, then the folder last used. PDF export: see §11.

## 9. Profile editor (S14a)

- **Profile items** (the Profile menu until S25, now in Tools). Edit profile… opens the editor. Open profile file opens `{area_id}.json` in the program Windows uses for JSON. Reload profile re-reads it (after a hand edit) and redraws without reloading the WTT; a damaged file is reported and the profile in use stays. Regenerate corridors… replaces the corridors with generated ones; if any are hand-tuned it asks first: Replace all, Keep mine (beside the generated ones), or Cancel (Fred's choice).
- **Editor window** (modal), with OK, Cancel and Apply. Tabs:
  - **General:** the sim ID (editable only when the timetable has none), the sim name (blank by default since S22: the tidied area ID is shown instead), and every location's display name (with the corridors it is on), plus Import names from CSV….
  - **Corridors:** the corridors in tab order (rename in place, ▲/▼ to reorder, New… from two ends, Remove, and Add for a generated corridor not in the profile). For the selected one: an axis preview (Up end on the left, stations heavier), Space by distance, the locations (name, distance, platforms; Add location… after the selected one, Remove, ▲/▼), and the sections (a layout preset or tracks as text, headway as m:ss, the WTT's minimum run).
  - **Categories:** name, colour (`#RRGGBB` with a swatch), line style, priority and the number of trains.
- Every cell edit is a `ProfileEdits` edit on a draft. One that is refused shows why under the tabs, and the cell goes back to its old value. Cancel or closing with unapplied changes asks before discarding them.
- **Apply** saves through `ProfileStore`, then rebuilds the forecast network, the corridor tabs, the train list's colours and the graph from the loaded WTT. The working plan, disruptions, notes and selection are kept. The corridor shown stays, with its view, if it is still there; a removed corridor's tab closes and the first is shown.
- **No sim ID** (Fred's choice: remember per file). A timetable without an area ID gets an unsaved `unknown` profile and says so. The editor asks for a sim ID, offering the live sim's `area_id` if connected. On Apply the profile is saved under it, and the settings remember it for that `.wtt` path (`SimIdByTimetable`), so the next load finds it. If a profile with that ID is already saved, it asks whether to use the saved one or replace it.

## 10. Saved sessions, resume and review (S16)

- **Autosave (SES-1).** Every live session and every replay saves itself to `Documents\Stringline\sessions\{area}-{yyyyMMdd-HHmm}.stringline` (`SessionWriter`, every 2 s and on close). A live session keeps the frames the engine uses; a replay refers to its `.sslog` and keeps how far it got. Both keep every plan step (edits, undo, redo; disruptions, notes and links are edits too). The file is made only once there is something to resume: the first live frame, or a replay's first edit.
- **Ended or left running.** Disconnect, or starting another session, ends the session. Closing the app (or a crash) leaves it running.
- **Start-up offer (Fred's choice).** If the most recently saved session was left running, the info bar says so ("The last session (waterloo 2026-10-19 16:39 at 06:20:00, replay of waterloo-4x.sslog, 1 plan step) was not ended. Resume it?") with Resume and Review buttons. File ▸ Resume saved session… and Review saved session… open any session file.
- **Resume.** The session's timetable is loaded (asked for if it has moved).
  - A live session is rebuilt from its frames and steps; the clock stands at its last report, and the info bar says to connect. Connect then asks: Carry on (the same engine state and file, new frames appended) or Start a new session.
  - A replay session starts its recording again, paused where it had got to, with its plan. Its recording is asked for if it has moved.
- **Review (SES-3; Fred's choices: the plan as it was at each moment, read-only).** The session replays from its start, paused, in the transport bar (a live session's own frames, or a replay's recording). As the replay clock passes each step's sim time the plan is restored to the steps made by then (`PlanLog.By`), so seeking back takes edits away again. Edits, drags, B+drag, undo and redo are refused with a message, the title ends "(review)", and nothing is written to the file. Starting any other session leaves review.

## 11. Reports and exports (S17)

- **Delays tab (SES-2; Fred: a tab beside Regulation).** The side tabs are Conflicts, Regulation, Delays and History. Above: one row per train (WTT train or clone) with its state (Not started, Running, Finished, Cancelled), the latest point it reported at and when, its delay there, its worst actual delay and where, and its final delay (at the last point it runs to; blue when that is a forecast). Sorted by worst delay by default (the worst offenders), or by final delay, latest delay, headcode or start time. "All trains" lists every train in the plan instead of only those seen. The summary reads "42 trains seen · 9 late (5+ min) · worst 2A94 +14 at Vauxhall"; live trains not matched to the plan are counted but not listed. Below: the selected train's timing points with WTT, plan, actual and forecast times and the delay against the WTT and against the plan. Picking a row selects the train on the graph, and selecting a train anywhere picks its row. Rows are updated in place, so the selection survives updates and re-sorting.
- **What the delays are (Fred: WTT, plan and forecast).** Built from `TrainSchedule`, so they agree with the detail panel (`DelayReport` in Planning). At each point the actual time, or the forecast where the train has not reported, is compared with the booked one: the departure or pass, or the arrival where there is no departure (a destination). A train standing at a stop (arrived, departure only forecast) is compared at its arrival, so its delay there is actual until it leaves. Delays are against the WTT, or against the plan for an ad-hoc copy; the delay against the plan is shown beside it. Points after a short termination have none. Whole minutes rounded towards zero, as elsewhere.
- **Refresh.** The table is worked out only while its tab is shown: at once on a new plan or a change of filter, and at most once a second for live updates alone.
- **CSV.** File ▸ Export ▸ Delay table as CSV… (or CSV… on the tab) writes one row per train and timing point; Train delays as CSV… (CSV by train…) one row per train. The trains are those the tab lists. A first line `# Sim – Timetable (vX) – as at HH:mm:ss` names the sim and timetable (#6, S22), then the header row. RFC 4180, CRLF, UTF-8 with a byte order mark (for Excel); times `HH:mm:ss` with `+1` after midnight, delays in signed seconds, `actual`/`forecast` basis columns, blank for unknown (`DelayReportCsv` lists the columns). The file name offered is `{area}-delays-{HHmm}.csv` or `{area}-train-delays-{HHmm}.csv` in the export folder.
- **PDF (GR-10; Fred: the current view, fitted to the page).** File ▸ Export ▸ Graph as PDF (A3 landscape)… (Ctrl+P), (A4 landscape)… or (view size)…. The view's time and corridor range, orientation, colours, selection and now line are laid out afresh on the page inside 10 mm margins (one point per view pixel), so text keeps its size. One vector page, with the fonts embedded and the sim, corridor and time as its title.
- **Printing (Fred: a page preset only).** A3 landscape is the default PDF page; print it from a PDF reader. There is no Print command.
- **CLI.** `stringline report (--log x.sslog | --session s.stringline) [--wtt t.wtt] [--at 07:00] [--all] [--by-train] out.csv` writes the same CSV. A session brings its plan edits as they stood at that time (a replay session reports as at where it had got to). `stringline render-png` (alias `render`) writes a PDF when the output ends in `.pdf`, with `--page a3|a4|view`.

## 12. Settings, About and packaging (S18)

- **Options window (Fred: all four groups).** Tools ▸ Options… (Ctrl+,; View ▸ Settings… until S25, #8), modal, with OK, Cancel and Restore defaults. Its tabs:
  - **Forecast and conflicts:** knock-on (the same switch as the View menu), default dwell and turnaround, "possibly stopped" time (`ForecastOptions`), platform margin and least conflict shown (`ConflictOptions`).
  - **Suggestions:** allow terminating short or cancelling (the same switch as the panel), and the platform change, line change, terminate and cancel penalties in minutes (`SuggestionOptions`).
  - **Folders:** timetables (must exist), sessions, recordings and exports. A blank box uses the default, shown under it.
  - **Logging and housekeeping:** detailed (Debug) logging, days of logs to keep (from the next start), Open log folder, and delete saved sessions last saved more than N days ago (off by default).
  - **Display** (S24, #13): the sim clock's size, Normal, Large or Huge (16, 24 or 36 px; `AppSettings.ClockSize`).
  - Values are typed as text and checked together on OK (durations as `m:ss`, minutes or `90s`, each within a range); the window stays open with the reason until they pass. Nothing changes on Cancel.
- **Applying.** The settings are saved (`AppSettings.Planning`, folders, logging, `DeleteSessionsAfterDays`). The engine takes the forecast and conflict options at once (`PlanningEngine.SetForecast(…, conflicts)`), suggestions are worked out again with the new penalties, the folders apply to the next save, and the log level changes at once (a Serilog level switch).
- **Housekeeping.** With deleting on, sessions older than the days chosen are deleted at start-up (`SessionCatalogue.DeleteOlderThan`), never the newest (so it can still be resumed), the one being written or one named on the command line. Turning it on asks first ("Delete N saved sessions … now?"); Not now leaves them until the next start.
- **Sim clock (S24, #13).** The sim time (and speed, smaller and grey) is at the right of the menu row, in bold Consolas at the size Tools ▸ Options ▸ Display sets. It went there, not on the toolbar, because the toolbar has no room to spare at 1280 px (a window test checks the clock and the toolbar's last button are both on screen at Normal and Huge). The status bar keeps its "Sim 09:14:32".
- **Help menu.** Getting started (online) opens the README; Open log folder; About Stringline. The Tools menu has Options… and the profile's items (§1).
- **About window.** The icon, version and commit (`0.1.0 (9806001)`, from the build's informational version), the notices GPLv3 §5(d) asks for (copyright, no warranty, the licence with a link, the source link), View licence (the GPL's text, embedded), Copy version (version, OS and runtime, for bug reports), the components it is built with and their licences (`AboutInfo.Credits`, which a test checks against `Directory.Packages.props` and `THIRD-PARTY-NOTICES.md`), and the SimSig disclaimer.
- **Logging.** `%LOCALAPPDATA%\Stringline\logs\stringline-yyyyMMdd.log`, one file a day, 14 kept by default. Each run starts with the version, commit, OS, runtime and whether it is the single-file build. Unhandled exceptions on any thread and unobserved task exceptions are logged. Credentials never are.
- **Icon.** A small train graph on a rounded tile, drawn by `AppIcon` (Stringline.Graph) at 16–256 px into `src/Stringline.App/Assets/stringline.ico` by `stringline icon`. Below 32 px it shows only the two stepped trains, so it does not read as an X.
- **Packaging (Fred: zip and installer).** `build/publish.ps1` publishes a single-file, self-contained `Stringline.exe` for win-x64 (native libraries inside, compressed, about 78 MB), then zips it with `LICENSE.txt`, `THIRD-PARTY-NOTICES.md`, `OFL.txt` and `README.md`. With `-Installer` it also builds `Stringline-x.y.z-setup.exe` with Inno Setup 6 (`installer/Stringline.iss`):
  - per-user by default, with no admin prompt (all users can be chosen);
  - a Start-menu shortcut, and an optional desktop one;
  - Windows 10 2004 or later, x64;
  - the uninstaller leaves user data.
- **Releases.** Pushing a `v*` tag runs `.github/workflows/release.yml` on Windows. It tests, publishes with the tag's version and makes a draft GitHub release with both files. Running it by hand builds them as workflow artifacts.
