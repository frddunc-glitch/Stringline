# Privacy

Stringline keeps its files on your PC and sends nothing anywhere except to the SimSig Interface Gateway you connect it to. It has no telemetry, no analytics, no crash reporting, no update check and no account.

This page says what Stringline stores, where and how, what it sends and to whom, and how to remove all of it. The same text is in the app (**Help ▸ Privacy**), in the [user guide](https://github.com/frddunc-glitch/Stringline/blob/main/docs/user-guide/privacy.md) and on the [wiki](https://github.com/frddunc-glitch/Stringline/wiki/privacy).

## What is sent, and to whom

Stringline sends only to the SimSig gateway at the host and port you enter in **Session ▸ Connect to SimSig…**. That is usually `localhost`, which is your own PC. It sends:

- the connection request. For a payware sim this carries the SimSig forum user name and password you entered. SimSig checks them. Stringline sends them nowhere else;
- the gateway topics to listen to, and requests for the sim's state;
- with **Assist** turned on (it is off unless you turn it on), the route requests and train descriptions you confirm, one at a time.

The gateway is plain STOMP over TCP, as SimSig provides it, and is not encrypted. To `localhost` nothing leaves your PC. If you connect to SimSig on another PC (for example a multiplayer host), your login crosses your network unencrypted, as it would for any gateway client.

Nothing else is sent:

- a replay reads a file and uses no network;
- the user guide is part of the app and opens from your PC;
- links in the app (Help ▸ Getting started, the About window, "This page online" in the guide) open in your browser only when you click them.

## What is stored, and where

| What | Where | What is in it |
|---|---|---|
| Settings | `%APPDATA%\Stringline\settings.json` | The gateway's host and port, your SimSig user name, whether to remember the password, and the password itself only if you ticked **Remember** (see below). Also your options, the timetables you last used, the folders you chose and the window layout. |
| Sim profiles | `%APPDATA%\Stringline\profiles` | Each sim's corridors, location names, distances, headways and colours, and what Stringline has learned about the sim from its timetables and recordings (`.simdata.json`). Nothing about you. |
| Logs | `%LOCALAPPDATA%\Stringline\logs` | What the app did, one file a day, kept for 14 days unless you change it (Tools ▸ Options ▸ Logging and housekeeping). Each connection's line names the host, the port and your SimSig user name. Logs never hold your password. |
| Timetable backups | `%LOCALAPPDATA%\Stringline\timetable-backups` | A copy of each timetable file the timetable editor replaced, made just before it was replaced; the last 20 of each timetable are kept. |
| The user guide | `%LOCALAPPDATA%\Stringline\help` | The guide as web pages, written the first time you open help. |
| Unpacked parts of the app | `%TEMP%\.net\Stringline` | Libraries Windows unpacks from `Stringline.exe` the first time it runs. |
| Saved sessions | `Documents\Stringline\sessions` | The sim's messages, your plan changes, and the gateway's host and port. |
| Recordings | `Documents\Stringline\recordings` | The gateway's messages as they arrived, and its host, port and topics. Never your login. |
| Exports | `Documents\Stringline\exports`, or where you save them | The pictures, PDFs and CSV files you export. |
| Timetable drafts | `Documents\Stringline\drafts` | The timetables you are editing, autosaved, with their undo history and the paths of the files they came from and are saved to. Timetables you save go where you save them, usually SimSig's Timetables folder. |

The folders for sessions, recordings and exports can be moved in Tools ▸ Options ▸ Folders. The installer adds Stringline to the Start menu and to Windows' list of apps. Stringline itself writes nothing to the registry.

### Your SimSig password

- It is kept only if you tick **Remember the password** in the Connect dialog. Otherwise it is held in memory while you are connected and forgotten when Stringline closes.
- A remembered password is encrypted with Windows Data Protection (DPAPI) for your Windows account. Another Windows account, or another PC, cannot decrypt it, so a copy of `settings.json` is no use elsewhere. Anyone who can sign in as you on this PC, or any program running as you, could decrypt it.
- It never goes into a log, a saved session, a recording, an export or a crash message. The connection request to the gateway is the only place it is sent.
- To remove it, press **Forget saved password** in the Connect dialog or in Tools ▸ Options ▸ Connection. It is removed at once. Unticking **Remember** and connecting removes it too. Uninstalling always removes it.
- The developer tool `stringline record` reads a password from the `STRINGLINE_PASSWORD` environment variable, or asks for it, and never stores it.

## How to remove everything

- **Uninstall** Stringline in Windows' **Settings ▸ Apps**. The uninstaller always removes your saved password. It then asks whether to remove your settings, profiles, logs and the unpacked files too. The answer is **No** unless you choose Yes, so that installing again picks up where you left off. A silent uninstall keeps them, unless it is given `/REMOVEUSERDATA`.
- **Your own files** in `Documents\Stringline` (sessions, recordings, exports and timetable drafts) are never removed. Delete that folder yourself if you want them gone.
- **The portable version** has no uninstaller. Close Stringline and delete its folder. Then delete `%APPDATA%\Stringline`, `%LOCALAPPDATA%\Stringline`, `%TEMP%\.net\Stringline` and `Documents\Stringline`, or whichever of them you want gone.

To open one of those folders, type its name (for example `%APPDATA%\Stringline`) in File Explorer's address bar.

## Questions

Ask on the project's [issue tracker](https://github.com/frddunc-glitch/Stringline/issues). Please never post your password. Logs and recordings do not hold it, but check anything before you attach it.

## Changes to this policy

This policy is kept with Stringline's source, as `PRIVACY.md`, and every change to it is in the project's history. Last changed on 22 October 2026.
