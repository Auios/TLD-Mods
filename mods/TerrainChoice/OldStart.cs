using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TerrainChoice
{
    /// The start of the public build v2023.05.02d (old terrain since 0.5.0; 0.3/0.4 worlds keep the beta's start).
    ///
    /// The old game's start is part of its scene, at fixed x/z beside the road's start (the origin): the house haz02,
    /// its outhouse, well, mailbox, phone booth and bus stop. TerrainGenerationSettings.FStart lifts the house, the
    /// outhouse and the player by the ground height at one anchor ("Map"), and sets the well, mailbox, phone booth and
    /// bus stop on the ground where each one stands; the house and the bus stop then flatten the ground around them
    /// (terrainHeightAlignToBuildingScript). The numbers below are that scene's values, read from the user's own copy
    /// of the old game with tools\OldRoadDump -oldstartdump (seeds 12345 and 777: the heights follow the anchor).
    ///
    /// The beta's start house (kezdohaz) is that same layout: the house with the outhouse, well, mailbox, phone booth and
    /// bus stop as children at exactly the old offsets, but turned 180 degrees inside the prefab and with its pivot on
    /// the ground (1.01 m under the old house's). So the beta's start house, unturned at the old house's x/z, is the old
    /// start; the children that stood on the ground in the old game are set on the ground again after it spawns.
    internal static class OldStart
    {
        // the old scene (global = old local: the old world is at the origin since 0.5.0)
        private const double AnchorX = 42.626014709472656, AnchorZ = 68.699996948242188;   // "Map", StartObjs[0]
        public const double HouseX = 41.610019683837891, HouseZ = 66.340003967285156;
        private const double HouseUp = 1.0480003356933594;          // haz02 above the anchor's ground
        private const double BetaPivotDown = 1.0099998712539673;    // the beta's haz02: the old house's parts sit this high above its pivot
        private const double OuthouseUp = 0.18199920654296875;      // KERTIBUDI (1) above the anchor's ground
        private const double PlayerX = 43.401016235351563, PlayerZ = 68.074005126953125, PlayerRy = 270.0;
        private const double FlatUp = 0.034999847412109375;         // the house's flattening points above the anchor's ground

        // terrainHeightAlignToBuildingScript points: flat inside r1, blended (on the squared distance) out to r2
        private static readonly double[] HouseFlatX = { 46.820018768310547, 39.500019073486328, 32.310020446777344 };
        private const double HouseFlatZ = 68.680000305175781, HouseR1 = 10.0, HouseR2 = 40.0;
        private const double BusFlatX = -7.1940059661865234, BusFlatZ = 9.1199884414672852, BusR1 = 4.0, BusR2 = 16.0;
        private const double BusX = -6.9340057373046875, BusZ = 9.5699882507324219;

        // StartObjsFix: on the ground where they stand (the beta's children of the start house, by name)
        private static readonly string[] GroundNames = { "PhoneBooth", "Buszmegallo", "Post", "kut" };
        private static readonly double[,] GroundXZ =
        {
            { 28.386013031005859, 79.459999084472656 },
            { BusX, BusZ },
            { 18.090005874633789, 63.009994506835938 },
            { 36.329994201660156, 36.589984893798828 },
        };

        private sealed class State
        {
            public double Anchor, FlatY, BusY;
            public double[] GroundY = new double[4];
            public List<Vector3d> Road;   // the old road's coords near the start
            public poiGenScript[] Owners;
        }

        private static volatile State _s;
        public static bool Active => _s != null;

        public static void Set(mapSettingScript ms, OldNoise noise)
        {
            if (noise == null || noise.Legacy || ms == null || OldRoad.Path == null) { _s = null; return; }
            var s = new State();
            s.Anchor = noise.Height((float)AnchorX, (float)AnchorZ);   // nothing else shapes the ground there yet
            s.FlatY = s.Anchor + FlatUp;
            s.Road = OldRoad.Path.ForwardPoints(400.0);
            s.Owners = ms.poiGens != null ? ms.poiGens.ToArray() : new poiGenScript[0];
            // the bus stop stood on the ground with the road's blending (its own flattening came after)
            s.BusY = Ground(s, noise, BusX, BusZ, withBus: false);
            for (int i = 0; i < GroundNames.Length; i++)
                s.GroundY[i] = GroundNames[i] == "Buszmegallo" ? s.BusY : Ground(s, noise, GroundXZ[i, 0], GroundXZ[i, 1], withBus: GroundNames[i] != "PhoneBooth");
            _s = s;
            Plugin.Log.LogInfo("Old start: the 2023 start house at " + HouseX.ToString("F1") + " " + (s.Anchor + HouseUp).ToString("F2") + " " + HouseZ.ToString("F1") + " (anchor ground " + s.Anchor.ToString("F3") + ")");
        }

        private static double Ground(State s, OldNoise noise, double x, double z, bool withBus)
        {
            bool pinned = false;
            double h = noise.Height((float)x, (float)z);
            h = Align(s, x, z, h, ref pinned, withBus);
            if (!pinned) h = OldRoadBlend.Blend(x, z, h, s.Road);
            return h;
        }

        /// The old game's building flattening (TerrainGenerationJob.AlignToBuilding) for the start house and the bus
        /// stop. pinned: inside a point's flat radius, where the old game kept the building's height over the road's.
        public static double Align(double x, double z, double value, ref bool pinned)
        {
            var s = _s;
            return s == null ? value : Align(s, x, z, value, ref pinned, true);
        }

        private static double Align(State s, double x, double z, double value, ref bool pinned, bool withBus)
        {
            if (x < -25.0 || x > 90.0 || z < -10.0 || z > 110.0) return value;   // beyond every point's reach
            for (int i = 0; i < HouseFlatX.Length; i++)
                if (Point(x, z, HouseFlatX[i], HouseFlatZ, s.FlatY, HouseR1, HouseR2, ref value, ref pinned)) return value;
            if (withBus) Point(x, z, BusFlatX, BusFlatZ, s.BusY, BusR1, BusR2, ref value, ref pinned);
            return value;
        }

        private static bool Point(double x, double z, double px, double pz, double py, double r1, double r2, ref double value, ref bool pinned)
        {
            double dx = x - px, dz = z - pz, d2 = dx * dx + dz * dz;
            if (d2 < r1 * r1) { value = py; pinned = true; return true; }
            if (d2 < r2 * r2) value = py + (value - py) * ((d2 - r1 * r1) / (r2 * r2 - r1 * r1));
            return false;
        }

        private static bool Owns(poiGenScript p, State s)
        {
            foreach (var o in s.Owners) if (ReferenceEquals(o, p)) return true;
            return false;
        }

        public static bool IsStartHouse(poiGenScript.poiClass poi) =>
            _s != null && poi != null && poi.id == (int)itemdatabase.b.kezdohaz && Math.Abs(poi.pos.x - HouseX) < 1.0 && Math.Abs(poi.pos.z - HouseZ) < 1.0;

        /// poiGenScript.StartHouseStart with the old start's place instead of 45 m beside the road (same steps).
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.StartHouseStart))]
        private static class Place
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            private static bool Prefix(poiGenScript __instance)
            {
                var s = _s;
                if (s == null || !Owns(__instance, s)) return true;
                if (__instance.startHouseStarted) return false;
                __instance.startHouseStarted = true;
                if (!__instance.placeStartHouse) return false;
                var pos = new Vector3d(HouseX, s.Anchor + HouseUp - BetaPivotDown, HouseZ);
                var poi = __instance.AddPoi(pos, Vector3.zero, __instance.startHouseType, _alignedToRoad: false);
                if (DataFromMenuScript.s.newgame || (!syncScript.IsHostOrSingle() && syncScript.GetSpawnType() == SteamP2PMenuHandlerScript.startSpawnType.atStart))
                {
                    __instance.Spawn(poi);
                    var start = poi.pobj != null ? poi.pobj.transform.Find("PlayerStart") : null;
                    if (start != null && mainscript.s.player != null)
                    {
                        // the old player's spot and heading; the height of the beta's own PlayerStart (its controller)
                        var g = mainscript.GlobalFromUnityPos(start.position);
                        g.x = PlayerX; g.z = PlayerZ;
                        mainscript.s.player.Teleport(mainscript.UnityPosFromGlobal(g), new Vector3(0f, (float)PlayerRy, 0f));
                    }
                }
                poi.spawnedItems = true;
                Plugin.Log.LogInfo("Old start: start house placed at " + pos);
                return false;
            }
        }

        /// After the start house spawns: the outhouse at its old height, the others on the ground as in the old game.
        [HarmonyPatch(typeof(poiGenScript), nameof(poiGenScript.Spawn))]
        private static class GroundChildren
        {
            [HarmonyPostfix]
            private static void Postfix(poiGenScript.poiClass _poi, GameObject __result)
            {
                var s = _s;
                if (s == null || __result == null || !IsStartHouse(_poi)) return;
                foreach (Transform c in __result.transform)
                {
                    double y;
                    if (c.name == "KERTIBUDI (1)") y = s.Anchor + OuthouseUp;
                    else
                    {
                        int i = Array.IndexOf(GroundNames, c.name);
                        if (i < 0) continue;
                        y = s.GroundY[i];
                    }
                    var g = mainscript.GlobalFromUnityPos(c.position);
                    g.y = y;
                    c.position = mainscript.UnityPosFromGlobal(g);
                }
            }
        }
    }

    /// The old game's road blending of the ground (TerrainGenerationJob.AlignToRoad, defPointRange 20): from the two
    /// road coords nearest to the point, the ground takes the road's height up to 10 m from the road's line and blends
    /// back to the landscape at 20 m; only where the nearest coord is within 20 m.
    internal static class OldRoadBlend
    {
        private const double Range = 20.0;

        public static double Blend(double x, double z, double value, List<Vector3d> pts)
        {
            var n = new Nearest(x, z);
            foreach (var p in pts) n.Add(p);
            return n.Apply(x, z, value);
        }

        /// From the beta's roads near the point (the old road's 10 m points as their bones). skip: the road's start
        /// point, which the old game's coords didn't have.
        public static double Blend(double x, double z, double value, List<roadGenScript.roadClass> roads, Vector3d skip)
        {
            if (roads == null) return value;
            var n = new Nearest(x, z);
            const double reach = 50.0 + Range + 15.0;   // a 100 m piece's middle bone to its ends, plus the range and the next coord
            for (int r = 0; r < roads.Count; r++)
            {
                var els = roads[r].roadElements;
                for (int e = 0; e < els.Count; e++)
                {
                    var bp = els[e].bonePos;
                    if (bp.Count == 0) continue;
                    var mid = bp[bp.Count / 2];
                    double mx = mid.x - x, mz = mid.z - z;
                    if (mx * mx + mz * mz > reach * reach) continue;
                    for (int i = 0; i < bp.Count; i++)
                    {
                        var p = bp[i];
                        if (p.x == skip.x && p.z == skip.z) continue;
                        n.Add(p);
                    }
                }
            }
            return n.Apply(x, z, value);
        }

        private struct Nearest
        {
            private readonly double _x, _z;
            private Vector3d _c1, _c2;
            private double _d1, _d2;
            private bool _has1, _has2;

            public Nearest(double x, double z) { _x = x; _z = z; _c1 = _c2 = default; _d1 = _d2 = double.MaxValue; _has1 = _has2 = false; }

            public void Add(Vector3d p)
            {
                // pieces share their end points: a coord counts once
                if (_has1 && p.x == _c1.x && p.z == _c1.z) return;
                if (_has2 && p.x == _c2.x && p.z == _c2.z) return;
                double dx = p.x - _x, dz = p.z - _z, d = dx * dx + dz * dz;
                if (d < _d1) { if (_has1) { _c2 = _c1; _d2 = _d1; _has2 = true; } _c1 = p; _d1 = d; _has1 = true; }
                else if (d < _d2) { _c2 = p; _d2 = d; _has2 = true; }
            }

            public double Apply(double x, double z, double value)
            {
                if (!_has1 || !_has2 || _d1 >= Range * Range) return value;
                double ax = _c2.x - _c1.x, az = _c2.z - _c1.z, len = Math.Sqrt(ax * ax + az * az);
                if (len < 1e-9) return value;
                double rx = x - _c1.x, rz = z - _c1.z;
                double along = Math.Abs(rx * ax + rz * az) / len;    // project(c - c1, c2 - c1): a length, never negative
                double perp = Math.Abs(rz * ax - rx * az) / len;     // project(c - c1, Perpendicular(c2 - c1))
                double h = _c1.y + (_c2.y - _c1.y) * Clamp01(along / len);
                double t = Clamp01((perp - Range * 0.5) / (Range * 0.5));
                value = h + (value - h) * t;
                // the old game kept the ground 0.1 m under the road's middle 9 m (the beta's road pieces are ~10.9 m wide)
                if (perp < 4.5) value -= 0.1;
                return value;
            }

            private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
        }
    }
}
