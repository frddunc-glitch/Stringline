# Installing

## What you need

- Windows 10 (version 2004 or later) or Windows 11, 64-bit. Nothing else needs installing: .NET is inside the app.
- SimSig, with a sim you can run. Stringline works with free and payware sims.

## Downloading

Download one of these from the [Releases](https://github.com/frddunc-glitch/Stringline/releases) page:

| File | What it is |
|---|---|
| `Stringline-x.y.z-setup.exe` | The installer. It installs for your Windows account only, without asking for administrator rights, and adds Stringline to the Start menu (a desktop shortcut is optional). |
| `Stringline-x.y.z-win-x64.zip` | The portable version. Unzip it anywhere and run `Stringline.exe`. |

The first start takes a few seconds longer while Windows unpacks the app.

## "Unknown publisher" and SmartScreen

The downloads are not yet code-signed, so Windows says **Unknown publisher**, and SmartScreen may warn you the first time you run them. Choose **More info ▸ Run anyway**. Only a code-signing certificate makes Windows name the publisher; releases will be signed once there is one.

## Updating

Install the new version over the old one, or unzip the new portable version in place of the old. Your settings, profiles and sessions are kept.

## Uninstalling

Use **Settings ▸ Apps** in Windows (or delete the portable folder). Uninstalling leaves your own files in place:

| What | Where |
|---|---|
| Settings | `%APPDATA%\Stringline\settings.json` |
| Sim profiles | `%APPDATA%\Stringline\profiles` |
| Logs | `%LOCALAPPDATA%\Stringline\logs` |
| The user guide, as last opened | `%LOCALAPPDATA%\Stringline\help` |
| Saved sessions | `Documents\Stringline\sessions` |
| Recordings | `Documents\Stringline\recordings` |
| Exports | `Documents\Stringline\exports` |

Delete those folders by hand if you want them gone too. The last three can be moved in [Tools ▸ Options ▸ Folders](settings.md#folders).

Next: [turn on the Interface Gateway](gateway.md).
