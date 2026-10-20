# 08 — Timetable Editor (issue #20)

*Draft 1, 21 October 2026, from issue #20 and the design review with Fred. The sessions are Phase 4d (S40–S48) in `07-implementation-plan.md`.*

## 1. Why, and what it is not

SimSig's own editor (F4 ▸ Timetables ▸ Edit) enters one train at a time through several dialogs, and community timetable writers work around it with spreadsheets and importers. Issue #20 asks Stringline to be a better place to write and change timetables. SimSig remains the judge of whether the result is valid: the gateway has no command to send a timetable to SimSig (`00-…md` §2), so a saved `.wtt` is loaded in SimSig, checked with its Timetable Analyser (F4 ▸ Tools 1), and then run.

What Stringline brings that F4 does not:
- a **keyboard-first grid** for a train's timing points, and lists of trains, rules and seed groups that can be sorted, filtered and edited many at a time;
- the **train graph of the draft**, with its booked conflicts (headway, platform, single line), and drag grips (S26) that retime a path;
- **bulk tools**: repeat a train as a clockface pattern, auto-fill timings from learned run times, shift, renumber, and find and replace;
- **checks before SimSig sees the file**: the analyser's rules that can be checked offline, plus checks against what other timetables and recordings of the same sim show;
- **from control to timetable**: a session's working plan (S10–S20) can become a draft;
- a **test loop**: run the saved timetable in SimSig with Stringline connected, and get a report of what went wrong.

Out of scope: reading SimSig's simulation data. `.sim` files are encrypted zips (`header.dat`, `sim.dat`), so Stringline never reads, decrypts or ships them. There is also no route-setting logic, so Stringline cannot prove a path exists. It can only say whether one has been seen.

## 2. Decisions (Fred, 2026-10-21)

| Topic | Decision |
|---|---|
| UI | Grid plus live graph: a Timetable editor workspace with the trains list, train details and trip grid, beside a graph board of the draft. Drag on the graph edits the draft. |
| Fidelity | **Lossless.** The editor edits the XML document itself. Everything it does not model is written back untouched. A save with no edits is semantically identical (§3.3), tested on the whole corpus. |
| Coverage | Everything: trains and trips; activities and rules; seed groups and categories; the header; Decisions, Class of Service, Scenarios and Driving (caution speed sets) as structured lists. |
| Order | Phase 4d runs after Phase 4c, S32 and S33. It uses S37's learned signals as seed points and checks, and S32's rule handling for the draft's graph. |
| Plan → timetable | Yes: Timetable ▸ New draft from working plan. |
| Bulk tools | All four: repeat as pattern, auto-fill timings, multi-train edits, and a spreadsheet round trip. |
| Saving | A new version: the build number goes up on every save, and the original is never overwritten without asking. Backups are kept. Details in §6. |
| Live use | Allowed, on a separate draft. The session keeps the timetable SimSig is running. The draft has its own board and never becomes the session's plan. |

## 3. Document model (lossless)

### 3.1 `WttDocument` (`Stringline.Timetable.Editing`, portable)
- It holds the parsed `SavedTimetable.xml` as an `XDocument`, with no whitespace and no comments, as `WttReader` reads it. It also keeps the zip's other entries as bytes, which are rewritten as they are.
- **Typed views** read and write through to their elements: `HeaderNode`, `CategoryNode`, `TrainNode`, `TripNode`, `ActivityNode`, `RuleNode`, `SeedGroupNode`, plus generic `ListNode`s for `Decisions`, `TTClassOfServiceRules`, `ScenarioOptions` and `CautionSpeedSets`. A property the editor does not know about is never touched.
- **Value conventions** match the file: booleans are written in the style the file already uses (`True`/`False`, or Delphi `-1`/`0`, `04-…md` §1). A value set back to its default is removed, as SimSig omits defaults. A new element goes where SimSig puts it in the parent's element order, which is taken from the XML format page and the corpus.
- **`ToWorkingTimetable()`** runs the existing `WttReader` import over the in-memory XML. Import, matching, corridors, forecast and graph code then all see a draft exactly as they would see the saved file.

