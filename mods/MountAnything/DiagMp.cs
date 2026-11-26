using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// Two-game RevaMP check on one PC (test copies only): one game runs -madiag mphost (continues its last save,
    /// hosts on UDP 27070), a second copy runs -madiag mpjoin (joins 127.0.0.1:27070 from its menu). They talk through
    /// files in a folder both reach: TLD_MP_SHARED, else "mp-shared" next to the game folder (beside both copies):
    ///  1. the joiner's view of every mounted item (item, what it's on, pose there) against the host's;
    ///  2. the host mounts a held item on the car's other side, then takes the item on the ground off: the joiner sees both;
    ///  3. the joiner mounts an item on the car's roof: the host sees it.
    internal static partial class Diag
    {
        private static readonly string Shared = Environment.GetEnvironmentVariable("TLD_MP_SHARED") is string s && s.Length > 0
            ? s : Path.Combine(Path.GetDirectoryName(BepInEx.Paths.GameRootPath), "mp-shared");
        private static string SharedFile(string name) => Path.Combine(Shared, name);

        private static string Mp(string method, params object[] args)
        {
            var t = AccessTools.TypeByName("TLDRevamp.Net.Mp");
            var m = t != null ? AccessTools.Method(t, method) : null;
            if (m == null) return "(no RevaMP " + method + ")";
            var ps = m.GetParameters();
            var call = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) call[i] = i < args.Length ? Convert.ChangeType(args[i], ps[i].ParameterType, CultureInfo.InvariantCulture) : ps[i].DefaultValue;
            return m.Invoke(null, call) as string ?? "";
        }

        private static string MpStatus() { try { return Mp("Status"); } catch { return ""; } }

        private static void Event(string who, string line)
        {
            Directory.CreateDirectory(Shared);
            File.AppendAllText(SharedFile(who + "-events.txt"), line + Environment.NewLine);
            Write("event " + line);
        }

        private static string FindEvent(string who, string prefix)
        {
            var f = SharedFile(who + "-events.txt");
            if (!File.Exists(f)) return null;
            return File.ReadAllLines(f).FirstOrDefault(l => l.StartsWith(prefix));
        }

        /// One line per mounted item, comparable between machines (save ids differ): item, kind, parent, pose.
        private static string ViewLine(tosaveitemscript t, Pose p) =>
            Norm(t.name) + " " + p.Kind + " " + Norm(p.ParentName) + " " + V(p.Pos) + " " +
            p.Rot.x.ToString("R", Ic) + " " + p.Rot.y.ToString("R", Ic) + " " + p.Rot.z.ToString("R", Ic) + " " + p.Rot.w.ToString("R", Ic);

        private static string Norm(string n) => n.Replace("(Clone)", "").Trim().Replace(' ', '_');

        private static List<string> View() => Poses().Select(kv => ViewLine(savedatascript.s.items[kv.Key], kv.Value)).OrderBy(x => x).ToList();

        private class VItem { public string Name, Kind, Parent; public Vector3d Pos; public Quaternion Rot; }
        private static VItem ParseView(string l)
        {
            var f = l.Split(' ');
            return new VItem { Name = f[0], Kind = f[1], Parent = f[2], Pos = new Vector3d(double.Parse(f[3], Ic), double.Parse(f[4], Ic), double.Parse(f[5], Ic)),
                               Rot = new Quaternion(float.Parse(f[6], Ic), float.Parse(f[7], Ic), float.Parse(f[8], Ic), float.Parse(f[9], Ic)) };
        }

        /// Does `mine` (this machine's view) have the item of `line` mounted on the same kind of thing at the same pose?
        private static bool Has(List<string> mine, string line, out string how)
        {
            var w = ParseView(line);
            var best = mine.Select(ParseView).Where(m => m.Name == w.Name && m.Kind == w.Kind && m.Parent == w.Parent)
                           .OrderBy(m => (m.Pos - w.Pos).magnitude).FirstOrDefault();
            if (best == null) { how = "missing"; return false; }
            double dp = (best.Pos - w.Pos).magnitude; float da = Quaternion.Angle(best.Rot, w.Rot);
            // the game's joint glue (the sausage, air fresheners) hangs from its point and swings: each machine shows its own swing
            bool swings = savedatascript.s != null && savedatascript.s.items.Values.Any(t => t != null && t.attachable != null && t.attachable.attached &&
                                                                                          !t.attachable.parentAtAttach && t.name.Replace("(Clone)", "").Trim() == w.Name);
            how = "off by " + (dp * 1000).ToString("F1") + " mm, " + da.ToString("F2") + " deg" + (swings ? " (hangs on a joint: position only)" : "");
            return dp < 0.05 && (da < 2f || swings);
        }

        private static int _wait;
        private static string _liveName, _offName;

        private static tosaveitemscript CarWithMounts()
        {
            foreach (var kv in Poses())
            {
                if (kv.Value.Kind != "item") continue;
                var t = savedatascript.s.items[kv.Key];
                // on the car's body, or on one of its parts (a door is an item of its own)
                for (var to = t.attachable.AttachedToTosave(); to != null; to = to.transform.parent != null ? to.transform.parent.GetComponentInParent<tosaveitemscript>() : null)
                    if (to.car != null) return to;
            }
            return null;
        }


        // ------------------------------------------------------------------ host

        private static float HostStep()
        {
            var pl = Pl;
            switch (_step)
            {
                case 0:
                    if (Directory.Exists(Shared)) foreach (var f in Directory.GetFiles(Shared)) File.Delete(f);
                    Directory.CreateDirectory(Shared);
                    Write("hosting: " + Mp("Host", 27070, ""));
                    Event("host", "hosting");
                    _step = 1; _wait = 0;
                    return 1f;
                case 1:   // a player joins
                {
                    var s = MpStatus();
                    if (!s.Contains("\"remotePlayers\":0") && s.Contains("\"remotePlayers\":")) { Write("a player joined: " + s.Substring(0, Math.Min(200, s.Length))); _step = 2; return 45f; }
                    if (++_wait > 300) { Write("nobody joined in 5 minutes"); return -1f; }
                    return 1f;
                }
                case 2:
                {
                    var v = View();
                    File.WriteAllLines(SharedFile("host-view.txt"), v.ToArray());
                    Write("host view: " + v.Count + " mounted items written");
                    Event("host", "view " + Time.realtimeSinceStartup.ToString("F0", Ic));
                    _car = CarWithMounts();
                    if (_car == null) { Write("no car with mounted items here"); return -1f; }
                    // a plain item: not one with something mounted on it (the siphon carrying the magazine snagged on the car
                    // and the game dropped it, 2026-11-24), not one with an R of its own (it gets no glue while carried)
                    var item = LooseItems(pl, 120f).FirstOrDefault(t => t.attachable == null && !Mounting.OwnR(t.P) &&
                                                                        !t.GetComponentsInChildren<attachablescript>().Any(a => a.attached && a.tosave != t));
                    if (item == null) { Write("no loose item to mount"); return -1f; }
                    _liveName = item.name;
                    if (pl.pickedUp != null) pl.Drop();
                    // spots on the car's sides the camera sees from where it will stand (the car may be parked against a wall)
                    _hostSpots = CarSpots(1.4f);
                    Write("host: " + _hostSpots.Count + " spots on " + _car.name + "'s sides in view");
                    Hold(item);
                    var s0 = _hostSpots.Count > 0 ? _hostSpots[0] : null;
                    _plans.Clear(); _plans.Add(new Plan { Name = "car's side", Item = item, Target = () => s0 != null ? s0.At : RoofOfCar(), From = () => s0 != null ? s0.From : _car.transform.position + _car.transform.right * 10f });
                    _step = 3;
                    return 1f;
                }
                case 3:
                    if (_plans[0].Target() == null) { Check(false, "host: no spot on the car to aim at"); _step = 6; return 0.5f; }
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    // flat, turned 45 degrees, tipped 15 away, 2 cm out: the joiner must see this pose
                    PressPlacement(new[] { Placement.Act.Reset, Placement.Act.TurnRight, Placement.Act.TurnRight, Placement.Act.TurnRight, Placement.Act.TiltAway, Placement.Act.Out, Placement.Act.Out }, false);
                    _step = 4; return 0.6f;
                case 4:
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    Mounting.IsMountable(pl.pickedUp, out var whyNot);
                    Write("host: holding " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ", looking at " + DescribeLook(pl) + "; mountable: " + (whyNot.Length == 0 ? "yes" : whyNot) + ", game glue offered " + pl.BCanAttachGlue + ", BAttach " + pl.BAttach + ", BnowPickedUp " + pl.BnowPickedUp + ", crsrLocked " + mainscript.s.crsrLocked);
                    _mountsBefore = (int)Mounting.Mounts; Mounting.TestPress = true; _step = 5; return 0.6f;
                case 5:
                {
                    Mounting.TestPress = false;
                    var it = _plans[0].Item;
                    bool ok = Mounting.Mounts > _mountsBefore && it.attachable != null && it.attachable.attached;
                    Check(ok, "host mounts " + it.name + " on the car's side: " + Mounting.LastMount);
                    if (ok) { var on = it.attachable.AttachedToTosave(); Check(on != null && (on == _car || on.transform.IsChildOf(_car.transform)), "host: " + it.name + " is on the car (" + (on != null ? on.name : "not on an item") + ")"); }
                    if (pl.pickedUp != null) pl.Drop();
                    if (ok && Poses().TryGetValue(it.idInSave, out var p)) Event("host", "live " + ViewLine(it, p));
                    _step = 50;
                    return 1f;
                }
                case 50:   // a live in-place mount (Shift+R's third mode), pushed into the car's side with collision off
                {
                    // a small one (a long plank held 0.9 m in front reaches into the car before it is placed)
                    var item = LooseItems(pl, 120f).FirstOrDefault(t => t.attachable == null && !Mounting.OwnR(t.P) && t != _plans[0].Item &&
                                                                        !t.GetComponentsInChildren<attachablescript>().Any(a => a.attached && a.tosave != t) &&
                                                                        t.GetComponentsInChildren<Renderer>().Select(r => r.bounds.size.magnitude).DefaultIfEmpty(9f).Max() < 0.6f);
                    var sp = _hostSpots.Count > 1 ? _hostSpots[1] : null;
                    _hostIpSpot = sp != null ? sp.At : (Vector3?)null; _hostIpFrom = sp != null ? sp.From : Vector3.zero;
                    if (item == null || _hostIpSpot == null) { Check(false, "host in place: " + (item == null ? "no loose item" : "no spot on the car")); _step = 6; return 8f; }
                    _hostIp = item;
                    if (pl.pickedUp != null) pl.Drop();
                    InPlace.Reset(pl);
                    Hold(item);
                    _driveHeld = true;   // the host's window is behind its pause menu: run the game's held-item keys (Shift+R, R) here
                    _step = 51; return 1f;
                }
                case 51: InPlace.TestPress = true; _step = 52; return 0.4f;   // closest point -> origin
                case 52: InPlace.TestPress = true; _step = 53; return 0.4f;   // origin -> in place
                case 53:
                    Write("host: holding " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ", Shift+R line: " + InPlace.LastLabel);
                    Check(InPlace.On && InPlace.Active(pl.pickedUp), "host: " + _hostIp.name + " carried in the In Place mode (" + InPlace.LastLabel + ")");
                    StandAndLook(_hostIpSpot.Value, 0.9f, _hostIpFrom);
                    _ipToggle = pl.attachableColliderDisableToggle;
                    pl.attachableColliderDisableToggle = true;
                    _step = 54; return 1f;
                case 54:
                {
                    StandAndLook(_hostIpSpot.Value, 0.9f, _hostIpFrom);
                    var rb = _hostIp.GetComponent<Rigidbody>();
                    if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; rb.position = _hostIpSpot.Value; }
                    _hostIp.transform.position = _hostIpSpot.Value;
                    _mountsBefore = (int)InPlace.Mounts;
                    Mounting.TestGlueDown = true;
                    _step = 55; return 0.6f;
                }
                case 55:
                {
                    Mounting.TestGlueDown = false; _driveHeld = false;
                    var at = _hostIp.attachable;
                    bool ok = InPlace.Mounts > _mountsBefore && at != null && at.attached;
                    Check(ok, "host mounts " + _hostIp.name + " in place on the car: " + Mounting.LastMount);
                    if (ok) { var on = at.AttachedToTosave(); Check(on != null && (on == _car || on.transform.IsChildOf(_car.transform)), "host: " + _hostIp.name + " in place is on the car (" + (on != null ? on.name : "not on an item") + ")"); }
                    pl.attachableColliderDisableToggle = _ipToggle;
                    if (pl.pickedUp != null) pl.Drop();
                    InPlace.Reset(pl);
                    if (ok && Poses().TryGetValue(_hostIp.idInSave, out var p)) Event("host", "ipl " + ViewLine(_hostIp, p));
                    _step = 56;
                    return 1f;
                }
                case 56:   // the game's own glue, live: a mirror glued with the game's G (RevaMP alone carries its pose)
                {
                    var sp = _hostSpots.Count > 2 ? _hostSpots[2] : null;
                    _hostGg = SpawnGameGlue();
                    if (sp == null || _hostGg == null) { Check(false, "host game glue: " + (sp == null ? "no third spot on the car" : "no item")); _step = 6; return 8f; }
                    _hostGgSpot = sp.At; _hostGgFrom = sp.From;
                    InPlace.Reset(pl);
                    _step = 57; return 1f;
                }
                case 57:
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(_hostGg);
                    _step = 58; return 1f;
                case 58:
                    StandAndLook(_hostGgSpot, 1.2f, _hostGgFrom);
                    _step = 59; return 0.6f;
                case 59:
                    StandAndLook(_hostGgSpot, 1.2f, _hostGgFrom);
                    Write("host: holding " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + " (game glue), looking at " + DescribeLook(pl) + ", the game offers " + pl.BCanAttachGlue);
                    _ggSent = RevampLink.PosesSent;
                    // the game's own G, as fpscontroller.RayStuff does it (Drop, BAttach, Attach(hit)): called here, since the
                    // game offers nothing in a window behind its pause menu (and then this mod's G would mount it instead)
                    if (pl.pickedUp == _hostGg.P && Mounting.FindHit(pl, out var gh))
                    {
                        var gp = pl.pickedUp; pl.Drop(); pl.BAttach = true; gp.tosave.attachable.Attach(gh);
                    }
                    _step = 60; return 0.6f;
                case 60:
                {
                    Mounting.TestGlueDown = false;
                    var at = _hostGg.attachable;
                    bool ok = at != null && at.attached && !Mounting.Added.Contains(at);
                    Check(ok, "host glues " + _hostGg.name + " with the game's own G (" + (at != null && at.attached ? "on " + (at.AttachedToTosave() != null ? at.AttachedToTosave().name : at.AttachedToPOI() != null ? at.AttachedToPOI().name : "the world") : "not attached") + ")");
                    Check(RevampLink.PosesSent == _ggSent, "host: no pose of this mod sent for it (RevaMP alone carries it)");
                    if (pl.pickedUp != null) pl.Drop();
                    if (ok && Poses().TryGetValue(_hostGg.idInSave, out var p)) Event("host", "ggl " + ViewLine(_hostGg, p));
                    _step = 6;
                    return 10f;
                }
                case 6:   // take the item on the ground off
                {
                    // the live mount as it stands now, 10 s on: did it move on the host after the mount?
                    var live = _plans.Count > 0 ? _plans[0].Item : null;
                    if (live != null && live.attachable != null && live.attachable.attached && Poses().TryGetValue(live.idInSave, out var lp2))
                        Event("host", "live2 " + ViewLine(live, lp2));
                    if (_hostIp != null && _hostIp.attachable != null && _hostIp.attachable.attached && Poses().TryGetValue(_hostIp.idInSave, out var ip2))
                        Event("host", "ipl2 " + ViewLine(_hostIp, ip2));
                    if (_hostGg != null && _hostGg.attachable != null && _hostGg.attachable.attached && Poses().TryGetValue(_hostGg.idInSave, out var gg2))
                        Event("host", "ggl2 " + ViewLine(_hostGg, gg2));
                    var g = Poses().Where(kv => kv.Value.Kind == "world").Select(kv => savedatascript.s.items[kv.Key]).FirstOrDefault(t => Mounting.Added.Contains(t.attachable));
                    if (g == null) { Write("nothing mounted on the ground to take off"); _step = 8; return 0.5f; }
                    _offName = g.name;
                    var line = ViewLine(g, Poses()[g.idInSave]);
                    if (pl.pickedUp != null) pl.Drop();
                    StandAndLook(g.transform.position, 1.2f);
                    pl.Pickup(g.P);
                    _step = 7;
                    _offLine = line;
                    return 1.5f;
                }
                case 7:
                {
                    var g = savedatascript.s.items.Values.FirstOrDefault(t => t != null && t.P != null && t.P == pl.pickedUp);
                    Check(g != null && (g.attachable == null || !g.attachable.attached), "host took " + _offName + " off the ground (held: " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ")");
                    pl.Drop();
                    Event("host", "off " + _offLine);
                    _step = 8; _wait = 0;
                    return 1f;
                }
                case 8:   // the joiner mounts one: do we see it?
                {
                    var e = FindEvent("joiner", "live ");
                    if (e == null) { if (++_wait > 240) { Write("the joiner never mounted"); _step = 10; } return 1f; }
                    _step = 9;
                    return 8f;
                }
                case 9:
                {
                    var e = FindEvent("joiner", "live ").Substring(5);
                    bool ok = Has(View(), e, out var how);
                    Check(ok, "host sees the joiner's mount " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " (" + how + ")");
                    Event("host", "sawjoiner " + (ok ? "PASS" : "FAIL"));
                    _step = 10; _wait = 0;
                    return 1f;
                }
                case 10:
                    if (FindEvent("joiner", "done") == null && ++_wait < 120) return 1f;
                    Write("RevaMP: " + MpStatus().Substring(0, Math.Min(300, MpStatus().Length)));
                    Write("attach stats: " + AttachStats() + "; poses sent " + RevampLink.PosesSent + ", applied " + RevampLink.PosesApplied + ", expired " + RevampLink.PosesExpired + ", re-applied after RevaMP " + RevampLink.PosesReapplied);
                    Mp("Stop");
                    return -1f;
                default:
                    return -1f;
            }
        }

        private static string _offLine;
        private static tosaveitemscript _hostIp;
        private static Vector3? _hostIpSpot;
        private static Vector3 _hostIpFrom, _hostGgSpot, _hostGgFrom;
        private static tosaveitemscript _hostGg;
        private static long _ggSent;
        private static List<Spot> _hostSpots = new List<Spot>();

        private class Spot { public Vector3 At, From; }

        /// Spots on the car's sides, each with the side to look from, where nothing stands between the camera and the car.
        private static List<Spot> CarSpots(float dist)
        {
            var res = new List<Spot>();
            foreach (var f in new[] { -1.3f, 1.3f, 0.6f, -0.6f, 0f })
                foreach (var side in new[] { 1f, -1f })
                {
                    var p = SideOfCar(side, f);
                    if (p == null) continue;
                    var from = _car.transform.position + _car.transform.right * side * 10f;
                    if (Seen(p.Value, from, dist) && !res.Any(r => (r.At - p.Value).magnitude < 0.5f)) res.Add(new Spot { At = p.Value, From = from });
                }
            return res;
        }

        /// From where StandAndLook puts the camera (dist from the point, on the side of from), the first thing the
        /// view hits is the car.
        private static bool Seen(Vector3 point, Vector3 from, float dist)
        {
            var pl = Pl;
            var camOff = pl.Cam.transform.position - pl.transform.position;
            var away = from - point; away.y = 0f;
            if (away.sqrMagnitude < 0.01f) return false;
            var cam = point + away.normalized * dist;
            cam.y = Mathf.Max(point.y + 0.4f, (Ground(cam) ?? cam).y + camOff.y);
            var dir = point - cam;
            foreach (var h in Physics.RaycastAll(cam, dir.normalized, dir.magnitude + 0.3f, pl.attachLayer).OrderBy(h => h.distance))
            {
                if (h.collider.isTrigger || h.collider.transform.IsChildOf(pl.transform) || pl.pickedUp != null && h.collider.transform.IsChildOf(pl.pickedUp.transform)) continue;
                return h.collider.transform.IsChildOf(_car.transform);
            }
            return false;
        }

        /// A side to look at the car's roof from with nothing in between.
        private static Vector3 RoofFrom(Vector3 roof)
        {
            var tr = _car.transform;
            foreach (var d in new[] { tr.right, -tr.right, tr.forward, -tr.forward })
                if (Seen(roof, tr.position + d * 10f, 1.4f)) return tr.position + d * 10f;
            return tr.position + tr.right * 10f;
        }

        private static string AttachStats()
        {
            try { var t = AccessTools.TypeByName("TLDRevamp.Net.Entities"); return AccessTools.Method(t, "AttachStats").Invoke(null, null) as string; }
            catch (Exception e) { return e.Message; }
        }

        // ------------------------------------------------------------------ joiner

        private static float JoinStep()
        {
            var pl = Pl;
            switch (_step)
            {
                case 0:   // the host's view
                    if (FindEvent("host", "view") == null) { if (++_wait > 300) { Write("the host never wrote its view"); return -1f; } return 1f; }
                    _step = 1;
                    return 5f;
                case 1:
                {
                    var mine = View();
                    var host = File.ReadAllLines(SharedFile("host-view.txt")).Where(l => l.Trim().Length > 0).ToList();
                    Write("joiner view: " + mine.Count + " mounted items, host's: " + host.Count);
                    foreach (var l in host)
                    {
                        bool ok = Has(mine, l, out var how);
                        Check(ok, "joiner sees " + l.Split(' ')[0] + " on " + l.Split(' ')[1] + " " + l.Split(' ')[2] + " (" + how + ")");
                    }
                    _step = 2; _wait = 0;
                    return 1f;
                }
                case 2:   // the host's live mount
                {
                    var e = FindEvent("host", "live ");
                    if (e == null) { if (++_wait > 120) { Check(false, "the host never mounted live"); _step = 4; } return 1f; }
                    _step = 3;
                    return 6f;
                }
                case 3:
                {
                    var e = FindEvent("host", "live ").Substring(5);
                    var e2 = FindEvent("host", "live2 ");
                    if (e2 == null && ++_wait < 20) return 1f;   // the host's pose 10 s after the mount
                    bool atMount = Has(View(), e, out var how);
                    if (e2 != null)
                    {
                        e2 = e2.Substring(6);
                        bool hostSame = Has(new List<string> { e2 }, e, out var hostMoved);
                        Write("host's own " + e.Split(' ')[0] + " 10 s after the mount against the mount: " + hostMoved + (hostSame ? "" : " (it moved on the host)"));
                        Check(Has(View(), e2, out var how2), "joiner sees the host's live mount " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " as the host has it now (" + how2 + "; against the mount event: " + how + ")");
                    }
                    else Check(atMount, "joiner sees the host's live mount " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " (" + how + ")");
                    _step = 30; _wait = 0;
                    return 1f;
                }
                case 30:   // the host's live in-place mount, as the host has it 10 s on
                {
                    var e = FindEvent("host", "ipl2 ") ?? FindEvent("host", "ipl ");
                    if (FindEvent("host", "ipl2 ") == null && ++_wait < 60) return 1f;
                    if (e == null) { Check(false, "the host never mounted in place"); _step = 4; _wait = 0; return 1f; }
                    e = e.Substring(e.IndexOf(' ') + 1);
                    Check(Has(View(), e, out var how), "joiner sees the host's live in-place mount " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " (" + how + ")");
                    _step = 31; _wait = 0;
                    return 1f;
                }
                case 31:   // the host's mirror glued with the game's own G: RevaMP's pose alone
                {
                    var e = FindEvent("host", "ggl2 ") ?? FindEvent("host", "ggl ");
                    if (FindEvent("host", "ggl2 ") == null && ++_wait < 30) return 1f;
                    if (e == null) { Check(false, "the host never glued a mirror with the game's G"); _step = 4; _wait = 0; return 1f; }
                    e = e.Substring(e.IndexOf(' ') + 1);
                    bool same = Has(View(), e, out var how);
                    Check(same, "joiner sees the host's game-glued " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " (" + how + ")");
                    // tighter than the general check: the pose is the attacher's, to the millimetre RevaMP sends
                    var mine = View().Select(ParseView).Where(m => m.Name == e.Split(' ')[0]).ToList();
                    var w = ParseView(e);
                    var best = mine.OrderBy(m => (m.Pos - w.Pos).magnitude).FirstOrDefault();
                    if (best != null) Check((best.Pos - w.Pos).magnitude < 0.003 && Quaternion.Angle(best.Rot, w.Rot) < 0.5f,
                                            "joiner: " + w.Name + " within 3 mm and 0.5 deg of the host's (" + how + ")");
                    _step = 4; _wait = 0;
                    return 1f;
                }
                case 4:   // the host's take-off
                {
                    var e = FindEvent("host", "off ");
                    if (e == null) { if (++_wait > 60) { Write("the host took nothing off"); _step = 6; } return 1f; }
                    _step = 5;
                    return 6f;
                }
                case 5:
                {
                    var e = FindEvent("host", "off ").Substring(4);
                    Check(!Has(View(), e, out var how), "joiner sees " + e.Split(' ')[0] + " taken off the ground (" + how + " = no longer mounted there)");
                    _step = 6;
                    return 1f;
                }
                case 6:   // the joiner mounts one on the car's roof
                {
                    _car = CarWithMounts();
                    if (_car == null) { Write("joiner: no car with mounted items"); _step = 20; return 1f; }
                    StandAndLook(_car.transform.position, 4f);
                    _step = 7;
                    return 1f;
                }
                case 7:
                {
                    var item = LooseItems(pl, 150f).FirstOrDefault(t => t.attachable == null);
                    if (item == null) { Write("joiner: no loose item"); _step = 20; return 1f; }
                    var roof = RoofOfCar();
                    var roofFrom = roof != null ? RoofFrom(roof.Value) : _car.transform.position + _car.transform.right * 10f;
                    _plans.Clear(); _plans.Add(new Plan { Name = "car roof", Item = item, Target = () => RoofOfCar(), From = () => roofFrom });
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(item);
                    Write("joiner holds " + item.name);
                    _step = 8;
                    return 1.5f;
                }
                case 8:
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    // as held, tipped 30 to the left, turned 15: the host must see this pose
                    PressPlacement(new[] { Placement.Act.TiltLeft, Placement.Act.TiltLeft, Placement.Act.TurnLeft }, false);
                    _step = 9; return 0.6f;
                case 9:
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    Write("joiner: holding " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ", looking at " + DescribeLook(pl));
                    _mountsBefore = (int)Mounting.Mounts; Mounting.TestPress = true; _step = 10; return 0.6f;
                case 10:
                {
                    Mounting.TestPress = false;
                    var it = _plans[0].Item;
                    bool ok = Mounting.Mounts > _mountsBefore && it.attachable != null && it.attachable.attached;
                    Check(ok, "joiner mounts " + it.name + " on the car's roof: " + Mounting.LastMount);
                    if (pl.pickedUp != null) pl.Drop();
                    if (ok && Poses().TryGetValue(it.idInSave, out var p)) Event("joiner", "live " + ViewLine(it, p));
                    _step = 11; _wait = 0;
                    return 3f;
                }
                case 11:
                {
                    var e = FindEvent("host", "sawjoiner");
                    if (e == null && ++_wait < 40) return 1f;
                    Write("host's answer: " + (e ?? "none"));
                    var it = _plans[0].Item;
                    Check(it.attachable != null && it.attachable.attached, "joiner's mount still on after the host's reply (not undone by the owner)");
                    _step = 20;
                    return 1f;
                }
                case 20:
                    Write("RevaMP: " + MpStatus().Substring(0, Math.Min(300, MpStatus().Length)));
                    Write("attach stats: " + AttachStats() + "; RevaMP attaches prepared by this mod " + RevampLink.Prepared + "; poses sent " + RevampLink.PosesSent + ", applied " + RevampLink.PosesApplied + ", expired " + RevampLink.PosesExpired + ", re-applied after RevaMP " + RevampLink.PosesReapplied);
                    Event("joiner", "done");
                    return -1f;
                default:
                    return -1f;
            }
        }
    }
}
