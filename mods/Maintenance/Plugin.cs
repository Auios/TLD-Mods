using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace Maintenance
{
    /// Vehicle Maintenance: engines, radiators and tyres wear with use, and the wire brush repairs worn engines and
    /// radiators. The wear rules and the brush hook are ported from Roadworthy (MIT, LICENSE-Roadworthy.txt); the
    /// multiplayer side is RevaMP's: wear runs on the machine that simulates the car and its condition reaches everyone
    /// with the car's state, and both options are the host's in a session.
    [BepInPlugin(Guid, "Vehicle Maintenance", Version)]
    [BepInDependency("tldrevamp.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "tldrevamp.maintenance";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static ModInfo Mod;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);
            Mod = MpApi.RegisterMod("maintenance", "Vehicle Maintenance", Version);
            Settings.Register(Mod);
            Engines.Register(Mod);
            new Harmony(Guid).PatchAll(typeof(Plugin).Assembly);
            // The game destroys BepInEx's manager object (this plugin's) once its scene loads, which stops Update here:
            // the per-frame work runs on an object of our own.
            var go = new GameObject("MaintenanceRunner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
            Log.LogInfo($"Vehicle Maintenance {Version}: part wear {Settings.Format(Settings.PartWear)}, radiator brush {Settings.Format(Settings.BrushRadiators)}; multiplayer API {(MpApi.Available ? "found" : "not found (single player only)")}");
        }

        static float _faultAt = -100f;
        /// Logged once in a while, not every frame.
        internal static void Fault(string what, System.Exception e)
        {
            if (Time.realtimeSinceStartup - _faultAt < 10f) return;
            _faultAt = Time.realtimeSinceStartup;
            Log.LogError(what + ": " + e);
        }
    }

    public sealed class Runner : MonoBehaviour
    {
        private void Update()
        {
            try { Wear.Tick(); } catch (System.Exception e) { Plugin.Fault("wear", e); }
            try { EngineStore.Tick(); } catch (System.Exception e) { Plugin.Fault("engine store", e); }
        }
    }
}