### 3.2 Edits
- Each edit is an immutable `TimetableEdit` with apply and undo: set a field, insert, remove or move a node, or a composite. The draft's `TimetableEditor` is the single writer (like `PlanningEngine`): it applies edits in order, keeps undo and redo, and publishes an immutable `DraftSnapshot` (document version, `WorkingTimetable`, validation result). The UI and graph only ever read snapshots.
- Train identity in the editor is the `TrainNode`'s element, not a headcode, so renumbering keeps the selection and the links (activities and rules that name the train are updated in the same composite edit, by `$UID` where the file uses UIDs).

### 3.3 Writer and round trip
- `WttWriter` writes a zip holding `SavedTimetable.xml` and a regenerated `TimetableHeader.xml` (the `[TH]` fields: root `ID` and `Version`, `Name`, `Description`, `StartTime`, `VMajor`/`VMinor`/`VBuild`). The XML is UTF-8 with no BOM and no declaration, as SimSig writes. Saves are atomic (temp file, then move).
- **Round-trip test (corpus):** open → save → open for every `.wtt` under `STRINGLINE_WTT_CORPUS`. The two XML trees must be equal element by element (names, order, text, attributes), and `WttReader` must give equal timetables. Byte equality is not required: zip compression and attribute quoting may differ.
- 👤 Fred loads a round-tripped Waterloo and one payware timetable in SimSig and runs the Analyser once (S40's gate). The output must match the original's.

## 4. Sim knowledge (`SimKnowledge`, portable)

Built per sim (area ID) from:
1. every `.wtt` for the sim in the timetable folders (`WttCatalogue.FindForArea`);
2. recordings and sessions (reported TIPLOCs and entry signals);
3. the profile: S37's learned signals, track circuits and berths, and the corridors.

It holds:
- **locations:** TIPLOCs seen; those used as entry points or seed points; stations (where trains stop); key locations (where activities happen); reversing locations;
- **per location:** the platforms, lines and paths seen, with counts;
- **links:** consecutive timing points as booked (A → B with B's path and A's line), with counts and the trains' direction. Skip chains (A–B–C where others book A–C) come from `LocationGraph` (`04-…md` §4.2);
- **run times:** the least and usual run time per link, by speed band (category `MaxSpeed`) and freight flag, net of allowances (as `CorridorMeasurement`); usual dwells per station and category;
- **sources:** the files and recordings it was built from, with timestamps. It is cached (`{area}.simdata.json` next to the profile) and rebuilt when a source changes. The draft being edited is excluded, so a draft cannot vouch for itself.

`stringline simdata --area waterloo [--folder f] [--location WIMB]` prints it.

## 5. Checks (`TimetableValidator`, portable)

Each issue is `ValidationIssue(Severity, Code, Train?, PointIndex?, Message, Hint, QuickFix?)`. The severities are Error (SimSig will refuse it or the train will fail), Warning (likely wrong) and Info.

**From SimSig's Analyser (F4 ▸ Tools 1)**, every check that can be made offline, using SimSig's wording so users recognise it:
- **times:** departure earlier than arrival; a time earlier than the previous location's; reverses with "passing time" ticked; a minor location out of order;
- **links:** associated train not defined; next train already used; divided portion already used; next not the final activity; associated train at a different location or platform; joining train lost;
- **trains that never run:** never used (no entry point and nothing refers to it); never runs, referred to by X;
- **endings:** terminates with no onward working (unless "berths here"); no further action after joining;
- **lengths:** under 8 m; a divided remainder under 8 m; length differs from the next working's;
- **traction:** no traction power;
- **seeding:** an arrival time at the seed start location; two trains seeded at one signal in one seed group;
- **trips:** no trips (fewer than two locations, or entry plus one).

**From sim knowledge** (Warnings, because absence of evidence is not proof):
- a platform, line or path code never seen at that location in any timetable for the sim ("Invalid platform, line code or path code" in SimSig);
- a TIPLOC never seen for the sim;
- **no known path:** a link A → B never booked in any timetable for the sim. SimSig may say "no path between timing points". The quick fix inserts the most-booked chain between them;
- **ambiguous line:** several lines leave A for B in other timetables and none is given. The quick fix sets the most used line;
- a run shorter than any seen for that link and speed band; a stop at a location where no train ever stops ("stops at non-station");
- an entry point not seen as one, or a seed point that is not a known signal;
- activities at a location where no timetable has them ("not safe to have activities").

**Rules:** a rule naming no train or several; a cycle; a `Location` the rule's train does not call at.

Checks run per train as it changes (incrementally), and in full on open and save. `stringline wtt-check t.wtt [--knowledge-folder f] [--severity warning]` prints them. A corpus baseline (released timetables should give few Errors) is recorded in `progress.md`, and checks that misfire on released timetables are fixed or demoted.

## 6. Saving and versions

SimSig's guidance (wiki: *Filenames, versions, titles and descriptions*; *Timetable filenames*):
- the version is `VMajor.VMinor.VBuild`, each 0–65535, and must always go up;
- builds count test versions (1.0.1, 1.0.2…), and a release raises the minor or major number and resets the build to 0;
- the version is never in the file name or the title;
- minor amendments keep the same file name;
- new timetables are named `Sim name YYYY-MM-DD [tags].wtt`.

So:
- **Save** raises `VBuild` by one and writes to the draft's file. The first save of a draft opened from a SimSig timetable asks whether to replace that file or save as a new one. The default is a new one in the same folder, named by the convention with a tag (e.g. `Waterloo 2010-05-01 Stringline.wtt`), so the shipped timetable is untouched. Replacing needs a confirmation.
- **Save As** chooses a folder and name. The default folder is the sim's SimSig Timetables folder, so the Loader lists it.
- **Release version…** raises the minor or major number and sets the build to 0.
- **Backups:** before any file is replaced, it is copied to `%LOCALAPPDATA%\Stringline\timetable-backups\{area}\{name} {version} {time}.wtt`, outside SimSig's folders so the Loader never lists them. The last 20 per timetable are kept.
- **Drafts autosave** to `Documents\Stringline\drafts\` as `.wtt` plus a small `.draft.json` (undo history and the source path), so a crash loses nothing and a draft can be reopened later.
- A save is blocked only by Errors that would make the file unreadable (no area ID, duplicate category IDs). Other Errors ask for a confirmation and are listed in the save dialog.

## 7. UI

- **Timetable menu** (between Plan and Tools; access keys checked by S25's test):
  - New draft ▸ from the loaded timetable / from a file… / blank for this sim / from the working plan;
  - Open draft…; Save (Ctrl+S in the editor window; the main window has no Ctrl+S); Save As…; Release version…; Close draft;
  - Check timetable; Test in SimSig… (§9);
  - Export trains ▸ CSV…; Import trains…
- **Editor workspace** (a board-style window like S21's `BoardWindow`, so it can sit on a second monitor):
  - **Trains:** a list sortable and filterable by headcode, UID, entry time and place, category, seed group and issue count. Multi-select works for bulk tools.
  - **Train details:** ID, UID, description (or `$template` with a preview), category, entry point and time, or seed point, gap and group; as required %, flags, origin and destination, operator and notes.
  - **Trip grid** (one row per timing point):
    - columns: location, with completion from sim knowledge (the name from the profile); arrival; departure or pass; SimSig notation (`12:30`, `12/34`, `12d34`, `12t34`, `12w34`, `12r34`); platform, line and path, with drop-downs of the codes seen, most used first; engineering and pathing allowances (minutes, `H` for a half); dwell; request %; set down; thru line; stop location; new category; activities (as `N: 2B44`, `DR: …`);
    - **keys:** typing `1234` gives 12:34 and `1234H` gives 12:34½; `+3` adds 3 min to the previous time; Enter moves down; Insert adds a row and Ctrl+Delete removes one; Ctrl+D fills down; Ctrl+Z and Ctrl+Y undo and redo;
    - auto-inserted points are shown greyed. Rows with issues are marked, and the hover gives the message and the quick fix.
  - **Other tabs:** rules, seed groups (with "referenced by" and "unassigned"), categories (train types and dwell times), header, and structured lists for Decisions, Class of Service, Scenarios and Driving.
  - **Issues panel:** every issue, filterable. A click goes to the train and row, and quick fixes can be applied singly or to all of one kind.
- **Draft board:** a graph board of the draft over the profile's corridors. It shows the booked layer only, with booked conflicts (the forecaster run with no live data, rules included as in S32). Optional ghosts show the original timetable and the actuals from a recording. The selection is shared with the grid. S26's grips edit the draft: retime from here, dwell, and shift the whole path. A drag gives one composite edit, clamped to the learned minimum run time, with a warning, not a block.
- **Live:** with a session open, the draft is a separate document and its board is labelled "Draft – *name*". Opening the draft from the loaded timetable copies it, so the session's WTT never changes.

## 8. Bulk tools (portable core, each one undoable edit with a preview)

- **Repeat as pattern:** copy one or more trains every N minutes between two times. The headcode follows a rule (e.g. `2A01`, +2 each). New UIDs follow a pattern, and next workings are paired at a terminus by the least turnaround that meets the dwell. The preview lists the new trains and draws them as ghosts.
- **Auto-fill timings:** given the times at some points, fill the rest from sim knowledge. The least run time for the link and the train's speed band, plus allowances, gives each pass time, and the usual dwell applies at stops. If the times given cannot be met, the shortfall is shown. **Insert timing points** adds the chain other timetables book between two points (the "no path" fix), and **set line** fills an ambiguous line.
- **Multi-train edits:** shift times (all, or from a location on); renumber; change category, platform, line or path at a location; set or clear allowances; find and replace over headcodes, UIDs, descriptions and codes. Links are kept.
- **Adjust times** works like SimSig's (whole minutes or `H`; 00:00 times are skipped).

## 9. Spreadsheet round trip, plan → draft and testing

- **CSV:** one row per timing point, with train fields repeated on its first row, in a documented column set (`docs/user-guide`). Export writes UTF-8 with a header. Import is tolerant (header aliases, quoting, half minutes, SimSig notation). It merges by UID, else by headcode and first time, or adds new trains. It shows what will change before applying, as one edit. An `.xlsx` export and import may follow if a GPL-compatible library is used (DocumentFormat.OpenXml and ClosedXML are MIT; checked when added).
- **From the working plan:** a copy of the loaded WTT, plus the plan's effect on each train as edits:
  - retimes become times;
  - platform and line changes become platform and line codes;
  - a skipped stop becomes a pass;
  - terminate short removes the later points, and the next working's link is flagged;
  - cancellations remove the train, or keep it with a "never used" warning (the user picks);
  - clones become new trains with new UIDs.
  - Disruptions are not timetable data and are listed as notes. The checks then run, so broken links show at once.
- **Test in SimSig:**
  - The dialog gives the steps: save; in SimSig, load the timetable and run F4 ▸ Tools 1 ▸ Timetable Analyser; then start the sim, with Stringline connected and recording.
  - **Timetable test report** (`stringline report --test`, and in the app at the end of a session run on a draft's timetable). It lists:
    - trains that never appeared (by `ttsummary` status, or no sighting past their entry time plus a margin);
    - unmatched workings, which often mean a path or link SimSig would not take;
    - sections where trains always ran slower than booked, with the time to add;
    - platform clashes and booked conflicts that happened.
  - The report opens in the draft's Issues panel against the trains.

## 10. Where the code goes

| Part | Project |
|---|---|
| `WttDocument`, nodes, `TimetableEdit`, `WttWriter`, SimSig time notation | `Stringline.Timetable` (namespace `Editing`) |
| `SimKnowledge`, `TimetableValidator`, bulk tools, CSV, plan → draft, test report, `TimetableEditor` (single writer) | **`Stringline.Editing`** (new, `net10.0`, references Timetable, Network and Planning; tests in `tests/Stringline.Editing.Tests`, added to `Stringline.Portable.slnf`; the architecture tests updated) |
| Draft board scene (booked layer, ghosts, grips as edits) | `Stringline.Graph` (a drag target abstraction, so `PlanDrag` serves plan commands and draft edits) |
| Editor workspace, menu and dialogs | `Stringline.App` (view-models without WPF types, as now) |
| `wtt-save`, `wtt-check`, `simdata`, `wtt-csv`, `report --test` | `Stringline.Tools` |
