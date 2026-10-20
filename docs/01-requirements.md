# 01 — Requirements

IDs are referenced from the implementation plan. Priority: **M** = MVP, **S** = should (v1), **C** = could (later).

## Connection
| ID | Requirement | P |
|---|---|---|
| CON-1 | Connect to the SimSig Interface Gateway by host/port (default `localhost:51515`) with optional SimSig username/password | M |
| CON-2 | Subscribe to `/topic/TRAIN_MVT_ALL_TOC`, `/topic/SimSig`, and optionally `/topic/TD_ALL_SIG_AREA` | M |
| CON-3 | Show connection state, sim name (`area_id`), sim clock, speed and paused state in a status bar | M |
| CON-4 | Auto-reconnect with back-off; graph keeps working (forecast frozen) while disconnected | M |
| CON-5 | Store credentials encrypted with Windows DPAPI; never log them | M |
| CON-6 | Record every frame to a `.sslog` file (toggle) | M |
| CON-7 | Replay a `.sslog` through the same pipeline at 1×–60× speed, pause, seek | M |

## Timetable
| ID | Requirement | P |
|---|---|---|
| TT-1 | Import a SimSig `.wtt` (zip with XML) — trains, trips, times, platforms, lines, activities, categories | M |
| TT-2 | Show import warnings (unknown fields, unparsable times) without failing | M |
| TT-3 | Match live trains to WTT trains (headcode + time window + next-working links); manual link/unlink | M |
| TT-4 | Show unmatched trains (actuals only) and flag them | M |
| TT-5 | Re-import a WTT mid-session, keeping working-plan edits where trains still match | S |

## Corridors and profile
| ID | Requirement | P |
|---|---|---|
| COR-1 | Auto-generate candidate corridors from WTT location sequences | M |
| COR-2 | Edit corridor: add/remove/reorder locations, set display names, distances, tracks per section, single/double line, default headway | M |
| COR-3 | Derived distance axis when no distances are entered | M |
| COR-4 | Save/load per-sim profile (JSON) keyed on `area_id` | M |
| COR-5 | Category colours, line styles and priorities editable | S |

## Graph
| ID | Requirement | P |
|---|---|---|
| GR-1 | Time–distance graph for a corridor; time horizontal by default (toggle vertical) | M |
| GR-2 | Layers: WTT, working plan, actual, forecast; each toggleable | M |
| GR-3 | Now line; auto-follow mode keeps now at a configurable position (e.g. 25% from left) | M |
| GR-4 | Pan, zoom (time and distance independently), zoom presets (1 h, 3 h, 6 h) | M |
| GR-5 | Headcode labels along paths with collision avoidance | M |
| GR-6 | Hover tooltip: headcode, location, planned/actual/forecast time, delay | M |
| GR-7 | Colour by category or by delay band (selectable) | S |
| GR-8 | Multiple corridor tabs; split view of two corridors | S |
| GR-9 | Smooth at 500 trains over 6 h window (≥ 30 fps while panning) | M |
| GR-10 | Export PNG/PDF of current view | S |

## Re-planning
| ID | Requirement | P |
|---|---|---|
| RP-1 | Select a train; side panel with its schedule (plan / actual / forecast / delay per location) | M |
| RP-2 | Drag a path in time — whole path or "from this location on" | M |
| RP-3 | Hold at location (extend dwell) by dragging the stop segment or entering minutes | M |
| RP-4 | Change platform/line at a location | M |
| RP-5 | Add/skip stop, terminate short, cancel, reinstate | S |
| RP-6 | Clone path as ad-hoc train with new headcode | S |
| RP-7 | Undo/redo every working-plan edit; edit history list | M |
| RP-8 | Working-plan edits never violate minimum running time (snap with a warning) | M |
| RP-9 | Regulation list per location derived from the working plan order | S |

## Disruptions
| ID | Requirement | P |
|---|---|---|
| DIS-1 | Draw a block/possession: section(s) × time range, by track | M |
| DIS-2 | Speed restriction: section × time range, running-time multiplier or added seconds | S |
| DIS-3 | Notes pinned to a time/location | S |
| DIS-4 | Forecasting respects disruptions | M |

## Forecast, conflicts, suggestions
| ID | Requirement | P |
|---|---|---|
| FC-1 | Forecast every active and upcoming train from latest actual along working plan | M |
| FC-2 | Recompute within 50 ms of an event for 500 trains | M |
| FC-3 | Detect headway, single-line opposing, platform and disruption conflicts; mark on graph | M |
| FC-4 | Conflict list panel; click to focus | M |
| FC-5 | Rule-based resolution suggestions with predicted delay impact; preview and accept | S |
| FC-6 | Optimised re-planning over a horizon (S20: Plan ▸ Re-plan, OR-Tools CP-SAT) | C |

## Session and reporting
| ID | Requirement | P |
|---|---|---|
| SES-1 | Save/resume session (SQLite): events, edits, disruptions, notes | M |
| SES-2 | Delay table per train and location; CSV export | S |
| SES-3 | Post-run review: replay a saved session's graph | S |

## Assist (opt-in)
| ID | Requirement | P |
|---|---|---|
| AS-1 | Send route request for next regulation step after confirmation | C |
| AS-2 | TD interpose for ad-hoc trains after confirmation | C |

Both built in S19 (`05-…md` §14): off by default, turned on for one live session.

## Non-functional
- Windows 10/11 x64, single-file self-contained publish.
- Core logic platform-neutral and unit-tested; ≥ 80% coverage on Gateway, Timetable, Planning.
- All times in sim seconds; UI formats as HH:mm(:ss), with ½-minute display option.
- Australian English in UI strings; UK railway terms where SimSig uses them (headcode, TIPLOC, Up/Down).
- Logging with Serilog to rolling files; no credentials in logs.
