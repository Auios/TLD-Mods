# Head Unit

A digital head unit for a car's radio slot, and the main example of TLD RevaMP's multiplayer mod API.

In the game: rare loot in a few models, each fitting only some cars. The game's radio keeps working inside it. Its
touch screen (look at a box and click, as with the game's knobs) is drawn as an old monochrome CRT: one phosphor colour
per model (the DH-1 ice blue, the DH-2 Wide green, the RX Pro amber), one blocky font, a box around everything that can
be touched, inverse for what is on, a dither under the crosshair. It has three tabs down the driver's side: the radio,
YouTube with a queue, and navigation (a map of the roads around the car and the road ahead). A unit can also be glued
anywhere in the car, like any item. In a session, everyone in or near the car sees the same video at the same moment,
and the same screen: whoever switches to RADIO, MEDIA or NAV, zooms the map, or opens + ADD, types in its bar,
searches or pages the results does it for everyone (the radio itself is shared by the game). What a player is in the
middle of stays their own: the queue list, FULL, BIG.

## What it shows

- **Registering a mod and a shared type.** `Plugin.Awake` calls `MpApi.RegisterMod` and `MpApi.RegisterType`; the
  join then checks every player has the same mod.
- **State as a record.** `State.cs`: what a car's unit shares (the queue, what plays and from when, the screen) is
  one record, written and read by `UnitStateType`, with a size cap (`MaxRecordBytes`). Edits from players go
  through `OnEdit`, which checks them before applying. With RevaMP's mod API 6 (`ReplayEdits`), an accepted edit
  reaches the other players as the edit itself, a few dozen bytes, and each replays it; an older RevaMP sends the
  whole record as before.
- **Sharing an object tied to a game object.** `UnitBehaviour.cs` makes sure the car is shared
  (`MpApi.ShareWorldObject`) and shares the unit's state under the car's session id (`MpApi.Share`,
  `MpApi.WorldObjectId`).
- **Shared timing, no streaming.** Playback is a start position on the session clock (`MpApi.SessionTime`). Each
  machine downloads and plays the video itself, on the audio clock (game time without sound output), and seeks when
  it is more than a second off, at most a few times in half a minute (`Video.cs`; changing the playback speed made the
  sound choppy). Only video ids pass between players.
- **Running without RevaMP.** With no RevaMP (`MpApi.Available` false) it works in single player.
- **Game items from the game's own.** `Units.cs` clones the game's radio item into new items with their own ids,
  so the game saves, loads and spawns them, and RevaMP shares them with the car.
- Two things every BepInEx mod for this game meets: per-frame work runs on the mod's own `DontDestroyOnLoad`
  object (the game destroys BepInEx's), and the player's camera is `mainscript.s.player.Cam` (no `Camera.main`).

## Files

| File | What it does |
| --- | --- |
| `Plugin.cs` | Settings, API registration, Harmony patches, the per-frame runner |
| `Units.cs` | The head unit models as game items, and which cars each fits |
| `UnitBehaviour.cs` | On every unit: finds its car and that car's shared state, and shows it |
| `State.cs` | The shared state record and its type (queue, playback position, the screen: tab, map zoom, the + ADD screen; a hints flag it no longer uses) |
| `Crt.cs` | The CRT canvas: boxes, the 5 × 7 font, pixel icons, and the phosphor's glow, scanlines and vignette (no Unity) |
| `Screens.cs` | Each screen's layout and drawing from a plain model of what it shows, and its touch areas (no Unity) |
| `Touch.cs`, `Controls.cs`, `Panel.cs` | The canvas on the screen with the picture or map in its window, crosshair clicks, the passengers' big picture |
| `Look.cs` | The unit's body colour and screen |
| `Video.cs` | Plays the video on this machine and keeps it at the shared position |
| `Ytdlp.cs` | Fetches yt-dlp (and optionally ffmpeg and Deno) and downloads each video to a temporary file that is deleted once nothing plays it |
| `Spotify.cs` | Turns a Spotify playlist, album or song link into YouTube Music searches (nothing plays from Spotify) |
| `Nav.cs`, `MapView.cs` | Navigation: providers for things on the road ahead, and the map |
| `Stations.cs` | Names for the game's radio stations |
| `Glue.cs` | A unit glued anywhere: only the screen shows |
| `SignIn.cs` | The player's own YouTube login for yt-dlp, when YouTube asks for one (no Unity) |
| `Probe.cs` | Checks for `tools/headunit.py`, called through RevaMP's debug bridge |

## Settings

`BepInEx/config/tldrevamp.headunit.cfg`, section `[Video]`: `Volume` (this machine only), `Resolution` (above 360
needs ffmpeg), `ConvertToVP8` (for Steam's own Proton, which can't decode H.264; turns itself on when needed); section
`[Screen]`: `ColourPicture` (the video in its own colours rather than in the screen's phosphor; this machine only);
section `[YouTube]`: `SignIn` (below).

Videos are streamed through a temporary file in `BepInEx/cache/HeadUnit`, because the game's video player can't
open YouTube's address under Proton. That file is not a library: it is deleted as soon as no unit nearby plays it or
has it next, and the folder is emptied at game start and when the game closes. What you play is your choice and your
responsibility: follow YouTube's terms of service and the rights of whoever made the video.

When a player first adds a link (+ ADD), the unit offers to download yt-dlp and Deno from their official GitHub
releases, and ffmpeg (an LGPL build) for anything above 360p, each checked against its release's checksums. Nothing
is downloaded until the player accepts on the screen. yt-dlp is its unpacked Windows build (`yt-dlp_win.zip`), kept
in `BepInEx/config/HeadUnit/yt-dlp-<checksum>`: the one-file `yt-dlp.exe` from 2026.11.23 on doesn't start under
Proton.

Every player needs these programs on their own machine: nothing passes between players but video ids. A player who
joins a car where videos already play sees GET TOOLS in place of + ADD until they have them, and the video starts as
soon as they arrive. When yt-dlp fails (usually because it is old and YouTube has changed), UPDATE fetches the newest
yt-dlp and checks it the same way: on the MEDIA tab in place of FULL while the video playing fails, and in the + ADD
dialog after a link or search fails.

## When YouTube asks to sign in

YouTube answers some connections with "Sign in to confirm you're not a bot" instead of the video: VPNs above all (a
VPN server's address is shared by many people), also shared or very busy lines. On a VPN, leaving the game out of it
(split tunneling: Mullvad's `mullvad-exclude`, or the app list in most VPN apps) is the simplest fix. Otherwise the
screen shows SIGN IN where the picture would be (and in the + ADD dialog after a link fails that way). It uses your own
login, the one YouTube asks for:

- **Firefox** (or LibreWolf, Zen): be signed in to YouTube there, then press USE FIREFOX LOGIN. The unit picks the
  profile you used last and yt-dlp reads its cookies on each video, so it stays signed in as long as the browser does.
  Under Proton this is your Linux Firefox. Its folder is kept in `[YouTube] SignIn`; empty it to sign out.
- **Any other browser**: export your youtube.com cookies as a `cookies.txt` (Netscape format, from a cookie-export
  extension) into `BepInEx/config/HeadUnit`. Chrome and Edge on Windows lock their own cookies away from other
  programs, so this is the way for them. YouTube replaces the cookies of an open YouTube tab now and then: export them
  from a private window you then close, or export again when SIGN IN comes back.

The login stays on your machine: it goes only to YouTube, through yt-dlp, never to other players. YouTube may limit an
account it sees fetching many videos this way; a second Google account is the careful choice.
