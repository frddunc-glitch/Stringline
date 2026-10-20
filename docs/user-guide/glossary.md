# Glossary

Railway words are used as SimSig uses them.

| Term | Meaning |
|---|---|
| **Actual** | Where and when a train really ran, as the sim reported it. Drawn as a heavy line. |
| **Ad-hoc train** | An extra train you add to the plan by cloning another ("Clone as ad-hoc…"). |
| **Area ID** | SimSig's short name for a sim, sent over the gateway (for example `waterloo`). Stringline names profiles by it. |
| **Assist** | Sending a route request or a TD interpose to SimSig from Stringline, when you turn it on. See [Assist](assist.md). |
| **Berth** | A train describer position on the panel, which holds a train's description. |
| **Board** | One train graph showing one corridor: the main graph, the split view's second graph, or a board window. All boards keep the same time. See [Boards](reading-the-graph.md#boards-split-view-and-board-windows). |
| **Block** (possession) | A section, or some of its tracks, closed for a time. Trains are held until it ends. |
| **Category** | A kind of train (express passenger, freight and so on) with its colour, line style and priority in the profile. |
| **Conflict** | Two trains, or a train and a block, that cannot both have what the plan gives them. See [Conflicts](conflicts-and-suggestions.md). |
| **Corridor** | A route through the sim drawn as one graph: its locations in order, Up end first. Each is a tab. |
| **Delay** | How late a train is against the WTT, in whole minutes. "On time" is within the minute either way. |
| **Down**, **Up** | The two directions of running. By convention Up is towards London (or the main centre). On the graph, the corridor's Up end is at the top. |
| **Dwell** | How long a train stands at a stop. |
| **Forecast** | Where a running train is expected to go from here, worked out from its latest report and the plan. Drawn dashed. |
| **Grip** | A handle on the selected train's line, dragged to re-plan it. See [Grips](re-planning.md#grips). |
| **Headcode** | A train's four-character reporting number, for example `1A23`. |
| **Headway** | The least time between two trains following each other on the same track. See [Headways](timetables-and-profiles.md#headways). |
| **Interface Gateway** | SimSig's feed of train movements, the clock and signalling, which Stringline reads. See [the gateway](gateway.md). |
| **Interpose** | Putting a description into a TD berth by hand. |
| **Knock-on** | Delay a late train passes on to the trains behind it. |
| **Now line** | The red line at the current sim time. |
| **Pass** | A timing point a train runs through without stopping. Shown in italics. |
| **Plan** (working plan) | The WTT with your changes. |
| **Plot** | A train control diagram printed over several pages at one scale, time running down the page. See [Plotting](exports-and-reports.md#plotting-a-train-control-diagram). |
| **Profile** | Stringline's settings for one sim: corridors, location names, distances, sections, headways and categories. |
| **Regulation** | Deciding the order trains take through a junction or section. See [the regulation list](regulation.md). |
| **Route request** | Asking SimSig to set a route, as clicking its entrance and exit signals does. |
| **Section** | The stretch of line between two neighbouring locations on a corridor. |
| **Session** | One connection or replay, saved as you go with your edits. See [Sessions](sessions-and-review.md). |
| **Single line** | A section with one track used in both directions. |
| **Speed restriction** | A section where trains must run more slowly for a time. |
| **`.sslog`** | A recording of the gateway's messages, made by Stringline. |
| **`.stringline`** | A saved session. |
| **STOMP** | The messaging protocol the gateway uses. |
| **TD** (train describer) | The panel's display of each train's description as it moves from berth to berth. |
| **TIPLOC** | A timing point location code, for example `WATRLMN` for London Waterloo. |
| **Timing point** | A location where the WTT times a train (to arrive, depart or pass). |
| **Unmatched train** | A train in the sim that Stringline could not match to the WTT. Drawn grey. |
| **WTT** | The working timetable: SimSig's `.wtt` file, with every train's booked times. |
