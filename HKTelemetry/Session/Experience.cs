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
            for (int slot = 1; slot <= 4; slot++)
            {
                int s = slot;
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

            int? best = null;
            foreach (string user in Directory.GetDirectories(userdata))
            {
                string vdf = Path.Combine(Path.Combine(user, "config"), "localconfig.vdf");
                if (!File.Exists(vdf)) continue;
                int? minutes = ParsePlaytime(File.ReadAllText(vdf));
                if (minutes.HasValue && (!best.HasValue || minutes > best)) best = minutes;
            }
            Set("steam_playtime_min", best);
        }

        /// <summary>
        /// Raíz de Steam: el juego está en &lt;Steam&gt;/steamapps/common/Hollow Knight/hollow_knight_Data.
        /// Si el juego está en otra biblioteca, se prueba la ruta por defecto de Steam.
        /// </summary>
        private static string FindSteamRoot(string dataPath)
        {
            try
            {
                var dir = new DirectoryInfo(dataPath); // .../hollow_knight_Data
                var steamapps = dir.Parent?.Parent?.Parent; // .../steamapps
                if (steamapps != null && steamapps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    string candidate = steamapps.Parent.FullName;
                    if (Directory.Exists(Path.Combine(candidate, "userdata"))) return candidate;
                }
            }
            catch { /* ruta rara: probar la de por defecto */ }

            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string pf = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(pf)) continue;
                string candidate = Path.Combine(pf, "Steam");
                if (Directory.Exists(Path.Combine(candidate, "userdata"))) return candidate;
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
