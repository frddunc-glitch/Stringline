# 04 — Timetable Import and Domain Model

## 1. SimSig `.wtt` file

*Verified in S4 against Waterloo, Llangollen and Penrith, and against Fred's whole timetable folder: 284 `.wtt` files from 100+ sims, SimSig 3.247 to 5.36, 188 901 trains. `WttReader` imports all of them with no errors and no unknown elements.*

A `.wtt` is a **zip** of `TimetableHeader.xml` (header only) and `SavedTimetable.xml` (everything). Stringline reads only `SavedTimetable.xml`; it also accepts that XML on its own. Every file seen is plain UTF-8 with no XML declaration. The reader falls back to Latin-1 for a hand-edited file that is not valid UTF-8.

**Value formats**
- Times are whole **seconds after midnight of the first day** and pass 86 400 after midnight (Waterloo runs to 91 800). They map straight to `SimTime`.
- Booleans are `True`/`False` in older files and Delphi's `-1`/`0` in newer ones. The WTT reader accepts both (and `1`). This is separate from the gateway's lenient JSON booleans, which still reject `-1`.
- **Allowances are in half minutes**: `EngAllowance` 3 means 1½ min. They are taken in the section **after** the point they are on. Checked across the corpus: pass-to-pass run times with allowance N are N × 30 s longer than the same section's usual run time, for N = 1…8, in thousands of sections. `PerfAllowance` never appears in the corpus but is read the same way.

**Root `SimSigTimetable`**: attribute `ID` (= gateway `area_id`, e.g. `waterloo`) and `Version` (the SimSig that saved it). Children: `Name`, `Description`, `StartTime`, `FinishTime`, `VMajor`/`VMinor`/`VBuild`, `TrainDescriptionTemplate`, `TrainCategories`, `Timetables`, `TimetableRules`. Also present and ignored: `SeedGroups`, `SeedGroupSummary`, `ScenarioOptions`, `CautionSpeedSets`, `Decisions`, `TTClassOfServiceRules`.

**`TrainCategory ID="…"`**: `Description`, `IsFreight`, `MaxSpeed` (mph), `TrainLength` (m), `AccelBrakeIndex`, `SpeedClass`, `Electrification`, `DwellTimes/*` (seconds). Category IDs can repeat within a file (the first wins).

**`Timetable`** (one per train):

| Element | Meaning |
|---|---|
| `ID` | Headcode. May carry a `-1`/`-2` suffix that is part of the headcode (`2O03` and `2O03-1` are different trains) |
| `UID` | Newer files only. **Not unique**: alternative paths of one service share it (195 of 284 files) |
| `Description` | Free text, or `$template` = the header's `TrainDescriptionTemplate` filled from `OriginTime`, `OriginName`, `DestinationName`, `DestinationTime`, `OperatorCode` and the category description (`$originTime $originName-$destName $operator ($stock)`) |
| `Category` | Category ID; train-level `MaxSpeed`, `TrainLength`, `IsFreight` and dwell elements override it |
| `EntryPoint`, `DepartTime` | Enters from off-area at that point, at that time. `DepartTime` is often absent (SimSig works it out) |
| `SeedPoint` (+ `Started`) | In the sim at the start, at that signal/berth |
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

