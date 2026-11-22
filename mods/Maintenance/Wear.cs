// Ported from Roadworthy v0.3.0-pre1 (runtime/EngineWear.cs, EngineWear), https://github.com/jaylikesbunda/TheLongDrive-Roadworthy
// Copyright (c) 2026 jaylikesbunda. MIT licence: see LICENSE-Roadworthy.txt next to this file. Changes: wear runs where
// RevaMP simulates the car (Simulated.Here) instead of on the host, the option comes from Settings, logging goes
// through the plugin's logger, a wear-rate multiplier (Settings.WearRate), and the engine's condition is also found
// through its part (attachablescript.condition).
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Maintenance
{
    /// Part wear. Engine: power, starting, oil use, heat and smoke follow the engine part's condition, and running the
    /// engine slowly wears it (faster when overheating or short of oil). Radiator and tyres already lose cooling and grip
    /// with condition in the base game; here they also wear: the radiator while the engine runs, tyres with distance and
    /// wheelspin. The wire brush restores engines and radiators (slower on the last two steps, BrushRepair); tyres are
    /// replaced. Cars are picked up from the one the player is in, then kept until destroyed.
    ///
    /// Multiplayer: wear is applied only on the machine that simulates the car (RevaMP's owner, usually its driver). The
    /// condition changes go through the game's own Refresh, and RevaMP sends the car's state from its owner to everyone,
    /// so the other machines see the worn part. The engine's power and smoke follow the condition on every machine.
    internal static class Wear
    {
        sealed class Part
        {
            internal partconditionscript Condition;
            internal gumiscript Tire;
            internal float Wear, LastRepair;
            internal int LastState = -1;
        }
        sealed class Car
        {
            internal carscript Script;
            internal float NextScan;
            internal readonly List<Part> Tires = new List<Part>();
            internal Part Radiator;
        }
        static readonly List<Car> cars = new List<Car>();
        public static long TireSteps, RadiatorSteps;

        sealed class Tracked
        {
            internal enginescript Engine;
            internal EngineCondition State;   // the add-on's own (Engines): the game gives engines none
            internal float Modifier, StartChance, OilUse, HeatMin, HeatMax, Smoke;
            internal int Applied = -1;
        }
        static readonly List<Tracked> tracked = new List<Tracked>();
        static readonly HashSet<int> known = new HashSet<int>();
        public static long Steps;

        public static string Summary()
        {
            int tires = 0; for (int i = 0; i < cars.Count; i++) tires += cars[i].Tires.Count;
            return "wear{tracked=" + tracked.Count + " steps=" + Steps + " cars=" + cars.Count + " tires=" + tires + " tireSteps=" + TireSteps + " radiatorSteps=" + RadiatorSteps + "}";
        }

        public static void Tick()
        {
            bool on = Settings.PartWear;
            if (on) Discover();
            if (tracked.Count == 0 && cars.Count == 0) return;
            float dt = Time.deltaTime * Settings.WearRate;   // the brush scaling below works on progress, not time
            bool running = Time.timeScale != 0f;
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                var t = tracked[i];
                if (t.Engine == null) { tracked.RemoveAt(i); continue; }
                if (!on) { Restore(t); tracked.RemoveAt(i); continue; }
                var st = t.State;
                if (st.Level != t.Applied) Apply(t, st.Level);
                EngineBrush.Fade(st, Time.deltaTime);
                if (!running || !Simulated.Here(t.Engine.gameObject)) continue;
                var e = t.Engine;
                float rpmFraction = e.maxRpm > 0f ? e.rpm / e.maxRpm : 0f;
                st.Wear += WearPolicy.Wear(e.running, rpmFraction, Overheat(e), e.OilWeak, dt);
                if (WearPolicy.StepDown(st.Wear, st.Level))
                {
                    st.Wear = 0f; st.Repair = 0f;
                    st.Level++;
                    Apply(t, st.Level);
                    Steps++;
                    Plugin.Log.LogInfo("engine " + e.name + " wore to condition " + st.Level);
                    Engines.Changed(e);
                }
                else if (st.Wear >= 1f) st.Wear = 1f;
            }
            if (!on) { cars.Clear(); known.Clear(); return; }
            for (int i = cars.Count - 1; i >= 0; i--)
            {
                var c = cars[i];
                if (c.Script == null) { cars.RemoveAt(i); continue; }
                if (Time.unscaledTime >= c.NextScan) Scan(c);
                TickCar(c, running && Simulated.Here(c.Script.gameObject), dt);
            }
        }

        static float Overheat(enginescript e)
        {
            return e.maxOptimalTemp2 > e.maxOptimalTemp1 ? Mathf.InverseLerp(e.maxOptimalTemp1, e.maxOptimalTemp2, e.temp) : 0f;
        }

        static void TickCar(Car c, bool wear, float dt)
        {
            var car = c.Script;
            var engine = car.Engine;
            var r = c.Radiator;
            if (r != null && r.Condition == null) c.Radiator = r = null;
            if (r != null)
            {
                SlowBrush(r);
                if (wear && engine != null)
                {
                    r.Wear += WearPolicy.RadiatorWear(engine.running, Overheat(engine), dt);
                    if (StepPart(r, WearPolicy.MaxState))
                    {
                        RadiatorSteps++;
                        Plugin.Log.LogInfo("radiator on " + car.name + " wore to condition " + r.Condition.state);
                    }
                }
            }
            if (c.Tires.Count == 0) return;
            float speed = 0f, slip = 0f;
            if (wear)
            {
                if (car.RB != null) speed = car.RB.velocity.magnitude;
                slip = WheelSlip(car);
            }
            for (int i = c.Tires.Count - 1; i >= 0; i--)
            {
                var p = c.Tires[i];
                if (p.Condition == null || p.Tire == null) { c.Tires.RemoveAt(i); continue; }
                // Grip follows the tyre's condition; refresh it when the state changes (also on copies).
                if (p.Condition.state != p.LastState) { p.Tire.Refresh(); p.LastState = p.Condition.state; }
                if (!wear) continue;
                p.Wear += WearPolicy.TireWear(speed, slip, dt);
                if (StepPart(p, WearPolicy.TireFloor)) { p.Tire.Refresh(); TireSteps++; }
            }
        }

        // Average grounded slip across the car's wheels (0 when airborne).
        static float WheelSlip(carscript car)
        {
            var wheels = car.allWH;
            if (wheels == null) return 0f;
            float sum = 0f; int n = 0;
            for (int i = 0; i < wheels.Count; i++)
            {
                var w = wheels[i];
                WheelHit hit;
                if (w == null || !w.GetGroundHit(out hit)) continue;
                sum += Math.Max(Math.Abs(hit.forwardSlip), Math.Abs(hit.sidewaysSlip));
                n++;
            }
            return n == 0 ? 0f : sum / n;
        }

        // The brush's last two steps are slower on radiators too.
        static void SlowBrush(Part p)
        {
            int state = p.Condition.state;
            if (state == p.LastState)
            {
                float scaled = WearPolicy.ScaleRepair(p.LastRepair, p.Condition.repairValue, state);
                if (scaled != p.Condition.repairValue) p.Condition.repairValue = scaled;
            }
            p.LastState = state; p.LastRepair = p.Condition.repairValue;
        }

        static bool StepPart(Part p, int floor)
        {
            int state = p.Condition.state;
            if (WearPolicy.StepDown(p.Wear, state, floor))
            {
                p.Wear = 0f;
                p.Condition.state = state + 1;
                p.Condition.Refresh(true);
                p.LastState = state + 1; p.LastRepair = p.Condition.repairValue;
                return true;
            }
            if (p.Wear >= 1f) p.Wear = 1f;
            return false;
        }

        // Tyres and radiators get swapped, so look again every few seconds.
        static void Scan(Car c)
        {
            c.NextScan = Time.unscaledTime + 3f;
            var car = c.Script;
            var rc = car.coolant == null ? null : car.coolant.condition;
            if (rc == null) c.Radiator = null;
            else if (c.Radiator == null || c.Radiator.Condition != rc) c.Radiator = new Part { Condition = rc, LastState = rc.state, LastRepair = rc.repairValue };
            var tires = car.GetComponentsInChildren<gumiscript>(false);
            for (int i = c.Tires.Count - 1; i >= 0; i--)
                if (c.Tires[i].Tire == null || Array.IndexOf(tires, c.Tires[i].Tire) < 0) c.Tires.RemoveAt(i);
            for (int i = 0; i < tires.Length; i++)
            {
                var g = tires[i];
                if (g == null || g.pc == null) continue;
                bool have = false;
                for (int j = 0; j < c.Tires.Count; j++) if (c.Tires[j].Tire == g) { have = true; break; }
                if (!have) c.Tires.Add(new Part { Tire = g, Condition = g.pc, LastState = g.pc.state });
            }
        }

        static void Discover()
        {
            var main = mainscript.s;
            var car = main == null || main.player == null ? null : main.player.Car;
            if (car == null) return;
            if (car.Engine != null) Track(car.Engine);
            for (int i = 0; i < cars.Count; i++) if (cars[i].Script == car) return;
            var c = new Car { Script = car };
            cars.Add(c);
            Scan(c);
        }

        internal static void Track(enginescript engine)
        {
            if (engine == null || !Settings.PartWear || !known.Add(engine.GetInstanceID())) return;
            tracked.Add(new Tracked
            {
                Engine = engine, State = Engines.Of(engine),
                Modifier = engine.modifier, StartChance = engine.startChance, OilUse = engine.OilConsumptionModifier,
                HeatMin = engine.engineHeatGainMin, HeatMax = engine.engineHeatGainMax, Smoke = engine.defSmokeRate
            });
        }

        static void Apply(Tracked t, int state)
        {
            var e = t.Engine;
            e.modifier = t.Modifier * WearPolicy.Power(state);
            e.startChance = t.StartChance * WearPolicy.StartChance(state);
            e.OilConsumptionModifier = t.OilUse * WearPolicy.OilUse(state);
            e.engineHeatGainMin = t.HeatMin * WearPolicy.Heat(state);
            e.engineHeatGainMax = t.HeatMax * WearPolicy.Heat(state);
            e.defSmokeRate = t.Smoke * WearPolicy.Smoke(state);
            t.Applied = state;
        }

        static void Restore(Tracked t)
        {
            var e = t.Engine;
            if (e == null) return;
            e.modifier = t.Modifier; e.startChance = t.StartChance; e.OilConsumptionModifier = t.OilUse;
            e.engineHeatGainMin = t.HeatMin; e.engineHeatGainMax = t.HeatMax; e.defSmokeRate = t.Smoke;
        }

        public static void Clear()
        {
            for (int i = 0; i < tracked.Count; i++) Restore(tracked[i]);
            tracked.Clear(); known.Clear(); cars.Clear();
        }
    }
}
