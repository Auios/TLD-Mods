using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using TLDRevamp.Multiplayer;
using UnityEngine;

namespace Maintenance
{
    /// Engine conditions kept with the world: the game's save has no place for them (an engine's save record holds its rpm
    /// and temperature only). Next to the save, as RevaMP keeps its own tables: `<save>.maintenance`, written with every
    /// save of the world by the machine whose world it is (single player or the host, never a joiner), moved along with
    /// the autosaves, read once per world. Engines are keyed by their id in the save (tosaveitemscript.idInSave).
    internal static class EngineStore
    {
        const string Header = "maintenance engines 1";
        public static long FilesRead, FilesWritten, Applied;

        static readonly Dictionary<uint, (int Level, float Wear)> _saved = new Dictionary<uint, (int, float)>();
        static readonly Dictionary<uint, enginescript> _bound = new Dictionary<uint, enginescript>();
        static string _readSave;
        static int _world;
        static float _at;

        static bool OwnsWorld => !MpApi.InSession || MpApi.IsHost;

        internal static uint SaveId(enginescript e)
        {
            var t = e.GetComponent<tosaveitemscript>();
            if (t == null) { var a = e.GetComponent<attachablescript>(); t = a != null ? a.tosave : null; }
            return t != null ? t.idInSave : 0;
        }

        static void EnsureWorld()
        {
            int id = mainscript.s != null ? mainscript.s.GetInstanceID() : 0;
            if (id == 0 || id == _world) return;
            _world = id;
            _saved.Clear(); _bound.Clear(); Engines.Clear();
            if (!OwnsWorld || _readSave == null) return;
            Read(_readSave + ".maintenance");
        }

        internal static bool TryGetSaved(enginescript e, out int level, out float wear)
        {
            level = 0; wear = 0f;
            if (!OwnsWorld) return false;
            EnsureWorld();
            uint id = SaveId(e);
            if (id == 0 || !_saved.TryGetValue(id, out var s)) return false;
            level = s.Level; wear = s.Wear; _bound[id] = e;
            return true;
        }

        /// Once a second (Runner): saved conditions onto engines the game has placed (a car that streams in, a load).
        internal static void Tick()
        {
            if (Time.realtimeSinceStartup < _at) return;
            _at = Time.realtimeSinceStartup + 1f;
            if (mainscript.s == null || savedatascript.s == null) return;
            EnsureWorld();
            Engines.Prune();
            if (!OwnsWorld || _saved.Count == 0) return;
            foreach (var kv in _saved)
            {
                if (!savedatascript.s.items.TryGetValue(kv.Key, out var it) || it == null || it.engine == null) continue;
                if (_bound.TryGetValue(kv.Key, out var b) && b == it.engine) continue;
                _bound[kv.Key] = it.engine;
                if (Engines.Known(it.engine)) continue;   // already changed here this session: that wins
                Engines.Set(it.engine, kv.Value.Level, kv.Value.Wear);
                Applied++;
            }
        }

        /// What the loaded engines have now, into the table (engines not loaded keep their saved entry).
        static void Refresh()
        {
            EnsureWorld();
            foreach (var kv in Engines.All)
            {
                if (kv.Key == null) continue;
                uint id = SaveId(kv.Key);
                if (id == 0) continue;
                if (kv.Value.Level == 0 && kv.Value.Wear <= 0f) _saved.Remove(id);
                else _saved[id] = (kv.Value.Level, kv.Value.Wear);
            }
        }

        static void Read(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != Header) throw new Exception("not an engine table");
                for (int i = 1; i < lines.Length; i++)
                {
                    var p = lines[i].Split(' ');
                    if (p.Length != 3 || !uint.TryParse(p[0], out uint id) || !int.TryParse(p[1], out int level) ||
                        !float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float wear) || level < 0 || level > WearPolicy.MaxState) continue;
                    _saved[id] = (level, Mathf.Clamp01(wear));
                }
                FilesRead++;
                Plugin.Log.LogInfo($"engine conditions: {_saved.Count} loaded with {Path.GetFileName(path)}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("engine conditions: " + Path.GetFileName(path) + " not readable: " + e.Message); }
        }

        static void Write(string path)
        {
            var sb = new StringBuilder(Header).Append('\n');
            foreach (var kv in _saved)
                sb.Append(kv.Key).Append(' ').Append(kv.Value.Level).Append(' ').Append(kv.Value.Wear.ToString("0.#####", CultureInfo.InvariantCulture)).Append('\n');
            File.WriteAllText(path, sb.ToString());
            FilesWritten++;
        }

        static string SavePath(string text) => pathscript.SaveDataName(pathscript.UnDuckStringBecauseItIsDuckedByDefault(text));

        // Every scene change goes through ResetStuff (new game, load, main menu, joining): no file yet. The game reading a
        // save: that file (as RevaMP's world tables do).
        [HarmonyPatch(typeof(DataFromMenuScript), nameof(DataFromMenuScript.ResetStuff))]
        static class WorldReset { static void Postfix() { _readSave = null; _world = 0; } }

        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.ReadSave))]
        static class FromReadSave { static void Postfix(string _name) { _readSave = _name; _world = 0; } }   // already the file's path

        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.Save))]
        static class WithSave
        {
            static void Postfix(string _text, bool __runOriginal)
            {
                if (!__runOriginal || !OwnsWorld) return;
                try
                {
                    Refresh();
                    string path = SavePath(_text);
                    _readSave = path;
                    if (_saved.Count > 0) Write(path + ".maintenance");
                    else if (File.Exists(path + ".maintenance")) File.Delete(path + ".maintenance");   // another world's, from before
                }
                catch (Exception e) { Plugin.Log.LogWarning("engine conditions: not saved: " + e.Message); }
            }
        }

        /// The game's autosave rotation (autosave1 → 2 → 3, before writing the new autosave1) moves the save files; their
        /// engine tables move the same way. Only when the autosave really runs (`__runOriginal`: RevaMP's test blockers and
        /// a joiner in the host's world stop it), and last, after the blockers.
        [HarmonyPatch(typeof(newSaveScreenScript), nameof(newSaveScreenScript.AutoSave))]
        static class RotateWithAutosaves
        {
            [HarmonyPriority(Priority.Last)]
            static void Prefix(bool __runOriginal)
            {
                if (!__runOriginal) return;
                try
                {
                    // under the same conditions as the saves themselves (RevaMP's world tables rotate this way too)
                    const string x = ".maintenance";
                    string d1 = pathscript.SaveDataName(pathscript.autosaveName1), d2 = pathscript.SaveDataName(pathscript.autosaveName2),
                           d3 = pathscript.SaveDataName(pathscript.autosaveName3);
                    if (!File.Exists(d1)) return;
                    if (File.Exists(d3) && File.Exists(d2)) Del(d3 + x);
                    if (File.Exists(d2)) { Del(d3 + x); Mv(d2 + x, d3 + x); }
                    Del(d2 + x); Mv(d1 + x, d2 + x);
                }
                catch (Exception e) { Plugin.Log.LogWarning("engine conditions: autosave rotation: " + e.Message); }
            }
            static void Del(string p) { if (File.Exists(p)) File.Delete(p); }
            static void Mv(string a, string b) { if (File.Exists(a)) File.Move(a, b); }
        }
    }
}
