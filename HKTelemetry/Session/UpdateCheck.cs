using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using HKTelemetry.Util;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Una vez por arranque, en segundo plano: ¿hay una release más nueva del mod en GitHub?
    /// Si la hay, la línea del menú principal lo dice (los jugadores no tienen otra forma de enterarse).
    /// Solo lee la etiqueta de la última release pública; no descarga ni instala nada.
    /// </summary>
    internal static class UpdateCheck
    {
        public const string Repo = "Alexdemaes1/HKTelemetry";
        public const string DownloadUrl = "github.com/" + Repo;

        /// <summary>Versión nueva disponible ("1.1.0"), o null.</summary>
        public static volatile string NewerVersion;

        private static int _started;

        public static void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            var t = new Thread(() =>
            {
                try
                {
                    Thread.Sleep(20000); // que el juego termine de arrancar
                    string latest = FetchLatestTag();
                    if (latest != null && IsNewer(latest, HKTelemetry.VersionString))
                    {
                        NewerVersion = latest;
                        Safe.Info($"[HKTelemetry] Hay una versión nueva del mod: {latest} (instalada {HKTelemetry.VersionString})");
                    }
                }
                catch (Exception e) { Safe.Info("[HKTelemetry] No se pudo comprobar si hay versión nueva: " + e.Message); }
            }) { IsBackground = true, Name = "HKTelemetry.UpdateCheck", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        private static string FetchLatestTag()
        {
            var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/" + Repo + "/releases/latest");
            req.UserAgent = "HKTelemetry/" + HKTelemetry.VersionString; // GitHub exige User-Agent
            req.Accept = "application/vnd.github+json";
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                var m = Regex.Match(sr.ReadToEnd(), "\"tag_name\"\\s*:\\s*\"v?([0-9]+(?:\\.[0-9]+){1,3})\"");
                return m.Success ? m.Groups[1].Value : null;
            }
        }

        internal static bool IsNewer(string candidate, string current)
        {
            try { return new Version(Pad(candidate)) > new Version(Pad(current)); }
            catch { return false; }
        }

        private static string Pad(string v) => v.Split('.').Length == 2 ? v + ".0" : v;
    }
}
