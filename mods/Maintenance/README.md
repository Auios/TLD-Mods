# Vehicle Maintenance

An add-on for TLD RevaMP (The Long Drive, beta branch): cars wear with use, and the wire brush repairs what wore.

- **Engines** lose a condition step in about four hours of normal driving, within minutes when they overheat, and much
  faster without oil. A worn engine has less power (a rusty one about three quarters), starts less reliably, burns more
  oil, runs hotter and smokes more. The game gives engines no condition of their own on the beta, so this add-on keeps
  one for each engine on the game's scale (new, used, middle, old, rusty), saved next to the world as
  `<save>.maintenance` (moved along with the autosaves).
- **Radiators** wear while the engine runs (about five hours per step, minutes when it boils).
- **Tyres** wear with distance (about 200 km per step) and fast with wheelspin or skids (about a minute of burnout per
  step). They stop at *old*, so worn tyres still grip; a fresh set is the way back to new.
- **The wire brush** repairs a switched-off engine and radiators, like doors. The last two steps (to *used* and *new*)
  take four and eight times as long, so a better engine from a wreck stays the quick way to a good one.

Part wear and the radiator brush are off by default (BepInEx config `tldrevamp.maintenance.cfg`):

| Option | What it does |
|---|---|
| `[Wear] PartWear` | Engines, radiators and tyres wear; the brush repairs engines and radiators. |
| `[Repair] BrushRadiators` | The brush repairs radiators even with part wear off. |
| `[Wear] WearRate` | How fast parts wear: 1 = the rates above (default), 2 = twice as fast. |

In multiplayer the host's values are used for everyone. Wear is worked out on the machine that simulates the car
(usually its driver's). RevaMP sends the radiator's and tyres' condition with the car; the add-on sends each engine's
to the other players itself, the host sends them to a player who joins, and only the host saves them. Everyone in a
session needs the same version of this add-on.

## Credits

The wear rules, their numbers and the radiator brush come from **Roadworthy** by jaylikesbunda
(<https://github.com/jaylikesbunda/TheLongDrive-Roadworthy>, v0.3.0-pre1), used under the MIT licence:
see [LICENSE-Roadworthy.txt](LICENSE-Roadworthy.txt). The files taken from it say so at the top. This add-on runs them
as a BepInEx plugin, moves the multiplayer side onto RevaMP's, and adds the engine's own condition, its saving and
sharing, and the brush on engines (the beta's engines have no condition for Roadworthy's engine wear to change).

## Building

```sh
dotnet build -c Release -p:GameDir="<The Long Drive folder with RevaMP installed>"
```

`-p:MpApi=<TLDRevamp.Multiplayer.dll>` builds against another RevaMP build's API. The add-on runs on RevaMP 0.69.3
too; from RevaMP's mod API 5 on it asks RevaMP which machine simulates a car (`MpApi.SimulatedHere`).

Licence: GPL-3.0, like the rest of this repository; the Roadworthy parts stay under their MIT licence.
