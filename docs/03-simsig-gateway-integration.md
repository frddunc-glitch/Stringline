# 03 — SimSig Interface Gateway Integration

Sources: SimSig wiki page *usertrack:interface_gateway* (official; read it in a browser — it blocks automated access), and the community reference by J. Deeth (gist `JDeeth/95ce3242392fe9124e58095dc73cdccf`, as of SimSig 5.21, Sept 2022). **Checked in S3 against Fred's recordings** (October 2026): Waterloo, a free sim, at 1×, at 4×/16× and with a track circuit failure, in `tests/fixtures/`, plus a short payware Penrith recording in `local-fixtures/`. §6 lists what was verified; parser tests in `RecordedFixtureTests` pin each fact down.

## 1. Transport
- SimSig hosts a STOMP server; default port **51515**. Do not run anything else on that port.
- Enable "Interface Gateway" in SimSig's start/options dialog before starting the sim.
- Use STOMP 1.1 framing: `CONNECT` with `accept-version:1.1`, `host`, and for payware sims `login`/`passcode` = SimSig forum username/password. Expect a delay while SimSig authenticates (about 1 s for Penrith). SimSig answers `CONNECTED` with exactly `version:1.1` and `server:SimSig`, and no `heart-beat` header.
- Frame terminator is NUL (`\0`). Bodies are UTF-8 JSON (content-type may be `text/plain`).
- Some clients have triggered access violations in SimSigLoader with unusual connect sequences — keep the handshake minimal and conventional: CONNECT → wait for CONNECTED → SUBSCRIBE(s).
- Reconnect strategy: exponential back-off 1 s → 30 s; resubscribe; request snapshot if TD topic used.

## 2. Topics

| Topic | Content | We use it for |
|---|---|---|
| `/topic/TRAIN_MVT_ALL_TOC` | `train_location`, `train_delay` | Actual running (core) |
| `/topic/SimSig` | `clock_msg`; accepts `idrequest` | Sim clock, speed, pause, sim name |
| `/topic/TD_ALL_SIG_AREA` | `CA_MSG`, `CB_MSG`, `CC_MSG`, `SG_MSG`; accepts commands | Optional: train presence between timing points, disruption hints, assist |
| `/topic/TRAIN_MVT_SUMMARY` | The reply to `{"ttsummary":{}}` sent to `/topic/SimSig` (§3.7) | Optional: train status (not yet run / active / done) and current location for every timetabled train |

## 3. Messages received

Values are often strings even when numeric/boolean (`"True"`, `"16250"`). All converters must accept string or native forms. Observed: `clock_msg` and `train_location`/`train_delay` numbers and booleans are native JSON; every TD and `SG_MSG` value is a string, as are the `ttsummary` delays.

### 3.1 `train_location`
```json
{"train_location":{"headcode":"5M01","uid":"4","action":"pass","location":"INTJN","platform":"5","time":15445}}
{"train_location":{"headcode":"1O28","uid":"5","action":"pass","location":"SVC92","platform":"","time":15310,"aspPass":6,"aspAppr":2}}
```
- `action`: `arrive` | `depart` | `pass`.
- `location`: TIPLOC of a timing point **or** a signal ID — distinguish by matching against WTT/profile TIPLOCs; keep signal passes for optional fine positioning. Signal IDs start with `S` but may have more letters (`S457`, `SVC92`, `SM269`). Signal passes are about two thirds of all `train_location` messages and always carry `aspPass`/`aspAppr` (native ints, 0 for red).
- `platform`: platform at a station or line taken at a junction. In practice it is given on departures and on passes at junctions, and is usually **empty on arrivals** (`arrive WLOO ""`, then `depart WLOO "11"`). Always empty at signals.
- `uid`: sim-internal, unique within the sim run — **our primary key for a live train**, not the WTT UID.
- `time`: sim seconds after midnight (int).

### 3.2 `train_delay`
```json
{"train_delay":{"headcode":"5M01","uid":"","delay":-240}}
```
Seconds, negative = early. Reported as expected entry delay; not reliably sent. Use only as a hint for trains not yet in the sim. Often sent several times per train with changing values (2F02: `0`, then `-173`). `uid` was always empty at Waterloo; at Penrith (payware) it held a WTT-style UID (`"1127S"` for headcode `1127`, `"178HS"` for `178H`).

