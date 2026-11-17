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
            public long PendingTruncate = -1; // recorte pendiente antes de reabrir (−1 = ninguno)
        }

        private readonly Dictionary<string, Sink> _sinks = new Dictionary<string, Sink>();
        private readonly List<RowSink> _rows = new List<RowSink>();
        private readonly StringBuilder _rowSb = new StringBuilder(512); // solo hilo de escritura
        private readonly ConcurrentQueue<Action> _jobs = new ConcurrentQueue<Action>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _thread;
        private volatile bool _stop;
        private volatile bool _compressOnClose;
        private int _writeErrors;

        public string Dir { get; }
        public bool Finished { get; private set; }

        /// <summary>Se llama en el hilo de escritura cuando todo está cerrado y comprimido (p. ej. para enviar la sesión).</summary>
        public Action OnFinished;
        public int WriteErrors => Volatile.Read(ref _writeErrors);
        public string LastError { get; private set; }

        /// <param name="sinks">nombre de fichero → cabecera CSV (null si no tiene) y tamaño máximo de cola.</param>
        /// <param name="rowSinks">ficheros CSV alimentados por búferes circulares (frames.csv, inputs.csv).</param>
        public TelemetryWriter(string dir, IEnumerable<(string name, string header, int maxQueue)> sinks,
                               IEnumerable<RowSink> rowSinks = null)
        {
            Dir = dir;
            Directory.CreateDirectory(dir);
            foreach (var (name, header, maxQueue) in sinks)
                _sinks[name] = new Sink { Path = Path.Combine(dir, name), Header = header, MaxQueue = maxQueue };
            if (rowSinks != null)
                foreach (var r in rowSinks)
                {
                    r.Path = Path.Combine(dir, r.Name);
                    r.Wake = _wake;
                    _rows.Add(r);
                }

            _thread = new Thread(Loop) { IsBackground = true, Name = "HKTelemetry.Writer", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        public long Lines(string sink)
        {
            if (_sinks.TryGetValue(sink, out var s)) return Interlocked.Read(ref s.Lines);
            var r = Row(sink);
            return r != null ? Interlocked.Read(ref r.Lines) : 0;
        }

        /// <summary>Bytes escritos en el fichero plano (sin leer el disco desde el hilo del juego).</summary>
        public long Bytes(string sink)
        {
            if (_sinks.TryGetValue(sink, out var s)) return Interlocked.Read(ref s.Bytes);
            var r = Row(sink);
            return r != null ? Interlocked.Read(ref r.Bytes) : 0;
        }

        public long Dropped(string sink)
        {
            if (_sinks.TryGetValue(sink, out var s)) return Interlocked.Read(ref s.Dropped);
            var r = Row(sink);
            return r != null ? Interlocked.Read(ref r.Dropped) : 0;
        }

        public long TotalDropped()
        {
            long n = 0;
            foreach (var s in _sinks.Values) n += Interlocked.Read(ref s.Dropped);
            for (int i = 0; i < _rows.Count; i++) n += Interlocked.Read(ref _rows[i].Dropped);
            return n;
        }

        private RowSink Row(string name)
        {
            for (int i = 0; i < _rows.Count; i++) if (_rows[i].Name == name) return _rows[i];
            return null;
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

        private volatile Action _statusJob;
        private int _statusSeenErrors;      // solo hilo de escritura
        private long _statusSeenDropped;

        /// <summary>
        /// Como Job, pero además se recuerda como «trabajo de estado» (session.json): si cerrar o comprimir
        /// los ficheros falla después del último, se vuelve a ejecutar para que los contadores finales
        /// incluyan ese error.
        /// </summary>
        public void StatusJob(Action job)
        {
            Action wrapped = () =>
            {
                _statusSeenErrors = WriteErrors;
                _statusSeenDropped = TotalDropped();
                job();
            };
            _statusJob = wrapped;
            Job(wrapped);
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
            foreach (var r in _rows)
            {
                try
                {
                    r.Writer = OpenFile(r.Path, r.Header);
                    r.Writer.Flush();
                    Interlocked.Exchange(ref r.Bytes, r.Writer.BaseStream.Length);
                }
                catch (Exception e)
                {
                    Fail(r.Name, e);
                    CloseRow(r);
                    r.PendingTruncate = 0; // una cabecera a medias no puede quedarse
                    if (TruncateTo(r.Path, 0)) r.PendingTruncate = -1;
                }
            }
            while (!_stop)
            {
                _wake.WaitOne(500);
                DrainAll();
                RunJobs();
            }
            DrainAll();
            RunJobs();
            // Lo que el último volcado no pudo escribir (fichero que no se pudo reabrir) ya no tendrá
            // otra oportunidad: se cuenta como descartado, nunca se pierde en silencio.
            int leftover = 0;
            foreach (var s in _sinks.Values)
            {
                while (s.Queue.TryDequeue(out _))
                {
                    Interlocked.Decrement(ref s.QueueCount);
                    Interlocked.Increment(ref s.Dropped);
                    leftover++;
                }
            }
            foreach (var r in _rows) leftover += r.DiscardPending();
            if (leftover > 0) Fail("close", new IOException($"{leftover} líneas sin escribir al cerrar"));
            foreach (var s in _sinks.Values) CloseSink(s);
            foreach (var r in _rows) CloseRow(r);
            if (_compressOnClose)
            {
                try { CompressAll(Dir); }
                catch (Exception e) { Fail("compress", e); }
            }
            // El último session.json pudo escribirse antes del volcado final (Stop llega un instante
            // después de encolarlo): si desde entonces hay más errores o descartes, se reescribe.
            if (_statusJob != null && (WriteErrors != _statusSeenErrors || TotalDropped() != _statusSeenDropped))
            {
                try { _statusJob(); }
                catch (Exception e) { Fail("status", e); }
            }
            Finished = true;
            try { OnFinished?.Invoke(); } catch (Exception e) { Safe.Report("writer.finished", e); }
        }

        private void DrainAll()
        {
            foreach (var s in _sinks.Values)
            {
                if (Volatile.Read(ref s.QueueCount) == 0) continue;
                // No se reabre sobre una línea a medias: primero el recorte pendiente (si sigue fallando,
                // las líneas esperan en la cola).
                if (s.PendingTruncate >= 0)
                {
                    if (!TruncateTo(s.Path, s.PendingTruncate)) continue;
                    s.PendingTruncate = -1;
                }
                int batch = 0;
                long good = Interlocked.Read(ref s.Bytes);
                try
                {
                    if (s.Writer == null) Open(s);
                    while (s.Queue.TryDequeue(out var line))
                    {
                        Interlocked.Decrement(ref s.QueueCount);
                        batch++;
                        s.Writer.Write(line);
                        s.Writer.Write('\n');
                    }
                    s.Writer.Flush();
                    long len = s.Writer.BaseStream.Length;
                    Interlocked.Add(ref s.Lines, batch);
                    Interlocked.Exchange(ref s.Bytes, len);
                }
                catch (Exception e)
                {
                    // No se sabe qué parte del lote llegó al disco: se cuenta entero como descartado y el
                    // fichero se recorta a la última longitud buena. Se reabre en el siguiente ciclo.
                    // (Si falló al abrir, batch = 0: las líneas siguen en la cola y se reintentan.)
                    Interlocked.Add(ref s.Dropped, batch);
                    Fail(System.IO.Path.GetFileName(s.Path), e);
                    CloseSink(s);
                    if (!TruncateTo(s.Path, good)) s.PendingTruncate = good;
                }
            }

            foreach (var r in _rows)
            {
                if (!r.HasRows) continue;
                if (r.PendingTruncate >= 0)
                {
                    if (!TruncateTo(r.Path, r.PendingTruncate)) continue;
                    r.PendingTruncate = -1;
                }
                int consumed = 0;
                long good = Interlocked.Read(ref r.Bytes);
                try
                {
                    if (r.Writer == null) r.Writer = OpenFile(r.Path, r.Header);
                    r.WriteTo(r.Writer, _rowSb, ref consumed);
                    r.Writer.Flush();
                    long len = r.Writer.BaseStream.Length;
                    Interlocked.Add(ref r.Lines, consumed);
                    Interlocked.Exchange(ref r.Bytes, len);
                }
                catch (Exception e)
                {
                    // Igual que en eventos: el lote entero a descartados y el fichero sin filas a medias.
                    Interlocked.Add(ref r.Dropped, consumed);
                    Fail(r.Name, e);
                    CloseRow(r);
                    if (!TruncateTo(r.Path, good)) r.PendingTruncate = good;
                }
            }
        }

        /// <summary>Recorta el fichero a la última longitud escrita con éxito (quita filas a medias).</summary>
        private bool TruncateTo(string path, long good)
        {
            try
            {
                if (!File.Exists(path)) return true;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                    if (fs.Length > good) fs.SetLength(good);
                return true;
            }
            catch (Exception e)
            {
                Fail("truncate", e);
                return false;
            }
        }

        private static void CloseRow(RowSink r)
        {
            try { r.Writer?.Dispose(); } catch { /* ya estaba roto */ }
            r.Writer = null;
        }

        private static void Open(Sink s) => s.Writer = OpenFile(s.Path, s.Header);

        private static StreamWriter OpenFile(string path, string header)
        {
            bool isNew = !File.Exists(path) || new FileInfo(path).Length == 0;
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 65536);
            var w = new StreamWriter(fs, Utf8, 65536);
            if (isNew && header != null)
            {
                w.Write(header);
                w.Write('\n');
            }
            return w;
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
