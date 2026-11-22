using System.Globalization;
using System.Text;
using UnityEngine;

namespace Maintenance
{
    /// Checks for the in-game tests, called through TLD Revamp's debug bridge (`call Maintenance.Probe.<name>`): what the
    /// add-on sees on the car the player is in. Reads only, except Condition (a test sets a part's state).
    public static class Probe
    {
        static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        /// The player's car: settings, counters, the engine (condition, power multiplier against the engine's own value
        /// when tracked, running, may the brush work on it now), the radiator and every tyre's condition.
        public static string Car()
        {
            var sb = new StringBuilder("{");
            sb.Append("\"partWear\":").Append(Settings.PartWear ? "true" : "false")
              .Append(",\"brushRadiators\":").Append(Settings.BrushRadiators ? "true" : "false")
              .Append(",\"wearRate\":").Append(F(Settings.WearRate))
              .Append(",\"steps\":").Append(Wear.Steps).Append(",\"radiatorSteps\":").Append(Wear.RadiatorSteps).Append(",\"tireSteps\":").Append(Wear.TireSteps)
              .Append(",\"brushSteps\":").Append(EngineBrush.Steps).Append(",\"sent\":").Append(Engines.Sent).Append(",\"received\":").Append(Engines.Received)
              .Append(",\"filesRead\":").Append(EngineStore.FilesRead).Append(",\"filesWritten\":").Append(EngineStore.FilesWritten);
            var car = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Car : null;
            if (car == null) return sb.Append(",\"car\":null}").ToString();
            sb.Append(",\"car\":\"").Append(car.name).Append("\",\"simulatedHere\":").Append(Simulated.Here(car.gameObject) ? "true" : "false");
            var e = car.Engine;
            if (e != null)
            {
                var ec = Engines.Of(e);
                sb.Append(",\"engine\":{\"state\":").Append(ec.Level).Append(",\"wear\":").Append(F(ec.Wear)).Append(",\"modifier\":").Append(F(e.modifier))
                  .Append(",\"running\":").Append(e.running ? "true" : "false").Append(",\"brush\":").Append(WearPolicy.CanBrush(Settings.PartWear, ec.Level, e.running) ? "true" : "false")
                  .Append(",\"repair\":").Append(F(ec.Repair)).Append(",\"saveId\":").Append(EngineStore.SaveId(e)).Append('}');
            }
            var rc = car.coolant != null ? car.coolant.condition : null;
            if (rc != null)
                sb.Append(",\"radiator\":{\"state\":").Append(rc.state).Append(",\"brush\":").Append(rc.CanRepair(WearPolicy.WireBrush) ? "true" : "false").Append('}');
            sb.Append(",\"tires\":[");
            bool first = true;
            foreach (var g in car.GetComponentsInChildren<gumiscript>(false))
            {
                if (g == null || g.pc == null) continue;
                if (!first) sb.Append(','); first = false;
                sb.Append(g.pc.state);
            }
            return sb.Append("]}").ToString();
        }

        /// Every engine prefab in the game's item list: its id, name, and any condition of the game's own on it (none on the
        /// beta, 2026-11-22: why engines have the add-on's own, Engines).
        public static string EngineCatalog()
        {
            var db = itemdatabase.s;
            if (db == null || db.items == null) return "{\"error\":\"no item database\"}";
            var sb = new StringBuilder("{\"engines\":[");
            int n = 0, with = 0;
            for (int i = 0; i < db.items.Length; i++)
            {
                var go = db.items[i];
                var e = go != null ? go.GetComponentInChildren<enginescript>(true) : null;
                if (e == null) continue;
                var c = e.GetComponent<attachablescript>() is attachablescript pa && pa.condition != null ? pa.condition : e.GetComponentInChildren<partconditionscript>(true);
                if (n++ > 0) sb.Append(',');
                if (c != null) with++;
                sb.Append("{\"id\":").Append(i).Append(",\"name\":\"").Append(go.name).Append("\",\"condition\":").Append(c != null ? "\"" + c.name + "\"" : "null").Append('}');
            }
            return sb.Append("],\"count\":").Append(n).Append(",\"withCondition\":").Append(with).Append('}').ToString();
        }

        /// The condition of the engine with this save id, if the game has it placed (after a load: what EngineStore put
        /// back, within a second).
        public static string EngineById(string saveId)
        {
            if (!uint.TryParse(saveId, out uint id) || savedatascript.s == null) return "{\"error\":\"save id\"}";
            if (!savedatascript.s.items.TryGetValue(id, out var it) || it == null || it.engine == null) return "{\"placed\":false}";
            var c = Engines.Of(it.engine);
            return "{\"placed\":true,\"state\":" + c.Level + ",\"wear\":" + F(c.Wear) + ",\"filesRead\":" + EngineStore.FilesRead + ",\"applied\":" + EngineStore.Applied + "}";
        }

        /// Test: set the player's car's engine ("engine") or radiator ("radiator") to a condition state, through the game's
        /// own Refresh (as wear and the brush do).
        public static string Condition(string part, string state)
        {
            var car = mainscript.s != null && mainscript.s.player != null ? mainscript.s.player.Car : null;
            if (car == null) return "{\"error\":\"not in a car\"}";
            if (!int.TryParse(state, out int s) || s < 0 || s > WearPolicy.MaxState) return "{\"error\":\"state 0-4\"}";
            if (part == "engine")
            {
                if (car.Engine == null) return "{\"error\":\"no engine\"}";
                Engines.Set(car.Engine, s, 0f); Engines.Changed(car.Engine);
                return "{\"engine\":" + Engines.Of(car.Engine).Level + "}";
            }
            var c = part == "radiator" ? (car.coolant != null ? car.coolant.condition : null) : null;
            if (c == null) return "{\"error\":\"no such part\"}";
            c.state = s; c.Refresh(true);
            return "{\"" + part + "\":" + c.state + "}";
        }
    }
}
