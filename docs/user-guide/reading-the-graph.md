# Reading the graph

![A Waterloo corridor three hours across, with the actual running, forecasts and conflicts](images/graph.png)

## The main window

From left to right: the **train list**, the **graph**, and the **side panel** (the selected train's detail above, with the Conflicts, Regulation, Delays and History tabs below). Above them are the menus, the sim clock (top right), and the toolbar with the corridor tabs. The status bar is at the bottom; while a replay plays, its transport bar is just above it.

Drag the grey splitters between the panels to resize them; double-click one to hide or show the panel beside it. See [Window layout](settings.md#window-layout).

## Time and distance

Time runs across the graph, left to right, and the corridor's locations run down its left edge, with the corridor's **Up end at the top**. A train's line slopes down to the right when it runs Down, and up to the right when it runs Up. A level stretch is a stop: the train is standing at that location. The steeper the line, the faster the train.

**View ▸ Vertical** (or **⇅ Vertical** on the toolbar) turns the graph so time runs down the page, with the locations across the top, as on a train control diagram.

Each **corridor** is a tab above the graph: click one, use the **Corridor** menu, or press Ctrl+1 to Ctrl+9. Tabs that do not fit are listed under **»**. See [Profiles and corridors](timetables-and-profiles.md#profiles-and-corridors).

The location names are on the left, bold for stations with platforms. A long name is shortened with "…"; hover over it for the whole name, its TIPLOC and its platforms. The profile editor can give it a shorter name for good.

## Lines on the graph

| What you see | What it is |
|---|---|
| Thin, faint line | The **WTT**: the train as booked. |
| Medium solid line | The **working plan**, where you have re-planned a train away from the WTT. |
| Heavy solid line | The **actual** running, as the sim reported it. |
| Dash-dot line (– · – ·) | The **forecast**: where a running train is expected to go from here, and a train not yet started if it is expected a minute or more off its plan. |
| Thin grey line with crosses at the ends | A **cancelled** train. |
| Very faint line | A train SimSig will **not run** this session (see [Trains that will not run](timetables-and-profiles.md#trains-that-will-not-run)). **View ▸ Show not-running trains** hides them. |
| Grey heavy line | A live train not matched to the timetable. |
| Thick line with a halo | The **selected** train. Other trains are dimmed. |
| Red vertical line | **Now**. The past is lightly shaded. |
| Red ring and red band | A [conflict](conflicts-and-suggestions.md). |
| Red hatched box | A [block or possession](disruptions-and-notes.md). |
| Amber band | A [speed restriction](disruptions-and-notes.md). |
| Small ring with a yellow label | A [note](disruptions-and-notes.md#notes). |

Each train is drawn in its category's colour, and some categories may be dashed or dotted. A category is never dash-dot, so a dash-dot line is always a forecast. The headcode is written along the line.

The toolbar's **WTT**, **Actual**, **Forecast**, **Disruptions** and **Conflicts** boxes (also in the View menu) show and hide those layers.

### Colour by delay

**By delay** on the toolbar (View ▸ Colour by delay) colours the actual and forecast lines by how late the train is at each timing point, instead of by category:

| Colour | Meaning |
|---|---|
| Green | On time (within the minute) or early |
| Amber | 1–5 min late |
| Orange | 6–15 min late |
| Red | More than 15 min late |

![The same graph coloured by delay](images/graph-by-delay.png)

Lateness is always said as the railway says it: "On time", "3 min late" or "1 min early". The lists show it as signed minutes (`+3`, `0`, `−1`) so that they fit and sort; hover over one to have it in words.

## Moving around

| To | Do |
|---|---|
| Zoom in or out in time | Mouse wheel, about the mouse |
| Zoom along the corridor | Ctrl + mouse wheel |
| Pan in time | Shift + mouse wheel, or drag the background |
| Show 1, 3 or 6 hours across | **1h**, **3h**, **6h** on the toolbar, or the View menu |
| Show the whole corridor | **Corridor ▸ Whole corridor** |
| Keep the now line in view | **Follow** on the toolbar, or Space with the graph focused |

Panning in time stops the graph following now; press **Follow** (or Space) to go back.

## Boards: split view and board windows

You can watch more than one corridor at once. Each graph is a **board**, with its own corridor, and all boards keep the **same time**: panning, zooming, the 1h/3h/6h buttons and **Follow** on any board move them all, while each keeps its own zoom along its corridor.

- **View ▸ Split view ▸ One above the other** or **Side by side** puts a second board in the main window, with a splitter between the two. Pick its corridor in the box above it. **✕** (or **View ▸ Split view ▸ Off**) closes it; it comes back with the same corridor.
- **View ▸ New board window** opens a board in a window of its own, for example on a second monitor. Open as many as you like; **View ▸ Close board windows** closes them all.
- Every board works like the main graph: hover, click to select (the selection is the same on every board), drag [grips](re-planning.md#grips), right-click menus and B+drag. A train being dragged, or a suggestion being previewed, shows its ghost on every board whose corridor it runs on.
- The board you used last is the one that **File ▸ Export** and **Corridor ▸ Whole corridor** act on. Ctrl+Z, Ctrl+Y, Ctrl+E and Ctrl+P work in board windows too.
- Stringline remembers the split view and the board windows (their corridors and places) and opens them again at the next start. **View ▸ Reset layout** gives the two halves of the split view equal room.

## Hovering

Hover over anything for its details:

- **A train:** its headcode and description, the location, its booked, planned, actual and forecast times there, and whether it is early, on time or late. Knock-on delay from other trains and "possibly stopped" (no report for a while) are shown when they apply.
- **A conflict ring or band:** what clashes, which trains, where and when, the delay needed to clear it, the headway used, and the best suggestion. See [Conflicts](conflicts-and-suggestions.md).
- **A block or speed restriction:** its section, tracks, times and effect, and its note.
- **A location name:** its whole name, TIPLOC and platforms.
- **A grip** on the selected train: what dragging it does, or why it cannot move. See [Grips](re-planning.md#grips).

## The train list

The list on the left has one row for each train seen in the sim: its colour, headcode, **Delay** (in minutes, at its latest booked point), where it was **Last at** and when, and its **WTT** match.

- **Sort** by delay (the default, the latest-running trains first), headcode or last report.
- **Filter** by typing part of a headcode, location or train.
- **Not matched only** lists the trains not matched to the timetable (in grey italics).
- Click a row to select the train on the graph; double-click to centre the graph on its last report.
- Under **Not running**, after the trains in the sim, are the timetable's trains that SimSig will not run this session, with the reason in **Last at** and their booked start in **Time**. See [Trains that will not run](timetables-and-profiles.md#trains-that-will-not-run).

## The status bar and sim clock

The **sim clock** is at the top right, with the sim's speed. [Tools ▸ Options ▸ Display](settings.md#display) makes it Large or Huge.

The status bar shows, left to right: the connection (green when connected), the sim and timetable, the sim time and speed, the number of trains (and how many are not matched), the number of conflicts, and what the last edit or drag did. "Recording" shows at the right while you [record](sessions-and-review.md#recording).
