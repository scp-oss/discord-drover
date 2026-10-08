# DroverSwitch

A small Windows tray app that sits next to [Discord Drover](../README.md) and lets you keep several
proxy configs and switch between them from the system tray, with an optional auto-failover mode.

It never touches drover's own Delphi code (`version.dll`). It only reads/writes plain files next to
`Discord.exe`, exactly the way drover's own installer does.

## What it does

- Tray icon = a colored dot: **green** while the active proxy can reach Discord, **red** when it can't,
  gray while checking.
- Left-click the tray icon opens a small Fluent/Win11-styled popup (follows the system light/dark theme)
  listing your saved configs, each with its own live status dot.
- Click a config to make it active. Right-click a config for "Изменить" / "Удалить".
- "Добавить" opens a dialog to add a new config: `protocol://host:port` or
  `protocol://login:password@host:port`, where `protocol` is `http` or `socks5` and `host` is an IP or a
  domain (the same syntax drover itself accepts - see the main [README](../README.md)). An empty address
  means "direct, no proxy".
- "Автопереключение" toggle: when a config is active and two checks in a row find it unreachable,
  DroverSwitch automatically activates the next reachable config in the list.
- **Settings only take effect the next time Discord starts** - drover reads `drover.ini` exactly once,
  when its `version.dll` is loaded into a fresh `Discord.exe` process (see `drover.dpr`'s initialization
  block). DroverSwitch shows a hint and an optional "Перезапустить Discord" button when Discord is
  currently running, instead of pretending the switch is instant.

## Why there's a second file next to drover.ini

`drover.ini` has room for exactly one `proxy =` line - that's drover's own format, by design, and this
app doesn't change it. So the full pool of configs (the ones that aren't currently active) is kept in a
companion file written right next to `drover.ini`, in every discovered Discord install folder:

```
drover-switch.ini
```

```ini
; DroverSwitch - pool of proxy configs for Discord Drover.
; drover.ini only stores ONE active proxy, so the rest of the list lives here.
; Safe to edit by hand: DroverSwitch re-reads this file automatically.

[active]
proxy = EU

[profiles]
EU = http://1.2.3.4:8080
Backup-RU = socks5://5.6.7.8:1080
Direct =
```

This is deliberately plain text and hand-editable, the same spirit as drover's own `drover-packet.bin`
("re-read before every connection, no restart of Discord needed"). DroverSwitch notices when the file's
timestamp changed by something other than itself and reloads it - no need to restart DroverSwitch either.

Its own persistent cache (survives even if Discord is reinstalled and the folder above disappears) lives
at `%APPDATA%\DroverSwitch\settings.json`.

## Finding Discord

Discord (stable, Canary and PTB) doesn't always install to the same place, so DroverSwitch locates every
installed variant's live `app-X.Y.Z` folder the same way drover's own installer does
(`installer/Main.pas`: registry `Uninstall\<app>\InstallLocation`, plus the
`Classes\Discord\shell\open\command` association, then scanning for `app-*` subfolders that actually
contain an executable). Every config switch is written to **all** of them, so stable/Canary/PTB stay in
sync - again, matching drover's own installer behavior.

If no folder with `version.dll` installed is found, DroverSwitch tells you to run `drover.exe`'s installer
first; it does not install drover itself.

## How "reachable" is measured

For each config, DroverSwitch opens a real tunnel to `discord.com:443` through that proxy (an HTTP
`CONNECT`, or a SOCKS5 handshake) with a 5-second timeout, and for "direct" it just opens a plain TCP
connection. This only tests connectivity - it never sends or reads actual Discord traffic.

## Building

Requires Windows, the .NET 8 SDK with the WPF workload, and:

```
dotnet restore
dotnet build -c Release
```

from `drover-switch/`. Dependencies (restored automatically):

- [`WPF-UI`](https://github.com/lepoco/wpfui) - Fluent/Win11 controls and theming.
- [`H.NotifyIcon.Wpf`](https://github.com/HavenDV/H.NotifyIcon) - the tray icon itself.

These were current stable versions at the time this was written; if `dotnet restore` can't find the
pinned version in `src/DroverSwitch/DroverSwitch.csproj`, bump it to whatever's current - `dotnet add
package Wpf.Ui` / `dotnet add package H.NotifyIcon.Wpf` without a version grabs latest.

This project was written and reviewed without a Windows machine available in the dev environment, so it
hasn't been built or run yet - the Delphi side (`drover.ini` format, the Discord-folder discovery logic)
was cross-checked directly against this repo's `Options.pas` and `installer/Main.pas`, but give the first
build a careful look, especially around the exact `Wpf.Ui`/`H.NotifyIcon` API surface for whatever
versions NuGet resolves.
