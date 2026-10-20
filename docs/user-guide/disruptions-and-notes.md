# Disruptions and notes

Add the sim's failures and restrictions to the plan, so that the forecast and conflicts take them into account. Like every edit, they can be undone, and they are listed in the History tab.

## Blocks and possessions

A **block** (or possession) closes a section, or some of its tracks, for a time. A train planned through it is held until the block ends, and shows a **BK** [conflict](conflicts-and-suggestions.md#kinds-of-conflict). It is drawn as a red hatched box over the section and time, labelled with its tracks ("Block · Down").

To add one, either:

- **right-click the graph** where the block is and choose **Add block/possession here…**. The dialog is filled in with the section around the click, from that time (rounded down to 5 minutes) for an hour; or
- **hold B and drag** across the graph to draw it: the draft snaps to the locations and to 5-minute steps, and the status bar gives its span. Release to open the dialog, filled in from the draft; Esc drops it.

## Speed restrictions

A **speed restriction** makes trains run more slowly through a section for a time. It is drawn as an amber band labelled with its effect ("SR ×1.5 +30 s"). Right-click the graph and choose **Add speed restriction here…**.

## The disruption dialog

| Box | What to enter |
|---|---|
| From, To | The locations at each end (either way round). The same location in both makes a disruption at that one location. |
| Track | **All lines**, one of the section's tracks ("Down (Down trains)", "Up (Up trains)", "Single (both ways)"), or a WTT line code. |
| From time, Until | When it starts and ends. |
| Factor, Added minutes | For a speed restriction: how much longer the run takes (1.5 is half as long again) and any time added on top. It must make running slower. |
| Note | Anything to remember about it (the fault number, say). |

## Changing or removing one

Right-click a block or restriction for **Edit…** and **Remove**. Hover over it for its details. To move or resize one, edit it in the dialog.

## Notes

A **note** is a reminder pinned to a location and time ("Driver's PNB here", "Points failure reported"). It is drawn as a small ring with the first line of its text in a yellow box; hover for the whole note.

- Add one: right-click the graph and choose **Add note here…** (at the nearest location, at the minute clicked).
- Change or remove one: right-click it for **Edit note…** and **Remove note**.

A note can be up to 500 characters long.

## Showing and hiding

The toolbar's **Disruptions** box (View ▸ Show disruptions and notes) hides or shows them all. Hidden disruptions still affect the forecast and conflicts.
