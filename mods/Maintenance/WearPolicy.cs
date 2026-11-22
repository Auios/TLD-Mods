// Ported from Roadworthy v0.3.0-pre1 (runtime/EngineWear.cs, EngineWearPolicy), https://github.com/jaylikesbunda/TheLongDrive-Roadworthy
// Copyright (c) 2026 jaylikesbunda. MIT licence: see LICENSE-Roadworthy.txt next to this file. Changes: namespace and
// class name; the radiator brush table helpers come from Roadworthy's SmallFixes.cs (RadiatorRepairPolicy).
using System;

namespace Maintenance
{
    /// Pure decisions for part wear (no Unity objects, unit-tested).
    /// Condition states are the game's own: 0 new, 1 used, 2 middle, 3 old, 4 rusty.
    public static class WearPolicy
    {
        public const int MaxState = 4;
        // The wire brush restores a worn engine at normal speed down to "middle";
        // the last two steps to used and new take 4x and 8x as long. Swapping in a
        // better engine from a wreck stays the quick way to a good one.
        public const int SlowFrom = 2;

        // Wear per second at full load. About 4 hours of normal driving per
        // condition step, so a long trip costs one step, not the engine.
        public const float BaseRate = 1f / 14000f;
        // Running hot or short of oil wears far faster than driving.
        public const float OverheatRate = 1f / 600f;
        public const float LowOilRate = 1f / 900f;

        static float T(int state) { return Math.Min(Math.Max(state, 0), MaxState) / (float)MaxState; }
        static float Lerp(float a, float b, float t) { return a + (b - a) * t; }

        // Multipliers applied to the engine's own values. A rusty engine still
        // drives: about three quarters of the power, harder starts, more oil,
        // more heat and smoke.
        public static float Power(int state) { return Lerp(1f, 0.75f, T(state)); }
        public static float StartChance(int state) { return Lerp(1f, 0.6f, T(state)); }
        public static float OilUse(int state) { return Lerp(1f, 3f, T(state)); }
        public static float Heat(int state) { return Lerp(1f, 1.3f, T(state)); }
        public static float Smoke(int state) { return Lerp(1f, 3f, T(state)); }

        // Wear gained this frame. rpmFraction is rpm/maxRpm; overheat and lowOil are 0..1.
        public static float Wear(bool running, float rpmFraction, float overheat, float lowOil, float deltaTime)
        {
            if (!running || !(deltaTime > 0f)) return 0f;
            float load = Clamp01(rpmFraction);
            float rate = BaseRate * (0.3f + 1.7f * load * load);
            rate += OverheatRate * Clamp01(overheat);
            rate += LowOilRate * Clamp01(lowOil);
            return rate * deltaTime;
        }

        // A step of condition is lost once wear reaches 1; a rusty engine stays rusty.
        public static bool StepDown(float wear, int state) { return wear >= 1f && state < MaxState; }

        // The brush works on any worn engine that is switched off.
        public static bool CanBrush(bool optionOn, int state, bool running)
        {
            return optionOn && !running && state > 0;
        }

        // Share of the brush's progress kept for an engine in this state.
        public static float BrushSpeed(int state)
        {
            if (state > SlowFrom) return 1f;
            if (state == SlowFrom) return 0.25f;
            return 0.125f;
        }

        // Repair progress after scaling this frame's gain from the brush.
        public static float ScaleRepair(float previous, float current, int state)
        {
            if (float.IsNaN(previous) || float.IsNaN(current) || !(current > previous)) return current;
            return previous + (current - previous) * BrushSpeed(state);
        }

        // Radiators wear only while the engine runs: about 5 hours per step,
        // minutes when the engine is boiling.
        public const float RadiatorRate = 1f / 18000f;
        public const float RadiatorOverheatRate = 1f / 900f;

        public static float RadiatorWear(bool running, float overheat, float deltaTime)
        {
            if (!running || !(deltaTime > 0f)) return 0f;
            return (RadiatorRate + RadiatorOverheatRate * Clamp01(overheat)) * deltaTime;
        }

        // Tires wear with distance (about 200 km per step) and fast with
        // wheelspin or skids (about a minute of burnout per step). They stop at
        // "old" so worn tires still grip; a fresh set is the way back to new.
        public const float TireRatePerMetre = 1f / 200000f;
        public const float TireSlipRate = 1f / 60f;
        public const float TireSlipFrom = 0.3f;
        public const int TireFloor = 3;

        public static float TireWear(float speedMps, float slip, float deltaTime)
        {
            if (!(deltaTime > 0f)) return 0f;
            float speed = float.IsNaN(speedMps) || speedMps < 0f ? 0f : Math.Min(speedMps, 100f);
            float excess = Clamp01((Math.Abs(float.IsNaN(slip) ? 0f : slip) - TireSlipFrom) / (1f - TireSlipFrom));
            return (TireRatePerMetre * speed + TireSlipRate * excess) * deltaTime;
        }

        public static bool StepDown(float wear, int state, int floor) { return wear >= 1f && state < floor; }

        // itemdatabase.repairProperty.metal and the wire brush's cleaning type.
        public const int Metal = 4;
        public const int WireBrush = 1;

        // A radiator borrows the metal table only while the option is on and only
        // when its own table does not already accept the wire brush.
        public static bool UseMetal(bool optionOn, bool isRadiator, bool nativeAcceptsBrush)
        {
            return optionOn && isRadiator && !nativeAcceptsBrush;
        }

        public static bool AcceptsTool(bool[][] table, int tool)
        {
            if (table == null) return false;
            for (int s = 0; s < table.Length; s++)
                if (table[s] != null && tool < table[s].Length && table[s][tool]) return true;
            return false;
        }

        static float Clamp01(float v) { return float.IsNaN(v) ? 0f : v < 0f ? 0f : v > 1f ? 1f : v; }
    }
}
