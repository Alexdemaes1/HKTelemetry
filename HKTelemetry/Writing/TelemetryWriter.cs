using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using HKTelemetry.Util;
using Newtonsoft.Json.Linq;

namespace HKTelemetry.Writing
{
    /// <summary>
    /// Escritura de una sesión en un hilo dedicado (docs/plan.md, decisión 8).
    /// - El hilo del juego solo encola líneas (Enqueue) o trabajos (Job). Nunca toca disco.
    /// - El hilo de escritura vuelca cada 500 ms (o antes si la cola crece) en texto plano, en append.
    ///   Si el juego se cierra de golpe se pierde como mucho el último volcado y lo escrito sigue legible.
    /// - Al cerrar la sesión comprime cada fichero a .gz y borra el plano.
    /// - Nada se pierde en silencio: descartes y errores quedan en contadores (session.json y HUD).
    /// </summary>
    internal sealed class TelemetryWriter
    {
        public const string Events = "events.jsonl";
        public const string Frames = "frames.csv";
        public const string Inputs = "inputs.csv";

        /// <summary>Ficheros de datos que se comprimen al cerrar (y en la recuperación).</summary>
        public static readonly string[] DataFiles = { Events, Frames, Inputs };

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private sealed class Sink
        {
            public string Path;
            public string Header;
            public int MaxQueue;
            public readonly ConcurrentQueue<string> Queue = new ConcurrentQueue<string>();
            public int QueueCount;
            public StreamWriter Writer;
            public long Lines;
            public long Dropped;
            public long Bytes;   // tamaño del fichero plano, actualizado en el hilo de escritura
        }

        private readonly Dictionary<string, Sink> _sinks = new Dictionary<string, Sink>();
        private readonly ConcurrentQueue<Action> _jobs = new ConcurrentQueue<Action>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _compressOnClose;
        private int _writeErrors;

        public string Dir { get; }
        public bool Finished { get; private set; }
        public int WriteErrors => Volatile.Read(ref _writeErrors);
        public string LastError { get; private set; }

        /// <param name="sinks">nombre de fichero → cabecera CSV (null si no tiene) y tamaño máximo de cola.</param>
        public TelemetryWriter(string dir, IEnumerable<(string name, string header, int maxQueue)> sinks)
        {
            Dir = dir;
            Directory.CreateDirectory(dir);
            foreach (var (name, header, maxQueue) in sinks)
                _sinks[name] = new Sink { Path = Path.Combine(dir, name), Header = header, MaxQueue = maxQueue };

            _thread = new Thread(Loop) { IsBackground = true, Name = "HKTelemetry.Writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public long Lines(string sink) => _sinks.TryGetValue(sink, out var s) ? Interlocked.Read(ref s.Lines) : 0;
        /// <summary>Bytes escritos en el fichero plano (sin leer el disco desde el hilo del juego).</summary>
        public long Bytes(string sink) => _sinks.TryGetValue(sink, out var s) ? Interlocked.Read(ref s.Bytes) : 0;

        public long Dropped(string sink) => _sinks.TryGetValue(sink, out var s) ? Interlocked.Read(ref s.Dropped) : 0;

        public long TotalDropped()
        {
            long n = 0;
            foreach (var s in _sinks.Values) n += Interlocked.Read(ref s.Dropped);
            return n;
        }

        /// <summary>Encola una línea (sin '\n'). Hilo-seguro y sin bloqueo.</summary>
        public void Enqueue(string sink, string line)
        {
            if (_stop || !_sinks.TryGetValue(sink, out var s)) return;
            int n = Interlocked.Increment(ref s.QueueCount);
            if (n > s.MaxQueue)
            {
                Interlocked.Decrement(ref s.QueueCount);
                Interlocked.Increment(ref s.Dropped);
                return;
            }
            s.Queue.Enqueue(line);
            if (n == 4096) _wake.Set(); // volcado anticipado si la cola crece (~64 KB)
        }

        /// <summary>Trabajo de E/S arbitrario en el hilo de escritura (session.json, hash, catálogos...).</summary>
        public void Job(Action job)
        {
            if (_stop) return;
            _jobs.Enqueue(job);
            _wake.Set();
        }

        /// <summary>
        /// Cierra la sesión: vuelca lo pendiente, cierra ficheros y (opcional) comprime.
        /// joinMs = 0 no bloquea al juego; en ApplicationQuit conviene esperar un poco.
        /// </summary>
        public void Stop(bool compress, int joinMs)
        {
            _compressOnClose = compress;
            _stop = true;
            _wake.Set();
            if (joinMs > 0) _thread.Join(joinMs);
        }

        private void Loop()
        {
            while (!_stop)
            {
                _wake.WaitOne(500);
                DrainAll();
                RunJobs();
            }
            DrainAll();
            RunJobs();
            foreach (var s in _sinks.Values) CloseSink(s);
            if (_compressOnClose)
            {
                try { CompressAll(Dir); }
                catch (Exception e) { Fail("compress", e); }
            }
            Finished = true;
        }

        private void DrainAll()
        {
            foreach (var s in _sinks.Values)
            {
                if (Volatile.Read(ref s.QueueCount) == 0) continue;
                try
                {
                    if (s.Writer == null) Open(s);
                    while (s.Queue.TryDequeue(out var line))
                    {
                        Interlocked.Decrement(ref s.QueueCount);
                        s.Writer.Write(line);
                        s.Writer.Write('\n');
                        Interlocked.Increment(ref s.Lines);
                    }
                    s.Writer.Flush();
                    Interlocked.Exchange(ref s.Bytes, s.Writer.BaseStream.Length);
                }
                catch (Exception e)
                {
                    // La línea en curso se pierde: se cuenta. El fichero se reabre en el siguiente ciclo.
                    Interlocked.Increment(ref s.Dropped);
                    Fail(System.IO.Path.GetFileName(s.Path), e);
                    CloseSink(s);
                }
            }
        }

        private static void Open(Sink s)
        {
            bool isNew = !File.Exists(s.Path) || new FileInfo(s.Path).Length == 0;
            var fs = new FileStream(s.Path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536);
            s.Writer = new StreamWriter(fs, Utf8, 65536);
            if (isNew && s.Header != null)
            {
                s.Writer.Write(s.Header);
                s.Writer.Write('\n');
            }
        }

        private static void CloseSink(Sink s)
        {
            try { s.Writer?.Dispose(); } catch { /* ya estaba roto */ }
            s.Writer = null;
        }

        private void RunJobs()
        {
            while (_jobs.TryDequeue(out var job))
            {
                try { job(); }
                catch (Exception e) { Fail("job", e); }
            }
        }

        private void Fail(string where, Exception e)
        {
            Interlocked.Increment(ref _writeErrors);
            LastError = $"{where}: {e.Message}";
            Safe.Report("writer." + where, e);
        }

        // ---------- utilidades estáticas (también para la recuperación) ----------

        /// <summary>Escritura atómica: fichero temporal y sustitución.</summary>
        public static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, Utf8);
            if (File.Exists(path))
            {
                try { File.Replace(tmp, path, null); return; }
                catch { File.Delete(path); }
            }
            File.Move(tmp, path);
        }

