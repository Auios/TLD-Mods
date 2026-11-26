MountAnything 0.3.0 - The Long Drive BETA branch (V2024.11.26b_test), works alongside TLD RevaMP (tested with 0.69.3)

Mount (glue) any item you hold onto whatever you look at: a car's body, another item, a building, the ground.

Use
  Every item you carry works like the game's own glueable items (a mirror, the clock):
  1. Pick an item up and hold it where it should go, turned the way you want it (as you always turn held items).
  2. Look at the spot and press G (the game's own Glue key, "[G]: Attach"). The item sticks there as you hold it.
  While you carry it, the game's own keys for glueable items work too:
    R          toggle its collision (off: you can hold it partly inside things while you line it up)
    Shift+R    attach mode, in turn:
                 closest point  its closest point goes on the spot you look at (the game's own)
                 origin         its origin goes on the spot (the game's own)
                 in place       it stays exactly where and how you hold it, mounted on what it touches or is pushed
                                into (the thing you look at if it touches that, else the one it's in deepest). G is
                                offered only while it touches something. Turn its collision off with R to push it
                                partly into a wall, a car or the ground first.
                 preview        a see-through green copy shows where and how it will be mounted; turn, tilt and move
                                it with the placement keys below, then G mounts it where the copy is (the 0.2 way)
                 then back to closest point. The line at the bottom of the screen names the mode.
  To take it off, pick it up as usual (F).
  Carried items that already use R for something else (jerrycans and other things you pour, magazines, ammo boxes)
  get all of this too. R stays theirs where it does something (a can or tank to pour into, a gun to reload): there R
  pours or reloads and doesn't touch the collision; anywhere else R toggles collision as for every item. Shift+R only
  switches the mode, it never pours.
  Items the game itself hangs on a joint (the fuel can, air fresheners, the sausage) keep only the game's two modes:
  their joint pins the item's middle to the mount point and lets it swing, so they can't stay as held.

What changed in 0.3: the see-through preview and the placement keys are now Shift+R's preview mode instead of always on.
Before 0.3 the item only got the game's glue when you pressed G, so the game's R and Shift+R never showed for it.

Placement keys (in the preview mode, while the green copy shows; standing, not in a car seat)
  Left / Right arrow    turn it around the surface's normal (like turning it on a table)
  Up / Down arrow       tip its top away from you / toward you
  , / .                 tip its top to the left / right
  = / -                 move it out of / into the surface
  /                     reset: square on the surface again (no turn, tilt or offset), and switch what it starts from:
                        as held -> flat (lying on its biggest side) -> upright (its own up side out) -> as held
  hold Left Shift       fine steps (1 degree, 1 mm instead of 15 degrees, 1 cm)
  Holding a key repeats it. The settings start fresh for every item you pick up.
  The item starts the way you hold it (turn it in your hands first, or press / for flat or upright). Its middle goes on the
  crosshair and its lowest point touches the surface there (then the = / - offset).
  None of these keys is one of the game's default keys on foot or one of RevaMP's (PageUp, PageDown, K, F11).
  They can all be changed in the settings.

In the preview mode, items the game could already glue rigidly (mirrors, the clock, the compass, plates...) get the green
copy and the keys too, and the game's G mounts them at the shown pose (turn off with GameGlueItems). Ones that hang on a
joint (air fresheners, the sausage) keep the game's own glue, without a preview. In closest point and origin, the game
glues all of these itself, as without the mod.
Car parts that normally only fit their slot (wheels, doors, engine parts...) can be mounted
anywhere too (turn off with AllowSlotParts); the game's own key (F) still puts them into their slot.

Settings: BepInEx\config\anonymoustld.mountanything.cfg
  [Mount] Enabled         = true
  [Mount] Key             = None    (None = the game's Glue key; or any Unity KeyCode name, e.g. H)
  [Mount] AllowSlotParts  = true
  [Mount] AlignToSurface  = false   (true: every placement starts upright: the item's own up side out of the surface)
  [Mount] InPlaceMode     = true    (false: no in-place step in Shift+R)
  [Mount] PreviewMode     = true    (false: no preview step in Shift+R)
  [Mount] GameControls    = true   (false: a carried item gets the game's glue only when you press the mount key, so
                                     the game's R and Shift+R don't apply to it: the 0.2 way)
  [Place] Preview         = false   (true: the green copy and the placement keys for every item, in every mode but in
                                     place: the 0.2 way. 0.2 called this [Place] Enabled; that old line is ignored now)
  [Place] ShowPreview     = true
  [Place] ShowHelp        = true
  [Place] GameGlueItems   = true
  [Place] TurnLeft / TurnRight / TiltAway / TiltToward / TiltLeft / TiltRight / MoveOut / MoveIn / Reset /
          FineModifier    = LeftArrow / RightArrow / UpArrow / DownArrow / Comma / Period / Equals / Minus / Slash /
                            LeftShift   (Unity KeyCode names; None turns one off)
  [Place] AngleStep = 15, FineAngleStep = 1 (degrees), MoveStep = 0.01, FineMoveStep = 0.001 (metres)
  [Place] PreviewColor    = RGBA of the green copy (A = how strongly it glows)

Saving: mounted items are saved by the game itself (its glue record) and come back mounted after loading, at the
pose you gave them.
Multiplayer (RevaMP): everyone in the session needs this mod, the same version (RevaMP checks it at join). Mounts and
take-offs are shared by RevaMP like the game's own glued items; the mod sends the item's exact pose along with them.
The preview and the keys are only on your own screen.

Install: copy MountAnything.dll to BepInEx\plugins\MountAnything\.
Build: dotnet build -c Release (against the game in TLD_DIR, else the default Steam library; or -p:GameDir=...).
Licence: GPL-3.0 (LICENSE). It links TLD RevaMP's multiplayer API, which is GPL-3.0 too.
Without the mod, the game skips the mount record of an item that has no glue of its own, so such items should come
back loose where they were (not tested).