### 3.3 `clock_msg`
```json
{"clock_msg":{"area_id":"waterloo","clock":15720,"interval":500,"paused":false}}
```
Sent on each sim-minute change and on speed/pause change (those land on any second). Speed ratio = 500 / interval. `idrequest` re-sends the last one (may be stale — don't trust its clock for more than identity).
- Intervals seen: `500` 1×, `124`/`125` 4×, `62` 8×, `31` 16×, and **`1` for SimSig's flat-out setting** — the real speed then depends on the PC (about 32× at Waterloo: 60 sim s per 1.9 s). `ClockEvent.IsMaximumSpeed` flags it; measure the speed from successive clocks instead.
- Before a sim has started, the `idrequest` reply is a placeholder: `clock:0, interval:1000, paused:false` (seen at Waterloo and Penrith). It can also carry the previous run's interval (`31` while actually at 4×) until the next real tick.

### 3.4 TD messages
```json
{"CA_MSG":{"area_id":"waterloo","from":"0243","to":"0235","descr":"2R02","msg_type":"CA","time":"16250"}}
{"CB_MSG":{"area_id":"waterloo","from":"0130","descr":"","msg_type":"CB","time":"16249"}}
{"CC_MSG":{"area_id":"waterloo","to":"0149","descr":"1A30","msg_type":"CC","time":"16257"}}
```

### 3.5 `SG_MSG` (by `obj_type`)
| obj_type | prefix | key fields |
|---|---|---|
| `track` | T | `clear` |
| `point` | P | `dn dr cn cr kn kr locked` |
| `signal` | S | `aspect` (0 red … 6 green), `rset`, `bpull`, `appr_lock`, `auto`, `trts`, `lp`, `stackN`, `stackX` |
| `route` | R | `is_set` (ID format `R{signal}{A..}{M/S/C/V}`) |
| `ulc` (subroute) | U | `locked`, `overlap` |
| `flag` | L | `state` 0–63 |
| `frame` | F | `release_given`, `release_taken`, `reminder` |
| `crossing` | G | `state`, reminders, `blocked` |
| `ahb` | H | `state`, `failed`, `failed_ack` |

Verified at Waterloo for track, point, signal, route, ulc and flag, all values strings (`"True"`, `"0"`). Frame, crossing and AHB objects do not exist there and are still unverified. Every `SG_MSG` also carries `area_id`, `msg_type:"SG"` and `time`.

### 3.6 Snapshot
Send `{"snapshot":{}}` on TD topic. Reply is **one JSON object with many duplicate `SG_MSG` keys**. Parse with `Utf8JsonReader`, emitting one event per property — never `JsonDocument`/`JsonSerializer` on the whole body. At Waterloo: 2 974 `SG_MSG`s, 419 KB, and **only** `SG_MSG` — berth contents are not included, so a TD berth map can only be built from the CA/CB/CC steps that follow.

### 3.7 `ttsummary` reply
`{"ttsummary":{}}` sent to `/topic/SimSig` is answered on `/topic/TRAIN_MVT_SUMMARY` with a **top-level JSON array**, not an object — the only such message. One element per timetabled train (1 779 at Waterloo, 99 KB):
```json
[{"headcode":"1W51","uid":"","status":"active","delay":"0","locationId":"WLOO","location":"Waterloo","platformId":"","platform":""},
 {"headcode":"2C05","uid":"","status":"ok","delay":"0"}]
```
`status` was `ok` (by the counts, not yet entered), `active` (in the sim; only then are the location fields present) or `done`. `uid` was always empty; `delay` is a string. Parsed into one `TimetableSummaryEvent`. It does not include the timetable itself (times, paths), so the WTT file stays the plan source.

## 4. Commands (outbound) — only for the opt-in assist feature
| Command | Topic | Body |
|---|---|---|
| TD step / cancel / interpose | TD_ALL_SIG_AREA | `{"ca_msg":{"from":"..","to":"..","descr":".."}}`, `{"cb_msg":{"from":".."}}`, `{"cc_msg":{"to":"..","descr":".."}}` |
| Route request | TD_ALL_SIG_AREA | `{"routerequest":{"route":"RHJ1AM"}}` or `{"routerequest":{"fromSignal":"SHJ3","toSignal":"SHJ5"}}` |
| Replace signal | TD_ALL_SIG_AREA | `{"bpull":{"signal":"SHJ10"}}` |
| Snapshot | TD_ALL_SIG_AREA | `{"snapshot":{}}` |
| Identity / clock | SimSig | `{"idrequest":{}}` |
| Timetable summary | SimSig | `{"ttsummary":{}}` — reply on `TRAIN_MVT_SUMMARY` (§3.7) |

Malformed JSON makes SimSig show an error dialog to the host (once). All outbound bodies must come from tested builders.

## 5. Sim time
- Integer seconds after midnight on the sim's start day; runs past 86 400 for overnight sims → `SimTime` is an unbounded int, formatted modulo 24 h with a day marker.
- WTT times are on the same basis.

## 6. Verified from Fred's recordings (S3)
Answers to the questions S2 left open. "Fred" means Fred's own observation at the sim; everything else is from the recordings.

1. **Handshake.** Our CONNECT is accepted as is. CONNECTED is `version:1.1`, `server:SimSig`, with no `heart-beat`. With no heart-beats, nothing dropped: sessions of 27 and 73 min, including paused stretches (Fred: no dialogs on connect or disconnect, no drops). Silence lasted at most 60 s, because `clock_msg` ticks each sim minute while running.
2. **Every WTT timing point is reported.** For 4×/16× trains with a single WTT entry, all 801 timing points between a train's first and last reported location had a `train_location`, including passes. Passes at stations without a platform are reported with an empty platform. **Found in S5:** SimSig reports every timing location the train passes, *including ones its own schedule leaves out*: Waterloo's 1W51 is booked WLOO–CJML–WIMB but reports INTJN, VAUX, QTRDBAT and EARLSFD too. A reported TIPLOC is therefore not evidence that it is on the train's WTT path.
3. **Non-signal locations are always WTT TIPLOCs.** No entry point (`EUPMNS`, `ECY457`, …) ever appeared; trains entering show up first at a signal or timing point. Four WTT TIPLOCs (`EASTPUT`, `LINFD`, `CLAPYD`, `LONGH`) were never reported, presumably because no recorded train ran over them.
4. **Train life cycle.** Trains already in the sim at the start report `arrive` at their location 10 s after the start. A new working keeps the **uid** and changes the headcode (uid 15: `2F02` → `2H01`). A divided portion gets a **new uid** and starts with `depart` (`2P05`, uid 13, from `1W51` at Waterloo). Some headcodes carry a `-1`/`-2` suffix (`2O03-1`, `5Y00-1` → `2L00-1`). **Checked in S4: the suffix is in the WTT itself** — Waterloo's WTT has both `2O03` and `2O03-1` as separate workings, and 160 suffixed headcodes in all — so the matcher (S5) must match headcodes exactly, not strip the suffix. No message marks a train leaving the sim. No headcode was ever shared by two uids.
5. **`train_delay`** comes several times per train before it enters, not just once, with changing values; `uid` is empty for free sims (§3.2).
6. **`ttsummary` exists** (§3.7).
7. **Rates.** Waterloo 1× with TD: about 1.9 frames/s on average (`SG_MSG` 1.6/s, `train_location` 0.14/s), plus a 419 KB snapshot on connect. 4× and 16× without TD: about 3 `train_location`/s. Flat out with TD: about 27 frames/s. All easily handled.
8. **Wrong payware password** (Fred): SimSig answers CONNECT with ERROR; `record` reports it and exits without retrying.
9. **Multiplayer:** not tested yet; all recordings were single player.

Also learnt:
- **Failures are not reported.** Fred failed track circuit T1620 in `waterloo-failure.sslog`. The only sign is `SG_MSG track T1620 clear:"False"` from 05:22:11 to 05:54:35, about 32 minutes, where a train normally occupies it for about 12 s. Detecting failures (S13) would mean spotting an occupied track with no train.
- Non-standard headcodes are common in payware sims (`178H`, `AR85`, `1127` at Penrith); `Headcode` keeps them as they are.
- The `train_location` time is the sim time of the event, so it runs up to a minute ahead of the last `clock_msg`.

## 7. How Stringline implements this (S2)
- **Handshake.** `CONNECT` with `accept-version:1.1` and `host:<host>`, plus `login`/`passcode` only when a username is given. No `heart-beat` header, so neither side sends heart-beats (STOMP default 0,0); the frame reader still skips bare EOLs between frames in case SimSig sends them. We wait up to 60 s for CONNECTED, because payware sims authenticate online.
- **After CONNECTED:** `SUBSCRIBE` (ids 0, 1, …, `ack:auto`) to each topic; then `{"idrequest":{}}` to `/topic/SimSig` so the clock arrives straight away; then `{"snapshot":{}}` if the TD topic is subscribed.
- **ERROR in reply to CONNECT** is treated as a credential problem: `GatewayConnectException`, **no retry**, so a wrong password doesn't hammer the forum login. An ERROR after connecting, a dropped connection, a refused connection or a CONNECT timeout all trigger a reconnect, with back-off 1 s → 2 → 4 … → 30 s, reset after each successful CONNECTED.
- **Stop:** `DISCONNECT` (no receipt requested), then close.
- **Frames:** `content-length` honoured (bodies may contain NUL), otherwise read to NUL; CR LF tolerated; header escapes decoded except on CONNECT/CONNECTED; 64 MiB frame limit for big snapshots.
- **Parsing:** each top-level property of a body becomes one event, so duplicate `SG_MSG` keys work. Message type names match case-insensitively. Inner objects are read as a flat field map (last duplicate wins, nested values skipped), then typed leniently. Required fields: `train_location` uid + location + time (headcode optional, unknown `action` → `Unknown`); `train_delay` delay; `clock_msg` clock + interval; `CA_MSG` from + to, `CB_MSG` from, `CC_MSG` to; `SG_MSG` obj_id (`obj_type` inferred from the ID prefix letter if absent). Anything else becomes an `UnknownEvent` with the raw JSON and is logged. (S3: a top-level array is the `ttsummary` reply and becomes one `TimetableSummaryEvent`.)
- **`ttsummary`:** `record --ttsummary` subscribes to `/topic/TRAIN_MVT_SUMMARY` and sends `{"ttsummary":{}}` to `/topic/SimSig` after each connect. The reply is recorded like any other frame; S3 found its format (§3.7).

### Recording checklist (👤 Fred; done for Waterloo and Penrith in S3, repeat for new sims)
1. Free sim, normal speed, 30–60 min: `stringline record --td --ttsummary --out tests/fixtures/<sim>-1x.sslog`. Save the `.wtt` you loaded next to it.
2. Same or another sim at 4× or faster, 30–60 min, without `--td` (movement rates only): `--out …-4x.sslog`.
3. A session with an in-game failure (track circuit, points or signal), with `--td`, and if possible a split/join or a new working (headcode change).
4. A payware sim with `--user <forum name>` → `local-fixtures/`. Also try a wrong password once and note what SimSig does (question 8).
5. Note answers to the questions in §6 in a short notes file next to the recordings. The summary that `record` prints at the end answers question 7.

## 8. Replay (S3)
- **`ReplayTimeline`** plays a recording's frames using the **wall-clock gaps as recorded** (`t`), divided by a speed factor (∞ = as fast as the consumer reads). Pause, resume, speed changes and seeks apply at once, even mid-wait. Seeking forward hands out the skipped frames immediately (flagged fast-forward) so downstream state stays complete. Seeking back restarts from the first frame and flags it as rewound. A seek target matches the first frame stamped at or after it (`sim`, minute resolution).
- **`ReplaySource : ISimEventSource`** = timeline + `MessageParser`. Events are stamped with the time they are replayed (option: keep the recorded times), so arrival times match the replay speed as they would live. After a backward seek it yields a `ReplayRewoundEvent` first: consumers must drop their state.
- **`stringline replay-server file.sslog [--speed 8] [--port 51515] [--host 127.0.0.1] [--start 09:00] [--no-wait] [--exit-at-end]`** is a fake SimSig on STOMP:
  - Answers CONNECT with SimSig's exact CONNECTED headers, accepting any login.
  - Sends each frame, with `content-length`, to the clients subscribed to its topic. It does not wait for slow or absent clients, as a running sim would not.
  - Waits for the first client to be ready before playing: its first SEND (Stringline sends `idrequest` after subscribing), or 1 s after its first SUBSCRIBE.
  - `--start` pre-rolls the recording to that sim time before listening, so earlier frames build state but are not sent.
  - Answers `idrequest` with the latest `clock_msg`, `snapshot` with the latest `SG_MSG` of every object seen (rebuilt in SimSig's duplicate-key form), and `ttsummary` with the latest recorded summary. Replies go to the topic, as SimSig's would.
  - Logs and ignores other commands, and logs malformed JSON as the error dialog SimSig would show. Sends RECEIPT when asked.
  - Keys: Space pauses or resumes, `+` and `-` double or halve the speed, Q quits. `--speed 0` plays flat out.
  - Listens on loopback only unless `--host any` or an address is given.
- Recordings are replayed verbatim: a recording made at 1× and replayed at 8× still says `interval:500`. That is another reason to measure speed from clock arrivals (§3.3).
