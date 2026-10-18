# 06 — Forecasting, Conflict Detection and Re-planning

## 1. Working plan
- `WorkingPlan = Apply(WTT, commands[0..n])` — deterministic, so undo/redo is "drop the last command and rebuild" (rebuild is cheap; cache per-train if needed).
- Commands (all implement `IPlanCommand`, serialisable to the session DB):
  - `ShiftTrain(train, fromIndex, deltaSeconds)`
  - `SetDwell(train, index, departTime)`
  - `ChangePlatform(train, index, platform)` / `ChangeLine(train, index, line)`
  - `SkipStop(train, index)` / `AddStop(train, index, dwell)`
  - `TerminateAt(train, index)` / `CancelTrain(train)` / `ReinstateTrain(train)`
  - `CloneTrain(source, newHeadcode, deltaSeconds)`
  - `AddDisruption(d)` / `RemoveDisruption(id)` / `EditDisruption(...)`
  - `LinkLiveTrain(simUid, trainId)` (session-level, not a plan change)
  - `AcceptSuggestion(suggestionId, commands[])` — a composite
- Validation on apply: times monotonic; run time between points ≥ min run time (× disruption factor); depart ≥ arrive. Violations clamp and return a warning shown to the user.

*Implemented in S10 (`Stringline.Planning`):* `WorkingPlan` (immutable: `TrainPlan`s, disruptions, live links), `WorkingPlanBuilder` (validation), the commands above plus `UnlinkLiveTrain`, `PlanCommandJson`, and `PlanningEngine` with undo/redo publishing `PlanSnapshot`s. The rules:
- **Points are never added or removed.** A command's index is the WTT point index. `SkipStop` turns an intermediate stop into a pass at its arrival time; `AddStop` turns a pass into a stop arriving at the pass time. `TerminateAt` keeps the points it no longer runs to (`RunningPoints` ends with a terminating stop), so `ReinstateTrain` (which clears a cancellation and a short termination) brings them back.
- **Minimum run time** (Fred): the fastest booked run between two locations by any WTT train, either way, less the allowances booked between them (`MinRunTable`). Across untimed points, the steps are summed (or the direct pair is used). A pair no train runs directly has no minimum, so only order is kept.
- **Clamping:** only a run or dwell that the command *shortens* is checked, so a WTT that is already tighter than the table elsewhere is left as booked. A run below the minimum moves the point (and its dwell) later; a departure before the arrival moves to the arrival. Each clamp is a `PlanWarning`. A moved point shortens the next run, which is then checked in turn, so a later departure pushes the train on only as far as it must (it uses its slack).
- **Disruptions** (S10 model, S13 UI): a speed restriction lengthens the minimum (`× factor + added`) for runs between calls at its two ends that depart while it is in force. A block never clamps (it becomes a conflict, S14). Adding or editing a disruption does not re-plan trains already in the plan.
- **Clones** take the source's current plan, with the new headcode, moved by the delta, not cancelled. The new `TrainId` is in the command (`WorkingPlan.NewCloneId` gives `{headcode}#c1`, `#c2`…), so a replay gives the same train.
- **Live links are commands** (Fred), so they undo. The engine rebuilds the live state with the plan's links whenever they change. Only WTT trains can be linked.
- **A command that fails changes nothing**, and is not added to the history. A suggestion is all-or-nothing.
- **History:** undo drops the last command and replays the rest over the WTT; a new command clears redo. Reloading the same timetable (same area and name) replays the history and drops commands that no longer apply; a different timetable, or `Clear`, starts a new history.

## 2. Forecaster (per corridor network, not per graph)

Inputs: working plan, live actuals, clock, section min run times, disruptions, headways.

