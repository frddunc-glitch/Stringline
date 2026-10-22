# The timetable editor

Stringline can change a SimSig timetable, or write a new one, in a window of its own: the **timetable editor**. You work on a **draft**, a copy of the timetable that is autosaved as you go, and save it as a timetable file when you are ready. SimSig is still the judge of whether the timetable works: load the saved file in SimSig and run its **Timetable Analyser** (F4 ▸ Tools 1) before you play it.

The editor never changes the timetable a session is running. A draft is always its own copy, read again from the file, so you can edit while you play.

## Making a draft

Everything is on the **Timetable** menu:

- **New draft ▸ From the loaded timetable** copies the timetable the session uses.
- **New draft ▸ From a file…** copies any `.wtt`.
- **New draft ▸ Blank for this sim** starts an empty timetable for the sim loaded or connected, with the loaded timetable's train categories.
- **Open draft…** opens a draft you worked on before, with its undo history.

One draft is open at a time. Opening another asks about the first one's unsaved changes.

## The trains list

The list on the left has every train in the draft. Click a column's heading to sort by it. The filter box finds trains by headcode, UID, entry time or place, category or seed group, and **With issues** shows only trains with Errors or Warnings. The dot at the start of a row is the colour of the train's worst issue: red for an Error, orange for a Warning, grey-blue for a note. Hover over it to read them.

**New…** adds a train after the selected one, **Copy…** copies it with a new headcode (and no UID, as UIDs must be unique), and **Delete…** deletes the selected trains. Ctrl-click and Shift-click select several.

## The train and its timing points

The **Train** tab shows the selected train's details at the top. Change a box and press Enter, or move on, to make the change. A new headcode renumbers the train and the activities and rules that name it. A description of `$template` shows what SimSig will show, from the header's template.

Below is the **trip grid**, one row for each timing point:

- **Location**, with completion from the TIPLOCs the sim's other timetables use.
- **Arr** and **Dep/pass**, in SimSig's notation: type `1234` or `12:34`, `1234H` for 12:34½, `12/34` for a pass, `12d34` set down only, `12t34` may stop on the through line, `12w34` waits for its booked time, `12r34` a request stop, and `+3` for 3 minutes after the time before. Times after midnight go on past 23:59 (`24:10`).
- **Plat**, **Line** and **Path**, with the codes other timetables use at that location, most used first.
- The engineering, pathing and performance allowances in minutes (`1H` is 1½), the dwell in seconds, the request stop's chance, set down, the through line, the stop location and a new category.
- **Activities**, such as `N: 2B44` for a next working.

Enter moves down a row. **Insert** adds a point above the current one, **Ctrl+Delete** removes the selected points, and **Ctrl+D** fills the current column down: the first selected row's value goes into the others, or, with one row selected, the value above comes into it. **Ctrl+Z** and **Ctrl+Y** undo and redo, as many steps as you like, and **Ctrl+S** saves.

Points shown grey were added by Stringline, by a quick fix. Edit one to keep it as yours.

### Activities

Under the grid are the **activities** of the point the grid is on: next workings, joins, divides, engine and coach detaches, platform shares and crew changes. Pick the kind and the train it is with, then **Add activity**. The trains offered first are those booked at that location near that time (for a next working or a divide, a train that starts there comes first); you can also type a headcode or UID. A train is named the way the timetable already names trains: by UID in a timetable that uses UIDs, else by headcode. Change an activity's kind, train, crew change time or duration in its row; ▲ and ▼ reorder them, **Delete** removes one and **Go to train** shows the train it is with. A train another train forms says so under its details ("Formed by 1A01 at WOKING (next working)").

A next working must be the last thing a train does, as SimSig needs: it can only go at the train's last location and after its other activities there (alternative next workings, chosen by a decision, may follow one another). An edit that breaks this is refused, and the status bar says why.

## The header

The **Header** tab has the timetable's name, description, start and finish times and the train description template. The version is not typed: saving raises it.

## Rules

The **Rules** tab lists the timetable's rules (such as "X appears after Y leaves"), with train X, train Y, the location Z for the rules that have one, and the time in minutes (`H` for a half: `0H`, `2`, `25`). Change any of them in the row. To add one, choose the rule, pick or type X and Y, give Z and the minutes, and press **Add rule**; **Delete rule** removes the selected one. Tick **Only rules about the selected train** to see the rules naming the train chosen in the list. A rule the checks have a problem with (a train it names that is not in the timetable, or rules that wait on each other) is marked, with the reason in its Issues column.

## Seed groups

The **Seed groups** tab lists the other times the sim can start at, each with its title, start time, the trains seeded in it and any seed point two of its trains share (SimSig puts one train at a signal). **New seed group…** asks for a title and time; put a train in it by giving it the group's ID on the Train tab. Under the list are the trains seeded at the timetable's own start. **Delete seed group** asks first, naming its trains: they are then seeded at the timetable's start. The header's summary of the groups, which SimSig's loader shows, is kept up to date.

## Categories

The **Categories** tab lists the train types: ID, description, freight, goods lines, maximum speed, length, acceleration and braking, power to weight, electrification, caution speed set, speed classes and how many trains use each. Under the list are the selected category's dwell times and its speed classes as tick boxes. Each sim names its own speed classes, which the timetable does not hold, so they are numbered here. **New category…** copies the selected one (or uses SimSig's usual values) with a new ID. A new ID typed in the ID column goes to every train and point that uses the category. **Delete category…** asks which category its trains move to first.

## Decisions, Class of Service, Scenarios and Driving

