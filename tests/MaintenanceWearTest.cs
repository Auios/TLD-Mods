// The wear rules' checks, ported from Roadworthy v0.3.0-pre1 (tests/EngineWearPolicyTests.cs),
// https://github.com/jaylikesbunda/TheLongDrive-Roadworthy, Copyright (c) 2026 jaylikesbunda, MIT licence
// (mods/Maintenance/LICENSE-Roadworthy.txt). Changes: WearPolicy instead of EngineWearPolicy, no game assemblies
// (the policy is plain C#), Roadworthy's option-file checks left out (the add-on's options are BepInEx config and
// RevaMP session settings), checks for the brush table helpers added.
using System;
using Maintenance;

static class MaintenanceWearTest
{
    static int checks, fails;
    static void Check(bool value, string message) { checks++; Console.WriteLine((value ? "PASS " : "FAIL ") + message); if (!value) fails++; }

    static int Main()
    {
        // Effects: a new engine is untouched, a rusty one is weaker but still drives.
        Check(WearPolicy.Power(0) == 1f && WearPolicy.StartChance(0) == 1f && WearPolicy.OilUse(0) == 1f && WearPolicy.Heat(0) == 1f && WearPolicy.Smoke(0) == 1f, "a new engine runs exactly as the game made it");
        Check(WearPolicy.Power(4) >= 0.7f && WearPolicy.Power(4) < 1f, "a rusty engine keeps most of its power");
        Check(WearPolicy.StartChance(4) >= 0.5f, "a rusty engine still starts most of the time");
        Check(WearPolicy.Power(1) > WearPolicy.Power(3) && WearPolicy.OilUse(3) > WearPolicy.OilUse(1), "effects get worse step by step");
        Check(WearPolicy.Power(9) == WearPolicy.Power(4) && WearPolicy.Power(-2) == 1f, "out-of-range states are clamped");

        // Wear: slow from driving, fast when hot or short of oil, none when off.
        Check(WearPolicy.Wear(false, 1f, 1f, 1f, 1f) == 0f, "a stopped engine does not wear");
        Check(WearPolicy.Wear(true, 0.5f, 0f, 0f, -1f) == 0f, "a negative frame time does not wear");
        float cruise = WearPolicy.Wear(true, 0.5f, 0f, 0f, 1f);
        float hours = 1f / cruise / 3600f;
        Check(hours > 3f && hours < 12f, "normal driving costs one condition step in a few hours, got " + hours);
        Check(WearPolicy.Wear(true, 1f, 0f, 0f, 1f) > cruise, "high revs wear faster");
        Check(1f / WearPolicy.Wear(true, 0.5f, 1f, 0f, 1f) < 20f * 60f, "overheating costs a step within minutes");
        Check(WearPolicy.Wear(true, 0.5f, 0f, 1f, 1f) > cruise * 5f, "running dry wears much faster");
        Check(WearPolicy.Wear(true, float.NaN, float.NaN, float.NaN, 1f) > 0f, "unknown readings count as idle, not as damage");
        Check(WearPolicy.StepDown(1f, 3) && !WearPolicy.StepDown(0.99f, 3) && !WearPolicy.StepDown(5f, 4), "a step is lost at full wear and rusty is the floor");

        // Repair: switched-off worn engines only; the last two steps are slower.
        Check(WearPolicy.CanBrush(true, 4, false) && WearPolicy.CanBrush(true, 1, false), "a worn engine that is off can be brushed all the way");
        Check(!WearPolicy.CanBrush(true, 3, true), "a running engine cannot be brushed");
        Check(!WearPolicy.CanBrush(true, 0, false), "a new engine needs no brushing");
        Check(!WearPolicy.CanBrush(false, 4, false), "with the option off the engine keeps its own repair rules");
        Check(WearPolicy.BrushSpeed(4) == 1f && WearPolicy.BrushSpeed(3) == 1f, "rusty to middle brushes at normal speed");
        Check(WearPolicy.BrushSpeed(2) < WearPolicy.BrushSpeed(3) && WearPolicy.BrushSpeed(1) < WearPolicy.BrushSpeed(2), "used and new take longer");
        Check(WearPolicy.ScaleRepair(0.2f, 0.6f, 4) == 0.6f, "full-speed steps keep the brush's progress");
        Check(Math.Abs(WearPolicy.ScaleRepair(0.2f, 0.6f, 2) - 0.3f) < 1e-6f, "the step to used keeps a quarter of the progress");
        Check(WearPolicy.ScaleRepair(0.6f, 0.1f, 1) == 0.1f && WearPolicy.ScaleRepair(float.NaN, 0.5f, 1) == 0.5f, "resets and unknown values pass through");

        // Radiator: wears only while the engine runs, fast when boiling.
        Check(WearPolicy.RadiatorWear(false, 1f, 1f) == 0f, "a radiator does not wear with the engine off");
        float radHours = 1f / WearPolicy.RadiatorWear(true, 0f, 1f) / 3600f;
        Check(radHours > 3f && radHours < 12f, "a radiator loses a step in a few hours of running, got " + radHours);
        Check(1f / WearPolicy.RadiatorWear(true, 1f, 1f) < 20f * 60f, "a boiling engine costs the radiator a step within minutes");

        // Tyres: distance and wheelspin, floor at old.
        Check(WearPolicy.TireWear(0f, 0f, 1f) == 0f, "a parked car's tyres do not wear");
        float km = 1f / WearPolicy.TireWear(1f, 0f, 1f) / 1000f;
        Check(km > 100f && km < 400f, "tyres lose a step every couple of hundred km, got " + km);
        Check(WearPolicy.TireWear(20f, 0.2f, 1f) == WearPolicy.TireWear(20f, 0f, 1f), "normal grip slip does not add wear");
        float burnout = 1f / WearPolicy.TireWear(0f, 2f, 1f);
        Check(burnout > 30f && burnout < 180f, "a burnout costs a step in about a minute, got " + burnout);
        Check(WearPolicy.TireWear(float.NaN, float.NaN, 1f) == 0f && WearPolicy.TireWear(-5f, -0.1f, 1f) == 0f, "bad readings do not wear tyres");
        Check(WearPolicy.StepDown(1f, 2, WearPolicy.TireFloor) && !WearPolicy.StepDown(1f, 3, WearPolicy.TireFloor), "tyres stop wearing at old");

        // The brush tables: a radiator borrows "metal" only when the option is on and its own table lacks the brush.
        Check(WearPolicy.UseMetal(true, true, false) && !WearPolicy.UseMetal(false, true, false) && !WearPolicy.UseMetal(true, false, false) && !WearPolicy.UseMetal(true, true, true), "the metal table only for a radiator, only with the option, only when needed");
        var table = new[] { new[] { false, false }, new[] { false, true } };
        Check(WearPolicy.AcceptsTool(table, WearPolicy.WireBrush) && !WearPolicy.AcceptsTool(table, 2) && !WearPolicy.AcceptsTool(null, 1) && !WearPolicy.AcceptsTool(new bool[][] { null }, 1), "a table accepts a tool if any state does; missing rows and tables don't");

        Console.WriteLine(fails == 0 ? "ALL PASS (" + checks + " part wear checks)" : "FAIL (" + fails + " of " + checks + " checks)");
        return fails == 0 ? 0 : 1;
    }
}