Algorithm v1 (deterministic, priority-aware, single pass with event queue):
1. For each active train, start state = last actual event (location, time, action). For trains not yet entered, start = planned entry time + `train_delay` hint if any.
2. For each subsequent planned point: `earliest = prevDepart + max(plannedRun, minRun × factor + added)`. Arrival forecast = `max(earliest, ...)`; departure = `max(arrival + minDwell, plannedDepart)` (trains don't leave early from stops; may recover on run allowances).
3. Recovery: a late train runs at min run time (uses up allowances) until back on plan.
4. **Interaction pass (optional toggle, default on):** process all train-section entries in time order via a priority queue; if the section/track is occupied within headway by another train, delay the entry (single line: opposing occupancy blocks the whole section). Ties broken by priority then planned order. This yields "knock-on" forecasts and is where conflicts become *visible delays* rather than overlaps.
5. Trains with no location for > X min of sim time beyond their forecast are flagged "possibly stopped" (feeds disruption hints).

Performance target: 500 trains × 40 points = 20k events → well under 50 ms in C#.

*Implemented in S11 (`Stringline.Planning`):* `Forecaster.Run(ForecastRequest)` → `Forecast` (a `TrainForecast` per train: `ForecastPoint`s with a basis of Actual, Arrived, Forecast or None, a status, `PossiblyStopped`, `KnockOnSeconds`). `ForecastNetwork` holds the sections the interaction pass works on. `PlanningEngine` re-forecasts whenever the live state, the plan or the forecast settings change, and publishes the result in `PlanSnapshot.Forecast`. The rules:
- **One event queue for both passes.** Each train's next departure is queued by time, then category priority (higher first), then plan order. Taking a departure works out the run to the next timed point. With the interaction pass off, the result is the independent forecast (steps 1–3). With it on, the same pass also checks each run against the trains already through its sections. `KnockOnSeconds` is the difference at the last point.
- **Start.** A matched live working's timing-point actuals are mapped onto its plan points in order. Reports at locations the plan does not book are skipped, as SimSig reports every timing location. From the latest one: having arrived, the train leaves after the dwell (and not before now); having left or passed, it is on its way. A working whose live train has moved on to its next working is finished. A train not seen starts at its planned time, plus the sim's `train_delay` for its headcode (the first train not started with that headcode), and not before now.
- **Formed trains.** A train formed from another (next working, divide, detach, drop) starts no earlier than that train reaches the point where it forms it, plus the turnaround: the WTT's terminate dwell for a next working, divide dwell for a divide, else 30 s. A forming train that will never get there (cancelled, terminated short, or failed) lets it start as planned.
- **Run** = the planned run, or less if the train is late, but never less than the minimum run (`MinRunTable`, lengthened by a speed restriction in force, or the planned run if no minimum is known). It never brings the train in before its planned time: `max(min, min(planned run, planned arrival − departure))`. So a late train uses up its allowances until it is back on time, and an early one stays early only where it passes.
- **Dwell** at a stop: the WTT point's dwell, else the train's (or its category's) station dwell, else 30 s (Fred); never before the planned departure. A pass has none.
- **Now.** No forecast time is before now. A running train not yet reported at its next point is forecast to reach it now. It is `PossiblyStopped` if that is more than 5 min (Fred) past both its forecast and its last report (a report at a signal counts, so a train creeping along a long section is not flagged).
- **Disruptions.** A speed restriction lengthens the minimum, as in S10. A block covering the run while it would run holds the train at its departure point until the block ends. A block with a track applies only to trains whose planned line out of that point has that name; S13 refines this.
- **Interaction pass** (Fred: on by default; View → Forecast knock-on). Sections come from the profile's corridors (`ForecastNetwork.For(profile, timetable)`). A section on two corridors is one section, so a branch's trains interact with the main line's. A run is mapped onto the sections between its ends on the first corridor that has both, with the time split by each section's minimum run in that direction. A run no corridor covers is not checked. In each section, a train following another the same way on the same track must enter and leave at least the headway after it. On a single line, an opposing train must have left. **No pair is held further apart than the WTT books them:** when both trains are booked through the section in that order, the spacing is the lesser of the headway and their booked spacing. Opposing trains booked to overlap by up to a minute (crossing-loop rounding) keep that overlap. So the WTT itself makes no knock-on delay: Waterloo's whole day on time gives 8 of 1 768 trains between 4 s and 3 min, where bookings are tighter than the shortest dwells.
- **Tracks side by side.** The profile gives one track each way on a double line, but Waterloo's main line is four tracks. A section is therefore given at least as many tracks each way as the WTT uses at once. Booked runs are put on tracks in entry order: a run entering or leaving within a minute of the last on a track, or overtaking it, takes another track. A following train takes whichever track lets it go soonest.
- **Holding.** A held train waits at its departure point: a pass there becomes a stop. A train already on its way (its departure is actual) is not held, but still occupies the sections.
- **Left out:** cancelled trains, and trains not seen whose plan ended before the session started. A train whose forecast throws is `Unavailable` (logged) and the rest carry on.
- **Drawn** (Fred): the forecast layer shows running trains, from their latest actual, and trains not started whose forecast is a minute or more off the plan. On-time future trains show only their plan. Under a minute is right time, as railway delay minutes go: the shortest dwells often differ from the WTT's half-minute bookings by seconds.
- **Measured** (`benchmarks/ --quick`, Release, Waterloo 1 768 trains / 7 834 points half way through the 4× recording): 10 ms independent, 23 ms with the interaction pass (both passes run).

## 3. Conflict detection
Run on the **working plan without the interaction pass** (so conflicts are shown as plan clashes the controller must resolve) and summarise knock-on delay from the forecast.

| Kind | Rule |
|---|---|
| Headway | Two same-direction trains on the same track enter a section less than headway apart, or the follower would arrive at B before leader clears B + headway |
| Overtake | Follower's plan has it reach B before leader on a single track without a loop at A/B |
| Single-line opposing | Opposing occupancy intervals overlap on a single-line section |
| Platform | Two trains occupy the same platform at overlapping times (+ re-occupation margin) |
| Disruption | Plan runs through a blocked section/time |
| Unrealistic | Plan run time < min run time (should be prevented, but imported WTTs may contain it) |

Severity = seconds of delay one of the trains must take to resolve.

## 4. Suggestion engine (rule-based v1)
For each conflict, generate candidates:
1. **Hold lower-priority** train at the last location before the conflict where it can wait (station platform, loop, or its origin) until clear + headway.
2. **Swap order** — hold the other train instead.
3. **Alternative platform/line** at the location (from profile platform count; from lines seen in the WTT for that location).
4. For blocks: **hold before the block**, or **terminate short / cancel** (only if user enables "allow cancellation suggestions").

Score = Σ (priority weight × added delay at destination) over all affected trains in a 90-minute forecast horizon, plus penalties for cancellations and platform changes. Re-run the forecaster per candidate (cheap). Offer top 3. Accepting applies the candidate's commands as one undoable step.

## 5. Later: optimiser (C)
Rolling-horizon MILP/CP over the next 60–90 min within the corridor using Google OR-Tools CP-SAT: decision variables are section entry times and order binaries per conflicting pair; objective weighted delay; constraints headway/single-line/platform/min-run. Outputs a command set for preview. Kept behind an interface `IReplanner` so v1 rules and the optimiser are interchangeable.

## 6. Tests
- Golden scenarios as fixtures: small synthetic corridors (double line with loop, single line with crossing loop, junction) and hand-written WTTs; expected forecasts/conflicts/suggestions asserted.
- Property tests: forecast never earlier than plan at stops; never faster than min run time; deterministic for same inputs.
