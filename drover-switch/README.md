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

## Where the pool of configs lives

`drover.ini` has room for exactly one `proxy =` line - that's drover's own format, by design, and this
app doesn't change it. The full pool of configs (the ones that aren't currently active) lives entirely in
DroverSwitch's own state at `%APPDATA%\DroverSwitch\settings.json` - independent of Discord's own install
folder, which gets replaced wholesale on every Discord auto-update (`app-X.Y.Z` → `app-X.Y.Z+1`) and so
was never a reliable place to keep anything long-lived. Only the one active config's proxy ever gets
written into `drover.ini` itself, in every discovered Discord install folder.

## Finding Discord

Discord (stable, Canary and PTB) doesn't always install to the same place, so DroverSwitch locates every
installed variant's live `app-X.Y.Z` folder the same way drover's own installer does
(`installer/Main.pas`: registry `Uninstall\<app>\InstallLocation`, plus the
`Classes\Discord\shell\open\command` association, then scanning for `app-*` subfolders that actually
contain an executable). Every config switch is written to **all** of them, so stable/Canary/PTB stay in
sync - again, matching drover's own installer behavior.

If a discovered Discord folder doesn't have `version.dll` yet, DroverSwitch installs it there itself (and
a default `drover-packet.bin`) on first run, bundled from the upstream `hdrover/discord-drover` release at
build time - no need to run `drover.exe`'s own installer by hand first.

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

## Auto-failover restarts Discord (0.2.15)

With "Автопереключение" on, when the active config fails two checks in a row DroverSwitch now picks the
**fastest** reachable config (lowest measured latency) and, if Discord is running, **restarts Discord
right away** - drover only reads `drover.ini` when Discord starts, so without the restart the switch did
nothing until the user restarted Discord by hand.

## Telegram через WebSocket (0.2.15)

A toggle at the top of the popup runs a bundled [Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy)
(MIT) - a local MTProto proxy that carries Telegram's traffic over WebSocket, for networks where Telegram's
own connections are blocked. The CI workflow builds the upstream CLI from a pinned commit (`TGWS_SHA`) into
one `tg-ws-proxy.exe` and embeds it; DroverSwitch extracts it to `%APPDATA%\DroverSwitch\tg-ws-proxy\`,
runs it hidden on port 1443 (all interfaces), restarts it if it dies and stops it on exit.

When it's on, two buttons appear at the top:

- **QR для телефона** - QR with `tg://proxy?server=<this PC's LAN IP>&port=1443&secret=dd…` for a phone on
  the same Wi-Fi. The LAN address is picked automatically (real Wi-Fi/Ethernet adapters before Hyper-V/WSL,
  VirtualBox, VMware, Docker and VPN ones; then the adapter with a gateway / the internet route); with several
  networks the dialog has a picker, and the choice is remembered (0.2.16).
- **Добавить в Telegram** - opens the same link (same LAN address, `127.0.0.1` only without any LAN) in
  Telegram Desktop, which offers to add the proxy (0.2.17).

Windows Firewall (0.2.17): before the proxy first listens (toggle turned on; at app start only once), DroverSwitch checks for its inbound allow rule
`DroverSwitch Telegram` (program `tg-ws-proxy.exe`, TCP, all profiles) and, if it's missing, adds it after one
UAC prompt (DroverSwitch.exe re-runs itself elevated with `--tgws-firewall <path>`) - before the proxy first listens, so Windows' own "allow access" prompt doesn't appear. Rules
Windows created for the exe are removed first (a dismissed prompt leaves a Block rule, which beats allow).
If the rule is still missing, the QR dialog shows a "Разрешить в брандмауэре" button. Access from the LAN is
protected by the proxy secret.

The secret is generated once and kept in `settings.json`, so links already added on phones keep working.

## Updates close the running app automatically (0.2.15)

`DroverSwitchSetup.exe` no longer asks "закрыть и обновить?" - it always closes the running DroverSwitch
(matched by name and by the installed exe path) and its `tg-ws-proxy.exe`, waits until the files are free,
installs and relaunches.
