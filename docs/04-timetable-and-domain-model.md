# 04 — Timetable Import and Domain Model

## 1. SimSig `.wtt` file

*Verified in S4 against Waterloo, Llangollen and Penrith, and against Fred's whole timetable folder: 284 `.wtt` files from 100+ sims, SimSig 3.247 to 5.36, 188 901 trains. `WttReader` imports all of them with no errors and no unknown elements.*

A `.wtt` is a **zip** of `TimetableHeader.xml` (header only) and `SavedTimetable.xml` (everything). Stringline reads only `SavedTimetable.xml`; it also accepts that XML on its own. Every file seen is plain UTF-8 with no XML declaration. The reader falls back to Latin-1 for a hand-edited file that is not valid UTF-8.

**Value formats**
- Times are whole **seconds after midnight of the first day** and pass 86 400 after midnight (Waterloo runs to 91 800). They map straight to `SimTime`.
- Booleans are `True`/`False` in older files and Delphi's `-1`/`0` in newer ones. The WTT reader accepts both (and `1`). This is separate from the gateway's lenient JSON booleans, which still reject `-1`.
- **Allowances are in half minutes**: `EngAllowance` 3 means 1½ min. They are taken in the section **after** the point they are on. Checked across the corpus: pass-to-pass run times with allowance N are N × 30 s longer than the same section's usual run time, for N = 1…8, in thousands of sections. `PerfAllowance` never appears in the corpus but is read the same way.

**Root `SimSigTimetable`**: attribute `ID` (= gateway `area_id`, e.g. `waterloo`) and `Version` (the SimSig that saved it). Children: `Name`, `Description`, `StartTime`, `FinishTime`, `VMajor`/`VMinor`/`VBuild`, `TrainDescriptionTemplate`, `TrainCategories`, `Timetables`, `TimetableRules`, `SeedGroups` (S34). Also present and ignored: `SeedGroupSummary` (the same groups as one string), `ScenarioOptions`, `CautionSpeedSets`, `Decisions`, `TTClassOfServiceRules`.

**`SeedGroups/SeedGroup`** (S34): `ID` (a GUID in braces), `StartTime` (seconds), `Title` (what the Loader shows, e.g. `14:00`). A seed group is a start time other than the timetable's own, with its own seeded trains. Penrith 2017-11-01 has 04:00 and 14:00. Read into `SeedGroup(Id, StartTime, Title)` on `WorkingTimetable.SeedGroups`; an empty `<SeedGroup />` is skipped quietly, one with no ID or time with a Warning.

**`TrainCategory ID="…"`**: `Description`, `IsFreight`, `MaxSpeed` (mph), `TrainLength` (m), `AccelBrakeIndex`, `SpeedClass`, `Electrification`, `DwellTimes/*` (seconds). Category IDs can repeat within a file (the first wins).

**`Timetable`** (one per train):

| Element | Meaning |
|---|---|
| `ID` | Headcode. May carry a `-1`/`-2` suffix that is part of the headcode (`2O03` and `2O03-1` are different trains) |
| `UID` | Newer files only. **Not unique**: alternative paths of one service share it (195 of 284 files) |
| `Description` | Free text, or `$template` = the header's `TrainDescriptionTemplate` filled from `OriginTime`, `OriginName`, `DestinationName`, `DestinationTime`, `OperatorCode` and the category description (`$originTime $originName-$destName $operator ($stock)`) |
| `Category` | Category ID; train-level `MaxSpeed`, `TrainLength`, `IsFreight` and dwell elements override it |
| `EntryPoint`, `DepartTime` | Enters from off-area at that point, at that time. `DepartTime` is often absent (SimSig works it out) |
| `SeedPoint` (+ `Started`) | In the sim at the start, at that signal/berth. `Started` is `-1` on every seeded train in a fresh WTT (kept as `Train.Started`) |
| `SeedGroup` | The seed group (by ID) a seeded train is seeded in. None: seeded when the sim starts at the timetable's start. Kept as `Train.SeedGroup` (seeded trains only); a group the header does not define is an Info note, and the train is taken as seeded at any start |
| neither | Starts at its first timing point: formed by another train's activity, or appears there |
| `AsRequired`, `Notes`, `OriginName`/`Time`, `DestinationName`/`Time` | As named. Origin time may be the previous evening (Penrith freight) |
| `Trips/Trip` | Ordered timing points |
| *(ignored)* | Performance details (`AccelBrakeIndex`, `SpeedClass`, `PowerToWeightCategory`…) and saved-game state (`Delay`, `Entry…`, `Shunt…`, `QueuePosition`…) |

