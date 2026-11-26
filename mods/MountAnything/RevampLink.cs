using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// RevaMP (TLD Revamp) already shares the game's glue: the machine that mounts an item sends its save_attachable
    /// record (Entities.AttachHook → AttachSync), and every other machine replays it through the game's loader
    /// (Entities.ApplyAttachSync; a non-owner's mount goes to the owner first, Entities.ApplyAttachReq). Joiners and
    /// copies placed later get the item's save records, loaded by the game's LoadStuff (Mounting.LoadHook covers that).
    /// The one gap: the replay skips an item whose copy has no attachablescript, and items mounted by this mod only get
    /// one when they are mounted. So right before RevaMP applies an attach message, the item it names gets one here.
    ///
    /// The mod also registers with RevaMP's multiplayer API, so RevaMP refuses a join when host and joiner don't both
    /// have the same MountAnything (a player without it couldn't show mounted items).
    internal static class RevampLink
    {
        public static bool Active, Registered;
        public static long Prepared;
        private static FieldInfo _byNet, _root, _rootId, _buf, _pos, _end;

        public static void Init(Harmony harmony)
        {
            try
            {
                var ents = AccessTools.TypeByName("TLDRevamp.Net.Entities");
                var reader = AccessTools.TypeByName("TLDRevamp.Net.NetReader");
                if (ents == null || reader == null) return;   // no RevaMP: single player only
                var ent = AccessTools.Inner(ents, "Ent");
                _byNet = AccessTools.Field(ents, "ByNet");
                _root = ent != null ? AccessTools.Field(ent, "Root") : null;
                _rootId = ent != null ? AccessTools.Field(ent, "RootId") : null;
                _buf = AccessTools.Field(reader, "Buf");
                _pos = AccessTools.Field(reader, "Pos");
                _end = AccessTools.Field(reader, "End");
                var sync = AccessTools.Method(ents, "ApplyAttachSync", new[] { reader });
                var req = AccessTools.Method(ents, "ApplyAttachReq", new[] { reader });
                if (_byNet == null || _root == null || _buf == null || _pos == null || _end == null || sync == null || req == null)
                {
                    Plugin.Log.LogWarning("RevaMP found, but not the attach code this version knows: items mounted during a session may show loose for other players until they rejoin.");
                }
                else
                {
                    var pre = new HarmonyMethod(typeof(RevampLink), nameof(BeforeApply));
                    harmony.Patch(sync, prefix: pre);
                    harmony.Patch(req, prefix: pre);
                    var poseM = AccessTools.Method(AccessTools.TypeByName("TLDRevamp.Net.EntityAttachments"), "ApplyAttachPose");
                    if (poseM != null) harmony.Patch(poseM, postfix: new HarmonyMethod(typeof(RevampLink), nameof(AfterRevampPose)));
                    else Plugin.Log.LogWarning("RevaMP: no ApplyAttachPose; a live mount may show a little off for other players");
                    Active = true;
                }
                RegisterWithRevamp();
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP link: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterWithRevamp()
        {
            try
            {
                _mod = TLDRevamp.Multiplayer.MpApi.RegisterMod("mountanything", "MountAnything", Plugin.Version, typeof(RevampLink).Assembly);
                Registered = true;
                ListenForPoses();
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP mod registration: " + e.Message); }
        }

        /// Both messages start with the item's network id (u32, little endian). Read without moving the reader.
        private static void BeforeApply(object[] __args)
        {
            try
            {
                var r = __args[0];
                var buf = (byte[])_buf.GetValue(r);
                int pos = (int)_pos.GetValue(r), end = (int)_end.GetValue(r);
                if (buf == null || pos + 4 > end) return;
                uint net = (uint)(buf[pos] | buf[pos + 1] << 8 | buf[pos + 2] << 16 | buf[pos + 3] << 24);
                var byNet = (IDictionary)_byNet.GetValue(null);
                if (byNet == null || !byNet.Contains(net)) return;
                var e = byNet[net];
                var ts = (tosaveitemscript)_root.GetValue(e);
                if (ts == null && _rootId != null && savedatascript.s != null) savedatascript.s.items.TryGetValue((uint)_rootId.GetValue(e), out ts);
                if (ts == null || ts.attachable != null) return;
                Mounting.Ensure(ts);
                Mounting.AddedForRevamp++;
                Prepared++;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("RevaMP attach: " + ex.Message); }
        }

        /// After RevaMP put an attached item at its own copy of the pose: that is relative to the mount point's parent,
        /// which on this machine can be another collider than on the mounting one (the point is made anew from the
        /// record): a katana on a car 81 mm off, a sponge on a house 610 mm (two-game tests, 2026-11-24). It can come
        /// after this mod's pose for the item; the mod's pose wins.
        private static void AfterRevampPose(attachablescript at)
        {
            if (at == null || _known.Count == 0 && !Watch) return;
            try { AfterRevampPoseNow(at); }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP pose: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void AfterRevampPoseNow(attachablescript at)
        {
            uint net = TLDRevamp.Multiplayer.MpApi.WorldObjectId(at.gameObject);
            if (Watch) Plugin.Log.LogInfo("RevaMP put " + at.name + " (net " + net + ") at its attach pose, frame " + Time.frameCount + "; a pose of this mod for it: " + _known.ContainsKey(net) + ", one waiting: " + _pending.ContainsKey(net));
            if (net != 0 && _known.TryGetValue(net, out var k)) Reapply(net, k);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Reapply(uint net, PendingPose k)
        {
            if (Time.realtimeSinceStartup > k.Until) { _known.Remove(net); return; }
            var go = TLDRevamp.Multiplayer.MpApi.FindWorldObject(net);
            var ts = go != null ? go.GetComponent<tosaveitemscript>() : null;
            var at = ts != null ? ts.attachable : null;
            if (at == null || !at.attached || at.point == null) { _known.Remove(net); return; }
            var pt = Frame(at);
            var np = pt.position + pt.rotation * k.Pos; var nq = pt.rotation * k.Rot;
            float moved = (ts.transform.position - np).magnitude;
            if (moved < 1e-4f && Quaternion.Angle(ts.transform.rotation, nq) < 0.01f) return;
            if (Watch) Plugin.Log.LogInfo("pose re-applied to " + ts.name + " after RevaMP's attach: it had moved it " + (moved * 1000f).ToString("F1") + " mm");
            ts.transform.SetPositionAndRotation(np, nq);
            PosesReapplied++;
        }

        /// Poses applied in the last seconds: RevaMP may apply its own after them (a replay of the same attach).
        private static readonly Dictionary<uint, PendingPose> _known = new Dictionary<uint, PendingPose>();
        public static long PosesReapplied;

        // ------------------------------------------------------------------ the item's pose on its mount point
        // RevaMP's attach message is the game's save record: the point the item is on, not the item's own pose there.
        // The game keeps the item where it is when it attaches it from a record (attachablescript.tempDontMoveAtAttach),
        // which is right when loading a save (the item's record has its pose) but not on another player's machine,
        // whose copy of the item is wherever its last smoothed pose left it (0.9 m off in the first two-game test).
        // The game's own multiplayer sends that pose with the record (syncScript.SendAttachable: lpos/lrot); here it goes
        // as a mod event, and every other machine puts its copy there once the attach has arrived.

        private static object _mod;   // TLDRevamp.Multiplayer.ModInfo (kept as object: no RevaMP, no type load)
        private struct PendingPose { public Vector3 Pos; public Quaternion Rot; public float Until; }
        private static readonly Dictionary<uint, PendingPose> _pending = new Dictionary<uint, PendingPose>();
        public static long PosesSent, PosesApplied, PosesExpired;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ListenForPoses()
        {
            TLDRevamp.Multiplayer.MpApi.OnEvent((TLDRevamp.Multiplayer.ModInfo)_mod, "pose", e =>
            {
                if (e.From == TLDRevamp.Multiplayer.MpApi.MyPlayerId) return;
                var r = e.Data;
                uint net = r.UInt();
                var p = new Vector3(r.Float(), r.Float(), r.Float());
                var q = new Quaternion(r.Float(), r.Float(), r.Float(), r.Float());
                if (!r.IsValid || net == 0 || p.sqrMagnitude > 400f) return;
                _pending[net] = new PendingPose { Pos = p, Rot = q.normalized, Until = Time.realtimeSinceStartup + 15f };
                ApplyPending();
            });
        }

        /// What a pose is relative to: what the game's mount record names, the same object on every machine - the item it
        /// is mounted on, or the building - else the point itself (on the ground: a point of its own). Each game makes its
        /// own point: relative to the point, a katana mounted on a car showed 3.65 degrees and 5 mm off on the joiner;
        /// relative to the collider the point hangs on, a sponge on a building 90 mm off on the host (the point was made
        /// under one of the building's parts there, under another here) (2026-11-24).
        private static Transform Frame(attachablescript at)
        {
            var to = at.AttachedToTosave();
            if (to != null) return to.transform;
            var poi = at.AttachedToPOI();
            if (poi != null) return poi.transform;
            return at.point;
        }

        /// The machine that mounted the item: its pose on what it is mounted on, to everyone else.
        internal static void SendPose(attachablescript at)
        {
            if (!Registered || _mod == null || at == null || !at.attached || at.point == null) return;
            try { SendPoseNow(at); }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP pose: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendPoseNow(attachablescript at)
        {
            if (!TLDRevamp.Multiplayer.MpApi.InSession) return;
            uint net = TLDRevamp.Multiplayer.MpApi.WorldObjectId(at.gameObject);
            if (net == 0) return;
            _known.Remove(net);   // mounted here anew: a pose from elsewhere no longer applies
            var pt = Frame(at); var t = at.transform;
            var lp = Quaternion.Inverse(pt.rotation) * (t.position - pt.position);
            var lq = Quaternion.Inverse(pt.rotation) * t.rotation;
            if (TLDRevamp.Multiplayer.MpApi.SendEvent((TLDRevamp.Multiplayer.ModInfo)_mod, "pose", w =>
                {
                    w.UInt(net); w.Float(lp.x); w.Float(lp.y); w.Float(lp.z); w.Float(lq.x); w.Float(lq.y); w.Float(lq.z); w.Float(lq.w);
                }, TLDRevamp.Multiplayer.EventTarget.Others)) PosesSent++;
        }

        /// Called every frame: poses whose item is now mounted here are applied; old ones are dropped.
        /// The test: after a pose is applied, report the first time anything moves the item off it (10 s).
        internal static bool Watch;
        private class Watched { public Transform T, F; public Vector3 Lp, Tw, Fw; public Quaternion Lq; public float Since; public int Frame; }
        private static readonly List<Watched> _watch = new List<Watched>();

        private static string Path(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            for (; t != null; t = t.parent) { if (sb.Length > 0) sb.Append(" < "); sb.Append(t.name); var rb = t.GetComponent<Rigidbody>(); if (rb != null) sb.Append(rb.isKinematic ? "[kin rb]" : "[rb]"); }
            return sb.ToString();
        }

        internal static void CheckWatch(string when)
        {
            for (int i = _watch.Count - 1; i >= 0; i--)
            {
                var w = _watch[i];
                if (w.T == null || w.F == null || Time.realtimeSinceStartup - w.Since > 10f) { _watch.RemoveAt(i); continue; }
                var lp = Quaternion.Inverse(w.F.rotation) * (w.T.position - w.F.position); var lq = Quaternion.Inverse(w.F.rotation) * w.T.rotation;
                float d = (lp - w.Lp).magnitude * 1000f, a = Quaternion.Angle(lq, w.Lq);
                if (d < 1f && a < 0.05f) continue;
                Plugin.Log.LogInfo("pose watch: " + w.T.name + " moved off the applied pose by " + d.ToString("F1") + " mm, " + a.ToString("F2") + " deg, " +
                                   (Time.frameCount - w.Frame) + " frames after (" + when + "); the item moved " + ((w.T.position - w.Tw).magnitude * 1000f).ToString("F1") + " mm, its frame " + w.F.name + " " +
                                   ((w.F.position - w.Fw).magnitude * 1000f).ToString("F1") + " mm; under the frame " + w.T.IsChildOf(w.F) + "; path " + Path(w.T));
                _watch.RemoveAt(i);
            }
        }

        internal static void ApplyPending()
        {
            if (_watch.Count > 0) CheckWatch("update");
            if (_pending.Count == 0) return;
            try { ApplyPendingNow(); }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP pose: " + e.Message); _pending.Clear(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPendingNow()
        {
            List<uint> done = null;
            foreach (var kv in _pending)
            {
                var go = TLDRevamp.Multiplayer.MpApi.FindWorldObject(kv.Key);
                var ts = go != null ? go.GetComponent<tosaveitemscript>() : null;
                var at = ts != null ? ts.attachable : null;
                if (at != null && at.attached && at.point != null)
                {
                    var pt = Frame(at);
                    var np = pt.position + pt.rotation * kv.Value.Pos; var nq = pt.rotation * kv.Value.Rot;
                    if (Watch) Plugin.Log.LogInfo("pose applied to " + ts.name + " (net " + kv.Key + ") on " + pt.name + ": moved it " + ((ts.transform.position - np).magnitude * 1000f).ToString("F1") + " mm, " +
                                                  Quaternion.Angle(ts.transform.rotation, nq).ToString("F2") + " deg (frame " + Time.frameCount + ")");
                    ts.transform.SetPositionAndRotation(np, nq);
                    if (Watch) _watch.Add(new Watched { T = ts.transform, F = pt, Lp = kv.Value.Pos, Lq = kv.Value.Rot, Since = Time.realtimeSinceStartup, Frame = Time.frameCount, Tw = np, Fw = pt.position });
                    PosesApplied++;
                    _known[kv.Key] = new PendingPose { Pos = kv.Value.Pos, Rot = kv.Value.Rot, Until = Time.realtimeSinceStartup + 20f };
                    (done ??= new List<uint>()).Add(kv.Key);
                }
                else if (Time.realtimeSinceStartup > kv.Value.Until) { PosesExpired++; (done ??= new List<uint>()).Add(kv.Key); }
            }
            if (done != null) foreach (var k in done) _pending.Remove(k);
        }
    }
}
