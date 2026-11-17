using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HKTelemetry.Util;
using HKTelemetry.Writing;
using Newtonsoft.Json.Linq;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Envío automático de las sesiones cerradas al receptor de Google Drive (server/Code.gs,
    /// docs/upload-setup.md). Reglas:
    /// - Hilo propio en segundo plano. Nunca en el hilo del juego.
    /// - Solo mientras NO hay sesión abierta (menú principal): jugar nunca compite con la subida.
    ///   Si empieza una sesión, se termina el trozo en curso y se para.
    /// - Solo sesiones cerradas y ya comprimidas (sin ficheros planos ni .tmp).
    /// - Zip determinista (sin compresión: los .gz ya lo están) partido en trozos de 5 MB; el
    ///   receptor descarta trozos repetidos (mismo nombre y sha256), así que reintentar es seguro.
    /// - Hecho → upload.json en la carpeta de la sesión. Fallo → se reintenta más tarde; los datos
    ///   siguen en el PC y 2_ENVIAR_DATOS.bat sigue sirviendo.
    /// </summary>
    internal static class Uploader
    {
        public const string MarkerFile = "upload.json";
        internal static int PartBytes = 5 * 1024 * 1024;   // modificable en pruebas
        private const int Attempts = 3;

        private static Thread _thread;
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static string _root;

        // Estado para el HUD y el log (escrito solo por el hilo de subida).
        public static volatile int PendingCount = -1;   // -1 = aún no se ha mirado
        public static volatile int UploadedThisLaunch;
        public static volatile string LastError;
        public static volatile bool Busy;
        public static volatile bool Preparing;          // hay una sesión cerrándose (comprimiendo)

        private static volatile bool _cancel;
        private static volatile HttpWebRequest _current;
        private static long _fastUntilTicks;            // sondeo rápido solo un rato después de un aviso

        public static bool Configured => !string.IsNullOrEmpty(HKTelemetry.Settings.UploadUrl);
        public static bool Enabled => Configured && HKTelemetry.Settings.AutoUpload;

        /// <summary>Al iniciar el mod. Sin URL configurada no arranca nada.</summary>
        public static void Start(string root)
        {
            if (_thread != null) return;
            _root = root;
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { /* ya activo */ }
            _thread = new Thread(Loop) { IsBackground = true, Name = "HKTelemetry.Uploader", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        /// <summary>Al cerrar una sesión (y desde el menú): mirar si hay algo que enviar.</summary>
        public static void Poke()
        {
            Interlocked.Exchange(ref _fastUntilTicks, DateTime.UtcNow.AddMinutes(2).Ticks);
            Wake.Set();
        }

        /// <summary>Al empezar a jugar: cortar la subida en curso (se reanuda en el menú).</summary>
        public static void Cancel()
        {
            _cancel = true;
            try { _current?.Abort(); } catch { }
        }

        private static void Loop()
        {
            // Dejar que el juego termine de arrancar (y la recuperación de sesiones cortadas).
            Thread.Sleep(15000);
            int retryMs = 60_000;
            while (true)
            {
                bool again = false;
                try { again = Pass(); }
                catch (Exception e) { LastError = e.Message; Safe.Info("[HKTelemetry] Envío: " + e.Message); }
                // Error → espera creciente (manda sobre todo lo demás); sesión cerrándose → mirar en 5 s,
                // pero solo durante 2 min después de un aviso; si no, cada 5 min.
                bool fast = again && DateTime.UtcNow.Ticks < Interlocked.Read(ref _fastUntilTicks);
                int wait = LastError != null ? retryMs : (fast ? 5000 : 300_000);
                retryMs = LastError != null ? Math.Min(retryMs * 2, 30 * 60_000) : 60_000;
                Wake.WaitOne(wait);
            }
        }

        /// <summary>Una pasada. Devuelve true si hay sesiones que todavía no se pueden enviar (escribiéndose).</summary>
        private static bool Pass()
        {
            // Mientras se juega no se mira nada (ni siquiera la carpeta): cero trabajo durante la partida.
            if (!Enabled || _root == null || SessionManager.Active || !Directory.Exists(_root)) return false;
            _cancel = false;
            var pending = FindPending(_root, out bool notReady, SessionManager.CurrentDir);
            PendingCount = pending.Count;
            Preparing = notReady;
            if (pending.Count == 0) { LastError = null; return notReady; }
            string error = null;
            foreach (string dir in pending)
            {
                if (SessionManager.Active || _cancel || !Enabled) break;
                Busy = true;
                try
                {
                    UploadDir(dir, HKTelemetry.Settings.UploadUrl, () => _cancel || SessionManager.Active);
                    UploadedThisLaunch++;
                    PendingCount = Math.Max(0, PendingCount - 1);
                    Safe.Info($"[HKTelemetry] Sesión enviada: {Path.GetFileName(dir)}");
                }
                catch (OperationCanceledException) { break; }
                catch (Exception e)
                {
                    if (_cancel || SessionManager.Active) break; // cortada al empezar a jugar: no es un error
                    error = e.Message;
                    // Al log, no a Safe.Report: un fallo de red no es un fallo de grabación (hook_errors).
                    Safe.Info("[HKTelemetry] Envío fallido (se reintentará): " + e.Message);
                    break; // sin red o receptor caído: no insistir con las demás ahora
                }
                finally { Busy = false; }
            }
            LastError = error;
            return notReady;
        }

        /// <summary>Sesiones cerradas, comprimidas y sin upload.json, de la más antigua a la más nueva.</summary>
        internal static List<string> FindPending(string root, out bool notReady, string activeDir = null)
        {
            notReady = false;
            var list = new List<string>();
            var dirs = Directory.GetDirectories(root);
            Array.Sort(dirs, StringComparer.Ordinal);
            foreach (string dir in dirs)
            {
                if (activeDir != null && string.Equals(dir.TrimEnd('\\', '/'), activeDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(Path.Combine(dir, MarkerFile))) continue;
                string sj = Path.Combine(dir, "session.json");
                if (!File.Exists(sj)) continue;
                bool busy = Directory.GetFiles(dir, "*.tmp").Length > 0;
                foreach (string name in TelemetryWriter.DataFiles) busy |= File.Exists(Path.Combine(dir, name));
                bool closed;
                try { var j = JObject.Parse(ReadText(sj)); closed = j["end"] != null && j["end"].Type == JTokenType.Object; }
                catch { closed = false; }
                if (busy || !closed) { if (closed) notReady = true; continue; }
                list.Add(dir);
            }
            return list;
        }

        /// <summary>Empaqueta y sube una sesión; escribe upload.json al terminar. Lanza si algo falla.</summary>
        internal static void UploadDir(string dir, string url, Func<bool> cancel)
        {
            string session = Path.GetFileName(dir);
            var meta = JObject.Parse(ReadText(Path.Combine(dir, "session.json")));
            string player = (string)meta["player_id"] ?? "";

            string tmpDir = Path.Combine(Path.GetDirectoryName(dir), "_upload_tmp");
            Directory.CreateDirectory(tmpDir);
            string zipPath = Path.Combine(tmpDir, session + ".zip");
            try
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                BuildZip(dir, zipPath);
                long len = new FileInfo(zipPath).Length;
                int parts = (int)Math.Max(1, (len + PartBytes - 1) / PartBytes);
                string zipSha;
                using (var fs = File.OpenRead(zipPath))
                using (var sha = SHA256.Create())
                {
                    zipSha = Hex(sha.ComputeHash(fs));
                    fs.Position = 0;
                    for (int part = 1; part <= parts; part++)
                    {
                        if (cancel != null && cancel()) throw new OperationCanceledException();
                        int n = (int)Math.Min(PartBytes, len - (long)(part - 1) * PartBytes);
                        var buf = new byte[n];
                        ReadExactly(fs, buf, n);
                        SendPart(url, player, session, part, parts, buf, cancel);
                    }
                }
                var marker = new JObject
                {
                    ["utc"] = DateTime.UtcNow.ToString("o"),
                    ["parts"] = parts,
                    ["bytes"] = len,
                    ["zip_sha256"] = zipSha,
                };
                TelemetryWriter.WriteAtomic(Path.Combine(dir, MarkerFile), marker.ToString());
            }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }

        /// <summary>
        /// Zip determinista: ficheros en orden, con su fecha de modificación y el mismo nivel de compresión.
        /// Así un reintento genera exactamente los mismos bytes y los trozos ya subidos siguen valiendo.
        /// (En el Mono del juego NoCompression se ignora y se usa Deflate; sigue siendo determinista dentro
        /// del mismo runtime, y los .gz apenas cambian de tamaño.)
        /// </summary>
        internal static void BuildZip(string dir, string zipPath)
        {
            var files = Directory.GetFiles(dir);
            Array.Sort(files, StringComparer.Ordinal);
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (string f in files)
                {
                    string name = Path.GetFileName(f);
                    if (name == MarkerFile || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                    var entry = zip.CreateEntry(Path.GetFileName(dir) + "/" + name, CompressionLevel.NoCompression);
                    entry.LastWriteTime = new DateTimeOffset(File.GetLastWriteTimeUtc(f).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);
                    using (var src = OpenShared(f))
                    using (var dst = entry.Open())
                        src.CopyTo(dst);
                }
            }
        }

        private static void SendPart(string url, string player, string session, int part, int parts, byte[] data, Func<bool> cancel)
        {
            string sha;
            using (var h = SHA256.Create()) sha = Hex(h.ComputeHash(data));
            string full = url + (url.Contains("?") ? "&" : "?") +
                          "v=1&player=" + Uri.EscapeDataString(player) +
                          "&session=" + Uri.EscapeDataString(session) +
                          "&part=" + part + "&parts=" + parts + "&sha256=" + sha;
            byte[] body = Encoding.ASCII.GetBytes(Convert.ToBase64String(data));

            Exception last = null;
            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                try
                {
                    var req = (HttpWebRequest)WebRequest.Create(full);
                    _current = req;
                    req.Method = "POST";
                    req.ContentType = "text/plain";
                    req.ContentLength = body.Length;
                    req.Timeout = 120_000;
                    req.ReadWriteTimeout = 120_000;
                    req.AllowAutoRedirect = true;   // Apps Script responde con 302 a script.googleusercontent.com
                    req.UserAgent = "HKTelemetry/" + HKTelemetry.VersionString;
                    using (var rs = req.GetRequestStream()) rs.Write(body, 0, body.Length);
                    string text;
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                        text = sr.ReadToEnd();
                    JObject j;
                    try { j = JObject.Parse(text); }
                    catch { throw new IOException("respuesta no válida del receptor: " + Trim(text)); }
                    if (j["ok"] != null && (bool)j["ok"]) return;
                    string err = (string)j["error"] ?? "?";
                    // Errores de formato: reintentar no sirve.
                    if (!err.StartsWith("server", StringComparison.Ordinal)) throw new InvalidDataException("receptor: " + err);
                    last = new IOException("receptor: " + err);
                }
                catch (InvalidDataException) { throw; }
                catch (Exception e) { last = e; }
                finally { _current = null; }
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (attempt < Attempts) Wake.WaitOne(attempt * 5000); // se despierta antes si llega un aviso
            }
            throw new IOException($"no se pudo enviar el trozo {part}/{parts}: {last?.Message}", last);
        }

        private static void ReadExactly(Stream s, byte[] buf, int n)
        {
            int off = 0;
            while (off < n)
            {
                int r = s.Read(buf, off, n - off);
                if (r <= 0) throw new EndOfStreamException();
                off += r;
            }
        }

        private static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private static string Trim(string s) => s == null ? "" : (s.Length > 200 ? s.Substring(0, 200) : s);

        // Lecturas que no impiden al escritor sustituir o borrar el fichero (WriteAtomic usa File.Replace).
        private static FileStream OpenShared(string path) =>
            new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        private static string ReadText(string path)
        {
            using (var fs = OpenShared(path))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
                return sr.ReadToEnd();
        }

        /// <summary>Texto corto para la línea del menú principal (null = nada que decir).</summary>
        public static string StatusText()
        {
            if (!Configured) return null;
            if (!HKTelemetry.Settings.AutoUpload) return "envío automático desactivado";
            if (Busy) return "enviando datos...";
            if (Preparing) return "preparando datos...";
            int p = PendingCount;
            if (p < 0) return null;
            if (p == 0) return "datos enviados";
            if (p == 1) return "1 sesión por enviar";
            // Sin asignar un string nuevo en cada frame del menú: solo cuando cambia el número.
            if (p != _lastPending || _lastPendingText == null)
            {
                _lastPending = p;
                _lastPendingText = p + " sesiones por enviar";
            }
            return _lastPendingText;
        }

        private static int _lastPending = -1;
        private static string _lastPendingText;
    }
}
