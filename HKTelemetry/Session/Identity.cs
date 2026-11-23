using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Quién juega, sin preguntar nada (docs/schema.md §3):
    /// - Ajuste PlayerId = "auto" (el del kit): la cuenta de Steam que está jugando. Identificador estable
    ///   "S" + id de cuenta (el nombre de Steam cambia; el id no) y, aparte, nombre de Steam e id64.
    ///   Se leen de ficheros de Steam (registro ActiveUser y config/loginusers.vdf), nunca de la API nativa
    ///   de Steamworks: llamarla en mal momento puede cerrar el juego. Nunca se lee AccountName (el
    ///   usuario con el que se inicia sesión).
    /// - Sin Steam: "U" + 8 caracteres aleatorios, guardado en los ajustes (InstallId).
    /// - Cualquier otro valor de PlayerId (p. ej. "P00" en desarrollo) se usa tal cual.
    /// La anonimización se hace después, al limpiar los datos.
    /// </summary>
    internal static class Identity
    {
        private const ulong SteamId64Base = 76561197960265728UL;

        public static string PlayerId { get; private set; } = "P00";
        public static string SteamName { get; private set; }
        public static string SteamId64 { get; private set; }
        public static uint SteamAccountId { get; private set; }
        public static string Source { get; private set; } = "default";

        /// <summary>Para la línea del menú: el nombre de Steam si se conoce.</summary>
        public static string DisplayName => Source != "settings" && !string.IsNullOrEmpty(SteamName) ? SteamName : PlayerId;

        /// <summary>Al iniciar el mod (hilo principal; registro + un fichero de pocos KB).</summary>
        public static void Resolve(GlobalSettings s, Action save)
        {
            try { ReadSteam(); }
            catch (Exception e) { Safe.Report("identity.steam", e); }

            string configured = (s.PlayerId ?? "").Trim();
            if (configured.Length > 0 && !configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                PlayerId = Sanitize(configured);
                Source = "settings";
            }
            else if (SteamAccountId != 0)
            {
                PlayerId = "S" + SteamAccountId;
            }
            else
            {
                if (!Regex.IsMatch(s.InstallId ?? "", "^[0-9a-f]{8}$"))
                {
                    s.InstallId = Guid.NewGuid().ToString("N").Substring(0, 8);
                    try { save?.Invoke(); } catch (Exception e) { Safe.Report("identity.save", e); }
                }
                PlayerId = "U" + s.InstallId;
                Source = "install_id";
            }
        }

        private static void ReadSteam()
        {
            string root = SteamRoot();
            uint active = 0;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess"))
                {
                    object v = key?.GetValue("ActiveUser");
                    if (v is int i) active = unchecked((uint)i);
                }
            }
            catch { /* sin registro (Linux/Proton) */ }

            var users = new List<(ulong id, string persona, bool mostRecent, long ts)>();
            try
            {
                if (root != null)
                {
                    string vdf = Path.Combine(Path.Combine(root, "config"), "loginusers.vdf");
                    if (File.Exists(vdf)) users = ParseLoginUsers(File.ReadAllText(vdf, Encoding.UTF8));
                }
            }
            catch (Exception e) { Safe.Report("identity.vdf", e); } // el id del registro sigue valiendo

            if (active != 0)
            {
                ulong id64 = SteamId64Base + active;
                SteamAccountId = active;
                SteamId64 = id64.ToString();
                foreach (var u in users) if (u.id == id64) SteamName = u.persona;
                Source = "steam_active_user";
                return;
            }
            // Steam no está en marcha (raro: el juego lo exige): la última cuenta que inició sesión.
            (ulong id, string persona, bool mostRecent, long ts) best = default;
            foreach (var u in users)
                if (best.id == 0 || (u.mostRecent && !best.mostRecent) || (u.mostRecent == best.mostRecent && u.ts > best.ts)) best = u;
            if (best.id > SteamId64Base)
            {
                SteamAccountId = (uint)(best.id - SteamId64Base);
                SteamId64 = best.id.ToString();
                SteamName = best.persona;
                Source = "steam_login_users";
            }
        }

        // El cuerpo del bloque salta las cadenas entre comillas: un nombre como "{TAG} Bob" no lo corta.
        private static readonly Regex UserBlock =
            new Regex("\"(\\d{17})\"\\s*\\{((?:\"(?:[^\"\\\\]|\\\\.)*\"|[^{}\"])*)\\}");

        /// <summary>Bloques "id64" { ... } de loginusers.vdf: PersonaName, MostRecent y timestamp (nada más).</summary>
        internal static List<(ulong id, string persona, bool mostRecent, long ts)> ParseLoginUsers(string vdf)
        {
            var list = new List<(ulong, string, bool, long)>();
            foreach (Match m in UserBlock.Matches(vdf))
            {
                if (!ulong.TryParse(m.Groups[1].Value, out ulong id)) continue;
                string body = m.Groups[2].Value;
                string persona = Value(body, "PersonaName");
                bool recent = Value(body, "MostRecent") == "1";
                long.TryParse(Value(body, "timestamp"), out long ts);
                list.Add((id, persona, recent, ts));
            }
            return list;
        }

        private static string Value(string body, string key)
        {
            var m = Regex.Match(body, "\"" + key + "\"\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Replace("\\\"", "\"").Replace("\\\\", "\\") : null;
        }

        private static string SteamRoot()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string p = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', Path.DirectorySeparatorChar);
                }
            }
            catch { }
            // Linux, Steam Deck, Proton y Mac (sin registro): subir desde el juego o rutas por defecto.
            string dataPath = null;
            try { dataPath = GameDataPath(); } catch { }
            foreach (string c in SteamPaths.Candidates(dataPath))
            {
                try { if (File.Exists(Path.Combine(Path.Combine(c, "config"), "loginusers.vdf"))) return c; }
                catch { }
            }
            return null;
        }

        // Aparte: así la lectura del registro no depende de Unity (y se puede probar fuera del juego).
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static string GameDataPath() => Application.dataPath;

        /// <summary>Solo letras, números y '_' (va en nombres de carpeta y en el receptor), máx. 16.</summary>
        private static string Sanitize(string id)
        {
            var sb = new StringBuilder();
            foreach (char c in id) if (c < 128 && (char.IsLetterOrDigit(c) || c == '_')) sb.Append(c);
            string r = sb.ToString();
            if (r.Length > 16) r = r.Substring(0, 16);
            return r.Length > 0 ? r : "P00";
        }

        public static Dictionary<string, object> Snapshot() => new Dictionary<string, object>
        {
            ["player_id"] = PlayerId,
            ["source"] = Source,
            ["steam_name"] = SteamName,
            ["steam_id64"] = SteamId64,
            ["steam_account_id"] = SteamAccountId == 0 ? (object)null : SteamAccountId,
        };
    }
}
