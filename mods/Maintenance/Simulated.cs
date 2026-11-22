using System;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace Maintenance
{
    /// Does this machine run the car's game logic? RevaMP 0.69.4 and later answer it (MpApi.SimulatedHere, API 5); on an
    /// older RevaMP the call is missing, and a car whose body RevaMP holds kinematic (a copy of another player's car) counts
    /// as simulated elsewhere. Looked up by reflection once: MpApi.ApiVersion is a constant, so it can't tell.
    internal static class Simulated
    {
        static Func<GameObject, bool> _here;
        static bool _resolved;

        internal static bool Here(GameObject go)
        {
            if (!_resolved) Resolve();
            if (_here != null) return _here(go);
            if (!MpApi.InSession || go == null) return true;
            var rb = go.transform.root.GetComponent<Rigidbody>();
            return rb == null || !rb.isKinematic;
        }

        static void Resolve()
        {
            _resolved = true;
            try
            {
                var m = typeof(MpApi).GetMethod("SimulatedHere", new[] { typeof(GameObject) });
                if (m != null) _here = (Func<GameObject, bool>)Delegate.CreateDelegate(typeof(Func<GameObject, bool>), m);
            }
            catch (Exception e) { Plugin.Log.LogWarning("MpApi.SimulatedHere not usable, using the body test: " + e.Message); }
            Plugin.Log.LogInfo(_here != null ? "multiplayer: wear runs where RevaMP simulates the car (SimulatedHere)" : "multiplayer: wear runs where the car's body isn't a copy (RevaMP before API 5)");
        }
    }
}
