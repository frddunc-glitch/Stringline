# 04 — Timetable Import and Domain Model

## 1. SimSig `.wtt` file

A `.wtt` is a **zip** containing `SavedTimetable.xml` (plus sometimes notes/other files). The XML root is `SimSigTimetable` with header attributes/elements (name, ID, version, sim ID, start/finish time, categories) and a `Timetables` collection of `Timetable` elements, one per working.

Elements we expect per `Timetable` (to be confirmed against fixtures in Session 3 — **the parser must be tolerant, log unknown elements, and never fail the whole import for one train**):

| Element | Meaning |
|---|---|
| `ID` | Headcode |
| `UID` | Timetable UID |
| `Description` | Free text, e.g. "07:12 Origin – Destination" |
| `AccelBrakeIndex`, `MaxSpeed`, `TrainLength`, `SpeedClass` | Train performance |
| `Category` | Reference to a header-defined category (colour, performance defaults) |
| `EntryPoint`, `DepartTime` / `EntryTime` | Where/when the train enters the sim |
| `SeedPoint` | Train present at sim start |
| `Trips/Trip` | Ordered timing points |
| `Trip/Location` | TIPLOC |
| `Trip/ArrTime`, `Trip/DepPassTime` | Seconds after midnight |
| `Trip/IsPassTime` | Pass (no stop) flag |
| `Trip/Platform`, `Trip/Line`, `Trip/Path` | Platform at station / line out / path in |
| `Trip/EngAllowance`, `PathAllowance`, `PerfAllowance` | Allowances (seconds) |
| `Trip/Activities/Activity` | Next working (`Activity` code, `AssociatedTrain`), joins, splits, divides |

Reference implementations to consult (read, don't copy): `jonathanp0/go-simsig` (wttxml package) and `thejosmeister/SimsigTtTools` (Python).

Location display names are **not** in the WTT; they come from the profile (user-entered or imported CSV).

## 2. Domain model (Stringline.Core)

```csharp
record Train(TrainId Id, Headcode Headcode, string? Uid, string Description,
             CategoryRef Category, int? MaxSpeedMph, IReadOnlyList<TimingPoint> Points,
             TrainLinks Links);                                   // next working, joins, splits

record TimingPoint(int Index, Tiploc Location,
                   SimTime? Arrive, SimTime? Depart,             // Depart = pass time when IsPass
                   bool IsPass, string? Platform, string? LineOut, string? PathIn,
                   int AllowanceSeconds, IReadOnlyList<Activity> Activities);

record Category(string Id, string Name, int Priority, SkColour Colour, LineStyle Style);

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

1. On first event for a new `SimUid`: candidates = WTT trains with the same headcode and not already matched.
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
