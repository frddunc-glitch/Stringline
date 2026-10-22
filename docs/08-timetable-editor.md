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

### 3.4 Built in S40
- **The writer copies SimSig's layout and escaping**, not just its tree (`04-…md` §1, "How SimSig writes it"): two-space indent, CRLF, a CRLF at the end, `<DwellTimes/>`, the five entities and `&#x00BD;`-style references for everything outside printable ASCII. So a save with no edits is **byte for byte** for 271 of the 285 corpus files (Waterloo and Llangollen included), and the regenerated header equals SimSig's. The other 14 hold raw line breaks from hand edits and differ only in how a line feed is written.
- **SimSig's element order and defaults come from the corpus** (`WttSchema`): one order per element, which no file breaks; trains, trips, activities and rules leave out no and 0; categories, the header, seed groups and lists write every value.
- **`TimetableEdit.Change(node, n => …)`** runs a node's typed setters on a copy and turns what changed into field edits of the original, so one code path (the setters) knows the conventions, and the elements keep their identity. Adding or removing repeated items (trips, activities) is `Insert`/`Remove`, never `Change`.
- **Primitive edits:** set a field, set attributes, insert (at an index or in SimSig's order), remove, move, composite. `Apply` returns the undo, whose `Apply` returns the redo; the undo restores the same elements in the same places, including a self-closing element.
- **`WttDocumentImport`** maps each imported train, timing point, activity and rule back to its element (the reader records where each came from). **Renumber** uses it: it changes only the activities and rules that resolve to *this* train, by headcode or UID, whichever they hold (Fred: headcodes repeat, so other trains with the same headcode keep their links).
- **Versions:** a file with none is taken as 1.0.0, so its first save is 1.0.1 (Fred). Backups go to `{folder}\{area}\{name} {version} {yyyyMMdd-HHmmss}.wtt`, the newest 20 kept.
- **`SimTimeNotation`** is in Core (the grid in the App and the CSV in Editing both need it). A clock time more than 12 h before the previous time is taken as the next day's.

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

### 4.1 Built in S41
- **Bands** (Fred): a train's maximum speed (its own, else its category's) in fixed bands, up to 50, 51–75, 76–90, 91–100, 101–125 and over 125 mph (plus unknown), each passenger or freight (`RunBand`, written `76-90` or `51-75 freight`). Category IDs are random per timetable, so **dwells are keyed by station and band** too (Fred), the same key as run times.
- **Run times** are from the departure or pass at A to the arrival or pass at B, less A's allowances, as `CorridorMeasurement` measures them; runs of zero or less are left out. Consecutive points at one location are one call. "Usual" is the median (the lower middle), "least" the fastest. When a band has none, `NearestRunTime` and `NearestDwell` use the same freight flag at the nearest speed (the slower of two equally near), then the other flag.
- **Dwells** are learned at intermediate stops with no activity (a next working or join books its own wait).
- **Roles:** timing (booked in a timetable), station (a booked stop), key (an activity), reversing (out and back, or a next working that goes back the way it came), entry, seed, and reported (in play).
- **Links** are directed (A → B is not B → A), with A's line and B's path counted, and Down or Up from SimSig's direction flag at B (else A). **Skip chains** are `LocationGraph`'s expansions over all the sim's timetables at once. `Chain(A, B)` gives a skip chain with those ends (either way round), else the fewest booked links from A to B, the most booked on a tie: S42's "no known path" fix and S46's insert timing points.
- **Play:** a reported location is a timing location if some timetable books it, it is on a corridor, or a train stopped there; anything else is a signal (SimSig reports only those two, and older recordings carry no aspect). A train first reported more than 2 min after play began (as `LiveState`) entered the sim at that signal; its WTT entry point is found by headcode when the headcode has only one. On Waterloo's 4 h recording every entry signal maps to one entry point (S4304 ↔ EUPMNS, S457 ↔ ECY457…). A recording whose clock names another sim is left out.
- **Profile:** location names, corridors, and the learned signals, track circuits and berths with their places.
- **Sources** (Fred): every `.wtt` for the sim in the timetable folders; the **saved sessions for the area** (live ones by their frames, replays by their recording, `PlayFiles.ForArea` in Persistence); any recordings given; and the profile. Editing never reads recordings or sessions itself: it is given a `PlayReader`, so it keeps to Timetable, Network and Planning.
- **Cache:** `SimKnowledgeStore` keys it by each source's path, time and size; a source added, removed or changed rebuilds it, as does another format. A source that cannot be read is named and left out, and stays left out until it changes. The excluded files (the draft) are never sources.
- **Speed:** the 112 sims of the 285-file corpus build in 23 s in all, none over 1 s (Cardiff, 7 timetables and 6 499 trains, is the slowest at about 1 s). Waterloo with its 4 h recording builds in 1.5 s.

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

