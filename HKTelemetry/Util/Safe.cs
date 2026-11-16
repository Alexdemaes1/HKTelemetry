using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace HKTelemetry.Util
{
    /// <summary>
    /// Envoltorio de todo código del mod que corre dentro del juego. Nunca relanza: cuenta el error,
    /// guarda el mensaje y el Runner lo vuelca a ModLog.txt desde el hilo principal con límite de
    /// frecuencia (Logger.Log escribe en disco de forma síncrona: no se puede llamar por cada error).
    ///
    /// También mide el tiempo que pasa el hilo principal dentro del mod (hooks incluidos) para el
    /// evento perf. Solo cuenta la sección más externa (las anidadas no se suman dos veces).
    ///
    /// IMPORTANTE: el orig(...) de un hook On.* se llama FUERA de Safe.Run / Enter-Exit, para no
    /// tragarse excepciones del propio juego ni medir tiempo del juego como si fuera del mod.
    /// </summary>
    internal static class Safe
    {
        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static readonly Dictionary<string, int> PerWhere = new Dictionary<string, int>();
        private static int _errors;

        private static int _mainThread = -1;
        private static int _depth;      // solo hilo principal
        private static long _ticks;     // solo hilo principal

        /// <summary>Errores totales desde que arrancó el juego.</summary>
        public static int ErrorCount => Volatile.Read(ref _errors);

        /// <summary>Último error (para el HUD).</summary>
        public static string LastError { get; private set; }

        public static void SetMainThread() => _mainThread = Thread.CurrentThread.ManagedThreadId;

        // ---------------- medición de tiempo ----------------

        /// <summary>Abre una sección medida. Devuelve 0 si no hay que medir (anidada u otro hilo).</summary>
        public static long Enter()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return 0;
            return _depth++ == 0 ? Stopwatch.GetTimestamp() : 0;
        }

        public static void Exit(long t0)
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            if (--_depth == 0 && t0 != 0) _ticks += Stopwatch.GetTimestamp() - t0;
            if (_depth < 0) _depth = 0;
        }

        /// <summary>Ticks acumulados desde la última llamada (una vez por frame, desde el Runner).</summary>
        public static long TakeTicks()
        {
            long t = _ticks;
            _ticks = 0;
            return t;
        }

        // ---------------- ejecución protegida ----------------

        public static void Run(string where, Action action)
        {
            long t0 = Enter();
            try { action(); }
            catch (Exception e) { Report(where, e); }
            finally { Exit(t0); }
        }

        public static T Run<T>(string where, Func<T> func, T fallback)
        {
            long t0 = Enter();
            try { return func(); }
            catch (Exception e) { Report(where, e); return fallback; }
            finally { Exit(t0); }
        }

        public static void Report(string where, Exception e)
        {
            try
            {
                Interlocked.Increment(ref _errors);
                int n;
                lock (PerWhere)
                {
                    PerWhere.TryGetValue(where, out n);
                    PerWhere[where] = ++n;
                }
                LastError = $"{where}: {e.GetType().Name}: {e.Message}";
                // Los 3 primeros de cada sitio con traza completa; después, uno de cada 100.
                if (n <= 3) Pending.Enqueue($"[{where}] error #{n}: {e}");
                else if (n % 100 == 0) Pending.Enqueue($"[{where}] {n} errores acumulados. Último: {e.GetType().Name}: {e.Message}");
            }
            catch
            {
                // Nunca propagar desde el propio informe de errores.
            }
        }

        /// <summary>Mensajes informativos generados fuera del hilo principal (p. ej. el writer).</summary>
        public static void Info(string message) => Pending.Enqueue(message);

        /// <summary>Solo desde el hilo principal. Vuelca como mucho <paramref name="max"/> mensajes por llamada.</summary>
        public static void Drain(Action<string> log, int max = 5)
        {
            for (int i = 0; i < max && Pending.TryDequeue(out var msg); i++)
            {
                try { log(msg); } catch { /* el logger no puede romper el juego */ }
            }
        }
    }
}
