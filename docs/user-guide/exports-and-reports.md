# Exports and reports

## The Delays tab

The **Delays** tab is a punctuality report for the session.

**Above:** one row per train, with its **State** (Not started, Running, Finished, Cancelled), the **Latest** point it reported at and its delay there (**Late**), its **Worst** delay and where (**At**), and its **Final** delay at the end of its run (blue when that is still a forecast).

- **Sort** by worst delay (the default), final delay, latest delay, headcode or start time.
- **All trains** lists every train in the plan, not only those seen so far.
- The summary line reads, for example, "42 trains seen · 9 late (5+ min) · worst 2A94 +14 at Vauxhall".

**Below:** the selected train's timing points, with the WTT, plan, actual and forecast times, and its delay against the WTT (**vs WTT**) and against your plan (**vs plan**).

Delays are in whole minutes against the WTT (against the plan for an ad-hoc train), as everywhere in Stringline. Picking a row selects the train on the graph, and selecting a train anywhere picks its row.

## Exporting the graph

**File ▸ Export** saves the graph **as you see it**: the same time and corridor range, orientation, colours, selection and now line.

| Item | Saves |
|---|---|
| Graph as PNG… (Ctrl+E) | A picture at the graph's size on screen |
| Graph as PNG at 2× size… | The same, twice as big, for sharper printing |
| Graph as PDF (A3 landscape)… (Ctrl+P) | A vector PDF on an A3 page, ready to print |
| Graph as PDF (A4 landscape)… | The same on A4 |
| Graph as PDF (view size)… | A PDF page the size of the graph on screen |

The PNG and PDF have a title band naming the sim, timetable, corridor and time. To print, open the PDF and print it from your PDF reader. With the [split view or board windows](reading-the-graph.md#boards-split-view-and-board-windows), the graph exported is the board you used last.

## Plotting a train control diagram

**File ▸ Export ▸ Plot (multi-page PDF)…** prints a corridor over a stretch of time as a printed train control diagram: at **one scale on every page**, time running **down** the page, with the location names across the top of each page. Choose:

| Box | What |
|---|---|
| Corridor | Which corridor (the board you used last at first) |
| From, To | The stretch of time, such as 06:00 to 18:00. A time after midnight takes the next day, or add `+1` |
| Minutes a page | How much time each page covers. Two hours a page gives about 3 mm a minute on A3 |
| Paper | A3 or A4, portrait or landscape |
| Time runs | Down the page (the usual way for a printed diagram), or across |

It makes as many pages as the time needs (up to 100), each headed with the sim, corridor, its times and "page 2 of 6"; the last page is cut short rather than stretched, so the scale never changes. The plot shows the layers you have turned on, with no train selected. Stringline remembers your choices for next time.

Exports are offered in `Documents\Stringline\exports` at first, then in the folder you last used.

## CSV files

For a spreadsheet:

- **File ▸ Export ▸ Delay table as CSV…** (or **CSV…** on the Delays tab): one row per train and timing point.
- **File ▸ Export ▸ Train delays as CSV…** (or **CSV by train…**): one row per train.

They hold the trains the Delays tab lists. The first line names the sim, timetable and time; then come the column headings. Times are `HH:mm:ss` (with `+1` after midnight), and delays are in seconds (negative when early). The files open directly in Excel.

## From the command line

Stringline's source includes a command-line tool, `stringline`, which can draw a graph to PNG or PDF (`render-png`, and a multi-page plot with `--plot`) and write the delay CSV (`report`) from a recording or a saved session, without the app. It needs the .NET SDK; see the [README](https://github.com/frddunc-glitch/Stringline#for-developers).
