TerrainChoice 0.5.0 - pick the terrain when you start or host a new game (The Long Drive BETA branch only)

Beta = Steam branch "beta", V2024.11.26b_test (build 16539689). It does nothing useful on the public game.

WHAT IT DOES
  The game-mode settings (where you pick map and biome for a new game) get a new dropdown right under the biome:
    Roads: with crossroads  - (default) the beta's normal world: main road plus side roads and crossroads
    Roads: main road only   - the beta's own built-in "main road only" mode: no side roads, no crossroads
    Old terrain (2023), main road
                            - the world of the public game v2023.05.02d: its landscape (height and biomes), its
                              road (the same path, bends and slopes) and its roadside buildings (the same buildings
                              in the same places, every few km), as in the old game for the same seed; drawn with
                              the beta's road pieces, power poles, building models, scenery and textures.
                              Nothing from the public game's files is needed or shipped.
  - The choice is applied before any road generates.
  - It's kept in the save: the map name gets " - main road only" / " - 2023 terrain", which also shows on the load screen
    and in the lobby list.
  - Loaded worlds keep their terrain. The setting only affects NEW games.
  - Joiners always get the host's terrain:
      - the game's own multiplayer: through the map name the host already sends;
      - RevaMP: at the end of RevaMP's Welcome message.

EVERY PLAYER NEEDS IT
  - With RevaMP, the mod registers with RevaMP's mod check:
      - a joiner without it is refused with "install TerrainChoice 0.5.0";
      - a joiner WITH it can't join a host WITHOUT it ("remove terrainchoice").
  - With the game's own multiplayer there is no check: a joiner without the mod would get crossroads in a main-road-only world.

INSTALL
  Copy BepInEx\plugins\TerrainChoice\TerrainChoice.dll into the beta game folder's BepInEx\plugins\TerrainChoice\.
  Remove that folder to uninstall.

CONFIG  BepInEx\config\anonymoustld.terrainchoice.cfg
  [NewGame] Terrain = Crossroads | MainRoadOnly | OldTerrain   (same as the dropdown)
  [Debug]   DumpUi  = false                        (true writes the settings UI hierarchy to the log once)

TEST MODE (test copies only)
  TheLongDrive.exe -tcdiag crossroads|mainroadonly|oldterrain|load [-tcseed 12345]
  - crossroads / mainroadonly / oldterrain: starts a new game with that terrain, writes road statistics and screenshots
    (BepInEx\TerrainChoice-diag.txt, TerrainChoice-*.png), autosaves, quits.
  - load: continues that save and writes the same statistics.

OLD TERRAIN: WHAT IS AND ISN'T OLD
  Old (exactly the 2023 formulas and scene values, checked against the real public game): ground height, the
  desert/grass/snow pattern, the road's path and slopes (within 1 mm over 40 km), how the ground meets the road (flat
  10 m either side, back to the landscape at 20 m), which buildings stand where along the road (612/612 for seed 12345),
  and the start: the house, its outhouse, well, mailbox, phone booth and bus stop where the old game had them, the
  ground flattened around them as in the old game, and you wake up where the old game put you. As in the old game the
  road starts at the start: behind it there is only open land.
  Beta: the models (road pieces, power poles, buildings and what is inside them), scenery (cacti, trees, rocks, the
  big desert rock formations), textures, the ground under the roadside buildings (beta footprints). The beta's own
  random buildings are left out. The old game's scattered desert buildings off the road are not there yet.
  Other differences: the old mansion is the beta's mansion; the old village (falu01) never appeared on the old road
  either; the road sign by the old start isn't there (the beta has no such building).
  A far save loads a little slower the first time: the old road is computed from the start up to where you are.

  Worlds started with 0.3/0.4 keep the world they were made with (save tag " - old terrain"): the old landscape 60 m
  east, a road with buildings behind the start too, and the beta's start house 45 m beside the road. New old-terrain
  worlds get the save tag " - 2023 terrain".

CHANGES
  0.5.0  Old terrain is now the 2023 world as it was: no 60 m shift, no road behind the start, the 2023 start (house,
         outhouse, well, mailbox, phone booth, bus stop, ground and wake-up spot) and the 2023 road blending (the road
         2 cm higher, as the old game drew it). 0.3/0.4 old-terrain saves load as before. Every player needs 0.5.0.
  0.4.2  With TLD RevaMP 0.69.1 builds that find buildings' roads from the seed (PureRoads), the start house and
         other road-aligned buildings followed the beta's road, not the old one: an old-terrain world could start
         hundreds of metres from the road. The old road now goes into RevaMP's road lookup too: the start house
         is beside the road again (45 m). Worlds started with 0.4.1 keep their start where it is.

BUILD
  dotnet build -c Release   (against the game in TLD_DIR, else the default Steam library; or -p:GameDir=...)

LICENCE  GPL-3.0 (LICENSE). It links TLD RevaMP's multiplayer API, which is GPL-3.0 too.
