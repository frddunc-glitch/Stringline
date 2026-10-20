# Assist: route requests and interposing

Stringline normally only reads from SimSig. **Assist** lets it send two kinds of command back, when you ask:

- a **route request** for a train's move, such as the route out of the platform a regulation step has it leave from;
- a **TD interpose**: putting a description (usually a headcode) into a train describer berth, for an ad-hoc train or one that has lost its description.

Assist is **off** until you turn it on, and it works only while connected to SimSig: never in a replay or a review.

## Turning it on

Choose **Session ▸ Assist: send commands to SimSig…**. Stringline says what assist does and asks you to confirm. Choose **Allow for this session**.

Assist then stays on until the session ends: when you disconnect, the connection fails, or you start another session. The next session starts with it off again. To turn it off sooner, choose the same menu item again (it is ticked while on).

> **Multiplayer.** In a multiplayer game, a route you request or a description you interpose acts on the **shared** sim, as if you had done it on the panel. Only act on your own panel, and agree it with the other players first.

## Requesting a route

1. Right-click a train on the graph and choose **Request route in SimSig…**, or right-click a row in the [regulation list](regulation.md) and choose the same.
2. The dialog says what the train does there: for example "2S01 calls at Bletchley (platform 2) at 08:06, from Denbigh Hall towards Milton Keynes."
3. Give **either** the route's ID as SimSig names it (for example `RWL123A`), **or** the signals at each end of the route (**From signal** and **To signal**, for example `SWL12` and `SWL14`). The lists offer every route and signal SimSig has named, once Stringline has its snapshot. That needs **Also subscribe to train describer and signalling messages** ticked when you [connect](connecting.md).
4. Press **Send to SimSig**. Nothing is sent until you do; **Cancel** sends nothing.

SimSig sets the route only if it can (the route must be free and not locked against), and it does not say whether it did. **Check the panel.**

Stringline **remembers** the route in the sim's [profile](timetables-and-profiles.md), against the move: the location, platform or line, and where the train comes from and heads for. The next train making the same move gets it filled in ("Last time: RWL123A"), so you only need to check it and press Send.

## Interposing a description

1. Right-click a train on the graph, or a row in the regulation list, and choose **Interpose description in SimSig…**.
2. Pick or type the **Berth** as SimSig names it (for example `0235`). The list offers every berth a description has stepped through since you connected. If SimSig shows the train's description in a berth already, the dialog says which.
3. The **Description** is the train's headcode; change it if you need to.
4. Press **Send to SimSig**.

## What is logged

Every command sent is written to the app's log, with what was sent ("Assist: sending /topic/TD_ALL_SIG_AREA {"routerequest":{"route":"RWL123A"}}"), as is turning assist on and off. See [Troubleshooting and logs](troubleshooting.md).