### 5.1 Built in S42
- **`TimetableValidator`** (`Stringline.Editing.Validation`) checks a `WttDocumentImport` and gives a `ValidationResult`: the document's issues, each train's (keyed by its element) and the rules'. `ValidationIssue` carries the train's ID and headcode, the point, the train's element and the closest element (trip, rule, category), SimSig's message, a hint and an optional `QuickFix(Title, TimetableEdit)`. `IssueCatalogue` holds every check's code, severity, title and hint. The Analyser's messages are those of the SimSig wiki's *Timetable Tools 1* page (32 messages).
- **Fred's choices:** the three Analyser checks that need SimSig's core data are approximated from sim knowledge, as Info: *wrong traction power* (no train of the power type ever entered there; `LocationKnowledge.EntryPower`), *not a sensible ending point* (no train of the sim ends there; `Ends`), and *exits and re-enters* (a location where trains leave the area, `Exits`, and none is booked through, `Through`). *Terminates with no onward working* fires where the last point is a stop with no next working, join or "berths here". The corpus baseline builds each sim's knowledge from all its timetables, the checked one included.
- **Alternative workings** (from the baseline): released timetables write one working several times: a copy per seed group (seeded mid-journey, `Started`), a copy per day (`EntryDecision`/`EntryChoice`, with `Decision`/`Choice` on activities, written `WED`, `[any] MON | TUE` or `[not] MON | TUE`), or a pair held apart by rules (`X not if Y` and `X appears after Y`). A train's **presence** is the starts it can be in (a seeded train its group's; an entering train the starts before it enters; a formed train its formers') and the choices it needs (a rule on Y is a choice on whether Y runs). Two trains that can never be in one game do not clash for *next train already used*, *divided portion already used*, *two trains seeded at one signal* or *next not the final activity* (alternative next workings). Two formers joined to each other (older files have both name the combined next working) do not clash either.
- **Where a train ends** (from the baseline): a train whose last point departs later than it arrives calls there and leaves the area (64 194 of the corpus's 65 252 trains with an onward working book departure = arrival); a train stopping last where the timetable's own trains show the area ends (some train's last point is a pass there, or an entering train's first point is there) leaves too.
- **Severities after the baseline:** *terminates with no onward working*, *out of order*, *arrival time at the seed location*, *length differs from the next working*, *ambiguous line* and *rule names no single train* are Info; *no traction power*, *shorter than the divided portion*, *remainder too short* and *category defined twice* are Warnings. Only a missing sim ID blocks a save: 11 released timetables define a category twice and SimSig reads them (§6).
- **Ambiguous line** fires only where every other booking of the link names a line (where some leave it out, SimSig copes); **out of order** not where the middle location is a reversing one.
- **Quick fixes:** insert the booked chain ("no known path": the chain's locations as passes, timed in proportion to the usual run times for the train's band, to the half minute, with B's direction flag), set the most used line, stop where a train reverses on a pass, clear a seeded train's first arrival, and move a next working last.
- **Incremental:** `Revalidate(previous, import, edits)` checks again the trains the edits touch (`TimetableEdit.Targets`, a removed node's parent included) and every train linked to them before or after (activities, formations, a shared seed signal, exclusion rules), and reuses the rest. An edit to the header, a category, a seed group or a rule, or one that moves where the area ends, checks everything. A test compares incremental and full checks through edits on Waterloo.
- **Knowledge** is used only when it is for the same sim and was built from at least one timetable; a "never seen" message says how much was seen ("never seen in 2 timetables (3 536 trains) and 1 recording").
- **Speed:** a full check of the corpus's biggest timetable, import included, takes 0.2 s.

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
- A save is blocked only by Errors that would make the file unreadable (no area ID). Other Errors ask for a confirmation and are listed in the save dialog. (S42: a category defined twice is a Warning, as 11 released timetables have one and SimSig reads them.)

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

### 7.1 Built in S43
- **Fred's choices:** a new file's suggested name takes its **date from the original's name** (a full `YYYY-MM-DD`, else its year, else today's: `waterloo 2010.wtt` → `Waterloo 2010 Stringline.wtt`; `TimetableNames`); "auto-inserted" rows are the **points Stringline inserted** (a quick fix's chain, later S46's tools), greyed until the user edits them and remembered in the `.draft.json`, never in the `.wtt`; **one draft at a time** (opening another asks about the first); a draft left open by a crash is **offered back at the next start**.
- **`TimetableEditor`** (`Stringline.Editing.Drafts`) runs on its owner's thread (the UI thread, which also reads the document for the grid), rather than on a queue like `PlanningEngine`: an edit, Waterloo's import and an incremental check take about 130 ms in Debug. It publishes a `DraftSnapshot` (the import, the issues, unsaved changes, undo and redo, version, files, greyed points) after each edit, undo, redo, save or new knowledge.
- **Undo history that survives a restart:** `TimetableEdit.ApplyRecorded` applies an edit one primitive at a time and records each step, and its undo's step, by **element path** (`Timetables[0]/Timetable[12]/Trips[0]/Trip[3]`: each level's name and index among siblings of that name, so a version written into the header leaves paths where they were). A forward record fits the tree before the edit and a backward one the tree after it, so the history replays on the draft's `.wtt` read back from disk. In the session, undo uses the edits' own objects (elements keep their identity); after a restart, the records.
- **Drafts** (`DraftStore`): `Documents\Stringline\drafts\{source name}.wtt` (written as the document is, no version change) and `.draft.json` (`DraftState`: source and target paths, sim, open flag, the `.wtt`'s SHA-256, undo and redo records, the undo step last saved at, greyed points). Both are written atomically, 2 s after the last edit (at least every 30 s while editing), on open, save and close. A history whose hash no longer matches its `.wtt` is left out, and the draft counts as unsaved.
- **Saving:** the first Save of a draft of a file asks "Save as a new file… / Replace *name* / Cancel" (replacing is confirmed again); Save As offers the source's folder (else SimSig's Timetables folder) and the convention's name. The checks run in full first: an Error that blocks saving refuses it, other Errors are listed (the first 12) with "Save anyway". Backups go to `AppPaths.TimetableBackups`. **Close draft** (and closing the window or the app) with unsaved changes asks **Save / Keep as a draft / Discard the draft / Cancel**; Windows ending the session autosaves the draft as open, so it is offered back.
- **Live:** a draft is always read again from its file (`WttDocument.Load`), never taken from the session, so the session's `WorkingTimetable` and plan are untouched (an App test checks it).
- **Knowledge in the app:** built in the background on open (`SimKnowledgeStore` beside the profiles; the Options timetables folder, the saved sessions for the sim, the profile; the draft's source and target left out), then the draft is checked in full again. The status bar says what it was built from.
- **The workspace** (`EditorWindow`, `TimetableEditorViewModel`): the trains list (sortable columns, a filter over headcode, UID, entry, category and seed group, "with issues", multi-select; New, Copy and Delete train); the Train tab (details, `$template` previewed from the import, and the trip grid); the Header tab; the Issues panel (level and text filters, a click goes to the train and point, Apply fix, Fix all of this kind). Trip grid: SimSig notation in and out (`12/34` sets the pass, `d` and `t` their ticks, `w` waiting for the booked time, `r` a 50 % request if none is set; a plain time clears the pass and the wait), allowances as minutes with `H`, drop-downs of the codes other timetables use there (most used first) and completion of TIPLOCs. Keys: Enter, Insert (a point above), Ctrl+Delete, Ctrl+D (the first selected row's value down, or the row above's into one), Ctrl+Z/Y, Ctrl+S, F7. A committed cell is one edit at once (not at the end of the row).
- **Not yet:** the draft board (S44); activities, rules, seed groups and categories as tabs (S45) — activities show read-only in the grid; bulk tools (S46); New draft ▸ from the working plan, and CSV (S47).

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
