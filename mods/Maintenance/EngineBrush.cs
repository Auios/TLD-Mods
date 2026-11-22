using HarmonyLib;
using UnityEngine;

namespace Maintenance
{
    /// The wire brush on a worn engine. Engines have no condition of the game's own, so the brush aimed at one found the
    /// condition above it, the car body's, and scrubbed that. With part wear on, a brush aimed at a worn engine that is
    /// switched off works the engine instead: progress at the game's own repair speed for the tool (repairModifier x
    /// mainscript.repairValueIncrease), the last two steps slower (WearPolicy.BrushSpeed), the brush itself wearing at each
    /// step as it does on any part (drotkefescript.Romlas). Anything else (a new or running engine, the option off,
    /// another tool) is the game's as before. The new condition goes to the other players (Engines.Changed).
    [HarmonyPatch(typeof(drotkefescript), nameof(drotkefescript.Use))]
    internal static class EngineBrush
    {
        public static long Steps;

        static bool Prefix(drotkefescript __instance, Transform ghead, Transform head, Transform gyro)
        {
            try { return !Brush(__instance, ghead, head, gyro); }
            catch (System.Exception e) { Plugin.Fault("engine brush", e); return true; }
        }

        /// True when the brush worked an engine this frame (the game's own Use is skipped then).
        static bool Brush(drotkefescript tool, Transform ghead, Transform head, Transform gyro)
        {
            if (!Settings.PartWear || tool.cleaningType != WearPolicy.WireBrush || tool.slide == null || ghead == null || mainscript.s == null) return false;
            var s = tool.slide;
            if (s.disabled || !Physics.Raycast(ghead.position, ghead.forward, out var hit, s.distance, s.LM)) return false;
            var engine = hit.collider.GetComponentInParent<enginescript>();
            if (engine == null) return false;
            var c = Engines.Of(engine);
            if (!WearPolicy.CanBrush(true, c.Level, engine.running)) return false;
            s.doingWork = false;
            s.Use(ghead, head, gyro);
            if (s.force <= tool.minForce) return true;   // on the engine, not scrubbing: nothing else gets the stroke either
            s.doingWork = true;
            c.BrushFrame = Time.frameCount;
            c.Repair += tool.repairModifier * mainscript.s.repairValueIncrease * Time.deltaTime * WearPolicy.BrushSpeed(c.Level);
            if (c.Repair >= 1f)
            {
                c.Repair = 0f; c.Wear = 0f; c.Level--;
                Steps++;
                Plugin.Log.LogInfo("engine " + engine.name + " brushed to condition " + c.Level);
                Engines.Changed(engine);
                tool.Romlas();
            }
            return true;
        }

        /// Progress fades when the brush stops, as a part's repairValue does (partconditionscript, repairValueDecrease).
        internal static void Fade(EngineCondition c, float dt)
        {
            if (c.Repair <= 0f || Time.frameCount - c.BrushFrame <= 1 || mainscript.s == null) return;
            c.Repair = Mathf.Max(0f, c.Repair - mainscript.s.repairValueDecrease * dt);
        }
    }
}
