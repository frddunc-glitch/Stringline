# 03 — SimSig Interface Gateway Integration

Sources: SimSig wiki page *usertrack:interface_gateway* (official; read it in a browser — it blocks automated access), and the community reference by J. Deeth (gist `JDeeth/95ce3242392fe9124e58095dc73cdccf`, as of SimSig 5.21, Sept 2022). **Everything here must be confirmed against real recordings in Session 2**, and this document updated.

## 1. Transport
- SimSig hosts a STOMP server; default port **51515**. Do not run anything else on that port.
- Enable "Interface Gateway" in SimSig's start/options dialog before starting the sim.
- Use STOMP 1.1 framing: `CONNECT` with `accept-version:1.1`, `host`, and for payware sims `login`/`passcode` = SimSig forum username/password. Expect a delay while SimSig authenticates.
- Frame terminator is NUL (`\0`). Bodies are UTF-8 JSON (content-type may be `text/plain`).
- Some clients have triggered access violations in SimSigLoader with unusual connect sequences — keep the handshake minimal and conventional: CONNECT → wait for CONNECTED → SUBSCRIBE(s).
- Reconnect strategy: exponential back-off 1 s → 30 s; resubscribe; request snapshot if TD topic used.

## 2. Topics

| Topic | Content | We use it for |
|---|---|---|
| `/topic/TRAIN_MVT_ALL_TOC` | `train_location`, `train_delay` | Actual running (core) |
| `/topic/SimSig` | `clock_msg`; accepts `idrequest` | Sim clock, speed, pause, sim name |
| `/topic/TD_ALL_SIG_AREA` | `CA_MSG`, `CB_MSG`, `CC_MSG`, `SG_MSG`; accepts commands | Optional: train presence between timing points, disruption hints, assist |
| `/topic/TRAIN_MVT_SUMMARY` | Reportedly sent in response to a `ttsummary` request | **Investigate in Session 2** — could expose the loaded timetable live |

## 3. Messages received

Values are often strings even when numeric/boolean (`"True"`, `"16250"`). All converters must accept string or native forms.

### 3.1 `train_location`
```json
{"train_location":{"headcode":"5M01","uid":"4","action":"pass","location":"INTJN","platform":"5","time":15445}}
{"train_location":{"headcode":"1O28","uid":"5","action":"pass","location":"SVC92","platform":"","time":15310,"aspPass":6,"aspAppr":2}}
```
- `action`: `arrive` | `depart` | `pass`.
- `location`: TIPLOC of a timing point **or** a signal ID — distinguish by matching against WTT/profile TIPLOCs; keep signal passes for optional fine positioning.
- `platform`: platform at a station or line taken at a junction.
- `uid`: sim-internal, unique within the sim run — **our primary key for a live train**, not the WTT UID.
- `time`: sim seconds after midnight (int).

### 3.2 `train_delay`
```json
{"train_delay":{"headcode":"5M01","uid":"","delay":-240}}
```
Seconds, negative = early. Reported as expected entry delay; not reliably sent. Use only as a hint for trains not yet in the sim.

### 3.3 `clock_msg`
```json
{"clock_msg":{"area_id":"waterloo","clock":15720,"interval":500,"paused":false}}
```
Sent on each sim-minute change and on speed/pause change. Speed ratio = 500 / interval. `idrequest` re-sends the last one (may be stale — don't trust its clock for more than identity).

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
| `signal` | S | `aspect` (0 red … 6 green), `rset`, `bpull`, `appr_lock`, `auto`, `trts` |
| `route` | R | `is_set` (ID format `R{signal}{A..}{M/S/C/V}`) |
| `ulc` (subroute) | U | `locked`, `overlap` |
| `flag` | L | `state` 0–63 |
| `frame` | F | `release_given`, `release_taken`, `reminder` |
| `crossing` | G | `state`, reminders, `blocked` |
| `ahb` | H | `state`, `failed`, `failed_ack` |

### 3.6 Snapshot
Send `{"snapshot":{}}` on TD topic. Reply is **one JSON object with many duplicate `SG_MSG` keys**. Parse with `Utf8JsonReader`, emitting one event per property — never `JsonDocument`/`JsonSerializer` on the whole body.

## 4. Commands (outbound) — only for the opt-in assist feature
| Command | Topic | Body |
|---|---|---|
| TD step / cancel / interpose | TD_ALL_SIG_AREA | `{"ca_msg":{"from":"..","to":"..","descr":".."}}`, `{"cb_msg":{"from":".."}}`, `{"cc_msg":{"to":"..","descr":".."}}` |
| Route request | TD_ALL_SIG_AREA | `{"routerequest":{"route":"RHJ1AM"}}` or `{"routerequest":{"fromSignal":"SHJ3","toSignal":"SHJ5"}}` |
| Replace signal | TD_ALL_SIG_AREA | `{"bpull":{"signal":"SHJ10"}}` |
| Snapshot | TD_ALL_SIG_AREA | `{"snapshot":{}}` |
| Identity / clock | SimSig | `{"idrequest":{}}` |
| Timetable summary (TBC) | SimSig? | `{"ttsummary":{}}` — verify |

Malformed JSON makes SimSig show an error dialog to the host (once). All outbound bodies must come from tested builders.

## 5. Sim time
- Integer seconds after midnight on the sim's start day; runs past 86 400 for overnight sims → `SimTime` is an unbounded int, formatted modulo 24 h with a day marker.
- WTT times are on the same basis.

## 6. Things to verify in Session 2 (record answers here)
1. Exact CONNECT/CONNECTED headers SimSig accepts/returns; heart-beat support.
2. Does `train_location` fire for every WTT timing point, including passes with no platform?
3. Is `location` ever a TIPLOC not in the WTT (e.g. entry/exit points)?
4. Behaviour on train entry, exit, splitting/joining, and new-working (headcode change: does uid persist?).
5. Is `train_delay` sent on entry only?
6. Does `ttsummary` exist, which topic, what payload?
7. Message rates on a busy sim at 4× and 16× speed.
8. Payware authentication failure behaviour (ERROR frame? disconnect?).
9. Multiplayer: does each client see all areas' trains?