**`Trip`**: `Location` (TIPLOC), `ArrTime`, `DepPassTime` (departure, or pass time with `IsPassTime`), `Platform`, `Line` (out), `Path` (in), `DownDirection`, `EngAllowance`/`PathAllowance`/`PerfAllowance`, `DwellTime`, `RequestPercent` (request stop), `SetDownOnly`, `NewCategory`, `Activities`. Some points have **no times at all** (8 656 in the corpus, e.g. single-point trains that only exist to form another). Ignored: `Auto…`, `PrevPathEndDown`/`NextPathStartDown`, `StopLocation`, `StopAdjustment`, `WaitForBookedTime`, `BerthsHere`, `AllowStopsOnThroughLines`, `SetDownKind`, `CrewChange`.

**`Activity`**: `Activity` code plus `AssociatedTrain` (headcode, older files) or `AssociatedUID` (newer files). Codes (confirmed with SimsigTtTools and the Waterloo divide notes):

| Code | `ActivityKind` | Associated train |
|---|---|---|
| 0 | Next | the next working (this train becomes it) |
| 1 / 2 | DivideRear / DivideFront | the new rear / front portion |
| 3 | Join | the train this one joins |
| 4 / 5 | DetachEngineRear / DetachEngineFront | the detached engine |
| 6 / 7 | DropCoachesRear / DropCoachesFront | the dropped coaches |
| 8 | JoinedBy | the train that joins this one |
| 9 | PlatformShare | the other train |
| 10 | CrewChange | often a crew number, not a train |

