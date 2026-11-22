using System.Collections.Generic;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace Maintenance
{
    /// An engine's condition, kept by this add-on: the game gives radiators and tyres a condition (partconditionscript)
    /// but no engine on the beta has one (none of its 40 engine models, 2026-11-22), so engines get their own here, on the
    /// game's scale: 0 new, 1 used, 2 middle, 3 old, 4 rusty.
    internal sealed class EngineCondition
    {
        internal int Level;
        internal float Wear;     // progress towards the next step down (0..1)
        internal float Repair;   // the brush's progress towards the next step up (0..1)
        internal int BrushFrame = -100;
    }

    /// Every engine this machine knows a condition for, and how the conditions travel between players:
    ///  - the machine that changes one (wear on the car's simulating machine, a brush anywhere) sends it to the others;
    ///  - the host sends what it knows to a player who joins;
    ///  - a machine that meets an engine it knows nothing about asks the host.
    /// Engines are named by RevaMP's id of the shared object they are in (MpApi.WorldObjectId: the car, or the engine
    /// itself when it lies loose); a car has one engine. Saved with the world by EngineStore.
    internal static class Engines
    {
        static readonly Dictionary<enginescript, EngineCondition> _of = new Dictionary<enginescript, EngineCondition>();
        static readonly HashSet<uint> _asked = new HashSet<uint>();
        public static long Sent, Received, Asked;

        internal static IEnumerable<KeyValuePair<enginescript, EngineCondition>> All => _of;

        /// The engine's condition: known here, else saved with the world (single player, host), else new (0) for now -
        /// and in a session as a joiner, the host is asked.
        internal static EngineCondition Of(enginescript e)
        {
            if (_of.TryGetValue(e, out var c)) return c;
            c = new EngineCondition();
            if (EngineStore.TryGetSaved(e, out int level, out float wear)) { c.Level = level; c.Wear = wear; }
            else if (MpApi.InSession && !MpApi.IsHost) Ask(e);
            _of[e] = c;
            return c;
        }

        internal static bool Known(enginescript e) => _of.ContainsKey(e);

        /// Set from outside (a saved table, an event, a test) without sending it on.
        internal static void Set(enginescript e, int level, float wear)
        {
            var c = Of(e);
            c.Level = Mathf.Clamp(level, 0, WearPolicy.MaxState); c.Wear = Mathf.Clamp01(wear); c.Repair = 0f;
        }

        /// A condition changed here (a wear step, a brush step): tell the others.
        internal static void Changed(enginescript e)
        {
            if (!MpApi.InSession || !_of.TryGetValue(e, out var c)) return;
            uint net = MpApi.WorldObjectId(e.gameObject);
            if (net == 0) return;
            if (MpApi.SendEvent(Plugin.Mod, "level", w => { w.UInt(net); w.Byte((byte)c.Level); }, EventTarget.Others)) Sent++;
        }

        static void Ask(enginescript e)
        {
            uint net = MpApi.WorldObjectId(e.gameObject);
            if (net == 0 || !_asked.Add(net)) return;
            if (MpApi.SendEvent(Plugin.Mod, "ask", w => w.UInt(net), EventTarget.Host)) Asked++;
        }

        internal static void Register(ModInfo mod)
        {
            MpApi.OnEvent(mod, "level", OnLevel);
            MpApi.OnEvent(mod, "ask", OnAsk);
            MpApi.PlayerJoined += p => { if (MpApi.IsHost) SendAllTo(); };
            MpApi.SessionStarted += () => _asked.Clear();
            MpApi.SessionEnded += () => _asked.Clear();
        }

        static enginescript EngineIn(uint net)
        {
            var go = MpApi.FindWorldObject(net);
            if (go == null) return null;
            var car = go.GetComponent<carscript>();
            return car != null && car.Engine != null ? car.Engine : go.GetComponentInChildren<enginescript>(true);
        }

        static void OnLevel(ModEvent ev)
        {
            uint net = ev.Data.UInt(); int level = ev.Data.Byte();
            if (!ev.Data.IsValid || level > WearPolicy.MaxState) return;
            var e = EngineIn(net);
            if (e == null) return;
            Set(e, level, 0f);
            Received++;
        }

        static void OnAsk(ModEvent ev)
        {
            if (!MpApi.IsHost) return;
            uint net = ev.Data.UInt();
            var e = ev.Data.IsValid ? EngineIn(net) : null;
            if (e == null) return;
            var c = Of(e);
            if (MpApi.SendEvent(Plugin.Mod, "level", w => { w.UInt(net); w.Byte((byte)c.Level); }, EventTarget.Others)) Sent++;
        }

        /// The host, when a player joins: every known engine that isn't new.
        static void SendAllTo()
        {
            foreach (var kv in _of)
            {
                if (kv.Key == null || kv.Value.Level == 0) continue;
                uint net = MpApi.WorldObjectId(kv.Key.gameObject);
                int level = kv.Value.Level;
                if (net != 0 && MpApi.SendEvent(Plugin.Mod, "level", w => { w.UInt(net); w.Byte((byte)level); }, EventTarget.Others)) Sent++;
            }
        }

        /// Destroyed engines out (a world unloaded, a part deleted).
        internal static void Prune()
        {
            var dead = new List<enginescript>();
            foreach (var kv in _of) if (kv.Key == null) dead.Add(kv.Key);
            foreach (var d in dead) _of.Remove(d);
        }

        internal static void Clear() { _of.Clear(); _asked.Clear(); }
    }
}
