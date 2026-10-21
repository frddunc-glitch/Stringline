# Conflicts and suggestions

Stringline forecasts where each train will run, and looks for **conflicts**: places where two trains, or a train and a block, cannot both have what the plan gives them. It then suggests ways to resolve each one.

## The Conflicts tab

The **Conflicts** tab (its heading counts them) lists every conflict still to come, on every corridor, in time order. A row gives:

- a red badge for the [kind](#kinds-of-conflict);
- the time;
- where (the section, or the location and platform);
- the trains (the one that has to give way first);
- the delay needed to resolve it, in minutes.

Hover over a row for the exact delay and any knock-on. Click a row to focus the conflict: Stringline selects the first train, shows a corridor that has the conflict, stops following and centres the graph on it, and works out [suggestions](#suggestions).

On the graph, a conflict is a red ring where the train meets the clash, and a red band over the section (or along a platform's location) for the time it covers. Hover over either for the details; click it to focus it. The toolbar's **Conflicts** box hides the marks. The status bar counts the conflicts.

## Kinds of conflict

| Badge | Kind | Means |
|---|---|---|
| HW | Headway | A train follows another on the same track too closely. |
| OT | Overtake | A train would catch up and pass another on the same track. |
| SL | Single line | Two trains are planned on a single-line section in opposite directions at once. |
| PL | Platform | Two trains are planned in the same platform at once (with a margin). |
| BK | Block | A train is planned through a block or possession. |
| RT | Run time | A train is planned to run faster than it can. |

Conflicts are found with the forecast **without** knock-on: each train as if the others were not in its way, so you see the clash itself, not the delay it would cause. Clashes the WTT itself books (two trains it times closer than the headway) are not reported.

Very small conflicts are not shown: the least is set in [Tools ▸ Options ▸ Forecast and conflicts](settings.md#forecast-and-conflicts), along with the platform margin.

## Headways

A headway conflict's tooltip names the headway it used and where it came from, for example "2 min 30 s (from the WTT)". Each section's headway is set by hand in the profile, worked out from the learned signals, taken from the WTT where it books trains closer, or the default. See [Headways](timetables-and-profiles.md#headways).

Where signals are placed, Stringline also checks trains at each signal between timing points. A train starting from a stand is slow at first, so a train running through behind it can catch it up before the next timing point even when they are far enough apart at both. The conflict names the signal: "1F01 catches 2S01 at S457", and its tooltip says "1 min 45 s (from signal blocks, at S457)".

If headway conflicts appear where the sim's signalling clearly lets trains run closer, set that section's headway by hand in the profile editor, or lower the default.

## Suggestions

Focusing a conflict opens a panel under the list, "Resolve: 1F01 too close behind 2S01", with up to three suggestions. Each says what it does ("Hold 1F01 at Clapham Junction until 08:07 (+2 min)") and what it costs ("Weighted delay +2 min · clears 2 conflicts · 1 new"). Hover over one for the trains it makes later or earlier.

- **Preview** draws the trains it changes as ghosts on the graph.
- **Apply** makes the change, as one edit you can undo.

Suggestions hold the train that gives way at the last place before the clash where it can wait, or hold the other train instead so that the order swaps; for a platform clash they try another platform, and for a block another line. **Allow cancelling** also suggests terminating a train short, or cancelling one not yet started, to get round a block. What each kind of change costs when the suggestions are ranked is set in [Tools ▸ Options ▸ Suggestions](settings.md#suggestions).

"No suggestion" means neither train can wait anywhere before the clash. Re-plan by hand with the [grips](re-planning.md#grips).

## Re-planning the next hour

**Plan ▸ Re-plan the next 60 min…** (Ctrl+Shift+R) clears the conflicts ahead all at once, rather than one at a time. It works out which train goes first at each clash, where trains wait, and (if allowed) which trains move to another platform, for the least weighted delay. It thinks for up to 5 seconds, then shows its plan in the panel under the conflicts list:

- what it does ("Re-plan to 09:05: 4 holds, 1 platform change"), each change on its own line, and how the conflicts and delay come out ("Conflicts to 09:05: 21 → 12 · weighted delay +6 min");
- **Preview**, which draws the trains it changes as ghosts on the graph;
- **Apply**, which makes all the changes as one edit you can undo.

The plan is worked out on the sim as it stood when you asked. Unlike suggestions it is not worked out again as the sim moves on, and Apply refuses it once the plan has changed: re-plan again. Picking a conflict shows that conflict's suggestions instead; **Close** shuts the re-plan.

It holds trains where suggestions do: at a stop, at their first point, or at a platform they pass. It clears clashes between trains (headways, single lines, platforms). Blocks and runs planned too fast are left to their own suggestions. "Nothing better found" means no train that could wait or move clears the clashes: trains arriving now, say.

How far ahead it looks, how long it thinks, whether it may change platforms, and whether it uses the optimiser or the suggestion rules (one conflict at a time) are in [Tools ▸ Options ▸ Suggestions](settings.md#suggestions).

## Knock-on in the forecast

**Plan ▸ Forecast knock-on from other trains** (on by default) holds trains in the forecast for headways and single lines, so a late train delays the trains behind it, and the forecast lines show the knock-on. Turn it off to see each train's forecast on its own. It does not change which conflicts are listed.
