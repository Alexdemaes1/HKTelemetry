using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Experiencia previa del jugador, calculada sin preguntarle (docs/schema.md §3.2):
    /// - Horas totales de Steam: localconfig.vdf de Steam (solo el campo Playtime de la app 367520).
    /// - Logros desbloqueados: la lista de logros del propio juego consultada a la plataforma
    ///   (Steam/GOG) con Platform.Current.IsAchievementUnlocked. Solo lectura.
    /// - Partidas guardadas: GameManager.GetSaveStatsForSlot (lo mismo que usa la pantalla de slots).
    /// Todo es crudo; "horas" y "se lo ha pasado" se derivan en Python.
    /// </summary>
    internal sealed class Experience
    {
        public const string SteamAppId = "367520";

        private readonly object _lock = new object();
        private readonly Dictionary<string, object> _data = new Dictionary<string, object>();
        private readonly List<Dictionary<string, object>> _saves = new List<Dictionary<string, object>>();

        private static Experience _launch;

        /// <summary>
        /// La experiencia es «antes del estudio»: basta con calcularla una vez por arranque del juego.
        /// Así la lectura y descifrado de los saves (hilo principal) ocurre una sola vez, durante el
        /// fundido de la primera carga.
        /// </summary>
        public static Experience ForLaunch(GameManager gm)
        {
            if (_launch == null)
            {
                _launch = new Experience();
                _launch.Collect(gm);
            }
            return _launch;
        }

        /// <summary>Copia segura para serializar en session.json.</summary>
        public Dictionary<string, object> Snapshot()
        {
            lock (_lock)
            {
                var copy = new Dictionary<string, object>(_data) { ["saves"] = new List<Dictionary<string, object>>(_saves) };
                return copy;
            }
        }

        private void Set(string k, object v) { lock (_lock) _data[k] = v; }

        /// <summary>Lanza la recogida. Llamar desde el hilo principal.</summary>
        public void Collect(GameManager gm)
        {
            Safe.Run("experience.achievements", () => CollectAchievements(gm));
            Safe.Run("experience.saves", () => CollectSaves(gm));

            string dataPath = Application.dataPath; // solo hilo principal
            ThreadPool.QueueUserWorkItem(_ => Safe.Run("experience.steam", () => CollectSteamPlaytime(dataPath)));
        }

        private void CollectAchievements(GameManager gm)
        {
            var handler = gm != null ? gm.achievementHandler : null;
            if (handler == null || handler.achievementsList == null || Platform.Current == null)
            {
                Set("achievements_source", "unavailable");
                return;
            }

            var unlocked = new List<string>();
            int known = 0, total = 0;
            foreach (var a in handler.achievementsList.achievements)
            {
                if (a == null || string.IsNullOrEmpty(a.key)) continue;
                total++;
                bool? got = Platform.Current.IsAchievementUnlocked(a.key);
                if (got.HasValue) known++;
                if (got == true) unlocked.Add(a.key);
            }

            Set("achievements_total", total);
            // Si la plataforma no responde a ninguno (sin Steam/GOG inicializado), null = desconocido.
            Set("achievements_unlocked", known > 0 ? unlocked : null);
            Set("achievements_source", known > 0 ? "platform" : "unavailable");
        }

        private void CollectSaves(GameManager gm)
        {
            if (gm == null) return;
            string dir = Application.persistentDataPath;
            for (int slot = 1; slot <= 4; slot++)
            {
                int s = slot;
                // Solo slots con fichero: si no, el juego lanza y registra FileNotFoundException en Player.log.
                if (!File.Exists(Path.Combine(dir, $"user{s}.dat"))) continue;
                gm.GetSaveStatsForSlot(s, stats => Safe.Run("experience.saves.cb", () =>
                {
                    if (stats == null) return;
                    lock (_lock)
                    {
                        _saves.Add(new Dictionary<string, object>
                        {
                            ["slot"] = s,
                            ["play_time_s"] = stats.playTime,
                            ["completion"] = stats.completionPercentage,
                            ["unlocked_completion_rate"] = stats.unlockedCompletionRate,
                            ["permadeath_mode"] = stats.permadeathMode,
                            ["boss_rush_mode"] = stats.bossRushMode,
                            ["map_zone"] = stats.mapZone.ToString(),
                            ["max_health"] = stats.maxHealth,
                        });
                    }
                }));
            }
        }

        // ---------- Steam (hilo de fondo: solo E/S de ficheros) ----------

        private void CollectSteamPlaytime(string dataPath)
        {
            string root = FindSteamRoot(dataPath);
            if (root == null) { Set("steam_playtime_min", null); return; }

            string userdata = Path.Combine(root, "userdata");
            if (!Directory.Exists(userdata)) { Set("steam_playtime_min", null); return; }

            // Cuenta que está jugando: HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser (id de cuenta,
            // el mismo número que la carpeta userdata/<id>). Si no se puede leer, el máximo entre cuentas.
            uint active = 0;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object v = key?.GetValue("ActiveUser");
                    if (v is int i) active = unchecked((uint)i);
                }
            }
            catch { /* sin registro (Linux/Proton) o sin permiso */ }
            // Sin registro (Linux, Steam Deck, Mac): la cuenta que ya identificó Identity (loginusers.vdf).
            if (active == 0) active = Identity.SteamAccountId;

            int? best = null, mine = null;
            int accounts = 0;
            foreach (string user in Directory.GetDirectories(userdata))
            {
                string vdf = Path.Combine(Path.Combine(user, "config"), "localconfig.vdf");
                if (!File.Exists(vdf)) continue;
                int? minutes = ParsePlaytime(File.ReadAllText(vdf));
                if (!minutes.HasValue) continue;
                accounts++;
                if (!best.HasValue || minutes > best) best = minutes;
                if (active != 0 && Path.GetFileName(user) == active.ToString()) mine = minutes;
            }
            // No se guardan ids de cuenta: solo los minutos, de dónde salen y cuántas cuentas tienen el juego.
            Set("steam_playtime_min", mine ?? best);
            Set("steam_playtime_source", mine.HasValue ? "active_user" : best.HasValue ? "max_fallback" : null);
            Set("steam_accounts", accounts);
        }

        /// <summary>
        /// Raíz de Steam (la que tiene userdata/): subiendo desde la carpeta del juego hasta steamapps o,
        /// si el juego está en otra biblioteca, las rutas por defecto de Windows, Linux/Steam Deck y Mac.
        /// </summary>
        private static string FindSteamRoot(string dataPath)
        {
            foreach (string candidate in SteamPaths.Candidates(dataPath))
            {
                try { if (Directory.Exists(Path.Combine(candidate, "userdata"))) return candidate; }
                catch { }
            }
            return null;
        }

        private static readonly Regex PlaytimeRx = new Regex("\"Playtime\"\\s+\"(\\d+)\"", RegexOptions.Compiled);

        /// <summary>Busca bloques "367520" { ... } y devuelve su "Playtime" (minutos).</summary>
        internal static int? ParsePlaytime(string vdf)
        {
            string key = "\"" + SteamAppId + "\"";
            int idx = 0;
            while ((idx = vdf.IndexOf(key, idx, StringComparison.Ordinal)) >= 0)
            {
                int brace = idx + key.Length;
                while (brace < vdf.Length && char.IsWhiteSpace(vdf[brace])) brace++;
                if (brace < vdf.Length && vdf[brace] == '{')
                {
                    int depth = 0, end = brace;
                    for (; end < vdf.Length; end++)
                    {
                        if (vdf[end] == '{') depth++;
                        else if (vdf[end] == '}' && --depth == 0) break;
                    }
                    var m = PlaytimeRx.Match(vdf.Substring(brace, Math.Min(end, vdf.Length - 1) - brace + 1));
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int minutes)) return minutes;
                }
                idx += key.Length;
            }
            return null;
        }
    }
}
