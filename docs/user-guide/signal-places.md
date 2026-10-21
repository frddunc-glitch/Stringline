# Signal places and sharing a profile

Once a signal is placed, Stringline uses it on the graph and in the forecast: see [Reading the graph](reading-the-graph.md#between-timing-points).

A timetable only names timing locations, so on its own Stringline knows where a train is only when it passes one. Trains also report every signal they pass, and with the train describer (TD) topic, every track circuit they enter and every berth their description steps into. Stringline learns where these are from the trains it sees, and keeps them in the sim's profile, so a profile you have played with knows the line in more detail than the timetable does. You can share it with other players of the same sim.

## How places are learned

A place is measured in **run time**, the way the graph's corridors are spaced. A signal half-way through the run from Vauxhall to Queenstown Road is at 0.5, whatever the distance. Each train that passes it between the two timing locations gives one sample. The place is the middle of the samples.

- Trains held at a signal on the way (a red aspect on approach), or much slower than the fastest train seen over the section, are left out, because their times say little about where the signal is.
- A train starting or stopping at either timing location passes a signal near it at a different share of its run time than one running through. Each kind of train has a place of its own: **running through**, **starting**, **stopping**, or **starting and stopping**.
- Each direction has its own places too: signals face one way.
- **Track circuits** are put to the train last at the signal before them, when only one train is near. The place is where its front enters the track circuit, and the end is where the track circuit clears behind it. A track circuit occupied with no train near (a failure, say) teaches nothing.
- **Berths** are placed by the train whose headcode steps into them. Berth `0457` goes with signal `S457`.

A place is **used** once enough trains have passed it (three, unless you change it) and their samples are close together (the middle half of them within a tenth of the section). Until then it is still **learning**. Stringline draws and forecasts with used places (and those set by hand), and offers them for [holding a train at a signal](re-planning.md#holding-at-a-signal); a place still learning is not offered.

Learning happens in every live session, replay and review, without slowing the graph. What a session taught goes into the profile when the session ends (or the replay finishes), and when you save the profile in the editor. Stringline remembers each session and how far it learned from it, so resuming, reviewing or replaying it again never counts the same train twice. Track circuits and berths need the **Subscribe to the TD topic** box ticked in the Connect dialog. Signals do not.

To turn learning off, or to change how many trains a place needs, see [Settings: Profile](settings.md#profile).

## The Signals tab

**Tools ▸ Edit profile… ▸ Signals** shows what has been learned. The line at the top counts the places that are used, still learning, or set by hand.

Pick a **Section** to see its places in order along the run, the direction the section is named in first. A section on a corridor is named after it; places between timing locations that are not next to each other on a corridor are under **Off the corridors**. For each place:

- **Kind**, **ID**, **Running** (the direction) and **Trains** (running through, starting, stopping);
- **Place**, from 0 at the first location to 1 at the second, and a track circuit's **End**;
- **Samples** (trains that have passed it), **Spread** (how far apart they were), and **State**: Used, Learning or Hand-set;
- **Notes**: the line the trains reported, the signal a track circuit follows or a berth goes with, and when it was last learned.

To correct a place, type a new **Place**. It is then **hand-set**: learning still counts the trains that pass it, but never moves it. **Add…** puts in a place by hand and **Delete** removes one. **Reset section…** forgets the places learned in the section, both ways, and **Reset all…** forgets everything learned for the sim. Places set by hand stay, and learning starts again with the next trains. Press **Apply** or **OK** to keep your changes.

## Sharing a profile

A profile holds the sim's corridors, location names, category styles and learned places. To share one:

1. Load the sim's timetable, then choose **Tools ▸ Export profile…**. Stringline adds what the running session has learned so far and saves a `.json` file.
2. Send the file to the other player.
3. They load the same sim's timetable and choose **Tools ▸ Import profile…**. The file must be for the same sim (its `areaId`). They then choose:
   - **Merge**: keep their own corridors, names and styles, and pool the shared profile's learned places with theirs. Samples are added together. Places they set by hand stay where they are.
   - **Replace**: use the shared profile instead of their own.

Either way, the profile is saved and the graph is redrawn. Your profile file is at `%APPDATA%\Stringline\profiles\<sim>.json` (**Tools ▸ Open profile file**). See [Timetables and profiles](timetables-and-profiles.md).