**`TimetableRule`**: `Rule` code (0 X appears after Y enters, 1 … after Y leaves, 2 … after Y arrives, 3 X not if Y, 4–9 X departs after Y arrives/enters/leaves/joins/divides/forms, 10 mutually exclusive, 11–13 X appears after Y joins/divides/forms, 14 alternatives), `Train`/`Train2` (headcodes) or `TrainUID`/`Train2UID`, `Time` (seconds after Y's event), optional `Location`.

**Import rules (`WttReader`)**
- Never fails the whole import for one train: a train without a usable headcode or without any usable timing point is skipped; a point without a location is skipped; a bad number is dropped. Each becomes an `ImportWarning` (Info / Warning / Error). Damaged XML keeps every train read before the damage (Error). Only a file that is not a timetable at all throws `WttFormatException`.
- Unknown elements are counted by path in `WttImportResult.UnknownElements` and logged once each.
- **`TrainId`** = UID if present, else headcode; the 2nd, 3rd… train with the same ID gets `#2`, `#3` in file order (Fred's choice). Re-importing the same file gives the same IDs.
- An activity's associated train is resolved by UID, else by headcode (also when a UID field holds a headcode). When several trains match, it picks the one that starts at (for forming activities) or calls at that location, nearest in time. A forming activity (next, divide, detach, drop) sets the formed train's `FormedFrom`. Unresolved links are a Warning, except crew changes.
- Rules resolve only when the name matches exactly one train. Otherwise they are kept with the raw name and an Info note.

Location display names are **not** in the WTT; they come from the profile (user-entered or imported CSV).

## 2. Domain model (Stringline.Core)

```csharp
// Stringline.Core.Timetables — implemented in S4 (see the source for every property).
class WorkingTimetable(TimetableHeader Header, IReadOnlyList<TrainCategory> Categories,
                       IReadOnlyList<Train> Trains, IReadOnlyList<TimetableRule> Rules);   // Find/Get(TrainId), WithHeadcode, FindCategory
record TimetableHeader(AreaId, SimSigVersion, Name, Description, TimetableVersion?, StartTime, FinishTime, TrainDescriptionTemplate?);
record TrainCategory(Id, Description, IsFreight, MaxSpeedMph?, LengthMetres?, AccelBrakeIndex?, SpeedClass?, Electrification?, DwellTimes Dwell);
record Train { TrainId Id; Headcode Headcode; string? Uid; string Description; string? CategoryId;
               TrainStart Start; Tiploc? EntryPoint; SimTime? EntryTime; Tiploc? SeedPoint;
               int? MaxSpeedMph; int? LengthMetres; bool IsFreight; bool AsRequired; origin/destination names and times; OperatorCode; Notes;
               DwellTimes Dwell; IReadOnlyList<TimingPoint> Points; TrainId? FormedFrom;
               /* derived */ Origin, Destination, FirstTime, LastTime, NextWorking }
record TimingPoint { int Index; Tiploc Location; SimTime? Arrive; SimTime? Depart /* pass time when IsPass */; bool IsPass;
                     string? Platform, Line /*out*/, Path /*in*/; bool? DownDirection;
                     int EngineeringAllowanceSeconds, PathingAllowanceSeconds, PerformanceAllowanceSeconds /* section after this point */;
                     int? DwellSeconds; int? RequestPercent; bool SetDownOnly; string? NewCategoryId; IReadOnlyList<TrainActivity> Activities }
record TrainActivity(ActivityKind Kind, int Code, Headcode? AssociatedHeadcode, string? AssociatedUid, TrainId? Train);
record TimetableRule(RuleKind Kind, int Code, string? TrainText, string? Train2Text, TrainId? Train, TrainId? Train2, int? DelaySeconds, Tiploc? Location);

// Profile (S6): display colour, priority and line style per category; they are not in the WTT.
record CategoryStyle(string CategoryId, string Name, int Priority, SkColour Colour, LineStyle Style);

// Live
record LiveTrain(string SimUid, Headcode Headcode, TrainId? MatchedTrain,
                 IReadOnlyList<ActualEvent> Actuals, LiveStatus Status);
record ActualEvent(Tiploc Location, ActualAction Action, SimTime Time, string? Platform);

// Network
record Corridor(string Id, string Name, IReadOnlyList<CorridorLocation> Locations,
                IReadOnlyList<Section> Sections, AxisMode Axis);
record CorridorLocation(Tiploc Tiploc, string DisplayName, double AxisPosition,
                        double? DistanceKm, int Platforms, bool IsTimingPointOnly);
record Section(Tiploc From, Tiploc To, IReadOnlyList<TrackDef> Tracks,
               int DefaultHeadwaySeconds, int MinRunSeconds /*derived*/);
record TrackDef(string Name, TrackDirection Direction);         // Up, Down, BiDirectional

// Planning
record PlannedPoint(Tiploc Location, SimTime? Arrive, SimTime? Depart, string? Platform, string? Line);
record TrainPlan(TrainId Id, IReadOnlyList<PlannedPoint> Points, bool Cancelled, PlanOrigin Origin);
record ForecastPoint(Tiploc Location, SimTime? Arrive, SimTime? Depart, ForecastBasis Basis);
record Disruption(string Id, DisruptionKind Kind, Tiploc From, Tiploc To, string? Track,
                  SimTime Start, SimTime End, double RunTimeFactor, int AddedSeconds, string Note);
record Conflict(string Id, ConflictKind Kind, TrainId A, TrainId? B,
                Tiploc Where, SimTime When, int SeveritySeconds);
```

Up/Down direction of a train on a corridor is inferred from whether its locations ascend or descend in corridor order.

## 3. Train matching (live ↔ WTT)

1. On first event for a new `SimUid`: candidates = WTT trains with the same headcode (exact, including any `-1` suffix) and not already matched. Several WTT trains can share a headcode and even a UID (alternative paths).
2. Score each by |actual time − planned time| at that location (or at entry point); accept the best within 60 min.
3. If a matched train has a next-working link and a new uid appears with the linked headcode at the same location, link it. If the uid persists across a headcode change, follow it.
4. Ambiguity or no candidate → `Unmatched`; draw actuals in grey; user can link manually from the train panel (stored in the session).
5. Matching is pure and unit-tested with fixtures.

## 4. Auto corridor generation (Stringline.Network)

1. Build a directed graph of consecutive TIPLOC pairs from all trains' timing points.
2. Collapse to an undirected graph; find the longest simple paths through high-traffic edges (weight = number of trains). Offer the top N as candidate corridors; branches become extra corridors.
3. Order locations along each corridor; mark a location "timing point only" if no train stops there.
4. Section min run time = minimum over trains of (arrive/pass at B − depart/pass at A), excluding obviously bad data (≤ 0).
5. Derived axis: cumulative sum of min run times normalised to 0..1; configured distances override.
6. Tracks per section: infer 2 (Up+Down) unless the user edits; single-line flagged when the user sets it (we cannot reliably infer it from the WTT, though trains crossing at loops hint at it — show a hint).
