# The regulation list

The **Regulation** tab is a signaller's crib sheet: the movements still to come at one location, in the order of the working plan, for the next three hours. It shows at a glance where you have changed the order of trains from the WTT.

## Opening it

- Click the **Regulation** tab and pick the location in **At**, or
- right-click a location on the graph (or a train at a location) and choose **Regulation list at…**.

## What it shows

| Column | Shows |
|---|---|
| Plan | The planned time |
| Train | The headcode |
| Does | What the train does there: starts, calls (with its times), passes or terminates; first "hold at S457 until 09:12" if the plan [holds it at a signal](re-planning.md#holding-at-a-signal) on the way |
| Plat | The platform |
| Fcst | The forecast time |
| Late | Minutes late, when a minute or more |
| Order | "Ahead of 1A23" or "Behind 2B45" where the plan runs it the other way round from the WTT, or "Added" for an ad-hoc train |

Rows **out of WTT order** are bold amber. Hover over a row for its booked and planned times, forecast and the line it leaves by.

Cancelled trains, and trains that have already made the movement, are not listed.

## Using it

Re-plan on the graph (hold a train, swap two trains' order with the [grips](re-planning.md#grips), apply a [suggestion](conflicts-and-suggestions.md#suggestions)), and the list follows. Then signal the trains in the order it gives.

With [assist](assist.md) on, right-click a row to **Request route in SimSig…** for that movement, or **Interpose description in SimSig…** for its train. Each is confirmed before it is sent.

A hold at a signal is a reminder only, even with assist on: SimSig's gateway has no command for it, so hold the train on the panel.
