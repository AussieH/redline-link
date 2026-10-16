# Redline Link

Redline Link turns **Forza Horizon 6**'s built-in Data Out telemetry into a live dashboard file, for the **Redline Dashboard** widget on a CORSAIR XENEON EDGE (in iCUE), when you don't have a Stream Deck. It's a small tray program: no window, no installer, no administrator rights, and it only talks to programs on your own PC.

The Stream Deck plugin **Redline Deck** writes exactly the same file. Use whichever suits you; if you run both, see [Running it with Redline Deck](#running-it-with-redline-deck).

## What it does

- Receives Forza Horizon 6's Data Out packets on UDP port **5605** (on this PC only, unless you turn network on).
- Writes `%APPDATA%\RedlineDeck\state.json` about ten times a second while you drive, and every 5 seconds otherwise. The file holds the car, speed, gear, revs, boost, fuel, lap times, tires, G-forces, pedals and position. The format is in [docs/STATE-FILE.md](docs/STATE-FILE.md).
- Serves the same state on a localhost WebSocket (and a plain `GET /state.json`) on TCP port **5605**, for widgets that want a smoother stream. Only local pages may connect: the Host must be this PC and the Origin absent, `null` or `file://`, so a website in your browser cannot read it.
- Passes every packet on, unchanged, to up to four other ports, so other telemetry apps keep working.

## Install

1. Download `RedlineLink-<version>.zip` from the [Releases](../../releases) page or from teatimeservers.ca, and unzip it anywhere.
2. Run `RedlineLink.exe`. A tray icon appears; hover it to see the status ("waiting on port 5605", "receiving 60/s").
3. In Forza Horizon 6, open **Settings > HUD and Gameplay** and set **Data Out** to On, **Data Out IP Address** to `127.0.0.1` and **Data Out IP Port** to `5605`.
4. Drive. The tray status changes to "receiving", and the widget fills in.

The tray menu has **Open the folder**, **Start with Windows** (a per-user startup entry, no admin) and **Quit**.

**Windows SmartScreen:** Redline Link is a new program that is not code-signed yet, so Windows may show "Windows protected your PC" the first time. Choose **More info**, then **Run anyway**. You can read every line of it here first.

**Uninstall:** Quit it from the tray, untick Start with Windows first if you ticked it, and delete the folder. Its settings and files are in `%APPDATA%\RedlineDeck`.

## Settings

`%APPDATA%\RedlineDeck\companion-settings.json`, written with the defaults on first start:

```json
{
  "port": 5605,
  "network": false,
  "forward": [],
  "wsPort": 5605
}
```

- `port`: the UDP port Forza sends to. Ports 5200-5300 are reserved by the game and are refused.
- `network`: `true` to accept packets from another PC (for example Forza on an Xbox), not just this one.
- `forward`: up to four targets, as `"5606"` (this PC) or `"192.168.1.20:5606"`. Packets are passed on unchanged. A target equal to Redline Link's own port on this PC is refused (it would loop).
- `wsPort`: the TCP port for the WebSocket and `GET /state.json`.

Restart Redline Link after editing. Command line: `RedlineLink.exe [--no-tray] [--port N] [--ws-port N]`.

## Running it with Redline Deck

Only one program can receive a UDP port. With both installed, chain them: point Forza at one and let it forward to the other, for example Forza -> Redline Link on 5605 -> Redline Deck on 5606 (add `"5606"` to `forward` and set Redline Deck's port to 5606), or the other way round with Redline Deck's own forwarder. While Redline Link runs it writes a heartbeat (`companion.json`) every 2 seconds, and Redline Deck then stops writing `state.json`, so the two never fight over the file.

## Building

Needs the .NET 10 SDK on Windows.

- **Native exe (3-5 MB),** with Visual Studio's C++ tools and the Windows SDK installed:
  `cd src` then `dotnet publish -c Release -o publish`
- **Without the Windows SDK,** a trimmed self-contained single file (about 11 MB, same code):
  `cd src` then `dotnet publish -c Release -o publish -p:PublishAot=false -p:PublishSingleFile=true -p:SelfContained=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true`

Run it from a console with `--no-tray` to test without the tray icon (Ctrl+C stops it).

## Privacy

Redline Link reads only the packets Forza sends it. It writes only in `%APPDATA%\RedlineDeck`, sends nothing anywhere except the forward targets you list, and opens no connection to the internet.

## License

MIT. Redline Link is an unofficial fan project, not affiliated with or endorsed by Playground Games, Microsoft or Xbox Game Studios. Forza Horizon is a trademark of Microsoft.
