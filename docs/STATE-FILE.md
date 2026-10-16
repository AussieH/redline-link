# The dashboard file

`%APPDATA%\RedlineDeck\state.json`: what Redline Link (or the Redline Deck Stream Deck plugin) knows about the car, for the Redline Dashboard widget in CORSAIR iCUE on a XENEON EDGE. iCUE widgets can read files in a folder under the user's home, and the folder must exist before the widget is added, so the writers create it as soon as they start.

## When it is written

- About **10 times a second** while driving, on every change of state, and **every 5 seconds** otherwise, so a reader can tell a quiet game from a writer that stopped.
- Written whole: `state.json.tmp`, then renamed over `state.json`. If a reader holds the file (iCUE does for a moment after each change), one retry 5 ms later, then a rewrite in place.
- `companion.json` beside it: Redline Link's heartbeat, `{"app":"Redline Link","pid":…,"at":<ms>,"port":5605}`, every 2 seconds and removed on exit. While it is fresh, the Redline Deck plugin leaves `state.json` alone.

## The format

Times in `at` are Unix milliseconds. Raw SI units, plus the driver's unit choices (Redline Link always writes the defaults), so a reader converts the same way the plugin does. Wheels are front left, front right, rear left, rear right.

```
{ "app": "Redline Deck", "v": 1, "writer": "plugin" | "companion", "at": <ms>,
  "state": "waiting" | "driving" | "paused" | "busy" | "error",
  "link": { "port", "pps", "packets" },
  "units": { "speed": "kmh"|"mph", "temp": "C"|"F", "power": "hp"|"kW"|"PS", "torque": "Nm"|"lbft", "boost": "psi"|"bar" },
  "car": { "ordinal", "cls", "piClass", "pi", "drive", "cylinders", "group" } | null,
  "session": { "topSpeed" (m/s), "peakG": { "lat", "lon", "total" }, "bestLap" (s) },
  -- while driving only --
  "speed" (m/s), "gear" ("R", "1".."10", "N"), "rpm": { "now", "max", "idle", "frac" }, "boost" (psi), "power" (W),
  "torque" (N·m), "fuel" (0-1), "race": { "on", "position", "lap", "laps", "current", "last", "best", "time" },
  "distance" (m), "tyres": { "tempF": [4], "slip": [4], "travel": [4] }, "g": { "lat", "lon", "vert" },
  "inputs": { "throttle", "brake", "clutch", "handbrake" (0-1), "steer" (-1..1) }, "pos": { "x", "y", "z" } (m),
  "airborne" }
```

- `state`: `waiting` before the first packet; `driving` while packets arrive and the game says a race (any driving) is on; `paused` in menus, during a rewind, or when packets stop for a second; `busy` when the port is taken by another program.
- `car.cls` and `car.piClass`: D, C, B, A, S1, S2, R, X. `car.drive`: FWD, RWD, AWD.
- `race.lap` counts from 1; the game does not send the total number of laps.
- `tyres` holds the four tires (FL, FR, RL, RR). The field is named `tyres` for compatibility: Redline Deck, Redline Dashboard and other readers already look for that name, so it stays as it is even though everything else here says "tires".
- `tyres.tempF` is the raw tire temperature as the game sends it.

## The WebSocket

`ws://localhost:5605/` sends this same JSON as a text frame at once on connect and on every write. `GET http://localhost:5605/state.json` answers with it once. Both refuse other Host names and web-page Origins (403).
