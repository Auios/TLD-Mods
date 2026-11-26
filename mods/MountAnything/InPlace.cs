using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// Two more attach modes on the game's Shift+R: closest point -> origin -> in place -> preview -> closest point.
    /// Preview: the green placement copy (Placement) for the carried item. In place, G mounts
    /// the carried item exactly where and how it is held, on what it touches or overlaps (also with its collision
    /// switched off by R, pushed partly into something). Nothing touched: nothing to mount on, no offer.
    /// The game's own two modes (fpscontroller.attachableOriginToggle) are left as they are; this mode sits on top: the
    /// game's switch stays at "closest point" while one of them is on.
    internal static class InPlace
    {
        /// The mode is on (for every carried item that shows the game's Shift+R).
        internal static bool On;
        internal static long Cycles, Mounts;
        /// Set by the unattended test: acts as if Shift+R went down in the next held-item frame.
        internal static bool TestPress;
        /// The pose the item had in the hands the moment it was mounted in place (the test checks it kept it).
        internal static Vector3 LastHeldPos;
        internal static Quaternion LastHeldRot;
        internal static string LastLabel = "";

        internal static bool Enabled => Plugin.Enabled.Value && Plugin.InPlaceMode.Value;
        internal static bool PreviewEnabled => Plugin.Enabled.Value && Plugin.PreviewMode.Value;

        /// Shift+R's fourth step: the green placement preview (Placement), for the carried item only.
        internal static bool PreviewStep;

        /// The carried item is one Shift+R's in place and preview apply to: the game's rigid glue (this mod's, or the item's
        /// own). Not an item the game hangs on a joint (a fuel can, an air freshener): its joint pins the item's middle to the
        /// mount point and lets it swing, so "in place" left a fuel can hanging in the air by its middle (play test
        /// 2026-11-26); those keep the game's two modes.
        /// Slot-only parts too (a hubcap): this mod mounts them anywhere (AllowSlotParts), and the game shows them Shift+R -
        /// left out, "In Place" showed and G mounted a hubcap at the crosshair instead (play test 2026-11-25).
        internal static bool Applies(pickupable p)
        {
            var at = p != null && p.tosave != null ? p.tosave.attachable : null;
            return at != null && !at.attached && (!at.onlyToSlot || Plugin.AllowSlotParts.Value) && at.parentAtAttach;
        }

        /// In place is the Shift+R mode now, for this carried item.
        internal static bool Active(pickupable p) => On && Enabled && Applies(p);

        /// The preview is the Shift+R mode now, for this carried item.
        internal static bool PreviewActive(pickupable p) => PreviewStep && PreviewEnabled && Applies(p);

        /// Back to the game's closest point (the test, between stages).
        internal static void Reset(fpscontroller pl) { On = false; PreviewStep = false; if (pl != null) pl.attachableOriginToggle = false; }

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.usePickedUpStuff))]
        private static class ShiftR
        {
            [HarmonyPrefix]
            private static void Prefix(fpscontroller __instance, bool hand, out bool __state)
            {
                __state = __instance != null && __instance.attachableOriginToggle;
                if (!TestPress || hand || __instance == null || inputscript.i == null) return;
                TestPress = false;
                inputscript.i.run = true; inputscript.i.reloadDown = true;   // Shift+R, this call only
            }

            /// After the game's own work for the carried item: it flipped its switch on Shift+R, and wrote the line.
            /// The cycle: closest point -> origin (both the game's) -> in place -> preview -> closest point; a step turned
            /// off in the settings is passed over. The game's switch stays at closest point during this mod's two steps.
            [HarmonyPostfix]
            private static void Postfix(fpscontroller __instance, pickupable pP, bool hand, bool __state)
            {
                if (__instance == null || hand || !Plugin.Enabled.Value || !__instance.canShiftR || pP == null || pP != __instance.pickedUp) return;
                bool was = __state, now = __instance.attachableOriginToggle;
                if (was != now && !Applies(pP)) { On = false; PreviewStep = false; }   // a joint item: only the game's two modes
                else if (was != now)
                {
                    Cycles++;
                    if (was) { On = Enabled; PreviewStep = !On && PreviewEnabled; }                          // origin -> in place / preview
                    else if (On) { On = false; PreviewStep = PreviewEnabled; __instance.attachableOriginToggle = false; }   // in place -> preview / closest point
                    else if (PreviewStep) { PreviewStep = false; __instance.attachableOriginToggle = false; }               // preview -> closest point
                }
                if (Active(pP)) __instance.shiftR = ls.l.g(ls.w.toggleattachmode) + ": In Place";
                else if (PreviewActive(pP)) __instance.shiftR = ls.l.g(ls.w.toggleattachmode) + ": Preview";
                LastLabel = __instance.shiftR;
            }
        }

        private static readonly Collider[] _near = new Collider[64];

        /// What the carried item touches or overlaps, as it is held now: the thing under the crosshair if that is one of
        /// them, else the one it is pushed into deepest. Its colliders count even while R has them switched off.
        internal static bool FindTouch(fpscontroller pl, out Collider best, System.Text.StringBuilder why = null)
        {
            best = null;
            var p = pl.pickedUp;
            if (p == null || p.cols == null) { why?.Append("the item has no collider list"); return false; }
            var root = p.transform; var me = pl.transform;
            Collider aimed = Mounting.FindHit(pl, out var hit) ? hit.collider : null;
            if (why != null)
            {
                why.Append("aimed at ").Append(aimed != null ? Describe(aimed) + " " + hit.distance.ToString("F2") + " m" : "nothing in reach");
                RaycastHit any = default; bool seen = false;
                foreach (var h in Physics.RaycastAll(pl.Cam.transform.position, pl.Cam.transform.forward, 3f, ~0, QueryTriggerInteraction.Ignore))
                    if (!h.collider.transform.IsChildOf(root) && !h.collider.transform.IsChildOf(me) && (!seen || h.distance < any.distance)) { any = h; seen = true; }
                if (seen && any.collider != aimed)
                    why.Append("; first thing in view (any layer) ").Append(Describe(any.collider)).Append(' ').Append(any.distance.ToString("F2")).Append(" m, in the glue layers ").Append((pl.attachLayer.value & (1 << any.collider.gameObject.layer)) != 0);
            }
            float bestDepth = -1f;
            bool aimedTouched = false;
            // R's "collision off" switches the item's colliders off; queries need them on: on for the look, then back
            var cols = p.cols;
            var was = new bool[cols.Count];
            for (int i = 0; i < cols.Count; i++) { var c = cols[i] != null ? cols[i].col : null; if (c == null) continue; was[i] = c.enabled; c.enabled = true; }
            try
            {
                for (int i = 0; i < cols.Count; i++)
                {
                    var c = cols[i] != null ? cols[i].col : null;
                    if (c == null || c.isTrigger) continue;
                    var b = c.bounds;
                    why?.Append("; item collider ").Append(Describe(c)).Append(" was on ").Append(was[i]).Append(", size ").Append((b.size * 100f).ToString("F0")).Append(" cm:");
                    int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * (TouchM + 0.005f), _near, Quaternion.identity, pl.attachLayer, QueryTriggerInteraction.Ignore);
                    for (int k = 0; k < n; k++)
                    {
                        var o = _near[k];
                        if (o == null || o.isTrigger || o.transform.IsChildOf(root) || o.transform.IsChildOf(me)) continue;
                        float depth = Depth(c, o, b.center, pl.Cam != null ? pl.Cam.transform.forward : Vector3.forward);
                        why?.Append(' ').Append(Describe(o)).Append(depth < 0f ? " apart" : " depth " + (depth * 1000f).ToString("F0") + " mm").Append(',');
                        if (depth < 0f) continue;
                        if (o == aimed) aimedTouched = true;
                        if (depth > bestDepth) { bestDepth = depth; best = o; }
                    }
                }
                // resting against something by the hand, collision on: the physics keeps a gap the nudges above can miss on a
                // building's mesh (a sponge against a house wall, play test 2026-11-25). Swept TouchM each way, the item meets it.
                if (best == null)
                {
                    var rb = p.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        var view = pl.Cam != null ? pl.Cam.transform.forward : Vector3.forward;
                        var rt = root;
                        var dirs = new[] { view, rt.right, -rt.right, rt.up, -rt.up, rt.forward, -rt.forward, Vector3.down, Vector3.up, Vector3.right, Vector3.left, Vector3.forward, Vector3.back };
                        float near = float.MaxValue;
                        foreach (var d in dirs)
                            foreach (var h in rb.SweepTestAll(d, TouchM, QueryTriggerInteraction.Ignore))
                            {
                                var o = h.collider;
                                if (o == null || o.isTrigger || o.transform.IsChildOf(root) || o.transform.IsChildOf(me) || (pl.attachLayer.value & (1 << o.gameObject.layer)) == 0) continue;
                                if (h.distance < near) { near = h.distance; best = o; }
                                if (o == aimed) aimedTouched = true;
                            }
                        why?.Append("; swept ").Append((TouchM * 1000f).ToString("F0")).Append(" mm each way: ").Append(best != null ? Describe(best) + " at " + (near * 1000f).ToString("F0") + " mm" : "nothing");
                    }
                }
            }
            finally
            {
                for (int i = 0; i < cols.Count; i++) { var c = cols[i] != null ? cols[i].col : null; if (c != null) c.enabled = was[i]; }
            }
            if (aimedTouched) best = aimed;
            return best != null;
        }

        private static string Describe(Collider c) =>
            c.name + "[" + c.GetType().Name + (c is MeshCollider m ? (m.convex ? " convex" : " concave") : "") + ", layer " + c.gameObject.layer +
            (c.GetComponentInParent<tosaveitemscript>() is tosaveitemscript t ? ", item " + t.name : c.GetComponentInParent<poiScript>() is poiScript poi ? ", building " + poi.name : "") + "]";

        private static float _explainedAt = -10f;

        /// G in this mode mounted nothing: what the item was near, in the log (once a second at most), for a report.
        internal static void Explain(fpscontroller pl, string reason)
        {
            if (Time.unscaledTime - _explainedAt < 1f) return;
            _explainedAt = Time.unscaledTime;
            var sb = new System.Text.StringBuilder();
            try { FindTouch(pl, out _, sb); } catch (System.Exception e) { sb.Append(" (" + e.Message + ")"); }
            Plugin.Log.LogInfo("In Place: G mounted nothing (" + reason + "): " + sb);
        }

        private static Transform _tracked; private static Vector3 _trackedPos; private static Quaternion _trackedRot; private static float _trackedAt;

        /// After an in-place mount: half a second on, how far the item is from where it was held, in the log.
        internal static void Track(Transform t, Vector3 heldPos, Quaternion heldRot)
        {
            _tracked = t; _trackedPos = heldPos; _trackedRot = heldRot; _trackedAt = Time.unscaledTime;
        }

        internal static void CheckTracked()
        {
            if (_tracked == null || Time.unscaledTime - _trackedAt < 0.5f) return;
            var t = _tracked; _tracked = null;
            Plugin.Log.LogInfo("In Place: " + t.name + " half a second after mounting is " + ((t.position - _trackedPos).magnitude * 1000f).ToString("F1") + " mm, " +
                               Quaternion.Angle(t.rotation, _trackedRot).ToString("F2") + " deg from where it was held (parent " + (t.parent != null ? t.parent.name + " under " + (t.parent.parent != null ? t.parent.parent.name : "-") : "none") + ")");
        }

        /// How far a held item can be from a surface and still count as touching it: the game's contacts keep an item
        /// held against a wall a little off it.
        internal const float TouchM = 0.03f;
        private static readonly Vector3[] _dirs = new Vector3[16];

        /// How deep c is in o; 0 for "touching" (within TouchM of it); -1 apart. Touching: moved TouchM toward o it
        /// overlaps. Which way is toward o: the nearest point of o where Unity has one (not on a building's mesh, whose
        /// bounds hold the item: a sponge held flat on a house wall never counted, 2026-11-25), the view, the item's own
        /// axes (flat against something: its thin side), the world's.
        private static float Depth(Collider c, Collider o, Vector3 centre, Vector3 view)
        {
            var ct = c.transform; var ot = o.transform;
            if (Physics.ComputePenetration(c, ct.position, ct.rotation, o, ot.position, ot.rotation, out _, out float dist)) return dist;
            int n = 0;
            bool closest = o is BoxCollider || o is SphereCollider || o is CapsuleCollider || o is MeshCollider mc && mc.convex;
            if (closest) { var to = o.ClosestPoint(centre) - centre; if (to.sqrMagnitude > 1e-8f) _dirs[n++] = to.normalized; }
            _dirs[n++] = view;
            _dirs[n++] = ct.right; _dirs[n++] = -ct.right; _dirs[n++] = ct.up; _dirs[n++] = -ct.up; _dirs[n++] = ct.forward; _dirs[n++] = -ct.forward;
            _dirs[n++] = Vector3.right; _dirs[n++] = Vector3.left; _dirs[n++] = Vector3.up; _dirs[n++] = Vector3.down; _dirs[n++] = Vector3.forward; _dirs[n++] = Vector3.back;
            for (int i = 0; i < n; i++)
                if (Physics.ComputePenetration(c, ct.position + _dirs[i] * TouchM, ct.rotation, o, ot.position, ot.rotation, out _, out _)) return 0f;
            return -1f;
        }
    }
}
