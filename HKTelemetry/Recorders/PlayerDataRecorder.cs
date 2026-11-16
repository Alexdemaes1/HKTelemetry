using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using HKTelemetry.Session;
using HKTelemetry.Util;
using HKTelemetry.Writing;
using Modding;
using Newtonsoft.Json;
using UnityEngine;

namespace HKTelemetry.Recorders
{
    /// <summary>
    /// Comodín pd_change (docs/schema.md §4.4) y eventos derivados boss_defeated / ability_gained / item_got.
    ///
    /// - Hooks Set*: el juego llama al hook ANTES de escribir, así que el campo aún tiene el valor
    ///   anterior (verificado en ModHooks.SetPlayerX). Solo se emite si el valor cambia de verdad.
    ///   Todos devuelven su entrada sin tocar. Nunca se suscribe un Get*Hook.
    /// - Barrido: cada frame se comparan unos pocos campos con una caché, para capturar lo que no
    ///   pasa por hooks (src = "diff"). Las listas se comparan una vez por segundo (pd_list).
    /// - Línea base silenciosa en el primer control_ready de la sesión + volcado completo a
    ///   pd_start.json.gz; al pedir volver al menú, pd_end.json.gz.
    /// </summary>
    internal static class PlayerDataRecorder
    {
        private const int DiffPerFrame = 40;
        private const int MaxPerFieldPerSecond = 30;

        private static bool _hooked, _seeded;
        private static bool[] _b; private static int[] _i; private static float[] _f; private static string[] _s; private static Vector3[] _v;
        private static object[] _listSnap; // copia tipada de cada lista (List<string>/List<int>/List<Vector3>)
        private static int _cursor;
        private static float _lastListCheck;
        private static int[] _rlSecond, _rlCount;
        private static readonly Dictionary<string, int> Suppressed = new Dictionary<string, int>();
        private static readonly HashSet<string> Unknown = new HashSet<string>();

        private static bool Live => _seeded && SessionManager.Active && PdTable.Ready;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            System.Threading.ThreadPool.QueueUserWorkItem(_ => Safe.Run("pd.table", PdTable.Build));
            // Solo bool, int y tipos no primitivos. Cada hook suscrito hace que la API cree un array en
            // CADA escritura de ese tipo (GetInvocationList, ModHooks.cs:632): float/string/Vector3 son
            // raras y el barrido las detecta en una fracción de segundo (src = "diff").
            ModHooks.SetPlayerBoolHook += OnSetBool;
            ModHooks.SetPlayerIntHook += OnSetInt;
            ModHooks.SetPlayerVariableHook += OnSetVariable;
        }

        public static void OnSessionStart()
        {
            _seeded = false;
            lock (Suppressed) Suppressed.Clear();
            Unknown.Clear();
        }

        /// <summary>Para session.json.</summary>
        public static Dictionary<string, object> Counters()
        {
            lock (Suppressed)
                return new Dictionary<string, object>
                {
                    ["pd_suppressed"] = new Dictionary<string, int>(Suppressed),
                    ["pd_unknown_fields"] = Unknown.Count,
                };
        }

        // ---------------- línea base y volcados ----------------

        /// <summary>Primer control_ready: todo cargado (incluida la mejora del save). Línea base sin emitir.</summary>
        public static void SeedAndDump()
        {
            // Se llama en cada control_ready; solo actúa la primera vez (o cuando la tabla esté lista).
            if (_seeded || !SessionManager.Active || !PdTable.Ready) return;
            var pd = PlayerData.instance;
            Seed(pd);
            Dump(pd, "pd_start.json.gz");
        }

        /// <summary>Foto final, antes de que QuitToMenu llame a PlayerData.Reset().</summary>
        public static void DumpEnd()
        {
            if (!SessionManager.Active || !PdTable.Ready) return;
            Dump(PlayerData.instance, "pd_end.json.gz");
        }

