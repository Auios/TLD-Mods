using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// How it works: the beta already has a free attachment, the "glue" (Glue key, attachablescript.Attach(RaycastHit)),
    /// and a save record for it (save_attachable: what it's on - an item and which of its colliders, a POI building, or
    /// the world - and where). Only items whose prefab has an attachablescript can use it. This mod gives a held item
    /// that lacks one an attachablescript (parentAtAttach: the item becomes a rigid child of what it's mounted on and
    /// loses its own Rigidbody while mounted, the same as the game's glued items) at the moment it is mounted, and again
    /// wherever such an item is rebuilt from a record that says it is mounted:
    ///  - savedatascript.LoadStuff (loading a save, an item placed back near the player, RevaMP building a joiner's copy);
    ///  - RevaMP's live attach messages (see RevampLink).
    /// Taken off (picked up, broken off), the item gets its plain self back: the added component is removed again.
    internal static class Mounting
    {
        /// Components this mod added (as opposed to ones the item's prefab has).
        internal static readonly HashSet<attachablescript> Added = new HashSet<attachablescript>();
        internal static long Mounts, AddedOnLoad, AddedForRevamp, Removed;
        internal static string LastMount = "";
        internal static Vector3 LastHitPoint, LastHitNormal;   // the test: where the last mount was aimed
        internal static float HintUntil;
        private static int _keyFrame = -1;

        /// Set by the unattended test: acts as if the mount key was pressed this frame.
        internal static bool TestPress;
        /// Set by the unattended test: acts as if the game's Glue key went down this frame (for an item the game glues).
        internal static bool TestGlueDown;
        /// Set by the unattended test: the game window has no focus (its pause menu is up), offer and place anyway.
        internal static bool TestUnfocused;

        internal static long Prepared;

        /// A carried item with an R of its own: R pours a jerrycan, reloads a gun from a magazine or box, fills it from the
        /// inventory (fpscontroller: pickedUp.ammo). They get the game's glue too; OwnRKeys keeps R theirs where the game
        /// offers it.
        internal static bool OwnR(pickupable p) => p != null && p.ammo != null;

        /// Called every frame: the item the player carries gets the game's glue as soon as it is picked up, so the game
        /// itself offers it as for its own glueable items (a mirror, the clock): G attaches it where it is held, R
        /// toggles its collision, Shift+R switches the attach point between its origin and its closest point. Put down
        /// without being mounted, Cleanup takes the glue away again.
        internal static void PrepareHeld()
        {
            if (!Plugin.Enabled.Value || !Plugin.GameControls.Value || mainscript.s == null) return;
            var pl = mainscript.s.player;
            var p = pl != null ? pl.pickedUp : null;
            if (p == null || p.tosave == null || p.tosave.attachable != null) return;
            if (!IsMountable(p, out _)) return;
            Ensure(p.tosave);
            Prepared++;
        }

        internal static attachablescript Ensure(tosaveitemscript ts)
        {
            if (ts == null) return null;
            if (ts.attachable != null) return ts.attachable;
            var at = ts.gameObject.AddComponent<attachablescript>();
            at.parentAtAttach = true;   // rigid: parented into what it's on, no joint
            at.tipus = new string[0];
            at.maintipus = new string[0];
            at.tosave = ts;
            at.T = ts.transform;
            at.RB = ts.GetComponent<Rigidbody>();
            ts.attachable = at;
            at.FStart();
            Added.Add(at);
            return at;
        }

        /// Called twice a second: a component this mod added to an item that is neither mounted nor carried is removed,
        /// so the item is the game's plain item again.
        /// Its colliders are switched back on first. While a glueable item is in the player's hands the game sets its
        /// colliders to the "toggle collision" setting every frame (fpscontroller: attachable.SetCollider(!toggle)), and
        /// only the game's Drop turns them back on - for items that still have the component then. An item taken off
        /// with the setting at off, in the half second before this ran, kept every collider off for good: nobody could
        /// pick it up or touch it (play session 2026-11-10: a fridge mounted on a car, taken off, put down again, 0/12).
        internal static long CollidersRestored;
        internal static void Cleanup()
        {
            if (Added.Count == 0) return;
            List<attachablescript> gone = null;
            var held = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.pickedUp : null;
            foreach (var at in Added)
            {
                if (at == null) { (gone ??= new List<attachablescript>()).Add(at); continue; }
                if (at.attached) continue;
                if (held != null && at.tosave != null && at.tosave.P == held) continue;   // carried: keeps the game's glue (PrepareHeld)
                (gone ??= new List<attachablescript>()).Add(at);
                var ts = at.tosave;
                if (ts != null && ts.P != null && ts.P.cols != null)
                {
                    bool anyOff = false;
                    foreach (var c in ts.P.cols) if (c != null && c.col != null && !c.col.enabled) { anyOff = true; break; }
                    try { at.SetCollider(_enable: true, _childrenToo: true); } catch (System.Exception e) { Plugin.Log.LogWarning("colliders back on: " + e.Message); }
                    if (anyOff) { CollidersRestored++; Plugin.Log.LogInfo("colliders back on: " + ts.name + " (" + ts.idInSave + "), taken off with the game's collision toggle at off"); }
                }
                if (ts != null && ts.attachable == at) ts.attachable = null;
                Object.Destroy(at);
                Removed++;
            }
            if (gone != null) foreach (var at in gone) Added.Remove(at);
        }

        internal static bool IsMountable(pickupable p, out string why)
        {
            why = "";
            if (p == null) { why = "nothing held"; return false; }
            var ts = p.tosave;
            if (ts == null) { why = "not a saved item"; return false; }
            if (ts.P != p) { why = "held by a part of it"; return false; }
            if (ts.car != null) { why = "a vehicle"; return false; }
            if (ts.ragdollPos != null) { why = "a body"; return false; }
            var at = ts.attachable;
            if (at != null)
            {
                if (at.attached) { why = "already mounted"; return false; }
                if (at.onlyToSlot && !Plugin.AllowSlotParts.Value) { why = "slot-only part"; return false; }
            }
            return true;
        }

        /// The first solid thing the crosshair is on (not the held item, not the player), the way the game's glue looks.
        internal static bool FindHit(fpscontroller pl, out RaycastHit hit) => FindHit(pl, new Ray(pl.Cam.transform.position, pl.Cam.transform.forward), pl.FrayRange, out hit);

        internal static bool FindHit(fpscontroller pl, Ray ray, float range, out RaycastHit hit)
        {
            hit = default;
            var hits = Physics.RaycastAll(ray, range, pl.attachLayer);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.isTrigger) continue;
                var t = h.collider.transform;
                if (pl.pickedUp != null && t.IsChildOf(pl.pickedUp.transform)) continue;
                if (t.IsChildOf(pl.transform)) continue;
                hit = h;
                return true;
            }
            return false;
        }

        /// The green placement preview applies to this carried item: on for every item ([Place] Preview), or Shift+R's
        /// preview mode is on.
        internal static bool Preview(pickupable p) => Plugin.Place.Value || InPlace.PreviewActive(p);

        /// Mounts the held item at the hit, as the game's glue does (drop, then attach), and returns its attachment.
        internal static attachablescript Mount(fpscontroller pl, RaycastHit hit)
        {
            var p = pl.pickedUp;
            var ts = p.tosave;
            var at = Ensure(ts);
            // the pose shown by the preview (worked out this frame for this hit), or the 0.1 way without placement
            bool placed = Preview(p) && Placement.Ready(p);
            Vector3 pos = Placement.Pos; Quaternion rot = Placement.Rot;
            pl.Drop();
            pl.BAttach = true;
            var t = ts.transform;
            if (placed) t.SetPositionAndRotation(pos, rot);
            else if (Plugin.AlignToSurface.Value) t.rotation = Quaternion.FromToRotation(t.up, hit.normal) * t.rotation;
            bool keep = at.tempDontMoveAtAttach;
            if (placed) at.tempDontMoveAtAttach = true;   // the game's glue would put the item's nearest point on the spot
            try
            {
                var col = hit.collider;
                if (col.GetComponentInParent<tosaveitemscript>() != null || col.GetComponentInParent<poiScript>() != null)
                    at.Attach(hit);   // the point rides the collider: a car's body, an item, a building
                else
                {
                    // the ground, a road, a scatter object: the game saves these as a world position (attach type 4) and
                    // loads them on a point of their own. The same now, so a terrain tile that unloads never takes the
                    // item with it. The floating-origin shift moves the point (NewPointShift).
                    at.attachedC = col;
                    at.Attach(at.NewPoint(null, hit.point + hit.normal * at.offsetback, Quaternion.identity));
                }
            }
            finally { at.tempDontMoveAtAttach = keep; }
            if (placed)
            {
                t.SetPositionAndRotation(pos, rot);
                var pt = at.point != null ? at.point : t.parent;
                Placement.LastPos = pt != null ? Quaternion.Inverse(pt.rotation) * (pos - pt.position) : pos;
                Placement.LastRot = pt != null ? Quaternion.Inverse(pt.rotation) * rot : rot;
            }
            Mounts++; JustMounted(at);
            LastHitPoint = hit.point; LastHitNormal = hit.normal;
            RevampLink.SendPose(at);
            var to = at.AttachedToTosave(); var poi = at.AttachedToPOI();
            LastMount = ts.name + " (" + ts.idInSave + ") on " + (to != null ? to.name + " (" + to.idInSave + ")" : poi != null ? "building " + poi.name : "the world (" + hit.collider.name + ")") +
                        ", attached " + at.attached + (placed ? ", placed " + Placement.State() : "");
            Plugin.Log.LogInfo("mounted " + LastMount);
            return at;
        }

        /// In place (Shift+R's third mode): the carried item stays exactly as it is held and is mounted on what it touches
        /// (InPlace.FindTouch). The game's "don't move it" switch keeps its attach from putting a point of the item on the
        /// spot; a point of its own on the ground, as Mount does.
        internal static attachablescript MountInPlace(fpscontroller pl, Collider col)
        {
            var p = pl.pickedUp;
            var ts = p.tosave;
            var at = Ensure(ts);
            var t = ts.transform;
            Vector3 pos = t.position; Quaternion rot = t.rotation;
            InPlace.LastHeldPos = pos; InPlace.LastHeldRot = rot;
            pl.Drop();
            pl.BAttach = true;
            t.SetPositionAndRotation(pos, rot);
            bool keep = at.tempDontMoveAtAttach;
            at.tempDontMoveAtAttach = true;
            _mounting = at;
            try
            {
                if (col.GetComponentInParent<tosaveitemscript>() != null || col.GetComponentInParent<poiScript>() != null)
                    at.Attach(at.NewPoint(col.transform, pos, Quaternion.identity), col);   // the point rides the collider it touches
                else
                {
                    at.attachedC = col;
                    at.Attach(at.NewPoint(null, pos, Quaternion.identity));
                }
            }
            finally { at.tempDontMoveAtAttach = keep; _mounting = null; }
            t.SetPositionAndRotation(pos, rot);
            Mounts++; InPlace.Mounts++; JustMounted(at);
            InPlace.Track(t, pos, rot);
            LastHitPoint = pos; LastHitNormal = Vector3.zero;
            RevampLink.SendPose(at);
            var to = at.AttachedToTosave(); var poi = at.AttachedToPOI();
            LastMount = ts.name + " (" + ts.idInSave + ") in place on " + (to != null ? to.name + " (" + to.idInSave + ")" : poi != null ? "building " + poi.name : "the world (" + col.name + ")") +
                        ", attached " + at.attached;
            Plugin.Log.LogInfo("mounted " + LastMount);
            return at;
        }

        /// The component being mounted right now (NewPointShift: the game's own glue items mounted on the ground too).
        private static attachablescript _mounting;

        /// An item the game glues by itself, rigidly, anywhere (not a slot part, not one on a joint): with placement on,
        /// the mod shows and places it too.
        private static bool GameGlueWeTake(pickupable p)
        {
            var at = p != null && p.tosave != null ? p.tosave.attachable : null;
            return Preview(p) && Plugin.PlaceGameGlue.Value && at != null && !Added.Contains(at) && at.parentAtAttach && !at.onlyToSlot && !at.attached;
        }

        private static bool _takeOver;

        /// Items this mod mounted in the last seconds: if something takes one off at once, the log names what did.
        private static readonly Dictionary<attachablescript, float> _justMounted = new Dictionary<attachablescript, float>();
        private static void JustMounted(attachablescript at) { if (at != null) _justMounted[at] = Time.unscaledTime; }

        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.Detach))]
        private static class DetachWatch
        {
            [HarmonyPrefix]
            private static void Prefix(attachablescript __instance)
            {
                if (__instance == null || !_justMounted.TryGetValue(__instance, out float at)) return;
                _justMounted.Remove(__instance);
                float ago = Time.unscaledTime - at;
                if (ago > 3f) return;
                var st = new System.Diagnostics.StackTrace(1, false);
                var sb = new System.Text.StringBuilder();
                for (int i = 0; i < st.FrameCount && i < 8; i++)
                {
                    var m = st.GetFrame(i).GetMethod();
                    if (m == null) continue;
                    if (sb.Length > 0) sb.Append(" < ");
                    sb.Append(m.DeclaringType != null ? m.DeclaringType.Name + "." : "").Append(m.Name);
                }
                Plugin.Log.LogInfo((__instance.tosave != null ? __instance.tosave.name : __instance.name) + " taken off " + ago.ToString("F2") + " s after this mod mounted it, by " + sb);
            }
        }

        /// The test: the game's Glue key was taken over this frame, but nothing was mounted (why).
        internal static string LastLost = "";
        private static void Lost(bool takeOver, string why)
        {
            if (!takeOver) return;
            LastLost = why;
            var pl = mainscript.s != null ? mainscript.s.player : null;
            if (pl != null && InPlace.Active(pl.pickedUp)) InPlace.Explain(pl, why);
            if (RevampLink.Watch) Plugin.Log.LogInfo("G taken over but nothing mounted: " + why);
        }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class RayStuffHook
        {
            /// Before the game's look-at work, the game's Glue key → the key is ours (the postfix mounts) for
            ///  - an item carrying the glue this mod gave it: the game would glue it the same way, but onto the terrain
            ///    tile under it (which unloads with the item) and without telling the other players the pose;
            ///  - an item the game would glue itself, whose placement is shown (it goes to the shown pose).
            [HarmonyPrefix]
            private static void Prefix(fpscontroller __instance)
            {
                var inp = inputscript.i;
                bool test = TestGlueDown;
                TestGlueDown = false;
                if (!Plugin.Enabled.Value || inp == null || !(inp.glueDown || test) || __instance == null || __instance.pickedUp == null) return;
                if (test) inp.glueDown = true;   // the test presses the game's key: the game's own glue sees it too
                var p = __instance.pickedUp;
                if (InPlace.Active(p)) { }   // in place: always ours (the game would move it to the crosshair)
                else if (Ours(p)) { if (Preview(p) && !Placement.ShownFor(p)) return; }
                else if (!Placement.ShownFor(p) || !Placement.ForGameGlue || !GameGlueWeTake(p)) return;
                inp.glueDown = false;
                _takeOver = true;
            }

            /// Carried with the glue this mod gave it (PrepareHeld), not mounted yet.
            private static bool Ours(pickupable p)
            {
                var at = p != null && p.tosave != null ? p.tosave.attachable : null;
                return at != null && !at.attached && Added.Contains(at);
            }

            /// After the game's own look-at work: holding an item that can be mounted here, looking at something solid →
            /// offer it (the game's own "[G]: Attach" line, or our hint for a key of our own), show the placement, and
            /// mount on the key.
            [HarmonyPostfix]
            private static void Postfix(fpscontroller __instance)
            {
                bool takeOver = _takeOver;
                _takeOver = false;
                var pl = __instance;
                if (!Plugin.Enabled.Value || pl == null || pl.pickedUp == null || _keyFrame == Time.frameCount) return;
                if (mainscript.s == null || mainscript.s.player != pl || (!mainscript.s.crsrLocked && !TestPress && !TestUnfocused)) return;   // the test: a game window without focus has its pause menu up
                var inp = inputscript.i;
                if (inp != null && inp.usingComputer) return;
                if (pl.BAttach || pl.BnowPickedUp || pl.pushgripping) { Lost(takeOver, "BAttach " + pl.BAttach + ", BnowPickedUp " + pl.BnowPickedUp + ", pushgripping " + pl.pushgripping); return; }
                bool gameGlue = pl.BCanAttachGlue;
                bool inPlace = InPlace.Active(pl.pickedUp);
                if (gameGlue && (pl.BCanAttach || !(inPlace || Ours(pl.pickedUp) || GameGlueWeTake(pl.pickedUp)))) { Lost(takeOver, "the game's own (BCanAttach " + pl.BCanAttach + ")"); return; }   // the game glues it itself (or into a slot)
                if (!IsMountable(pl.pickedUp, out var whyNot)) { Lost(takeOver, "not mountable: " + whyNot); return; }
                RaycastHit hit = default; Collider touch = null;
                if (inPlace)
                {
                    pl.BCanAttachGlue = false;   // in place: offered only while it touches something
                    if (!InPlace.FindTouch(pl, out touch)) { Lost(takeOver, "in place: touches nothing"); return; }
                }
                else if (!FindHit(pl, out hit)) return;
                bool gameKey = Plugin.MountKey.Value == KeyCode.None;
                if (gameKey) pl.BCanAttachGlue = true;   // the HUD shows "[G]: Attach"
                else HintUntil = Time.unscaledTime + 0.15f;
                if (!inPlace && Preview(pl.pickedUp)) Placement.Update(pl, hit, gameGlue);
                bool pressed = TestPress || takeOver || (gameKey ? !gameGlue && inp != null && inp.glueDown : Input.GetKeyDown(Plugin.MountKey.Value));
                if (!pressed) return;
                TestPress = false;
                _keyFrame = Time.frameCount;
                if (inPlace) MountInPlace(pl, touch); else Mount(pl, hit);
            }
        }

        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.LoadStuff), new[] { typeof(savedata), typeof(uint), typeof(bool) })]
        private static class LoadHook
        {
            /// An item rebuilt from records that say it is mounted, but whose prefab has no attachablescript: give it one
            /// before the game reads those records (it loads the attachment only for items that have one).
            [HarmonyPrefix]
            private static void Prefix(savedatascript __instance, savedata _data, uint _key)
            {
                if (_data == null || _data.itemData == null || _data.itemData.attachable == null) return;
                if (!__instance.items.TryGetValue(_key, out var ts) || ts == null || ts.attachable != null || ts.loaded) return;
                foreach (var a in _data.itemData.attachable)
                    if (a != null && a.id == _key && a.attachType != 0)
                    {
                        Ensure(ts);
                        AddedOnLoad++;
                        return;
                    }
            }
        }

        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.NewPoint), new[] { typeof(Transform), typeof(Vector3), typeof(Quaternion) })]
        private static class NewPointShift
        {
            /// A mount point standing on its own (mounted on the ground, or loaded so) must move with the game's
            /// floating-origin shift: it gets the game's own visszarako, as RevaMP gives the game's own free points.
            [HarmonyPostfix]
            private static void Postfix(attachablescript __instance, Transform __result)
            {
                if (__result == null || !(Added.Contains(__instance) || __instance == _mounting) || __result.parent != null) return;
                if (__result.GetComponent<visszarako>() != null) return;
                __result.gameObject.AddComponent<visszarako>().FStart(_dontlookUnderMap: true, _importantUnderMapLook: false, _moveWhenParented: false);
            }
        }
    }
}
