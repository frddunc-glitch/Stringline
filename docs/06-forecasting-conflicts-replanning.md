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