The **Lists** tab edits the rest of the timetable as lists: **Decisions** and their choices (ID, description or announcement, weight, chosen), **Class of Service** rules, **Scenario options** (an option and its value) and **Driving**, the caution speed sets and their speeds. What they hold that is not shown is kept as it is.

## Bulk tools

The **Bulk** menu works on the trains selected in the list (Ctrl+click and Shift+click to pick several, Ctrl+A for all), or the current train. Each tool asks what to do, then lists what it would change, with the changed and new trains drawn faintly on the draft board, and does it only when you press **Apply**. A tool's whole change is one step for Undo.

- **Shift times…** moves the trains' times by whole or half minutes (`+5`, `-2H`), the whole train or from a location on, as SimSig's Adjust times does: a time of 00:00 is left alone.
- **Renumber…** gives the trains headcodes counting on from one you give (`2A01`, then `2A03`… counting by 2). Activities and rules that name them follow.
- **Change category…**, **Platform, line or path at a location…** and **Allowances…** set those for every selected train (a blank code or 0 minutes clears it).
- **Find and replace…** replaces a whole value in headcodes, UIDs, descriptions or the trips' locations and codes.
- **Repeat as pattern…** copies one train every so many minutes up to a last start, its headcode counting on (and its UID, if that ends in a number). Its next working and other links are not copied:
- **Pair next workings…** joins trains that end at a terminus to trains that start there, each to the first that leaves at least the turnaround you give after it arrives.
- **Auto-fill timings** times the points a train has no time for, from the run times the sim's other timetables book for trains of its speed: points between two times share the run as the sim does, and points after the last time are timed on, with the usual dwell where there is a platform. If the times you gave leave less than the sim's quickest run, it says how much short.
- **Set lines** gives points with no line the line other timetables use most there (where two are used as often, it leaves the point for you).
- **Insert timing points** adds, for the selected trains, the points other timetables book between two points SimSig may find no path between (the Issues panel's quick fix, for all of them at once).

## The Issues panel

Under the train is every issue the checks found, Warnings and Errors at first (choose **All** for the notes too). The checks are the ones SimSig's Analyser makes that can be made without the sim, in its words, and checks against what the sim's other timetables, your saved sessions and its profile show, such as a platform no timetable uses. Those can only say what has not been seen, so they are Warnings or notes, never Errors. The status bar says how much they were checked against.

Click an issue to go to its train and point. **Apply fix** makes an issue's quick fix (for example, inserting the timing points other timetables book between two locations), and **Fix all of this kind** makes every one of that kind as one step. Undo takes a fix back like any edit. **Timetable ▸ Check timetable** (F7 in the editor) checks everything again.

## The draft on the graph

Under the trip grid is the **draft board**, a train graph of the draft. Pick its corridor above it. The corridors are the sim's profile's. If the sim has no profile yet, they are made from the draft for the board only, and the line under the board says so.

The board shows the draft as booked, with no live trains and no now line. Booked conflicts are ringed in red, as a session shows them: headways, platforms, single lines, and runs faster than any the original timetable books. Stringline checks the draft against the timetable it was made from. Trains you have not changed may stay as close as that timetable books them, as SimSig's released timetables do. Trains you retime or add are held to the headways and platform margins. The line under the board counts the conflicts and names the trains no corridor draws.

The board and the grid share their selection. Click a train on the board and the trains list and trip grid go to it, at the point you clicked. Pick a train or a row in the grid and the board picks the train and that point's grip.

Drag the grips to retime the draft, as on a session's graph:

- a **square** at a timing point retimes the train from there on;
- the **bar** at the end of a stop changes the departure, so the dwell;
- the **round handle** on the line moves the whole run, entry time too.

Times move in half minutes, as SimSig books them. A drag stops where the run would be faster than the sim is known to run it. Stringline takes the least run time it has seen for a train of that speed and kind, in the sim's other timetables and your play, else the least the original timetable books. Hold **Shift** to drag past it, and the line under the board warns you. Click a grip and the arrow keys nudge it, 30 s at a time (Shift: a minute). Each drag or nudge is one edit, so **Ctrl+Z** takes it back.

**View ▸ Draft ghosts** draws, faintly:

- **Original timetable**: where the trains you changed or deleted ran in the timetable the draft was made from (on at first);
- **Recording…**: where a recording's or saved session's trains really ran, matched against the draft. No session is needed. **Clear recording** takes them off.

**Draft ▸ Export graph** saves the board as a PNG or PDF, or a plot of several pages, titled "Draft – *name* *version*".

## Saving and versions

Stringline follows SimSig's advice on file names and versions:

- **Save** (Ctrl+S) raises the build number (1.0.2 to 1.0.3). The first time you save a draft made from a timetable, Stringline asks whether to save it as a new file beside the original (the default, named like `Waterloo 2010 Stringline.wtt`, with the date from the original's name) or to replace the original, which it asks you to confirm.
- **Save as…** saves to a file you choose.
- **Release version…** raises the minor or major number and sets the build back to 0 (1.0.3 to 1.1.0, or 2.0.0).
- Before a file is replaced, a copy of it goes to `%LOCALAPPDATA%\Stringline\timetable-backups`, where SimSig's Loader does not list it. The last 20 of each timetable are kept.
- If the checks found Errors, the save lists them and asks before saving. Only a timetable SimSig could not read at all (no sim ID) cannot be saved.

## Drafts and autosave

A draft is saved to `Documents\Stringline\drafts` a moment after each change, with its undo history, so a crash loses nothing. If Stringline stopped with a draft open, it offers it back at the next start.

**Close draft**, or closing the editor's window or Stringline, asks about changes not saved to a timetable file: save them, keep the draft to open again later, or discard the draft.
