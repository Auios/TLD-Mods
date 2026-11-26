using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using UnityEngine;

namespace MountAnything
{
    /// Unattended check of a build (test copies only): TheLongDrive.exe -madiag <mount|load> [-maseed N]
    ///  mount: new game with the seed; holds loose items one by one, aims the camera at a car's body, the ground, a
    ///         building, another item and the car's roof, and mounts each through the real key path (RayStuff); moves
    ///         the car and checks the mounted item rides it; screenshots; autosaves; writes the expected poses to
    ///         MountAnything-diag-state.txt; quits.
    ///  load:  continues the last save; checks every mounted item is back on the same thing at the same pose; takes
    ///         one off (the game's pickup); travels 3 km away and back and checks again; quits without saving.
    /// Results: BepInEx/MountAnything-diag.txt and BepInEx/MountAnything-*.png.
    internal static partial class Diag
    {
        private static string _mode;
        private static int _seed = 12345;
        private static int _state, _step;
        private static float _at;
        private static readonly CultureInfo Ic = CultureInfo.InvariantCulture;
        private static string Out => Path.Combine(Paths.BepInExRootPath, "MountAnything-diag.txt");
        private static string StateFile => Path.Combine(Paths.BepInExRootPath, "MountAnything-diag-state.txt");
        private static int _pass, _fail;
        private static bool _reported;

