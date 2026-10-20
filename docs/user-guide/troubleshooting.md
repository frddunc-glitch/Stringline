# Troubleshooting and logs

## Connecting

### The status bar stays on "Connecting to…" or "Disconnected — retrying"

- Check the sim is running, and that **Interface Gateway** was ticked **before** it started ([how](gateway.md)).
- Check the host and port in the Connect dialog: `localhost` and 51515 unless you changed them.
- Check nothing else is using port 51515: another sim, another Stringline, or a `stringline replay-server`.
- On another computer: check the firewall on the computer running SimSig lets port 51515 through ([Firewalls](gateway.md#firewalls)).

Stringline keeps retrying, so it connects as soon as SimSig is ready.

### "SimSig refused the connection"

A payware sim did not accept your SimSig forum username or password. Stringline does not retry, so that the forum login is not hammered. Correct the details and connect again. Check you can log in to the SimSig forum with them, and that your account has a licence for the sim.

### SimSig cannot open the gateway port

If SimSig says it cannot **open a TCP port for hosting the information gateway**, Windows would not let it use port 51515. Usually something else already has it: close the other sim or program (Microsoft's free [TCPView](https://learn.microsoft.com/sysinternals/downloads/tcpview) shows which program holds a port). Otherwise a firewall or anti-virus program is blocking it.

## The timetable

### No timetable is offered when the sim connects

The bar offers the timetables in SimSig's Timetables folder whose sim matches. If yours are elsewhere, use **Browse…**, or set the folder in [Tools ▸ Options ▸ Folders](settings.md#folders).

### Trains are grey, or "unmatched"

A grey train is one Stringline could not match to the timetable: a train added in the sim, a different timetable from the one loaded, or a train far off its booked path. Check the timetable is the one the sim is running (the status bar names it), or [link the train by hand](re-planning.md#the-right-click-menu).

### A train is "Not running" but SimSig runs it (or the other way round)

Stringline [works out](timetables-and-profiles.md#trains-that-will-not-run) which trains SimSig will run from the time the sim started. A train seen in the sim always runs. For one Stringline has wrong, select it and press **Run anyway**. Please [report it](#reporting-a-problem) with the timetable and the start time, so the rule can be put right.

### A corridor looks wrong

Corridors are worked out from the timetable. Change them in the [profile editor](timetables-and-profiles.md#the-profile-editor), or **Tools ▸ Regenerate corridors…** to start again.

## The graph

### Too many headway conflicts

The headway may be too long for the sim's signalling. See [Headways](conflicts-and-suggestions.md#headways).

### A drag stops, or a grip will not move

A grip cannot move a train into the past, before its last report, or faster than its minimum running time; a hollow grey grip cannot move at all. Hover over it for the reason. See [Grips](re-planning.md#grips).

### F1 or Help ▸ User guide does nothing

The guide is written to `%LOCALAPPDATA%\Stringline\help` and opened in your web browser. If nothing opens, check Windows has a default browser (Settings ▸ Apps ▸ Default apps), and look in the log.

## Logs

Stringline writes a log file a day to `%LOCALAPPDATA%\Stringline\logs` (**Help ▸ Open log folder**), and keeps 14 days of them. Each run starts with the version and system details. The logs never hold your SimSig password.

To get more detail, tick **Detailed logging (Debug)** in [Tools ▸ Options ▸ Logging and housekeeping](settings.md#logging-and-housekeeping), make the problem happen again, then untick it.

## Reporting a problem

1. **Help ▸ About Stringline ▸ Copy version** copies the version and system details.
2. Open an issue on the [issue tracker](https://github.com/frddunc-glitch/Stringline/issues), paste the details, and say what you did and what happened.
3. Attach the log for that day, and if you can, a [recording](sessions-and-review.md#recording) or the saved session that shows the problem.

Recordings and sessions of payware sims contain data from the sim: share them only as SimSig's terms allow.