        private static void Seed(PlayerData pd)
        {
            _b = new bool[PdTable.BoolGet.Length];
            for (int k = 0; k < _b.Length; k++) _b[k] = PdTable.BoolGet[k](pd);
            _i = new int[PdTable.IntGet.Length];
            for (int k = 0; k < _i.Length; k++) _i[k] = PdTable.IntGet[k](pd);
            _f = new float[PdTable.FloatGet.Length];
            for (int k = 0; k < _f.Length; k++) _f[k] = PdTable.FloatGet[k](pd);
            _s = new string[PdTable.StrGet.Length];
            for (int k = 0; k < _s.Length; k++) _s[k] = PdTable.StrGet[k](pd);
            _v = new Vector3[PdTable.V3Get.Length];
            for (int k = 0; k < _v.Length; k++) _v[k] = PdTable.V3Get[k](pd);
            _listSnap = new object[PdTable.Lists.Length];
            for (int k = 0; k < _listSnap.Length; k++) _listSnap[k] = CopyList(PdTable.Lists[k].Fi.GetValue(pd));
            _rlSecond = new int[PdTable.Count];
            _rlCount = new int[PdTable.Count];
            _cursor = 0;
            _seeded = true;
        }

        private static void Dump(PlayerData pd, string fileName)
        {
            var doc = new Dictionary<string, object>();
            for (int k = 0; k < PdTable.BoolNames.Length; k++) doc[PdTable.BoolNames[k]] = PdTable.BoolGet[k](pd);
            for (int k = 0; k < PdTable.IntNames.Length; k++) doc[PdTable.IntNames[k]] = PdTable.IntGet[k](pd);
            for (int k = 0; k < PdTable.FloatNames.Length; k++) doc[PdTable.FloatNames[k]] = PdTable.FloatGet[k](pd);
            for (int k = 0; k < PdTable.StrNames.Length; k++) doc[PdTable.StrNames[k]] = PdTable.StrGet[k](pd);
            for (int k = 0; k < PdTable.V3Names.Length; k++) { var v = PdTable.V3Get[k](pd); doc[PdTable.V3Names[k]] = new[] { v.x, v.y, v.z }; }
            foreach (var l in PdTable.Lists) doc[l.Name] = PdTable.ListAsStrings(l.Fi.GetValue(pd));
            foreach (var x in PdTable.Vars)
            {
                try { doc[x.Name] = VarValue(x.Fi.GetValue(pd), 0); }
                catch (Exception e) { doc[x.Name] = "<error: " + e.GetType().Name + ">"; }
            }

            var root = new Dictionary<string, object>
            {
                ["schema_version"] = HKTelemetry.SchemaVersion,
                ["session_id"] = SessionManager.SessionId,
                ["t_game"] = Clock.TGame,
                ["fields"] = doc,
            };
            var w = SessionManager.Writer;
            string path = Path.Combine(w.Dir, fileName);
            // Serializar ~1500 campos genera cientos de KB: en el hilo de escritura, no en el del juego.
            w.Job(() =>
            {
                string json = JsonConvert.SerializeObject(root);
                string tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var gz = new GZipStream(fs, CompressionMode.Compress))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(json);
                    gz.Write(bytes, 0, bytes.Length);
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            });
        }

        // ---------------- hooks (calientes: sin closures; try/catch explícito) ----------------

        private static bool OnSetBool(string name, bool value)
        {
            long t = Safe.Enter();
            try
            {
                if (Live && Lookup(name, PdTable.Kind.Bool, out var f))
                {
                    bool old = PdTable.BoolGet[f.Idx](PlayerData.instance);
                    _b[f.Idx] = value;
                    if (old != value) ChangeBool(f, old, value, "hook");
                }
            }
            catch (Exception e) { Safe.Report("pd.set_bool", e); }
            finally { Safe.Exit(t); }
            return value;
        }

        private static int OnSetInt(string name, int value)
        {
            long t = Safe.Enter();
            try
            {
                if (Live && Lookup(name, PdTable.Kind.Int, out var f))
                {
                    int old = PdTable.IntGet[f.Idx](PlayerData.instance);
                    _i[f.Idx] = value;
                    if (old != value) ChangeInt(f, old, value, "hook");
                }
            }
            catch (Exception e) { Safe.Report("pd.set_int", e); }
            finally { Safe.Exit(t); }
            return value;
        }




        /// <summary>Tipos no primitivos (enums como MapZone, structs de Godhome, listas reemplazadas). Raro.</summary>
        private static object OnSetVariable(Type type, string name, object value)
        {
            long t = Safe.Enter();
            try
            {
                if (Live && PdTable.ByName.TryGetValue(name, out var f))
                {
                    var pd = PlayerData.instance;
                    if (f.Kind == PdTable.Kind.List)
                    {
                        CheckList(f.Idx, value); // la lista se sustituye entera
                    }
                    else if (f.Kind == PdTable.Kind.Var)
                    {
                        string oldJson = VarJson(f.Fi.GetValue(pd));
                        string newJson = VarJson(value);
                        if (oldJson != newJson && Allow(f))
                            SessionManager.Emit("pd_change", Json.Obj().S("f", f.Name).S("t", "var")
                                .Raw("old", oldJson).Raw("new", newJson).S("src", "hook").End());
                    }
                }
                else if (Live) NoteUnknown(name);
            }
            catch (Exception e) { Safe.Report("pd.set_var", e); }
            finally { Safe.Exit(t); }
            return value;
        }

        private static bool Lookup(string name, PdTable.Kind kind, out PdTable.Field f)
        {
            if (PdTable.ByName.TryGetValue(name, out f) && f.Kind == kind) return true;
            NoteUnknown(name); // los FSM pueden escribir nombres que no existen: SetFieldSafe los ignora
            return false;
        }

        private static void NoteUnknown(string name)
        {
            if (Unknown.Add(name) && Unknown.Count <= 20)
                Safe.Info($"[HKTelemetry] PlayerData: escritura a un campo inexistente '{name}' (ignorada por el juego)");
        }

        /// <summary>JSON de un valor no primitivo, siempre a través de VarValue (seguro).</summary>
        private static string VarJson(object value) => JsonConvert.SerializeObject(VarValue(value, 0));

        /// <summary>
        /// Convierte un valor arbitrario en un árbol de primitivas, listas y diccionarios.
        /// NO se pasa el objeto original a Newtonsoft: un Vector3 dentro de un struct provoca recursión
        /// infinita por la propiedad 'normalized' y un StackOverflow cierra el juego sin poder capturarlo.
        /// Solo campos públicos y profundidad máxima 3.
        /// </summary>
        private static object VarValue(object v, int depth)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case bool _: case int _: case long _: case float _: case double _: case byte _: case short _: return v;
                case Enum e: return e.ToString();
                case Vector3 v3: return new[] { v3.x, v3.y, v3.z };
                case Vector2 v2: return new[] { v2.x, v2.y };
                case System.Collections.IList list:
                    if (depth >= 3) return list.Count;
                    var items = new List<object>();
                    foreach (var it in list) items.Add(VarValue(it, depth + 1));
                    return items;
            }
            if (depth >= 3) return v.ToString();
            var d = new Dictionary<string, object>();
            foreach (var fi in v.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                d[fi.Name] = VarValue(fi.GetValue(v), depth + 1);
            return d;
        }

        // ---------------- barrido por frame (Runner) ----------------

        public static void Poll()
        {
            if (!Live) return;
            var pd = PlayerData.instance;
            int nb = _b.Length, ni = _i.Length, nf = _f.Length, ns = _s.Length, nv = _v.Length;
            int total = nb + ni + nf + ns + nv;
            for (int step = 0; step < DiffPerFrame && total > 0; step++)
            {
                int c = _cursor;
                _cursor = (_cursor + 1) % total;
                if (c < nb)
                {
                    bool n = PdTable.BoolGet[c](pd);
                    if (n != _b[c]) { bool o = _b[c]; _b[c] = n; ChangeBool(PdTable.ByName[PdTable.BoolNames[c]], o, n, "diff"); }
                    continue;
                }
                c -= nb;
                if (c < ni)
                {
                    int n = PdTable.IntGet[c](pd);
                    if (n != _i[c]) { int o = _i[c]; _i[c] = n; ChangeInt(PdTable.ByName[PdTable.IntNames[c]], o, n, "diff"); }
                    continue;
                }
                c -= ni;
                if (c < nf)
                {
                    float n = PdTable.FloatGet[c](pd);
                    if (!n.Equals(_f[c])) { float o = _f[c]; _f[c] = n; ChangeFloat(PdTable.ByName[PdTable.FloatNames[c]], o, n, "diff"); }
                    continue;
                }
                c -= nf;
                if (c < ns)
                {
                    string n = PdTable.StrGet[c](pd);
                    if (!string.Equals(n, _s[c], StringComparison.Ordinal)) { string o = _s[c]; _s[c] = n; ChangeString(PdTable.ByName[PdTable.StrNames[c]], o, n, "diff"); }
                    continue;
                }
                c -= ns;
                {
                    Vector3 n = PdTable.V3Get[c](pd);
                    if (n != _v[c]) { Vector3 o = _v[c]; _v[c] = n; ChangeV3(PdTable.ByName[PdTable.V3Names[c]], o, n, "diff"); }
                }
            }

            // Listas modificadas in situ (scenesVisited.Add, equippedCharms...): sin hook. Una vez por segundo.
            float now = Time.realtimeSinceStartup;
            if (now - _lastListCheck >= 1f)
            {
                _lastListCheck = now;
                for (int k = 0; k < PdTable.Lists.Length; k++) CheckList(k, PdTable.Lists[k].Fi.GetValue(pd));
            }
        }

        /// <summary>
        /// Compara la lista actual con la copia guardada SIN generar basura (se ejecuta cada segundo):
        /// elemento a elemento con el tipo real. Solo si cambia se copia y se calcula el diff en texto.
        /// (Antes se convertían las 15 listas a strings cada segundo: basura → pausas del GC de ~3 ms.)
        /// </summary>
        private static void CheckList(int k, object value)
        {
            if (ListUnchanged(_listSnap[k], value)) return;
            var prevSnap = _listSnap[k];
            _listSnap[k] = CopyList(value);
            if (prevSnap == null) return;

            var cur = PdTable.ListAsStrings(value);
            var prev = PdTable.ListAsStrings(prevSnap);

            var added = new List<string>(cur);
            foreach (var x in prev) added.Remove(x);
            var removed = new List<string>(prev);
            foreach (var x in cur) removed.Remove(x);
            if (added.Count == 0 && removed.Count == 0) return; // solo reordenada

            var f = PdTable.Lists[k];
            SessionManager.Emit("pd_list", Json.Obj().S("f", f.Name)
                .Raw("added", JsonConvert.SerializeObject(added))
                .Raw("removed", JsonConvert.SerializeObject(removed))
                .I("count", cur.Count).End());
        }

        private static bool ListUnchanged(object prev, object cur)
        {
            if (prev == null) return cur == null;
            switch (cur)
            {
                case List<string> c: return prev is List<string> ps && Same(ps, c);
                case List<int> c: return prev is List<int> pi && Same(pi, c);
                case List<Vector3> c: return prev is List<Vector3> pv && Same(pv, c);
                case null: return false;
                default: return false; // PlayerData solo tiene listas de esos tres tipos (verificado)
            }
        }

        private static bool Same<T>(List<T> a, List<T> b)
        {
            if (a.Count != b.Count) return false;
            var eq = EqualityComparer<T>.Default; // string/int/Vector3 implementan IEquatable: sin boxing
            for (int k = 0; k < a.Count; k++) if (!eq.Equals(a[k], b[k])) return false;
            return true;
        }

        private static object CopyList(object cur)
        {
            switch (cur)
            {
                case List<string> c: return new List<string>(c);
                case List<int> c: return new List<int>(c);
                case List<Vector3> c: return new List<Vector3>(c);
                case null: return null;
                default: return PdTable.ListAsStrings(cur);
            }
        }

        // ---------------- emisión + límite por campo ----------------

        /// <summary>Sobre de pd_change sin cadena intermedia (Change* solo se llama con sesión activa).</summary>
        private static Json Pd(PdTable.Field f, string t) =>
            SessionManager.EmitBegin("pd_change")?.S("f", f.Name).S("t", t) ?? Json.Obj();

        private static bool Allow(PdTable.Field f)
        {
            int sec = (int)Time.realtimeSinceStartup;
            int g = f.Gid;
            if (_rlSecond[g] != sec) { _rlSecond[g] = sec; _rlCount[g] = 0; }
            if (++_rlCount[g] <= MaxPerFieldPerSecond) return true;
            lock (Suppressed)
            {
                Suppressed.TryGetValue(f.Name, out int n);
                Suppressed[f.Name] = n + 1;
            }
            return false;
        }

        private static void ChangeBool(PdTable.Field f, bool o, bool n, string src)
        {
            if (Allow(f))
                SessionManager.EmitEnd(Pd(f, "b").B("old", o).B("new", n).S("src", src));
            if (!o && n) Derived.OnBoolTrue(f.Name);
        }

        private static void ChangeInt(PdTable.Field f, int o, int n, string src)
        {
            if (Allow(f))
                SessionManager.EmitEnd(Pd(f, "i").I("old", o).I("new", n).S("src", src));
            if (n > o) Derived.OnIntUp(f.Name, o, n);
        }

        private static void ChangeFloat(PdTable.Field f, float o, float n, string src)
        {
            if (Allow(f))
                SessionManager.EmitEnd(Pd(f, "f").N("old", o).N("new", n).S("src", src));
        }

        private static void ChangeString(PdTable.Field f, string o, string n, string src)
        {
            if (Allow(f))
                SessionManager.EmitEnd(Pd(f, "s").S("old", o).S("new", n).S("src", src));
        }

        private static void ChangeV3(PdTable.Field f, Vector3 o, Vector3 n, string src)
        {
            if (Allow(f))
                SessionManager.Emit("pd_change", Json.Obj().S("f", f.Name).S("t", "v3")
                    .Raw("old", PdTable.FormatV3(o)).Raw("new", PdTable.FormatV3(n)).S("src", src).End());
        }

        // ---------------- eventos derivados ----------------

        /// <summary>
        /// boss_defeated, ability_gained e item_got: el análisis no necesita conocer nombres de campos.
        /// Siempre van además del pd_change crudo.
        /// </summary>
        private static class Derived
        {
            // Jefes: flag del diario (se pone en el momento de la muerte, solo la primera vez por partida)
            // + variantes de sueño y segundos combates que comparten entrada del diario.
            private static readonly HashSet<string> BossJournal = new HashSet<string>
            {
                "FalseKnight", "BigFly", "BigBuzzer", "Mawlek", "MegaMossCharger", "InfectedKnight", "MegaJellyfish",
                "MantisLord", "BlackKnight", "JarCollector", "MageLord", "FlukeMother", "DungDefender", "MegaBeamMiner",
                "MimicSpider", "HiveKnight", "Hornet", "TraitorLord", "Oblobble", "LobsterLancer", "GhostAladar",
                "GhostXero", "GhostHu", "GhostMarmu", "GhostNoEyes", "GhostMarkoth", "GhostGalien", "WhiteDefender",
                "GreyPrince", "HollowKnight", "FinalBoss", "Grimm", "NightmareGrimm", "PaleLurker", "NailBros",
                "Paintmaster", "Nailsage", "HollowKnightPrime",
            };

            private static readonly HashSet<string> BossFlags = new HashSet<string>
            {
                "falseKnightDreamDefeated", "mageLordDreamDefeated", "infectedKnightDreamDefeated",
                "hornetOutskirtsDefeated", "defeatedMegaBeamMiner2",
            };

            private static readonly HashSet<string> AbilityBools = new HashSet<string>
            {
                "hasDash", "hasWalljump", "hasSuperDash", "hasShadowDash", "hasAcidArmour", "hasDoubleJump",
                "hasDreamNail", "dreamNailUpgraded", "hasDreamGate", "hasNailArt", "hasCyclone", "hasDashSlash",
                "hasUpwardSlash", "hasSpell", "hasKingsBrand",
            };

            private static readonly HashSet<string> AbilityInts = new HashSet<string> { "fireballLevel", "quakeLevel", "screamLevel" };

            private static readonly Dictionary<string, string> ItemBools = new Dictionary<string, string>
            {
                ["hasLantern"] = "key", ["hasTramPass"] = "key", ["hasCityKey"] = "key", ["hasSlykey"] = "key",
                ["hasWhiteKey"] = "key", ["hasMenderKey"] = "key", ["hasWaterwaysKey"] = "key", ["hasSpaKey"] = "key",
                ["hasLoveKey"] = "key", ["hasMap"] = "other", ["hasQuill"] = "other", ["hasJournal"] = "other",
                ["hasXunFlower"] = "other", ["hasGodfinder"] = "other",
            };

            private static readonly Dictionary<string, string> ItemInts = new Dictionary<string, string>
            {
                ["heartPieces"] = "mask_shard", ["maxHealthBase"] = "mask", ["vesselFragments"] = "vessel_fragment",
                ["MPReserveMax"] = "vessel", ["nailSmithUpgrades"] = "nail", ["simpleKeys"] = "key",
                ["trinket1"] = "relic", ["trinket2"] = "relic", ["trinket3"] = "relic", ["trinket4"] = "relic",
                ["ore"] = "pale_ore", ["rancidEggs"] = "rancid_egg", ["grubsCollected"] = "grub",
                ["charmSlots"] = "notch", ["guardiansDefeated"] = "dreamer",
            };

            public static void OnBoolTrue(string name)
            {
                if (name.StartsWith("killed", StringComparison.Ordinal) && BossJournal.Contains(name.Substring(6)))
                    Emit("boss_defeated", Json.Obj().S("field", name).S("boss", name.Substring(6)).End());
                else if (BossFlags.Contains(name))
                    Emit("boss_defeated", Json.Obj().S("field", name).S("boss", name).End());
                else if (AbilityBools.Contains(name))
                    Emit("ability_gained", Json.Obj().S("field", name).I("old", 0).I("new", 1).End());
                else if (name.StartsWith("gotCharm_", StringComparison.Ordinal))
                    Emit("item_got", Json.Obj().S("field", name).S("kind", "charm").I("old", 0).I("new", 1).End());
                else if (ItemBools.TryGetValue(name, out var kind))
                    Emit("item_got", Json.Obj().S("field", name).S("kind", kind).I("old", 0).I("new", 1).End());
            }

            public static void OnIntUp(string name, int o, int n)
            {
                if (AbilityInts.Contains(name))
                    Emit("ability_gained", Json.Obj().S("field", name).I("old", o).I("new", n).End());
                else if (ItemInts.TryGetValue(name, out var kind))
                    Emit("item_got", Json.Obj().S("field", name).S("kind", kind).I("old", o).I("new", n).End());
            }

            private static void Emit(string type, string payload)
            {
                var hc = HeroController.UnsafeInstance;
                if (hc != null)
                {
                    // Posición del jugador al conseguirlo: une el evento con la sala y el catálogo.
                    var pos = hc.transform.position;
                    payload = payload.Substring(0, payload.Length - 1)
                              + string.Format(System.Globalization.CultureInfo.InvariantCulture, ",\"x\":{0:0.##},\"y\":{1:0.##}}}", pos.x, pos.y);
                }
                SessionManager.Emit(type, payload);
            }
        }
    }
}