        static Diag()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-madiag") _mode = args[i + 1].ToLowerInvariant();
                if (args[i] == "-maseed") int.TryParse(args[i + 1], out _seed);
            }
        }

        internal static void Write(string s)
        {
            Plugin.Log.LogInfo("[diag] " + s);
            File.AppendAllText(Out, DateTime.Now.ToString("HH:mm:ss") + " " + s + Environment.NewLine);
        }

        private static void Check(bool ok, string what)
        {
            if (ok) _pass++; else _fail++;
            Write((ok ? "PASS " : "FAIL ") + what);
        }

        public static void Tick()
        {
            if (_mode == null) return;
            Mounting.TestUnfocused = true;   // the test window has no focus: its pause menu is up
            RevampLink.Watch = true;
            // the game applies its saved window mode (fullscreen): a test window stays a small window
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width > 1280) Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
            try
            {
                // the game's per-frame work on the held item (fpscontroller.usePickedUpStuff: "toggle collision" among it)
                // returns at once while the cursor isn't locked, as in a test window behind its pause menu: run it here
                if (_driveHeld && Pl != null && Pl.pickedUp != null && mainscript.s != null)
                {
                    mainscript.s.crsrLocked = true;
                    Pl.usePickedUpStuff(Pl.pickedUp, hand: false);
                    _heldFrames++;
                }
                Step();
            }
            catch (Exception e) { Write("ERROR " + e); Finish(); }
        }

        private static void Finish()
        {
            Write("result: " + _pass + " passed, " + _fail + " failed; mounts " + Mounting.Mounts + ", added on load " + Mounting.AddedOnLoad +
                  ", added for RevaMP " + Mounting.AddedForRevamp + ", removed " + Mounting.Removed + "; quitting");
            _mode = null;
            Application.Quit();
        }

        private static fpscontroller Pl => mainscript.s != null ? mainscript.s.player : null;

        private static void Step()
        {
            float now = Time.realtimeSinceStartup;
            var d = DataFromMenuScript.s;
            switch (_state)
            {
                case 0:   // the main menu is up
                    if (d == null || !d.mainmenu || !menuhandler.isStarted()) return;
                    _state = 1; _at = now + 6f;
                    Write("---- run " + _mode + " seed " + _seed + ", MountAnything " + Plugin.Version + ", RevaMP link " + RevampLink.Active + ", registered " + RevampLink.Registered);
                    return;
                case 1:
                    if (now < _at) return;
                    if (!_reported)
                    {
                        _reported = true; PrefabReport();
                        Write("shaders loaded: " + string.Join(", ", Resources.FindObjectsOfTypeAll<Shader>().Select(s => s.name).Where(n => !n.StartsWith("Hidden/") || n.Contains("Colored")).Distinct().OrderBy(n => n).ToArray()));
                    }
                    if (_mode == "mpjoin")
                    {
                        if (FindEvent("host", "hosting") == null) return;   // the host game is up
                        Write("joining 127.0.0.1:27070: " + Mp("Join", "127.0.0.1:27070", ""));
                        _at = Time.realtimeSinceStartup + 300f;
                    }
                    else if (_mode == "load" || _mode == "mphost")
                    {
                        Write("continuing the last save: " + mainscript.GetLastSavePath());
                        menuhandler.s.AutoContinue();
                    }
                    else
                    {
                        d.seed = _seed;
                        Write("new game");
                        menuhandler.s.PressedStart();
                    }
                    _state = 2; _at = now + 600f;
                    return;
                case 2:   // in the world
                    if (d != null && !d.mainmenu && Pl != null && menuhandler.s != null && menuhandler.s.currentMainMap != null && (_mode != "mpjoin" || MpStatus().Contains("\"ready\":true")))
                    {
                        Write("world loaded, player at " + mainscript.GlobalFromUnityPos(Pl.transform.position));
                        _state = 3; _at = now + 35f;
                    }
                    else if (now > _at) { Write("the world never loaded"); Finish(); }
                    return;
                case 3:
                    if (now < _at) return;
                    _state = 4; _step = 0; _at = now;
                    return;
                case 4:
                    if (now < _at) return;
                    float wait = _mode == "load" ? LoadStep() : _mode == "mphost" ? HostStep() : _mode == "mpjoin" ? JoinStep() : MountStep();
                    if (wait < 0f) { Finish(); return; }
                    _at = now + wait;
                    return;
            }
        }

        // ------------------------------------------------------------------ mount run

        private class Plan
        {
            public string Name; public tosaveitemscript Item, Under; public Func<Vector3?> Target; public Func<Vector3> From; public float Range = 1.4f;
            /// Placement keys pressed at the first look (PreActs) and at the second (Acts): the preview between the two
            /// must turn by Angle degrees, and end with its lowest point Lift metres out of the surface.
            public Placement.Act[] PreActs, Acts; public bool Fine; public float Angle, Lift; public bool GameGlue, AimAtPress;
            /// Without the preview (0.3 default): mount with the game's attach point at the item's origin (Shift+R), not its
            /// closest point. NoGlue: an item with an R of its own (a jerrycan): carried, it gets the glue, R and Shift+R are checked, not mounted.
            public bool Origin, NoGlue;
        }
        private static Quaternion _heldRot;
        private static tosaveitemscript _ipA, _ipB;
        private static poiScript _ipPoi;
        private static bool _ipToggle;
        private static Vector3 _ipSpot;
        private static bool _pkOnCar;
        private static tosaveitemscript _ownR;   // the carried item with its own R (a jerrycan)
        private static bool _ownRToggle;
        private static long _ownRKept;
        private static tosaveitemscript _joint;   // a spawned item the game hangs on a joint (the fuel can)
        private static Vector3 _pkSpot, _pkFrom;   // Shift+R's preview stage: a free spot on the car, and where to look from
        private static tosaveitemscript _ipGround, _ipWall, _ipAgainst;
        private static Vector3 _ipAgainstFrom;

        /// One more in-place mount: the item held with its middle on a surface (half of it inside, collision off by R),
        /// G, and it must be mounted on the expected thing, exactly as held.
        private class IpCase
        {
            public string Name; public tosaveitemscript Item; public Func<Vector3?> Spot; public Func<Vector3> From; public float Range = 0.9f;
            public Func<attachablescript, string> On;   // null = on the expected thing; else what is wrong
            public bool GameGlue;
            /// Held the player's way: not placed by the test, the game's hand pulls it to the crosshair (collision on: it
            /// rests against the surface), the way the player had it (2026-11-25).
            public bool Natural;
        }
        private static readonly List<IpCase> _ipCases = new List<IpCase>();
        private static readonly List<tosaveitemscript> _ipDone = new List<tosaveitemscript>();
        private static int _ipIdx;
        private static Vector3? _ipCaseSpot;
        private static readonly List<Plan> _plans = new List<Plan>();
        private static int _planIdx;
        private static tosaveitemscript _car;
        private static int _mountsBefore;

        private static float MountStep()
        {
            var pl = Pl;
            switch (_step)
            {
                case 0:
                {
                    _car = savedatascript.s.items.Values.Where(t => t != null && t.car != null && !t.name.Contains("Bike") && (t.transform.position - pl.transform.position).magnitude < 150f)
                                .OrderBy(t => (t.transform.position - pl.transform.position).sqrMagnitude).FirstOrDefault();
                    var loose = LooseItems(pl, 120f);
                    Write("car: " + (_car != null ? _car.name + " (" + _car.idInSave + ") " + (_car.transform.position - pl.transform.position).magnitude.ToString("F0") + " m" : "none") +
                          "; loose items near: " + loose.Count + " (" + string.Join(", ", loose.Take(12).Select(t => t.name + (t.attachable != null ? "[att onlyToSlot " + t.attachable.onlyToSlot + "]" : "")).ToArray()) + ")");
                    var plain = loose.Where(t => t.attachable == null).ToList();
                    var part = savedatascript.s.items.Values.Where(t => t != null && t.P != null && t.car == null && t.attachable != null && t.attachable.onlyToSlot && !t.attachable.attached &&
                                                                         t.transform.parent == null && t.P.physlock == null && (t.transform.position - pl.transform.position).magnitude < 400f)
                                    .OrderBy(t => (t.transform.position - pl.transform.position).sqrMagnitude).FirstOrDefault();
                    if (_car == null || plain.Count < 4) { Write("not enough to test: car " + (_car != null) + ", plain items " + plain.Count); return -1f; }
                    var poi = UnityEngine.Object.FindObjectsOfType<poiScript>().Where(p => p.cols != null && p.cols.Any(c => c != null && !c.isTrigger))
                                .OrderBy(p => (p.transform.position - pl.transform.position).sqrMagnitude).FirstOrDefault();
                    var under = plain[3];
                    _ipA = plain.Count > 5 ? plain[5] : null; _ipB = plain.Count > 6 ? plain[6] : null; _ipPoi = poi;
                    var spare = plain.Skip(7).Where(t => !Mounting.OwnR(t.P)).ToList();
                    _ipGround = spare.Count > 0 ? spare[0] : null; _ipWall = spare.Count > 1 ? spare[1] : null; _ipAgainst = spare.Skip(2).FirstOrDefault(t => t.GetComponentsInChildren<Renderer>().Select(r => r.bounds.size.magnitude).DefaultIfEmpty(9f).Max() < 0.4f);   // small, like the player's sponge
                    var A = new Func<Placement.Act[], Placement.Act[]>(a => a);
                    // flat on the car's side, turned 30 degrees, 2 cm out
                    _plans.Add(new Plan { Name = "car body", Item = plain[0], Target = () => SideOfCar(), From = () => _car.transform.position + _car.transform.right * 10f,
                                          PreActs = A(new[] { Placement.Act.Reset }), Acts = A(new[] { Placement.Act.TurnRight, Placement.Act.TurnRight, Placement.Act.Out, Placement.Act.Out }), Angle = 30f, Lift = 0.02f });
                    // as held, tipped 15 degrees away, then fine steps: 1 degree to the right, 3 mm into the ground
                    _plans.Add(new Plan { Name = "ground", Item = plain[1], Range = 1.2f, Target = () => OpenGround(poi), From = () => OpenGround(poi).Value + AwayFromPoi(poi) * 3f,
                                          PreActs = A(new[] { Placement.Act.TiltAway }),
                                          Acts = A(new[] { Placement.Act.In, Placement.Act.In, Placement.Act.In, Placement.Act.TiltRight }), Fine = true, Angle = 1f, Lift = -0.003f });
                    var glue = loose.FirstOrDefault(t => t.attachable != null && !t.attachable.onlyToSlot && t.attachable.parentAtAttach && !t.attachable.attached);
                    if (glue != null)   // an item the game glues itself: its G is taken over, it goes where the preview shows
                        _plans.Add(new Plan { Name = "car's other side (game glue item " + glue.name + ")", Item = glue, Target = () => SideOfCar(-1f), From = () => _car.transform.position - _car.transform.right * 10f,
                                              Acts = A(new[] { Placement.Act.TurnLeft }), Angle = 15f, GameGlue = true });
                    else Write("no loose item the game glues rigidly by itself");
                    if (poi != null) _plans.Add(new Plan { Name = "building " + poi.name, Item = plain[2], Target = () => OnPoi(poi), From = () => PoiStand(poi),
                                                           PreActs = new[] { Placement.Act.Reset, Placement.Act.Reset }, Origin = true });   // upright
                    if (plain.Count > 4)
                    {
                        _plans.Add(new Plan { Name = "another item (" + under.name + ")", Item = plain[4], Under = under, AimAtPress = true, Target = () => OnItem(under), From = () => _car.transform.position });
                    }
                    if (part != null) _plans.Add(new Plan { Name = "car roof (slot part " + part.name + ")", Item = part, Target = () => RoofOfCar(), From = () => _car.transform.position + _car.transform.right * 10f });
                    var pour = savedatascript.s.items.Values.Where(t => t != null && t.P != null && t.P.ammo != null && t.car == null && t.attachable == null && t.transform.parent == null &&
                                                                         !_plans.Any(x => x.Item == t) &&
                                                                         t.P.physlock == null && (t.transform.position - pl.transform.position).magnitude < 400f)
                                   .OrderBy(t => (t.transform.position - pl.transform.position).sqrMagnitude).FirstOrDefault();
                    if (!Plugin.Place.Value && Plugin.GameControls.Value)
                    {
                        if (pour != null) _plans.Add(new Plan { Name = "carried, has its own R", Item = pour, NoGlue = true, Target = () => OpenGround(poi), From = () => OpenGround(poi).Value + AwayFromPoi(poi) * 3f });
                        else Write("no loose item with an R of its own (ammo) near");
                    }
                    Write("plan: " + string.Join("; ", _plans.Select(p => p.Item.name + " (" + p.Item.idInSave + ") -> " + p.Name).ToArray()));
                    _planIdx = 0;
                    _step = 1;
                    return 0.5f;
                }
                case 1:   // pick up the next item
                {
                    if (_planIdx >= _plans.Count) { _step = 40; return 0.5f; }
                    var p = _plans[_planIdx];
                    if (p.Under != null) PlaceUnder(p.Under);
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(p.Item);
                    Write("holding " + p.Item.name + ": pickedUp " + (pl.pickedUp == p.Item.P));
                    _step = 2;
                    return 1f;
                }
                case 2:   // stand in front of the target and look at it
                {
                    var p = _plans[_planIdx];
                    var t = p.Target();
                    if (t == null) { Check(false, p.Name + ": no target point"); _planIdx++; _step = 1; return 0.2f; }
                    StandAndLook(t.Value, p.Range, p.From?.Invoke());
                    PressPlacement(p.PreActs, false);
                    _step = 3;
                    return 0.6f;
                }
                case 3:
                {
                    var p = _plans[_planIdx];
                    StandAndLook(p.Target().Value, p.Range, p.From?.Invoke());   // again: the player may have settled
                    bool offered = pl.BCanAttachGlue || Time.unscaledTime < Mounting.HintUntil;
                    Write(p.Name + ": looking at " + DescribeLook(pl) + ", mount offered " + offered + (p.GameGlue ? " (the game's own glue offer; placement for game glue " + Placement.ForGameGlue + ")" : ""));
                    if (!Plugin.Place.Value && Plugin.GameControls.Value && !p.GameGlue)
                    {
                        var hat = p.Item.attachable;
                        if (p.NoGlue)
                        {
                            Check(pl.pickedUp == p.Item.P && hat != null && Mounting.Added.Contains(hat), p.Item.name + " (R is its own) carried with the game's glue from the mod: " + (hat == null ? "none" : "has it"));
                            _ownR = p.Item; _ownRToggle = pl.attachableColliderDisableToggle;
                            OwnRKeys.TestR = true;
                            _step = 70; return 0.4f;
                        }
                        if (hat != null && !Mounting.Added.Contains(hat))
                            Write(p.Item.name + ": has the game's own attachment (" + (hat.onlyToSlot ? "slot part" : "glue") + "), nothing added");
                        else
                            Check(hat != null && !hat.attached, p.Item.name + " carried with the game's glue from the mod (the game's own G, R and Shift+R apply)");
                        // a small item near drifts out of the crosshair as the player settles (AimAtPress): no offer check
                        if (!p.AimAtPress) Check(offered, p.Item.name + ": the game offers [G]: Attach");
                    }
                    _preRot = Placement.KeyRot; _framesBefore = Placement.DrawnFrames;
                    PressPlacement(p.Acts, p.Fine);
                    _step = 31;
                    return 0.5f;
                }
                case 70:   // an item with its own R (a jerrycan), carried: R without a target toggles collision, as for every item
                    Check(pl.attachableColliderDisableToggle != _ownRToggle, _ownR.name + ": R with nothing to pour into or reload toggles collision (" + _ownRToggle + " -> " + pl.attachableColliderDisableToggle + ")");
                    _ownRToggle = pl.attachableColliderDisableToggle; _ownRKept = OwnRKeys.Kept;
                    OwnRKeys.TestTarget = true; OwnRKeys.TestR = true;
                    _step = 71; return 0.4f;
                case 71:   // ... with a target (as if looking at a can to fill), R is the item's own: no collision toggle
                    Check(pl.attachableColliderDisableToggle == _ownRToggle && OwnRKeys.Kept > _ownRKept, _ownR.name + ": R where the game offers the item's own R leaves collision alone (kept " + (OwnRKeys.Kept - _ownRKept) + ")");
                    OwnRKeys.TestTarget = false;
                    pl.attachableColliderDisableToggle = false;
                    InPlace.Reset(pl);
                    InPlace.TestPress = true; _step = 72; return 0.4f;
                case 72:
                    Check(pl.attachableOriginToggle && pl.canShiftR, _ownR.name + ": Shift+R: closest point -> origin (" + InPlace.LastLabel + ")");
                    InPlace.TestPress = true; _step = 73; return 0.4f;
                case 73:
                    Check(InPlace.Active(pl.pickedUp) && InPlace.LastLabel.EndsWith("In Place"), _ownR.name + ": Shift+R: origin -> in place (" + InPlace.LastLabel + ")");
                    InPlace.TestPress = true; _step = 74; return 0.4f;
                case 74:
                    Check(!Plugin.PreviewMode.Value || InPlace.PreviewActive(pl.pickedUp) && InPlace.LastLabel.EndsWith("Preview"), _ownR.name + ": Shift+R: in place -> preview (" + InPlace.LastLabel + ")");
                    InPlace.Reset(pl);
                    if (pl.pickedUp != null) pl.Drop();
                    _planIdx++; _step = 1;
                    return 0.5f;
                case 31:   // the preview after the keys
                {
                    var p = _plans[_planIdx];
                    if (!Plugin.Place.Value)
                    {
                        // the game's way: the item goes as held, through the game's own Glue key
                        if (p.AimAtPress) StandAndLook(p.Target().Value, p.Range, p.From?.Invoke());
                        pl.attachableOriginToggle = p.Origin;
                        _heldRot = p.Item.transform.rotation;
                        _mountsBefore = (int)Mounting.Mounts;
                        if (p.GameGlue || Plugin.GameControls.Value) Mounting.TestGlueDown = true; else Mounting.TestPress = true;
                        _step = 4;
                        return 0.5f;
                    }
                    if (p.AimAtPress)
                    {
                        // a small item lying near drifts out of the crosshair as the player settles: aim and press at once,
                        // the preview is checked as it was in the frame that mounted (step 4)
                        StandAndLook(p.Target().Value, p.Range, p.From?.Invoke());
                        _mountsBefore = (int)Mounting.Mounts;
                        Mounting.TestPress = true;
                        _step = 4;
                        return 0.5f;
                    }
                    CheckPreview(p.Name, p.Item, _preRot, p.Angle, p.Lift, true);
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "MountAnything-preview-" + _planIdx + ".png"));
                    _previewPos = Placement.Pos; _previewRot = Placement.Rot;
                    _mountsBefore = (int)Mounting.Mounts;
                    if (p.GameGlue) Mounting.TestGlueDown = true; else Mounting.TestPress = true;
                    _step = 4;
                    return 0.5f;
                }
                case 4:
                {
                    var p = _plans[_planIdx];
                    Mounting.TestPress = false; Mounting.TestGlueDown = false;
                    var at = p.Item.attachable;
                    bool native = !Plugin.Place.Value;
                    bool ok = (Mounting.Mounts > _mountsBefore || native && p.GameGlue) && at != null && at.attached;
                    Check(ok, "mount " + p.Item.name + " on " + p.Name + ": " + (ok ? (native && p.GameGlue ? "glued by the game itself" : Mounting.LastMount) : "not mounted (held " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ")"));
                    if (ok) Check(p.Item.GetComponent<Rigidbody>() == null && p.Item.transform.parent != null, p.Item.name + " is a rigid child now (no Rigidbody, parent " + p.Item.transform.parent.name + ")");
                    if (ok && native)
                    {
                        pl.attachableOriginToggle = false;
                        float turned = Quaternion.Angle(_heldRot, p.Item.transform.rotation);
                        Check(turned < 0.5f, p.Item.name + " mounted the way it was held (turned " + turned.ToString("F2") + " deg)");
                        if (!p.GameGlue)
                        {
                            if (p.Origin)
                            {
                                float off = (p.Item.transform.position - (Mounting.LastHitPoint + Mounting.LastHitNormal * at.offsetback)).magnitude;
                                Check(off < 0.002f, p.Item.name + ": Shift+R origin mode: its origin on the aimed point (" + (off * 1000f).ToString("F1") + " mm off)");
                            }
                            else
                            {
                                // the game's rule (attachablescript.SetOwnPosition): the item's point nearest the aimed spot goes
                                // onto the aimed collider's point nearest to it, or onto the aimed spot itself on a mesh collider
                                var ac = at.attachedC;
                                bool box = ac != null && !(ac is MeshCollider);
                                float near = float.MaxValue;
                                foreach (var c in p.Item.GetComponentsInChildren<Collider>())
                                {
                                    if (c == null || !c.enabled || c.isTrigger) continue;
                                    if (!box) { near = Mathf.Min(near, (c.ClosestPoint(Mounting.LastHitPoint) - Mounting.LastHitPoint).magnitude); continue; }
                                    // the gap between the item and the collider it was aimed at (alternating nearest points)
                                    var q = ac.ClosestPoint(c.bounds.center);
                                    for (int k = 0; k < 8; k++) q = ac.ClosestPoint(c.ClosestPoint(q));
                                    near = Mathf.Min(near, (c.ClosestPoint(q) - q).magnitude);
                                }
                                Check(box ? near < 0.005f : near < 0.1f, p.Item.name + ": closest-point mode: " + (box ? "touches the collider it was aimed at (" + ac.name + ", " : "its nearest point by the aimed spot (") +
                                      (near * 1000f).ToString("F1") + " mm off)");
                            }
                        }
                    }
                    if (ok && !native && p.AimAtPress) CheckPreview(p.Name + " (at the mount frame)", p.Item, _preRot, p.Angle, p.Lift, false);
                    if (ok && !native) CheckPlaced(p.Item, p.AimAtPress ? (Vector3?)null : _previewPos, _previewRot);
                    if (pl.pickedUp != null) pl.Drop();
                    _planIdx++;
                    _step = 1;
                    return 1f;
                }
                case 40:   // in place (Shift+R's third mode): cycle to it, mount an item pushed into the car's side
                {
                    if (Plugin.Place.Value || !Plugin.InPlaceMode.Value || !Plugin.GameControls.Value || _ipA == null || _ipB == null)
                    { Write("in-place stage skipped (preview on, mode off, or not enough items)"); _step = 10; return 0.2f; }
                    if (pl.pickedUp != null) pl.Drop();
                    InPlace.Reset(pl);
                    Hold(_ipA);
                    Write("in place: holding " + _ipA.name + ": pickedUp " + (pl.pickedUp == _ipA.P));
                    _step = 41; return 1f;
                }
                case 41: InPlace.TestPress = true; _step = 42; return 0.4f;
                case 42:
                    Check(pl.attachableOriginToggle && !InPlace.On, "Shift+R: closest point -> origin (" + InPlace.LastLabel + ")");
                    InPlace.TestPress = true; _step = 43; return 0.4f;
                case 43:
                    Check(InPlace.On && !pl.attachableOriginToggle && InPlace.LastLabel.EndsWith("In Place"), "Shift+R: origin -> in place (" + InPlace.LastLabel + ")");
                    _ipSpot = (SideOfCar(1f, -1.3f) ?? SideOfCar(-1f, 1.3f) ?? SideOfCar()).Value;   // a free spot (the earlier mounts are mid-side)
                    StandAndLook(_ipSpot, 0.9f, _car.transform.position + _car.transform.right * 10f);
                    _ipToggle = pl.attachableColliderDisableToggle;
                    pl.attachableColliderDisableToggle = true;   // R: collision off, so it can go partly into the car
                    _step = 44; return 1f;
                case 44:
                {
                    StandAndLook(_ipSpot, 0.9f, _car.transform.position + _car.transform.right * 10f);
                    // into the car's side: its middle on the body's surface, half of it inside (R has its collision off)
                    var rb = _ipA.GetComponent<Rigidbody>();
                    if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = _ipSpot; }
                    _ipA.transform.position = _ipSpot;
                    Write("in place: " + _ipA.name + " held at " + DescribeLook(pl) + ", offered " + pl.BCanAttachGlue);
                    _mountsBefore = (int)InPlace.Mounts;
                    Mounting.TestGlueDown = true;
                    _step = 45; return 0.6f;
                }
                case 45:
                {
                    Mounting.TestGlueDown = false;
                    var at = _ipA.attachable;
                    bool ok = InPlace.Mounts > _mountsBefore && at != null && at.attached;
                    Check(ok, "in place: " + _ipA.name + " pushed into the car's side mounted: " + (ok ? Mounting.LastMount : "not mounted"));
                    if (ok)
                    {
                        var to = at.AttachedToTosave();
                        Check(to != null && (to == _car || to.transform.IsChildOf(_car.transform)), "in place: on the car (" + (to != null ? to.name : "nothing") + ")");
                        float dp = (_ipA.transform.position - InPlace.LastHeldPos).magnitude * 1000f, da = Quaternion.Angle(_ipA.transform.rotation, InPlace.LastHeldRot);
                        Check(dp < 0.5f && da < 0.05f, "in place: exactly as held (" + dp.ToString("F2") + " mm, " + da.ToString("F3") + " deg)");
                        _ipDone.Add(_ipA);
                    }
                    if (pl.pickedUp != null) pl.Drop();
                    _step = 50; return 0.5f;
                }
                case 50:   // more in-place mounts: on the ground, on a building, and an item the game glues itself
                {
                    _ipCases.Clear();
                    var side = Vector3.Cross(Vector3.up, AwayFromPoi(_ipPoi)).normalized;
                    _ipCases.Add(new IpCase { Name = "on the ground", Item = _ipGround, Range = 1.2f,
                        Spot = () => { var g = OpenGround(_ipPoi); return g == null ? null : Ground(g.Value + side * 1.5f) ?? Ground(g.Value - side * 1.5f); },
                        From = () => _ipCaseSpot.Value + AwayFromPoi(_ipPoi) * 3f,
                        On = at => at.AttachedToTosave() == null && at.AttachedToPOI() == null ? null : "not on the ground" });
                    if (_ipPoi != null)
                    {
                        _ipCases.Add(new IpCase { Name = "on building " + _ipPoi.name, Item = _ipWall, Spot = () => IpWall(), From = () => _ipWallFrom,
                            On = at => at.AttachedToPOI() == _ipPoi ? null : "not on the building" });
                        // the player's case (2026-11-25): held against the house wall by the hand, collision on - G wasn't offered
                        _ipCases.Add(new IpCase { Name = "held against building " + _ipPoi.name + ", collision on", Item = _ipAgainst, Natural = true,
                            Spot = () => IpWall(new[] { -0.7f, 1.2f, -1.2f, 0.4f }, out _, out _ipAgainstFrom), From = () => _ipAgainstFrom,
                            On = at => at.AttachedToPOI() == _ipPoi ? null : "not on the building" });
                    }
                    // a slot-only part (the player's hubcap, 2026-11-25): In Place applies to it too
                    var cap = AllowSlot() ? Spawn("Car07TireDisztarcsa") : null;
                    // on the car's side: there the game's hand point (0.9 m out) is on the surface; looking down at the ground it is
                    // half a metre up, and the hand pulled the cap off the ground before G (2026-11-25)
                    var cs = CarSpots(0.9f).Where(x => (x.At - _ipSpot).magnitude > 0.6f).OrderByDescending(x => Vector3.Dot(x.From - _car.transform.position, _car.transform.right)).FirstOrDefault();
                    _ipCases.Add(new IpCase { Name = "on the car's side (slot part " + (cap != null ? cap.name : "none") + ")", Item = cap,
                        Spot = () => cs != null ? cs.At : (Vector3?)null, From = () => cs.From,
                        On = at => { var to = at.AttachedToTosave(); return to != null && (to == _car || to.transform.IsChildOf(_car.transform)) ? null : "not on the car"; } });
                    var glue = SpawnGameGlue();
                    // a spot on the car's sides in view, away from the first in-place mount (the start car differs per run)
                    var gs = CarSpots(0.9f).Where(x => (x.At - _ipSpot).magnitude > 0.6f).OrderBy(x => Vector3.Dot(x.From - _car.transform.position, _car.transform.right)).FirstOrDefault();
                    _ipCases.Add(new IpCase { Name = "on the car's side (game glue item " + (glue != null ? glue.name : "none") + ")", Item = glue, GameGlue = true,
                        Spot = () => gs != null ? gs.At : (Vector3?)null, From = () => gs.From,
                        On = at => { var to = at.AttachedToTosave(); return to != null && (to == _car || to.transform.IsChildOf(_car.transform)) ? null : "not on the car"; } });
                    Write("in place, more: " + string.Join("; ", _ipCases.Select(c => (c.Item != null ? c.Item.name : "no item") + " -> " + c.Name).ToArray()));
                    _ipIdx = 0;
                    _step = 51; return 1f;
                }
                case 51:
                {
                    if (_ipIdx >= _ipCases.Count) { _step = 49; return 0.2f; }
                    var c = _ipCases[_ipIdx];
                    _ipCaseSpot = c.Item != null ? c.Spot() : null;
                    if (c.Item == null || _ipCaseSpot == null)
                    { Check(false, "in place " + c.Name + ": " + (c.Item == null ? "no item for it" : "no spot")); _ipIdx++; return 0.2f; }
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(c.Item);
                    Write("in place: holding " + c.Item.name + ": pickedUp " + (pl.pickedUp == c.Item.P));
                    _step = 52; return 1f;
                }
                case 52:
                {
                    var c = _ipCases[_ipIdx];
                    StandAndLook(_ipCaseSpot.Value, c.Range, c.From());
                    var at = c.Item.attachable;
                    Check(InPlace.Active(pl.pickedUp) && InPlace.LastLabel.EndsWith("In Place"),
                          "in place " + c.Name + ": " + c.Item.name + " carried in the In Place mode (" + InPlace.LastLabel + ")");
                    if (c.GameGlue) Check(at != null && !Mounting.Added.Contains(at), "in place " + c.Name + ": " + c.Item.name + " keeps its own glue (nothing added)");
                    pl.attachableColliderDisableToggle = !c.Natural;   // R: collision off, so it can go partly in
                    if (c.Natural) { _step = 55; _ipWaits = 0; return 0.5f; }
                    _step = 53; return 0.6f;
                }
                case 53:
                {
                    var c = _ipCases[_ipIdx];
                    StandToHold(_ipCaseSpot.Value, 0.9f, c.From(), c.Range);
                    // its middle on the surface: half of it inside
                    var rb = c.Item.GetComponent<Rigidbody>();
                    if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = _ipCaseSpot.Value; }
                    c.Item.transform.position = _ipCaseSpot.Value;
                    Write("in place " + c.Name + ": " + c.Item.name + " held at " + DescribeLook(pl) + ", offered " + pl.BCanAttachGlue);
                    _mountsBefore = (int)InPlace.Mounts;
                    Mounting.TestGlueDown = true;
                    _step = 54; return 0.6f;
                }
                case 55:   // held the player's way: the hand pulls it against the surface; then G, as a player presses it
                {
                    var c = _ipCases[_ipIdx];
                    StandToHold(_ipCaseSpot.Value, 0.96f, c.From());   // the hand's point a little behind the surface: pressed on it
                    if (++_ipWaits < 4) return 0.3f;
                    Write("in place " + c.Name + ": " + c.Item.name + " held at " + DescribeLook(pl) + ", " +
                          ((c.Item.transform.position - _ipCaseSpot.Value).magnitude * 1000f).ToString("F0") + " mm from the wall spot, offered " + pl.BCanAttachGlue);
                    Check(pl.BCanAttachGlue, "in place " + c.Name + ": the game offers [G]: Attach while it rests against the wall");
                    _mountsBefore = (int)InPlace.Mounts;
                    Mounting.TestGlueDown = true;
                    _step = 54; return 0.6f;
                }
                case 54:
                {
                    Mounting.TestGlueDown = false;
                    var c = _ipCases[_ipIdx];
                    var at = c.Item.attachable;
                    bool ok = InPlace.Mounts > _mountsBefore && at != null && at.attached;
                    Check(ok, "in place " + c.Name + ": " + c.Item.name + " mounted: " + (ok ? Mounting.LastMount : "not mounted (held " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + "; " + Mounting.LastLost + ")"));
                    if (ok)
                    {
                        var why = c.On(at);
                        Check(why == null, "in place " + c.Name + ": " + (why ?? "on it") + " (" + Mounting.LastMount + ")");
                        float dp = (c.Item.transform.position - InPlace.LastHeldPos).magnitude * 1000f, da = Quaternion.Angle(c.Item.transform.rotation, InPlace.LastHeldRot);
                        Check(dp < 0.5f && da < 0.05f, "in place " + c.Name + ": exactly as held (" + dp.ToString("F2") + " mm, " + da.ToString("F3") + " deg)");
                        Check(c.Item.GetComponent<Rigidbody>() == null && c.Item.transform.parent != null, "in place " + c.Name + ": " + c.Item.name + " is a rigid child now");
                        _ipDone.Add(c.Item);
                    }
                    if (pl.pickedUp != null) pl.Drop();
                    _ipIdx++;
                    _step = 51; return 1f;
                }
                case 49:   // in the air: nothing touched, nothing to mount on
                {
                    pl.attachableColliderDisableToggle = _ipToggle;
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(_ipB);
                    var air = OpenGround(_ipPoi);
                    if (air != null) StandAndLook(air.Value + Vector3.up * 4f, 2.5f, air.Value + AwayFromPoi(_ipPoi) * 3f);
                    _step = 46; return 1f;
                }
                case 46:
                {
                    var air = OpenGround(_ipPoi);
                    if (air != null) StandAndLook(air.Value + Vector3.up * 4f, 2.5f, air.Value + AwayFromPoi(_ipPoi) * 3f);
                    Write("in place: " + _ipB.name + " held in the air, looking at " + DescribeLook(pl) + ", offered " + pl.BCanAttachGlue);
                    _mountsBefore = (int)Mounting.Mounts;
                    Mounting.TestGlueDown = true;
                    _step = 47; return 0.6f;
                }
                case 47:
                    Mounting.TestGlueDown = false;
                    Check(Mounting.Mounts == _mountsBefore && (_ipB.attachable == null || !_ipB.attachable.attached) && pl.pickedUp == _ipB.P,
                          "in place: " + _ipB.name + " touching nothing: G mounts nothing, still carried");
                    InPlace.TestPress = true; _step = 48; return 0.4f;
                case 48:
                    if (!Plugin.PreviewMode.Value)
                    {
                        Check(!InPlace.On && !InPlace.PreviewStep && !pl.attachableOriginToggle, "Shift+R: in place -> closest point, preview mode off (" + InPlace.LastLabel + ")");
                        if (pl.pickedUp != null) pl.Drop();
                        _step = 10; return 0.5f;
                    }
                    Check(InPlace.PreviewStep && !InPlace.On && !pl.attachableOriginToggle && InPlace.LastLabel.EndsWith("Preview"), "Shift+R: in place -> preview (" + InPlace.LastLabel + ")");
                    InPlace.TestPress = true; _step = 60; return 0.4f;
                case 60:   // Shift+R's preview step: the whole cycle once more, then the green copy and its keys on the car
                    Check(!InPlace.PreviewStep && !InPlace.On && !pl.attachableOriginToggle && !InPlace.LastLabel.EndsWith("Preview") && !InPlace.LastLabel.EndsWith("In Place"),
                          "Shift+R: preview -> closest point (" + InPlace.LastLabel + ")");
                    InPlace.TestPress = true; _step = 61; return 0.4f;   // closest point -> origin
                case 61: InPlace.TestPress = true; _step = 62; return 0.4f;   // origin -> in place
                case 62: InPlace.TestPress = true; _step = 63; return 0.4f;   // in place -> preview
                case 63:
                {
                    Check(InPlace.PreviewStep && InPlace.PreviewActive(pl.pickedUp) && InPlace.LastLabel.EndsWith("Preview"), "Shift+R: round the cycle to preview again (" + InPlace.LastLabel + ")");
                    // a free spot on the car's side (the earlier mounts are on it too), else open ground by it
                    _pkOnCar = true; Vector3? s = null; float sign = -1f;
                    foreach (var c in new[] { new Vector2(-1f, -1.3f), new Vector2(1f, 1.3f), new Vector2(-1f, 0.6f), new Vector2(1f, -0.6f), new Vector2(-1f, -0.6f) })
                        if ((s = SideOfCar(c.x, c.y)) != null) { sign = c.x; break; }
                    if (s != null) _pkFrom = _car.transform.position + _car.transform.right * sign * 10f;
                    else
                    {
                        var g = OpenGround(_ipPoi);
                        var side = Vector3.Cross(Vector3.up, AwayFromPoi(_ipPoi)).normalized;
                        s = g == null ? null : Ground(g.Value + side * 3f) ?? Ground(g.Value - side * 3f);
                        _pkOnCar = false;
                        if (s != null) _pkFrom = s.Value + AwayFromPoi(_ipPoi) * 3f;
                    }
                    if (s == null)
                    {
                        Write("Shift+R preview mount skipped (no free spot on the car or the ground by it)");
                        if (pl.pickedUp != null) pl.Drop();
                        InPlace.Reset(pl);
                        _step = 10; return 0.5f;
                    }
                    _pkSpot = s.Value;
                    if (pl.pickedUp != _ipB.P) { if (pl.pickedUp != null) pl.Drop(); Hold(_ipB); }
                    StandAndLook(_pkSpot, 0.9f, _pkFrom);
                    _step = 64; return 1f;
                }
                case 64:
                    StandAndLook(_pkSpot, 0.9f, _pkFrom);
                    Check(Placement.ShownFor(pl.pickedUp), "Shift+R preview: the green copy shows for " + _ipB.name + " at the car (" + DescribeLook(pl) + ")");
                    Placement.TestActs.Enqueue(new KeyValuePair<Placement.Act, bool>(Placement.Act.TurnRight, false));
                    _step = 65; return 0.4f;
                case 65:
                    StandAndLook(_pkSpot, 0.9f, _pkFrom);
                    Check(Mathf.Abs(Placement.Turn - Plugin.AngleStep.Value) < 0.01f, "Shift+R preview: the turn key turns the copy (" + Placement.State() + ")");
                    _mountsBefore = (int)Mounting.Mounts;
                    Mounting.TestGlueDown = true;
                    _step = 66; return 0.6f;
                case 66:
                {
                    Mounting.TestGlueDown = false;
                    var at = _ipB.attachable;
                    bool ok = Mounting.Mounts > _mountsBefore && InPlace.PreviewStep && at != null && at.attached;
                    Check(ok, "Shift+R preview: G mounted " + _ipB.name + ": " + (ok ? Mounting.LastMount : "not mounted"));
                    if (ok)
                    {
                        var to = at.AttachedToTosave();
                        if (_pkOnCar) Check(to != null && (to == _car || to.transform.IsChildOf(_car.transform)), "Shift+R preview: on the car (" + (to != null ? to.name : "nothing") + ")");
                        else Check(to == null && at.AttachedToPOI() == null, "Shift+R preview: on the ground (" + (to != null ? to.name : at.AttachedToPOI() != null ? "a building" : "the ground") + ")");
                        CheckPlaced(_ipB, null, default);
                        _ipDone.Add(_ipB);
                    }
                    if (pl.pickedUp != null) pl.Drop();
                    InPlace.Reset(pl);
                    _step = 75; return 0.5f;
                }
                case 75:   // an item the game hangs on a joint (the fuel can, play test 2026-11-26): Shift+R has the game's two modes only
                {
                    _joint = Spawn("benzineskanna");
                    var at = _joint != null ? _joint.attachable : null;
                    if (at == null || at.parentAtAttach)
                    {
                        Write("joint item check skipped (" + (_joint == null ? "no fuel can" : at == null ? "the fuel can has no glue" : "the fuel can is glued rigidly") + ")");
                        _step = 10; return 0.5f;
                    }
                    InPlace.Reset(pl);
                    Hold(_joint);
                    _step = 76; return 1f;
                }
                case 76: InPlace.TestPress = true; _step = 77; return 0.4f;   // closest point -> origin
                case 77:
                    Check(pl.pickedUp == _joint.P && pl.attachableOriginToggle && !InPlace.On && !InPlace.PreviewStep, _joint.name + " (hangs on a joint): Shift+R: closest point -> origin (" + InPlace.LastLabel + ")");
                    InPlace.TestPress = true; _step = 78; return 0.4f;   // origin -> back to closest point, no in place
                case 78:
                    Check(!pl.attachableOriginToggle && !InPlace.On && !InPlace.PreviewStep && !InPlace.Active(pl.pickedUp) && !InPlace.LastLabel.EndsWith("In Place") && !InPlace.LastLabel.EndsWith("Preview"),
                          _joint.name + " (hangs on a joint): Shift+R: origin -> closest point, no in place or preview (" + InPlace.LastLabel + ")");
                    if (pl.pickedUp != null) pl.Drop();
                    InPlace.Reset(pl);
                    _step = 10; return 0.5f;
                case 10:   // the car moves: what is on it rides along
                {
                    _before = Poses();
                    var rb = _car.GetComponent<Rigidbody>();
                    var tr = _car.transform;
                    // away from the house, not into it (a car moved under its roof left no spot to aim at for the mp run)
                    var np = tr.position + AwayFromPoi(_ipPoi) * 3f + Vector3.up * 0.5f; var nr = Quaternion.AngleAxis(25f, Vector3.up) * tr.rotation;
                    if (rb != null) { rb.position = np; rb.rotation = nr; }
                    tr.SetPositionAndRotation(np, nr);
                    Write("car moved 3 m away from the house and turned 25 degrees");
                    _step = 11;
                    return 4f;
                }
                case 11:
                {
                    var after = Poses();
                    foreach (var it in _plans.Select(p => p.Item).Concat(_ipDone))
                    {
                        if (!_before.TryGetValue(it.idInSave, out var b) || !after.TryGetValue(it.idInSave, out var a)) continue;
                        if (b.Kind != "item" || b.ParentId != _car.idInSave) continue;
                        Check(Same(a, b), it.name + " stayed put on the moved car (" + Diff(a, b) + ")");
                    }
                    StandAndLook(_car.transform.position + Vector3.up * 0.6f, 5f);
                    _step = 12;
                    return 1.5f;
                }
                case 12:
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "MountAnything-mounted-car.png"));
                    _step = 13;
                    return 1.5f;
                case 13:
                {
                    var poi = _plans.FirstOrDefault(x => x.Name.StartsWith("building"));
                    if (poi != null && poi.Item.attachable != null && poi.Item.attachable.attached) StandAndLook(poi.Item.transform.position, 3f);
                    _step = 14;
                    return 1.5f;
                }
                case 14:
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "MountAnything-mounted-building.png"));
                    _step = 15;
                    return 1.5f;
                case 15:
                {
                    var poses = Poses();
                    var sb = new StringBuilder();
                    foreach (var it in _plans.Select(p => p.Item).Concat(_ipDone))   // the in-place mounts must load back too
                        if (poses.TryGetValue(it.idInSave, out var ps)) sb.AppendLine(ps.ToLine());
                    sb.AppendLine("player " + V(mainscript.GlobalFromUnityPos(pl.transform.position)));
                    File.WriteAllText(StateFile, sb.ToString());
                    Write("expected poses written for " + poses.Count + " mounted items");
                    savedatascript.s.saveScreen.AutoSave();
                    Write("autosaved");
                    _step = 16;
                    return 6f;
                }
                default:
                    return -1f;
            }
        }

        private static Dictionary<uint, Pose> _before;
        private static Quaternion _preRot, _previewRot;
        private static int _framesBefore;
        private static Vector3 _previewPos;

        // ------------------------------------------------------------------ placement checks

        private static void PressPlacement(Placement.Act[] acts, bool fine)
        {
            if (acts == null) return;
            foreach (var a in acts) Placement.TestActs.Enqueue(new KeyValuePair<Placement.Act, bool>(a, fine));
            Write("placement keys: " + string.Join(" ", acts.Select(a => a.ToString()).ToArray()) + (fine ? " (fine)" : ""));
        }

        /// The preview is up, drawn, turned by the keys as expected, and rests on the surface (plus the out/in offset).
        private static void CheckPreview(string name, tosaveitemscript item, Quaternion pre, float angle, float lift, bool live)
        {
            bool shown = !live || Placement.ShownFor(item.P);
            int frames = Placement.DrawnFrames - _framesBefore;
            if (live) Check(shown && frames > 0 && Placement.LastDrawnMeshes > 0, name + ": preview shown (" + Placement.LastDrawnMeshes + " meshes, drawn in " + frames + " frames of the last half second), " + Placement.State());
            if (!shown) return;
            float turned = Quaternion.Angle(pre, Placement.KeyRot);
            if (live) Check(Mathf.Abs(turned - angle) < 2f, name + ": the keys turned the preview " + turned.ToString("F1") + " deg (expected " + angle.ToString("F1") + ")");
            float gap = Placement.Gap();
            Check(Mathf.Abs(Placement.Lift - lift) < 1e-4f && Mathf.Abs(gap - lift) < 0.001f,
                  name + ": its lowest point is " + (gap * 1000f).ToString("F1") + " mm out of the surface (set " + (Placement.Lift * 1000f).ToString("F1") + ", expected " + (lift * 1000f).ToString("F1") + ")");
            if (Placement.Mode != Placement.Base.Held && Placement.TiltAway == 0f && Placement.TiltLeft == 0f)
            {
                float up = Vector3.Angle(Placement.Rot * Placement.BaseAxis, Placement.Normal);
                Check(up < 0.5f, name + ": " + Placement.Mode + ": its axis " + Placement.BaseAxis + " is " + up.ToString("F2") + " deg off the surface's normal");
            }
            // the middle of the item on the crosshair: the point on the surface the item's centre is over
            var off = Vector3.ProjectOnPlane(Placement.Pos + Placement.Rot * PlacementCenter() - Placement.Point, Placement.Normal).magnitude;
            Check(off < 0.002f, name + ": the item's middle is over the crosshair (" + (off * 1000f).ToString("F1") + " mm off)");
        }

        private static Vector3 PlacementCenter() => Placement.Center;

        /// The mounted item is exactly where the preview was (the pose mounting used, and the preview half a second before).
        private static void CheckPlaced(tosaveitemscript item, Vector3? pos, Quaternion rot)
        {
            // on its mount point (which rides what it is on): nothing moved it after mounting
            var t = item.transform; var pt = item.attachable.point;
            var lp = Quaternion.Inverse(pt.rotation) * (t.position - pt.position); var lq = Quaternion.Inverse(pt.rotation) * t.rotation;
            float dp = (lp - Placement.LastPos).magnitude * 1000f, da = Quaternion.Angle(lq, Placement.LastRot);
            Check(dp < 0.5f && da < 0.1f, item.name + " mounted at the pose the preview showed that frame, and stays there on its point (" + dp.ToString("F2") + " mm, " + da.ToString("F3") + " deg)");
            if (pos == null) return;
            float dp2 = (t.position - pos.Value).magnitude * 1000f, da2 = Quaternion.Angle(t.rotation, rot);
            Write(item.name + " vs. the preview the frame before the key: " + dp2.ToString("F1") + " mm, " + da2.ToString("F2") + " deg (the hand moves it)");
        }

        // ------------------------------------------------------------------ load run

        private static List<Pose> _expected;
        private static Vector3d _home;
        private static tosaveitemscript _taken;
        private static bool _toggleWas, _takenWasAdded, _driveHeld;
        private static int _heldFrames;

        private static float LoadStep()
        {
            var pl = Pl;
            switch (_step)
            {
                case 0:
                {
                    _expected = new List<Pose>();
                    if (!File.Exists(StateFile)) { Write("no " + StateFile); return -1f; }
                    foreach (var l in File.ReadAllLines(StateFile))
                        if (l.StartsWith("player ")) { var f = l.Split(' '); _home = new Vector3d(double.Parse(f[1], Ic), double.Parse(f[2], Ic), double.Parse(f[3], Ic)); }
                        else if (l.Trim().Length > 0) _expected.Add(Pose.FromLine(l));
                    Write("expecting " + _expected.Count + " mounted items");
                    CheckAll("after loading");
                    _step = 1;
                    return 0.5f;
                }
                case 1:   // take the one on the car off, the game's way (pick it up)
                {
                    var e = _expected.FirstOrDefault(x => x.Kind == "item" && savedatascript.s.items.TryGetValue(x.Id, out var t) && t != null && t.attachable != null && t.attachable.attached);
                    if (e == null) { Write("nothing mounted on an item to take off"); _step = 3; return 0.2f; }
                    _taken = savedatascript.s.items[e.Id];
                    if (pl.pickedUp != null) pl.Drop();
                    StandAndLook(_taken.transform.position, 1.2f);
                    // the game's "toggle collision" (R) at off: while the mod's component is still on the held item, the
                    // game switches its colliders off every frame; they must be back on once it is put down
                    _toggleWas = pl.attachableColliderDisableToggle;
                    _takenWasAdded = Mounting.Added.Contains(_taken.attachable);
                    pl.attachableColliderDisableToggle = true;
                    pl.Pickup(_taken.P);
                    _driveHeld = true; _heldFrames = 0;
                    Write("picked up " + _taken.name + " (" + _taken.idInSave + "), collision toggle at off, the mod's component: " + _takenWasAdded);
                    _step = 2;
                    return 1.5f;
                }
                case 2:
                {
                    Check(_taken.attachable == null || !_taken.attachable.attached, _taken.name + " came off when picked up");
                    // carried, it keeps the mod's glue (the game's own G/R/Shift+R, 0.3); without GameControls it goes at once
                    if (Plugin.GameControls.Value && _takenWasAdded)
                        Check(_taken.attachable != null && Mounting.Added.Contains(_taken.attachable), _taken.name + " keeps the mod's glue while carried");
                    else Check(_taken.attachable == null, _taken.name + " is a plain item again (the mod's component removed)");
                    Check(_taken.GetComponent<Rigidbody>() != null && _taken.transform.parent == null || pl.pickedUp == _taken.P, _taken.name + " has its Rigidbody back");
                    _driveHeld = false;
                    Write("held for " + _heldFrames + " frames of the game's held-item work");
                    pl.Drop();
                    _expected.RemoveAll(x => x.Id == _taken.idInSave);
                    _step = 20;
                    return 1f;
                }
                case 20:
                {
                    pl.attachableColliderDisableToggle = _toggleWas;
                    int on = 0, all = 0;
                    if (_taken.P != null && _taken.P.cols != null)
                        foreach (var c in _taken.P.cols) if (c != null && c.col != null) { all++; if (c.col.enabled) on++; }
                    Check(all > 0 && on == all, _taken.name + " put down after being taken off with the collision toggle at off: colliders " + on + "/" + all + " on" +
                          (_takenWasAdded ? "" : " (its prefab's own component: the game's Drop does it)"));
                    if (_takenWasAdded) Check(_taken.attachable == null, _taken.name + " put down: a plain item again (the mod's glue removed)");
                    _step = 3;
                    return 0.2f;
                }
                case 3:   // far away and back: unloaded and placed again
                    pl.Teleport(mainscript.UnityPosFromGlobal(_home + new Vector3d(3000, 300, 0)));
                    Write("teleported 3 km away");
                    _step = 4;
                    return 25f;
                case 4:
                {
                    int live = _expected.Count(x => savedatascript.s.items.TryGetValue(x.Id, out var t) && t != null);
                    Write("3 km away: " + live + " of " + _expected.Count + " mounted items still loaded here");
                    var u = mainscript.UnityPosFromGlobal(_home);
                    pl.Teleport(u + Vector3.up * 1f);
                    Write("back home");
                    _step = 5;
                    return 25f;
                }
                case 5:
                    CheckAll("after 3 km away and back");
                    var car = savedatascript.s.items.Values.Where(t => t != null && t.car != null).OrderBy(t => (t.transform.position - pl.transform.position).sqrMagnitude).FirstOrDefault();
                    if (car != null) StandAndLook(car.transform.position + Vector3.up * 0.6f, 5f);
                    _step = 6;
                    return 1.5f;
                case 6:
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "MountAnything-reloaded-car.png"));
                    _step = 7;
                    return 2f;
                default:
                    return -1f;
            }
        }

        private static void CheckAll(string when)
        {
            var now = Poses();
            foreach (var e in _expected)
            {
                string name = savedatascript.s.items.TryGetValue(e.Id, out var t) && t != null ? t.name : "item " + e.Id;
                if (!now.TryGetValue(e.Id, out var p)) { Check(false, when + ": " + name + " is not mounted (" + (t == null ? "not loaded" : "attachable " + (t.attachable != null)) + ")"); continue; }
                Check(p.Kind == e.Kind && p.ParentId == e.ParentId && (p.Kind != "poi" || p.ParentName == e.ParentName) && Same(p, e),
                      when + ": " + name + " on " + p.Kind + " " + p.ParentName + " (" + p.ParentId + "), " + Diff(p, e));
            }
        }

        // ------------------------------------------------------------------ helpers

        private class Pose
        {
            public uint Id, ParentId; public string Kind, ParentName; public Vector3d Pos; public Quaternion Rot;
            public string ToLine() => Id + " " + Kind + " " + ParentId + " " + ParentName.Replace(' ', '_') + " " + V(Pos) + " " +
                                      Rot.x.ToString("R", Ic) + " " + Rot.y.ToString("R", Ic) + " " + Rot.z.ToString("R", Ic) + " " + Rot.w.ToString("R", Ic);
            public static Pose FromLine(string l)
            {
                var f = l.Split(' ');
                return new Pose
                {
                    Id = uint.Parse(f[0], Ic), Kind = f[1], ParentId = uint.Parse(f[2], Ic), ParentName = f[3],
                    Pos = new Vector3d(double.Parse(f[4], Ic), double.Parse(f[5], Ic), double.Parse(f[6], Ic)),
                    Rot = new Quaternion(float.Parse(f[7], Ic), float.Parse(f[8], Ic), float.Parse(f[9], Ic), float.Parse(f[10], Ic))
                };
            }
        }

        private static string V(Vector3d v) => v.x.ToString("R", Ic) + " " + v.y.ToString("R", Ic) + " " + v.z.ToString("R", Ic);

        /// Every mounted item: what it is on and where, relative to that (the world: global position).
        private static Dictionary<uint, Pose> Poses()
        {
            var res = new Dictionary<uint, Pose>();
            foreach (var t in savedatascript.s.items.Values)
            {
                if (t == null || t.attachable == null || !t.attachable.attached || t.attachable.slot != null) continue;
                var at = t.attachable;
                var to = at.AttachedToTosave(); var poi = at.AttachedToPOI();
                Transform par = to != null ? to.transform : poi != null ? poi.transform : null;
                var p = new Pose { Id = t.idInSave, Kind = to != null ? "item" : poi != null ? "poi" : "world", ParentId = to != null ? to.idInSave : 0,
                                   ParentName = (to != null ? to.name : poi != null ? poi.name : "-").Replace(' ', '_') };
                if (par != null) { var lp = par.InverseTransformPoint(t.transform.position); p.Pos = new Vector3d(lp.x, lp.y, lp.z); p.Rot = Quaternion.Inverse(par.rotation) * t.transform.rotation; }
                else { p.Pos = mainscript.GlobalFromUnityPos(t.transform.position); p.Rot = t.transform.rotation; }
                res[t.idInSave] = p;
            }
            return res;
        }

        private static bool Same(Pose a, Pose b) => (a.Pos - b.Pos).magnitude < 0.02 && Quaternion.Angle(a.Rot, b.Rot) < 1f;
        private static string Diff(Pose a, Pose b) => "off by " + ((a.Pos - b.Pos).magnitude * 1000).ToString("F1") + " mm, " + Quaternion.Angle(a.Rot, b.Rot).ToString("F2") + " deg";

        private static List<tosaveitemscript> LooseItems(fpscontroller pl, float within) =>
            savedatascript.s.items.Values.Where(t => t != null && t.P != null && t.P == t.GetComponent<pickupable>() && t.car == null && t.ragdollPos == null && t.transform.parent == null &&
                                                     (t.attachable == null || !t.attachable.attached) && t.P.physlock == null && !t.P.pickedUp && t.P.CanPickup() &&
                                                     (t.transform.position - pl.transform.position).magnitude < within)
                .OrderBy(t => (t.transform.position - pl.transform.position).sqrMagnitude).ToList();

        private static Bounds CarBounds()
        {
            var cols = _car.GetComponentsInChildren<Collider>().Where(c => !c.isTrigger && c.enabled).ToArray();
            var b = cols[0].bounds;
            foreach (var c in cols) b.Encapsulate(c.bounds);
            return b;
        }

        private static Vector3? SurfaceToward(Vector3 from, Vector3 to, Transform under)
        {
            var dir = to - from;
            foreach (var h in Physics.RaycastAll(from, dir.normalized, dir.magnitude + 2f, Pl.attachLayer).OrderBy(h => h.distance))
                if (!h.collider.isTrigger && (under == null || h.collider.transform.IsChildOf(under))) return h.point;
            return null;
        }

        private static Vector3? SideOfCar() => SideOfCar(1f);

        private static Vector3? SideOfCar(float sign) => SideOfCar(sign, 0f);

        private static Vector3? SideOfCar(float sign, float forward)
        {
            var b = CarBounds();
            var tr = _car.transform;
            var c = b.center + Vector3.up * b.extents.y * 0.2f + tr.forward * forward;
            return SurfaceToward(c + tr.right * sign * (b.extents.magnitude + 2f), c, tr);
        }

        private static Vector3? RoofOfCar()
        {
            var b = CarBounds();
            return SurfaceToward(b.center + Vector3.up * (b.extents.y + 3f), b.center, _car.transform);
        }

        /// Open ground (no item, no building) under the point.
        private static Vector3? Ground(Vector3 at)
        {
            foreach (var h in Physics.RaycastAll(at + Vector3.up * 5f, Vector3.down, 20f, Pl.attachLayer).OrderBy(h => h.distance))
            {
                if (h.collider.isTrigger || h.collider.transform.IsChildOf(Pl.transform)) continue;
                if (h.collider.GetComponentInParent<tosaveitemscript>() != null || h.collider.GetComponentInParent<poiScript>() != null) return null;
                return h.point;
            }
            return null;
        }

        /// Open ground near the car: tries spots around it until one is clear.
        private static Vector3? GroundNearCar(float dist, float startDeg)
        {
            var tr = _car.transform;
            for (int i = 0; i < 12; i++)
            {
                var dir = Quaternion.AngleAxis(startDeg + i * 30f, Vector3.up) * tr.forward;
                var g = Ground(tr.position + dir * dist);
                if (g != null) return g;
            }
            return null;
        }

        private static Vector3 AwayFromPoi(poiScript poi)
        {
            if (poi == null) return -_car.transform.forward;
            var d = _car.transform.position - poi.transform.position; d.y = 0f;
            return d.sqrMagnitude > 0.01f ? d.normalized : Vector3.forward;
        }

        /// Open ground well away from the building and the car (the start car stands by the house).
        private static Vector3? OpenGround(poiScript poi)
        {
            var away = AwayFromPoi(poi);
            for (int i = 0; i < 16; i++)
            {
                var dir = Quaternion.AngleAxis((i % 2 == 0 ? 1 : -1) * (i / 2) * 20f, Vector3.up) * away;
                var g = Ground(_car.transform.position + dir * 9f);
                if (g != null) return g;
            }
            return null;
        }

        private static Vector3? _poiStand, _poiTarget;

        /// A point on the building's outside wall, seen from a spot outside it (rays from beside the car toward it).
        private static Vector3? OnPoi(poiScript poi)
        {
            if (_poiTarget != null) return _poiTarget;
            var stand = _car.transform.position + Vector3.up * 1.4f - AwayFromPoi(poi) * 0f;
            var center = poi.cols.Where(x => x != null && !x.isTrigger).Select(x => x.bounds.center).Aggregate((a, b) => a + b) / poi.cols.Count(x => x != null && !x.isTrigger);
            for (int i = 0; i < 48; i++)
            {
                var s = stand + Quaternion.AngleAxis(i * 15f, Vector3.up) * Vector3.forward * (i < 24 ? 6f : 10f);
                var to = new Vector3(center.x, s.y, center.z);
                foreach (var h in Physics.RaycastAll(s, (to - s).normalized, 40f, Pl.attachLayer).OrderBy(h => h.distance))
                {
                    if (h.collider.isTrigger || h.collider.transform.IsChildOf(Pl.transform) || h.collider.GetComponentInParent<tosaveitemscript>() != null) continue;
                    if (h.collider.GetComponentInParent<poiScript>() == poi && h.collider.GetComponentInParent<tosaveitemscript>() == null && h.distance > 1.5f)
                    { _poiTarget = h.point; _poiStand = s; return _poiTarget; }
                    break;
                }
            }
            return null;
        }

        private static Vector3 PoiStand(poiScript poi) { OnPoi(poi); return _poiStand ?? _car.transform.position; }

        private static Vector3 _ipWallFrom;

        /// Another spot on the same building wall, beside the one OnPoi found (an item is already mounted there).
        private static int _ipWaits;

        private static Vector3? IpWall() => IpWall(new[] { 0.7f, -0.7f, 1.2f, -1.2f, 0.4f, -0.4f }, out _, out _ipWallFrom);

        private static Vector3? IpWall(float[] offsets, out Vector3 normal, out Vector3 from)
        {
            normal = Vector3.zero; from = Vector3.zero;
            if (OnPoi(_ipPoi) == null || _poiStand == null) return null;
            var stand = _poiStand.Value; var target = _poiTarget.Value;
            var dir = target - stand; dir.y = 0f;
            var tangent = Vector3.Cross(Vector3.up, dir.normalized).normalized;
            foreach (var o in offsets)
            {
                var s = stand + tangent * o; var t = target + tangent * o;
                foreach (var h in Physics.RaycastAll(s, (t - s).normalized, 40f, Pl.attachLayer).OrderBy(h => h.distance))
                {
                    if (h.collider.isTrigger || h.collider.transform.IsChildOf(Pl.transform)) continue;
                    if (h.collider.GetComponentInParent<poiScript>() == _ipPoi && h.collider.GetComponentInParent<tosaveitemscript>() == null && h.distance > 1.5f)
                    { from = s; normal = h.normal; return h.point; }
                    break;
                }
            }
            return null;
        }

        private static bool AllowSlot() => Plugin.AllowSlotParts.Value;

        /// A new item from the item list, spawned the dev menu's way, beside the player.
        private static tosaveitemscript Spawn(string prefab)
        {
            var g = itemdatabase.s.items.FirstOrDefault(x => x != null && x.name == prefab);
            var go = g != null ? mainscript.s.Spawn(g) : null;
            var ts = go != null ? go.GetComponent<tosaveitemscript>() : null;
            if (ts == null) { Write("could not spawn " + prefab); return null; }
            var p = Pl.transform.position + Pl.transform.forward * 1.5f + Vector3.up * 1f;
            var rb = ts.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.position = p; }
            ts.transform.position = p;
            var at = ts.attachable;
            Write("spawned " + ts.name + " (" + ts.idInSave + ")" + (at != null ? ": onlyToSlot " + at.onlyToSlot + ", parentAtAttach " + at.parentAtAttach + ", useColliderAttachPoint " + at.useColliderAttachPoint : ": no attachable"));
            return ts;
        }

        /// A new item the game glues by itself, rigidly (a mirror, the clock, a plate), spawned the dev menu's way.
        private static tosaveitemscript SpawnGameGlue()
        {
            foreach (var n in new[] { "BigMirror", "clock", "plate" })
            {
                var g = itemdatabase.s.items.FirstOrDefault(x => x != null && x.name == n);
                var a = g != null ? g.GetComponent<attachablescript>() : null;
                if (a == null || a.onlyToSlot || !a.parentAtAttach) continue;
                var go = mainscript.s.Spawn(g);
                var ts = go != null ? go.GetComponent<tosaveitemscript>() : null;
                if (ts == null) continue;
                var p = Pl.transform.position + Pl.transform.forward * 1.5f + Vector3.up * 1f;
                var rb = ts.GetComponent<Rigidbody>();
                if (rb != null) { rb.velocity = Vector3.zero; rb.position = p; }
                ts.transform.position = p;
                Write("spawned " + ts.name + " (" + ts.idInSave + ")");
                return ts;
            }
            Write("no game glue item to spawn");
            return null;
        }


        private static Vector3? OnItem(tosaveitemscript under)
        {
            var b = under.GetComponentsInChildren<Collider>().Where(x => !x.isTrigger).Select(x => x.bounds).Aggregate((x, y) => { x.Encapsulate(y); return x; });
            return SurfaceToward(b.center + Vector3.up * 2f, b.center, under.transform);
        }

        /// The item the next one is mounted on lies on open ground near the car.
        private static void PlaceUnder(tosaveitemscript under)
        {
            var g = GroundNearCar(5f, 180f) ?? _car.transform.position - _car.transform.forward * 5f;
            var rb = under.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = g + Vector3.up * 0.4f; }
            under.transform.position = g + Vector3.up * 0.4f;
            Write("put " + under.name + " on the ground near the car");
        }

        /// Picks a loose item up the way the player would, from right in front of the camera (the game drops an item
        /// picked up from farther than its drop distance).
        private static void Hold(tosaveitemscript item)
        {
            var cam = Pl.Cam.transform;
            var p = cam.position + cam.forward * 0.9f;
            var rb = item.GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = p; }
            item.transform.position = p;
            Pl.Pickup(item.P);
        }

        /// Puts the player so the camera is `dist` from the point (horizontally, on the side of `from`, default: where
        /// it stands now) and aims the camera at it, the way RevaMP's test `mp lookat` aims. A held item comes along
        /// (the game drops one that ends up too far from the hand).
        private static void StandAndLook(Vector3 point, float dist, Vector3? from = null)
        {
            var pl = Pl;
            var cam = pl.Cam.transform;
            var camOff = cam.position - pl.transform.position;
            Transform held = pl.pickedUp != null ? pl.pickedUp.transform : null;
            Vector3 heldLocal = held != null ? cam.InverseTransformPoint(held.position) : Vector3.zero;
            Quaternion heldRot = held != null ? Quaternion.Inverse(cam.rotation) * held.rotation : Quaternion.identity;
            var away = (from ?? pl.transform.position) - point; away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            var camPos = point + away.normalized * dist;
            camPos.y = Mathf.Max(point.y + 0.4f, (Ground(camPos) ?? camPos).y + camOff.y);
            pl.Teleport(camPos - camOff);
            var dv = point - camPos;
            var flat = new Vector3(dv.x, 0f, dv.z);
            if (flat.sqrMagnitude > 1e-4f) pl.BodyRot.rotation = Quaternion.LookRotation(flat);
            pl.FyRot = -Mathf.Atan2(dv.y, flat.magnitude) * Mathf.Rad2Deg;
            if (!mainscript.IsVR()) pl.Th.localEulerAngles = new Vector3(pl.FyRot, 0f, 0f);
            if (held != null)
            {
                var hp = cam.TransformPoint(heldLocal); var hr = cam.rotation * heldRot;
                var rb = held.GetComponent<Rigidbody>();
                if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = hp; rb.rotation = hr; }
                held.SetPositionAndRotation(hp, hr);
            }
        }

        /// Stands so the point the game's hand holds the item at (as far out along the view as it was picked up: Hold
        /// picks up at 0.9 m) is `hold` metres from the camera at the point, as a player holding it there would: looking
        /// down at a car's side from eye height, StandAndLook's 0.9 m across put the hand 18 cm short, and the hand pulled
        /// the item off its spot before G. Range is the fallback for points below reach (the ground).
        private static void StandToHold(Vector3 point, float hold, Vector3 from, float range = 0.9f)
        {
            StandAndLook(point, Mathf.Min(range, hold), from);
            float dy = Pl.Cam.transform.position.y - point.y;
            if (Mathf.Abs(dy) >= hold - 0.05f) { StandAndLook(point, range, from); return; }
            StandAndLook(point, Mathf.Sqrt(hold * hold - dy * dy), from);
        }

        private static string DescribeLook(fpscontroller pl)
        {
            if (!Mounting.FindHit(pl, out var h)) return "nothing within " + pl.FrayRange.ToString("F1") + " m";
            var to = h.collider.GetComponentInParent<tosaveitemscript>(); var poi = h.collider.GetComponentInParent<poiScript>();
            return h.collider.name + " " + h.distance.ToString("F2") + " m (" + (to != null ? "item " + to.name : poi != null ? "building " + poi.name : "world") + ")";
        }

        private static void PrefabReport()
        {
            var db = itemdatabase.s;
            if (db == null || db.items == null) { Write("no item database"); return; }
            int items = 0, withAt = 0; var list = new List<string>();
            foreach (var g in db.items)
            {
                if (g == null) continue;
                var ts = g.GetComponent<tosaveitemscript>();
                if (ts == null) continue;
                items++;
                var at = g.GetComponent<attachablescript>();
                if (at == null) continue;
                withAt++;
                if (!at.onlyToSlot) list.Add(g.name + (at.parentAtAttach ? "" : "(joint)") + (at.isPart ? "(part)" : ""));
            }
            Write("item prefabs: " + items + ", with an attachablescript " + withAt + "; the game can glue anywhere: " + list.Count + " (" + string.Join(", ", list.ToArray()) + ")");
        }
    }
}
