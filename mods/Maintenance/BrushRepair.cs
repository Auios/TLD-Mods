// Ported from Roadworthy v0.3.0-pre1 (runtime/SmallFixes.cs, RadiatorRepair), https://github.com/jaylikesbunda/TheLongDrive-Roadworthy
// Copyright (c) 2026 jaylikesbunda. MIT licence: see LICENSE-Roadworthy.txt next to this file. Changes: a Harmony prefix
// instead of a Cecil call, options from Settings, radiators only (engines: EngineBrush).
using System.Collections.Generic;
using HarmonyLib;

namespace Maintenance
{
    /// The wire brush on radiators. The game decides what a tool may repair from the part condition's repair table
    /// (itemdatabase.repairProperties, per state and tool); radiators have a table without the brush. Before every
    /// CanRepair check, a radiator borrows the "metal" table (which takes the brush) while the option allows it, and gets
    /// its own back otherwise. (Roadworthy did the same for engines; on the beta engines have no condition, so EngineBrush
    /// does theirs.) Parts are recognised once and cached, so the hot path while a
    /// tool is aimed is one dictionary lookup.
    [HarmonyPatch(typeof(partconditionscript), nameof(partconditionscript.CanRepair))]
    internal static class BrushRepair
    {
        sealed class Seen { public itemdatabase.repairProperty Original; public bool Radiator; }
        static readonly Dictionary<int, Seen> seen = new Dictionary<int, Seen>();

        static void Prefix(partconditionscript __instance)
        {
            try { BeforeCanRepair(__instance); }
            catch (System.Exception e) { Plugin.Fault("brush", e); }
        }

        internal static void BeforeCanRepair(partconditionscript condition)
        {
            if (condition == null) return;
            int id = condition.GetInstanceID();
            if (!seen.TryGetValue(id, out var entry))
            {
                if (seen.Count > 4096) seen.Clear();
                entry = new Seen { Original = condition.RepairProperty, Radiator = IsRadiator(condition) };
                seen[id] = entry;
            }
            if (!entry.Radiator) return;
            // Part wear wears radiators down, so it also lets the brush fix them.
            bool metal = WearPolicy.UseMetal(Settings.BrushRadiators || Settings.PartWear, true, NativeAcceptsBrush(entry.Original));
            var want = metal ? (itemdatabase.repairProperty)WearPolicy.Metal : entry.Original;
            if (condition.RepairProperty != want) condition.RepairProperty = want;
        }

        static bool NativeAcceptsBrush(itemdatabase.repairProperty property)
        {
            var db = itemdatabase.s;
            int index = (int)property;
            if (db == null || db.repairProperties == null || index < 0 || index >= db.repairProperties.Length || db.repairProperties[index] == null) return false;
            return WearPolicy.AcceptsTool(db.repairProperties[index].b, WearPolicy.WireBrush);
        }

        static bool IsRadiator(partconditionscript condition)
        {
            var cooler = condition.GetComponentInParent<coolantTankscript>();
            if (cooler != null && cooler.condition == condition) return true;
            cooler = condition.GetComponentInChildren<coolantTankscript>(true);
            return cooler != null && cooler.condition == condition;
        }
    }
}
