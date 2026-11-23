using System;
using System.Collections.Generic;
using System.IO;

namespace HKTelemetry.Util
{
    /// <summary>
    /// Carpetas donde puede estar Steam, de la más probable a la menos (sin registro de Windows):
    /// 1. Subiendo desde la carpeta de datos del juego hasta «steamapps» (Windows, Linux, Proton y Mac:
    ///    en Mac Application.dataPath es .../steamapps/common/Hollow Knight/hollow_knight.app/Contents).
    /// 2. Rutas por defecto de cada sistema (juego en otra biblioteca, p. ej. la tarjeta SD de la Steam Deck).
    /// Solo devuelve rutas; quien llama comprueba qué fichero de Steam necesita.
    /// </summary>
    internal static class SteamPaths
    {
        public static IEnumerable<string> Candidates(string dataPath)
        {
            string fromGame = null;
            try
            {
                var dir = string.IsNullOrEmpty(dataPath) ? null : new DirectoryInfo(dataPath);
                for (int i = 0; dir != null && i < 6; i++, dir = dir.Parent)
                {
                    if (dir.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                    {
                        fromGame = dir.Parent?.FullName;
                        break;
                    }
                }
            }
            catch { /* ruta rara: seguir con las de por defecto */ }
            if (fromGame != null) yield return fromGame;

            foreach (var env in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string pf = null;
                try { pf = Environment.GetEnvironmentVariable(env); } catch { }
                if (!string.IsNullOrEmpty(pf)) yield return Path.Combine(pf, "Steam");
            }

            string home = null;
            try { home = Environment.GetEnvironmentVariable("HOME"); } catch { }
            if (!string.IsNullOrEmpty(home) && home.StartsWith("/"))
            {
                yield return Path.Combine(home, ".local/share/Steam");                            // Linux / Steam Deck
                yield return Path.Combine(home, ".steam/steam");                                  // Linux (enlace)
                yield return Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam"); // Flatpak
                yield return Path.Combine(home, "Library/Application Support/Steam");             // macOS
            }
        }
    }
}