**`TimetableRule`**: `Rule` code (0 X appears after Y enters, 1 … after Y leaves, 2 … after Y arrives, 3 X not if Y, 4–9 X departs after Y arrives/enters/leaves/joins/divides/forms, 10 mutually exclusive, 11–13 X appears after Y joins/divides/forms, 14 alternatives), `Train`/`Train2` (headcodes) or `TrainUID`/`Train2UID`, `Time` (seconds after Y's event; checked in S32, `03-…md` §6), optional `Location`. The forecast applies them since S32 (`TimetableRules`, `06-…md` §2).

**Import rules (`WttReader`)**
- Never fails the whole import for one train: a train without a usable headcode or without any usable timing point is skipped; a point without a location is skipped; a bad number is dropped. Each becomes an `ImportWarning` (Info / Warning / Error). Damaged XML keeps every train read before the damage (Error). Only a file that is not a timetable at all throws `WttFormatException`.
- Unknown elements are counted by path in `WttImportResult.UnknownElements` and logged once each.
- **`TrainId`** = UID if present, else headcode; the 2nd, 3rd… train with the same ID gets `#2`, `#3` in file order (Fred's choice). Re-importing the same file gives the same IDs.
- An activity's associated train is resolved by UID, else by headcode (also when a UID field holds a headcode). When several trains match, it picks the one that starts at (for forming activities) or calls at that location, nearest in time. A forming activity (next, divide, detach, drop) sets the formed train's `FormedFrom`. Unresolved links are a Warning, except crew changes.
- Rules resolve only when the name matches exactly one train. Otherwise they are kept with the raw name and an Info note.

**Which trains SimSig runs from a later start (S34, #16; checked on Penrith's 14:00 recording, `local-fixtures/penrith-payware.sslog`).**
- A WTT with seed groups books each service twice where the start falls mid-run: the original (entering, or formed from another) and a seeded twin (`UID` with an `S`, e.g. `286HS`), seeded part-way at the group's start. Started at 14:00, SimSig ran only the twins: all 7 seeded trains of the 14:00 group appeared within 11 s, and none of the originals did.
- **No tolerance before the start:** no unseeded train booked to enter before 14:00 ran, the latest being W552 at 13:58:30 (its first timing point is 14:01, after the start: the entry time is what counts). AR85 (enters 14:09) and 104K (14:03:30) ran. So `StartingTrains` uses the entry time, else the first booked time, strictly before the start.
- **Formations:** 186L (booked 14:13, formed from 186K, which enters at 13:35) did not run; its seeded twin did. 178J is formed by both 178H (not running) and its twin 178HS, so a train runs if any train forming it runs.
- Seeded trains with no group (Penrith's W595TU and 3SP7) are seeded at the timetable's start (00:00) and did not appear at 14:00.
- The recording is 14 minutes, so trains much later in the day are not checked; the rule only needs the start.

**How SimSig writes it (S40, from the 285-file corpus; `WttDocument` and `WttWriter` keep to it, `08-…md` §3)**
- Every file is a zip of `SavedTimetable.xml` then `TimetableHeader.xml` (deflate), and no other entries. The XML is indented two spaces with CRLF line ends, ends with a CRLF, and has no declaration and no byte order mark. An empty container is `<DwellTimes/>`; an empty text field is `<Description></Description>`.
- Text escapes the five entities (`&amp;` `&lt;` `&gt;` `&apos;` `&quot;`) and writes anything outside printable ASCII but the tab as a character reference with four upper-case hex digits (`&#x00BD;` for ½, `&#x000A;` for a line feed). No file holds a raw non-ASCII byte.
- `TimetableHeader.xml` is the root with its attributes and its `[TH]` children in the file's order: `Name`, `Description`, `StartTime`, `FinishTime`, `VMajor`, `VMinor`, `VBuild`, `TrainDescriptionTemplate`, `SeedGroupSummary`, `ScenarioOptions`. It matches the timetable's own fields in every file but three (Newport SO 2015, whose header has `VBuild` 0 against 1).
- **Defaults are left out** on trains, trips, activities and rules: a yes/no field is written only when yes, and a number only when not 0 (0 is written for an activity's code, a rule's number, `ClassOfService` and `ShuntInterpose`). Categories, the header, seed groups and the lists write every value.
- Each element's children come in one fixed order, with no file disagreeing (`WttSchema`). For example a `Trip` is `Location`, `NewCategory`, `DepPassTime`, `SetDownKind`, `SetDownOnly`, `ArrTime`, `Platform`, `Line`, `Path`, `Auto…`, the allowances, `IsPassTime`, … `RequestPercent`, `Activities`.
- A file's yes/no style is the same throughout: 206 files use `-1`/`0` and 78 `True`/`False` (one has no yes/no values).
- Saved through `WttWriter` with no edits, 271 of the 285 come back byte for byte. The other 14 were edited by hand and hold raw line breaks in descriptions, which XML reads as line feeds and the writer writes as `&#x000A;` (SimSig's own form); their trees are equal.

Location display names are **not** in the WTT; they come from the profile (user-entered or imported CSV).

## 2. Domain model (Stringline.Core)

```csharp
// Stringline.Core.Timetables — implemented in S4 (see the source for every property).
class WorkingTimetable(TimetableHeader Header, IReadOnlyList<TrainCategory> Categories,
                       IReadOnlyList<Train> Trains, IReadOnlyList<TimetableRule> Rules);   // Find/Get(TrainId), WithHeadcode, FindCategory
record TimetableHeader(AreaId, SimSigVersion, Name, Description, TimetableVersion?, StartTime, FinishTime, TrainDescriptionTemplate?);
record TrainCategory(Id, Description, IsFreight, MaxSpeedMph?, LengthMetres?, AccelBrakeIndex?, SpeedClass?, Electrification?, DwellTimes Dwell);
record Train { TrainId Id; Headcode Headcode; string? Uid; string Description; string? CategoryId;
               TrainStart Start; Tiploc? EntryPoint; SimTime? EntryTime; Tiploc? SeedPoint; string? SeedGroup; bool Started;
               int? MaxSpeedMph; int? LengthMetres; bool IsFreight; bool AsRequired; origin/destination names and times; OperatorCode; Notes;
               DwellTimes Dwell; IReadOnlyList<TimingPoint> Points; TrainId? FormedFrom;
               /* derived */ Origin, Destination, FirstTime, LastTime, NextWorking, BookedStart /* entry time, else FirstTime */ }
record SeedGroup(string Id, SimTime StartTime, string Title);   // WorkingTimetable.SeedGroups (init), FindSeedGroup(id)
record TimingPoint { int Index; Tiploc Location; SimTime? Arrive; SimTime? Depart /* pass time when IsPass */; bool IsPass;
                     string? Platform, Line /*out*/, Path /*in*/; bool? DownDirection;
                     int EngineeringAllowanceSeconds, PathingAllowanceSeconds, PerformanceAllowanceSeconds /* section after this point */;
                     int? DwellSeconds; int? RequestPercent; bool SetDownOnly; string? NewCategoryId; IReadOnlyList<TrainActivity> Activities }
record TrainActivity(ActivityKind Kind, int Code, Headcode? AssociatedHeadcode, string? AssociatedUid, TrainId? Train);
record TimetableRule(RuleKind Kind, int Code, string? TrainText, string? Train2Text, TrainId? Train, TrainId? Train2, int? DelaySeconds, Tiploc? Location);

// Profile — Stringline.Network.Profiles, implemented in S6 (§5): display colour, priority and line style per category; they are not in the WTT.
record CategoryStyle(string CategoryId, string Name, int Priority, Colour Colour, LineStyle Style = Solid);   // CreateDefaults(tt), Fallback
record StringlineProfile { Version; AreaId; Name; Corridors; LocationNames /* Tiploc → name */; Categories }  // DisplayName, FindCorridor, StyleFor, Create, Refresh

// Live — Stringline.Core.Live, implemented in S5. Immutable; LiveState (Stringline.Timetable.Live) replaces them on change.
record LiveTrain(string SimUid, ImmutableList<LiveWorking> Workings, ImmutableList<ActualEvent> Actuals, bool PresentAtStart);
                 // Current, Headcode, MatchedTrain (of the current working), FirstSeen, LastSeen, ActualsOf(working), WorkingOf(TrainId)
record LiveWorking(Headcode Headcode, int FirstActual, MatchStatus Status, TrainId? Train, MatchMethod Method,
                   int? DeviationSeconds, string? Reason);   // one headcode in the uid's life; Pending | Unmatched | Matched
record ActualEvent(Tiploc Location, TrainLocationAction Action, SimTime Time, string Platform, bool AtTimingPoint, int Working);
enum MatchMethod { None, HeadcodeAndTime, NextWorking, Formed, Manual }
class SimClock    // interpolated sim time between clock_msgs; speed measured from clock arrivals (see 02-…md §4)

// Network — implemented in S6 (§4). Locations are in Down order: the first is the Up end.
record Corridor(string Id, string Name, IReadOnlyList<CorridorLocation> Locations,
                IReadOnlyList<Section> Sections, AxisMode Axis = Derived);   // IndexOf, PositionOf, FindSection, DirectionOf(locations)
record CorridorLocation(Tiploc Tiploc, double AxisPosition /*0..1*/, double? DistanceKm, int Platforms, bool IsTimingPointOnly);
record Section(Tiploc From, Tiploc To, IReadOnlyList<TrackDef> Tracks, int DefaultHeadwaySeconds /*the headway used*/,
               int MinRunSeconds, int? MinRunDownSeconds, int? MinRunUpSeconds, int Samples /*derived*/,
               int? BookedHeadwaySeconds, int BookedHeadwayPairs /*derived, S27*/, HeadwaySource HeadwaySource /*Default | Wtt | Hand*/);   // MinRunFor(direction), IsSingleLine, ResolveHeadway(fallback)
record TrackDef(string Name, TrackDirection Direction);         // Up, Down, BiDirectional
// Display names are not on CorridorLocation: they live once per TIPLOC in StringlineProfile.LocationNames.

// Planning
// S10: implemented as below (see 06-…md §1 for the rules).
record PlannedPoint(int Index /*= WTT point index; never added or removed*/, Tiploc Location, SimTime? Arrive, SimTime? Depart, bool IsPass, string? Platform, string? Line);
record TrainPlan(TrainId Id, Headcode Headcode, string? CategoryId, PlanOrigin Origin /*Wtt | Clone*/, TrainId Source,
                 IReadOnlyList<PlannedPoint> Points, bool Cancelled, int? TerminatesAt, bool IsModified);   // RunningPoints, LastIndex, FromWtt, SameAs
record LiveLink(string SimUid, Headcode Headcode, TrainId? Train);   // a manual match, kept in the plan so it undoes
// S11: implemented as below (see 06-…md §2 for the rules).
record ForecastPoint(int Index, Tiploc Location, SimTime? Arrive, SimTime? Depart, bool IsPass,
                     ForecastBasis Basis /*None | Actual | Arrived | Forecast*/, int? DelaySeconds);
record TrainForecast(TrainId Train, Headcode Headcode, TrainForecastStatus Status /*NotStarted | Running | Finished | Unavailable*/,
                     IReadOnlyList<ForecastPoint> Points /*= RunningPoints*/, string? LiveUid, bool PossiblyStopped, int KnockOnSeconds);
class Forecast(SimTime? Now, bool Interaction, IReadOnlyList<TrainForecast> Trains);   // Find, Drawn
record ForecastSection(int Id, Tiploc From, Tiploc To, int HeadwaySeconds, bool SingleLine, int DownTracks, int UpTracks, …, HeadwaySource);  // in ForecastNetwork
record Disruption(string Id, DisruptionKind Kind /*Block | SpeedRestriction*/, Tiploc From, Tiploc To, string? Track,
                  SimTime Start, SimTime End, double RunTimeFactor, int AddedSeconds, string Note,
                  TrackDirection? Direction /*S13: Track is a corridor track run this way; null = a WTT line code*/);   // AppliesTo
record PlanNote(string Id, Tiploc Location, SimTime Time, string Text);   // S13, DIS-3
// S14: implemented as below (see 06-…md §3). A gives way; B is the other train, if any.
record Conflict(string Id, ConflictKind Kind /*Headway | Overtake | SingleLine | Platform | Disruption | Unrealistic*/, TrainId A, TrainId? B,
                Tiploc Where, SimTime When, int SeveritySeconds);   // + HeadcodeA/B, To, Start, End, PointIndex, Platform, DisruptionId, KnockOnSeconds, HeadwaySeconds/HeadwaySource (S27)
class ConflictSet(IEnumerable<Conflict>);   // by time; Find, Involving
record BlockHold(int Index, int ToIndex, string DisruptionId, SimTime Departure, SimTime HeldUntil);   // TrainForecast.BlockHolds
```

Up/Down direction of a train on a corridor is inferred from whether its locations ascend or descend in corridor order.

## 3. Train matching (live ↔ WTT)

*Implemented in S5: `TrainMatcher` (pure) and `LiveState` (the reducer that calls it), both in `Stringline.Timetable.Live`. Fred's four recordings match 100%: Waterloo 1× 35/35 workings, 4× 205/205, failure 39/39, and Penrith 9/9 (`stringline match` prints the report).*

A live train is a sim **uid**. Its life is split into **workings**, one per headcode: when SimSig gives the uid a new headcode (its next working), a new working starts. Each working is matched on its own, and is re-matched on every movement while it is `Pending`. `Matched` and `Unmatched` are final unless the user links by hand.

1. **Same uid, new headcode:** if the WTT train it was matched to forms a train with the new headcode (a `Next` activity first, then divide/detach/drop), that train is taken (`NextWorking` or `Formed`).
2. **Candidates** are the WTT trains with exactly this headcode (including any `-1` suffix) that no other working has. None: `Unmatched`.
3. **Score** each candidate by the mean |actual − booked| over the reported locations that are timed points of that train. The arrival time is used for an `arrive`, and the departure/pass time otherwise; for a second call at the same place, the nearer one is used. An `arrive` where only a departure is booked (an origin) costs nothing if early, because trains in the sim at the start report `arrive` at their origin long before they are due away. Before any such point has been reported (signals only), the score is how far the first actual lies outside the train's booked span (entry or first time to last time). Penalties are added on top for ranking only, not for the window:
   - +120 s per reported timing location that is not on the candidate's path. This is kept small, because **SimSig reports every timing location a train passes, including ones its own schedule leaves out** (Waterloo's 1W51 is booked WLOO–CJML–WIMB but reports INTJN, VAUX, QTRDBAT and EARLSFD too). So it only separates alternative paths.
   - +600 s for a **seeded** candidate when the live train was first seen more than 2 min after the session start, and for an **unseeded** candidate when it was seen at the start and a seeded alternative exists. Penrith has a seeded `…S` UID and an unseeded version of each train in the sim at 14:00, booked seconds apart.
4. **Decide:** drop candidates whose deviation exceeds the **60 min window**. If none are left, `Unmatched`. If one is left, `Matched`, even on a signal alone. If several are left, `Matched` once the best leads the next by 60 s, or after 3 timing points ("close call"; ties go to file order). Until then, `Pending`. A train whose WTT parent (`FormedFrom`) is matched elsewhere is marked `Formed`: a divided portion gets a new uid.
5. **Manual links** (`LiveState.Link(uid, headcode, train)`; `null` = "do not match") are keyed by (uid, headcode). They apply as soon as that working appears, take the train from any other working (which becomes `Unmatched`), and survive a reset/rewind, so S16 can restore them before replaying a session. `ClearLink` returns the working to automatic matching.
6. Unmatched workings keep their actuals (drawn grey) and a `Reason`. Matching is pure and unit-tested; `LiveFixtureTests` replays every recording and checks each match is consistent: same headcode, and well inside the window where the train calls.

## 4. Auto corridor generation (Stringline.Network)

*Implemented in S6: `LocationGraph`, `CorridorGenerator`, `CorridorMeasurement`, `CorridorBuilder` and `CorridorAxis`. `stringline corridors t.wtt [--edges] [--profile out.json]` prints the candidates. Waterloo gives the Main (WLOO–WIMB) and Windsor (WLOO–BARNES) lines, Llangollen one single-line corridor, and Penrith Blacktown–Valley Heights. All 284 timetables in Fred's corpus give consistent corridors, in under 0.5 s each.*

1. **Location graph.** Each train becomes a sequence of locations; repeats in a row (a reversal) are merged. An edge joins locations a train calls at one after the other. Weights count **trains, not runs**, so a shunt that loops does not inflate an edge.
2. **Skip edges.** SimSig schedules leave out timing locations a train passes. Waterloo's fast trains are booked WLOO–CJML, past VAUX. So an edge A–C is replaced by A–B–C when another train is booked A, B, C. The chain may hold at most 6 locations, none twice, and must be booked no slower than 2 × the direct time + 5 min (otherwise it is another route). The most common chain wins, and only if it agrees with the orders already accepted (B between A and C forbids A between B and C). Without that check, contradictory chains expanded each other without end: Tyneside took over 4 min. This repeats until nothing changes. Waterloo has 15 skip edges.
3. **Corridors, greedily.** The busiest edge not yet on a corridor seeds a new one. The edge's weight here excludes trains that only go out and back over it (run-rounds such as Llangollen's CARROG–REVCA19, shunts into sidings). The corridor grows at each end towards the neighbour most trains continue to. It stops when fewer than 25% of the trains arriving carry on.
   - **A branch follows its trains onto the main line** (Fred's choice), so corridors may overlap. Waterloo's Windsor corridor starts at WLOO.
   - After the first candidate, a candidate is dropped if it is shorter than 3 locations, lies inside an earlier corridor, or adds less than 5% new traffic (a siding off a corridor).
   - At most 6 candidates; the seed edge needs at least 2 trains. The first is `main`; the others are `{first}-{last}`, and a repeated pair of ends is named "A – B via X".
4. **Orientation: Up end first**, so Down trains ascend (Fred's choice). Each train votes by the share of its corridor points that SimSig flags Down. In a file that uses the flag, an unflagged point is Up: `DownDirection` is only ever written as true. With no flags, or a tie, the end where more trains start or finish goes first.
5. **Measurement** (`CorridorMeasurement`, also run for user-edited corridors). Each train's points are projected onto the corridor. A step between adjacent corridor locations is a run: the departure or pass at one end to the arrival or pass at the other, less the allowances booked in between. Runs of zero or less are ignored. A train booked straight past corridor locations (nothing in between) has its run shared over the sections it covers, in proportion to their direct minimums. Without this, most Waterloo sections would have only a handful of timed trains. The section's `MinRunSeconds` is the fastest run either way; `MinRunDownSeconds`/`MinRunUpSeconds` are kept per direction. A section with no timed run gets 60 s. Location stats: calls, stops, distinct platform names, starts and terminations. *Timing point only* means booked calls but no stops.
6. **Single-line hint.** A section is hinted single line when it has at least two timed runs each way and an Up and a Down run never share it. Overlaps up to 1 min, or half the shorter run on short sections, are booking rounding. Llangollen's trains cross at DEESIDE loop booked a minute apart. The hint is shown, not applied. Every section starts as double line (`Section.DoubleLine`) until the user changes it. Its headway is the WTT's where that is less than the default, else the default (S27; see `06-…md` §3). Llangollen's sections are all hinted; Waterloo's main line has none.
7. **Axis** (`CorridorAxis`). Derived: cumulative minimum run time, normalised to 0..1. A section of 0 s still gets 1 s, so locations never coincide. Distance mode: configured `DistanceKm` values are used; missing ones are interpolated by run time between known ones, and extrapolated beyond them at the known stretch's average rate. Distances may count down the corridor. With fewer than two distinct distances, the derived axis is used.
8. **Refresh** (`CorridorBuilder.Refresh`, `StringlineProfile.Refresh`) recomputes the derived values against a (re-)imported WTT. It keeps the order, distances, platform overrides, tracks, hand-set headways and axis mode, and works the other headways out again (S27). A section keeps its settings while its two locations stay neighbours, in either order.

## 5. Profile (Stringline.Network.Profiles)

*Implemented in S6.* `StringlineProfile` has `version` (1), `areaId`, `name`, `corridors` (in tab order), `locationNames` (TIPLOC → display name; a missing or blank name shows the TIPLOC) and `categories` (`CategoryStyle`).
- **JSON** (`ProfileJson`): camelCase, indented, LF line ends, enums as names, colours as `#RRGGBB`/`#RRGGBBAA`, nulls left out. Reading accepts comments and trailing commas, and ignores unknown properties. A file is refused with `ProfileFormatException` if it is not JSON, is from a newer version, has no `areaId`, or has an inconsistent corridor: a duplicate ID or location, sections that do not match the locations, or a section with no tracks.
- **Store** (`ProfileStore`): `{dir}/{area_id}.json`. The area ID is lower-cased and unsafe characters become `_`. The default directory is `%APPDATA%\Stringline\profiles` (`~/.config/Stringline/profiles` on Linux). Saves are atomic (temp file, then move). `Load` returns null when there is no file and refuses a file whose `areaId` is another sim's.
- **Default category styles** follow the headcode class most of the category's trains use: 1 red/90, 9 purple/85, 2 blue/70, 3 orange/60, 4 green/50, 6 brown/40, 7 olive/35, 8 teal/30, 5 grey/20, 0 near-black/10. An unused category goes by its freight flag. Generated styles are solid. A category the profile lacks uses `CategoryStyle.Fallback`.
- **Editing** (S14a, COR-2 and COR-5; `ProfileEdits`). Immutable edits on a `StringlineProfile`: the sim's name and ID (for a timetable with none), location display names, corridors (add a generated candidate or a new one between two ends, remove, rename, reorder), each corridor's locations (insert, remove, move, distance, platforms, axis mode), sections (tracks, headway) and category styles (name, colour, line style, priority). Every edit is checked by `ProfileValidator`, the same rules `ProfileJson` applies to a file, plus the editor's own: a corridor has a name and at least 2 locations, a headway is not negative, a distance is a number, a priority is 0–100. A refused edit throws `ProfileEditException` and leaves the profile as it was. A change to a corridor's locations rebuilds its sections and re-measures them from the WTT as Refresh does (a section keeps its tracks and headway while its two locations stay neighbours; a new pair gets a double line and the WTT's or the default headway). A headway typed is set by hand; a blank one goes back to automatic. The profile's own default headway (`WithDefaultHeadway`, null for the global one) works every headway not set by hand out again. A corridor ID never changes, so renaming changes only the tab name. New IDs come from the name (`Down Slow` → `down-slow`, then `down-slow-2`).
- **Hand-tuned and regenerating.** A corridor is hand-tuned if no generated corridor has the same ID, name, locations, distances, platforms, tracks and axis (after refreshing it), or if it has a headway set by hand (S27: WTT and default headways are not settings). `RegenerateCorridors` replaces the corridors with generated ones; keeping hand-tuned ones puts each in place of the generated corridor with its ID, or after them. Location names and categories are untouched.
- **Tracks** (`TrackLayouts`): presets Double line, Single line, Two bi-directional, Four tracks (fast and slow), Up only and Down only, or text such as `Up Fast:Up, Up Slow:Up, Down:Down, Relief:Bi` (without a direction, a name starting Up or Down runs that way, else both).
- **Learned signal places** (S37, #19; `SignallingPlaces`, JSON `signalling`, written only when there are some; the version stays 2, as an older Stringline ignores it). Three lists, `signals`, `trackCircuits` and `berths`, of `SignallingPlace`: `id` (as SimSig names it), `from` and `to` (the timing locations trains pass it between, in running order), `run` (`Through`, `Starting`, `Stopping` or `StartingAndStopping`; left out for Through), `direction` (on a corridor with both), `fraction` (0 at `from`, 1 at `to`, in run time; for a track circuit where the front enters it, with `endFraction` where it clears behind the train), `line` (the line or platform every train reported at `from`, or `mixedLines`), `signal` (a track circuit's block signal, a berth's matching signal), `next` (S38, signals only: the signals trains passed next after it, with counts, e.g. `{"S22": 21}`; for the last signal before `to`, the first one past it, since S39; left out when none was seen), `nextSeconds` (S39: the fastest seconds to each of them from runs not checked on the way, a block's running time; not counted across a stop at `to`), `aspects` (S39: the `aspPass` aspects trains passed it at, e.g. `[0, 2, 4, 6]`), `clearSeconds` (S39, track circuits: the fastest seconds from occupation to clearing behind the train), `samples` (the latest 64, to four places), `sampleCount` (all ever), `lastLearned` and `lastSession`, and `handSet`. A place is keyed by kind, ID, `from`, `to` and `run`. It is **used** with at least *n* samples (3, Tools ▸ Options ▸ Profile) and an interquartile range of at most 0.10; a hand-set place is always used and learning adds samples to it without moving it. `sessions` lists each session learned from with the sim time it was learned up to (a live session by its file's `created`, a replay or review by its recording's `created`), so a run ending before that is never counted again. `Pool` merges an imported profile's places (samples added, hand-set kept); `Reset`/`ResetAll` forget learned places but keep `sessions`. The learner is in `06-…md` §2.
- **Signal blocks in sections** (S39, #19; `BlockHeadways`, `06-…md` §3). A section has `aspects` (2–4, set by hand in the profile editor's Aspects column; written only when set) and, measured from the learned places and re-measured whenever they, the corridors or the samples threshold change, `blockHeadwaySeconds`, `blockSignals` (how many placed signals it was measured at) and `blockAspects` (the aspects used: hand-set, else learned on the corridor). `headwaySource` gains `Blocks`. The version stays 2: an older Stringline ignores the new fields and would read `Blocks` as an unknown source.
- **Location names CSV** (`LocationNamesCsv`): `TIPLOC,name` per row. A header row (first field TIPLOC/location/code, or second name/description), blank rows and `#` comments are skipped; fields may be quoted with `""` for a quote; extra columns are ignored. Unreadable rows are reported with their line numbers and skipped. TIPLOCs not in the timetable are reported but still imported, since another timetable for the sim may use them.