        /// <summary>Comprime los ficheros de datos planos de una carpeta de sesión a .gz.</summary>
        public static void CompressAll(string dir)
        {
            foreach (string name in DataFiles)
            {
                string plain = Path.Combine(dir, name);
                if (!File.Exists(plain)) continue;
                string gz = plain + ".gz";
                string tmp = gz + ".tmp";
                using (var input = new FileStream(plain, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var zip = new GZipStream(output, CompressionMode.Compress))
                    input.CopyTo(zip);
                if (File.Exists(gz)) File.Delete(gz);
                File.Move(tmp, gz);
                File.Delete(plain);
            }
        }

        /// <summary>
        /// Recupera sesiones que no se cerraron (crash, Alt+F4 forzado): comprime lo pendiente y
        /// marca end.reason = "recovered". Se ejecuta en segundo plano al arrancar el mod.
        /// </summary>
        public static void RecoverAll(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.GetDirectories(root))
            {
                try
                {
                    foreach (string tmp in Directory.GetFiles(dir, "*.tmp")) File.Delete(tmp);
                    string sessionPath = Path.Combine(dir, "session.json");
                    bool hadPlain = false;
                    foreach (string name in DataFiles) hadPlain |= File.Exists(Path.Combine(dir, name));
                    if (hadPlain) CompressAll(dir);

                    if (!File.Exists(sessionPath)) continue;
                    var json = JObject.Parse(File.ReadAllText(sessionPath, Utf8));
                    if (json["end"] == null || json["end"].Type == JTokenType.Null)
                    {
                        json["end"] = new JObject
                        {
                            ["utc"] = File.GetLastWriteTimeUtc(sessionPath).ToString("o"),
                            ["reason"] = "recovered",
                        };
                        WriteAtomic(sessionPath, json.ToString());
                        Safe.Info($"[HKTelemetry] Sesión recuperada tras cierre inesperado: {Path.GetFileName(dir)}");
                    }
                }
                catch (Exception e)
                {
                    Safe.Report("writer.recover", e);
                }
            }
        }
    }
}
