using System;
using System.Diagnostics;
using HKTelemetry.Recorders;
using HKTelemetry.Session;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry
{
    /// <summary>
    /// MonoBehaviour propio (DontDestroyOnLoad): late en menús, cinemáticas y pausa, a diferencia de
    /// HeroUpdateHook. Lleva el reloj, los sondeos por frame, el evento perf y vuelca a ModLog.txt los
    /// errores pendientes.
    ///
    /// Cada sondeo va en su propio bloque protegido: si uno falla de forma repetida, los demás siguen.
    /// Sin lambdas ni conversiones de método a delegado por frame (no genera basura).
    /// </summary>
    internal sealed class Runner : MonoBehaviour
    {
        public static Runner Create()
        {
            var go = new GameObject("HKTelemetry.Runner");
            DontDestroyOnLoad(go);
            return go.AddComponent<Runner>();
        }

        // Última ventana de perf (para el overlay de debug).
        public static float LastFps { get; private set; }
        public static float LastDtMaxMs { get; private set; }
        public static double LastModUsAvg { get; private set; }
        public static double LastModUsMax { get; private set; }

        private static readonly Action<string> LogAction = msg => HKTelemetry.Instance?.Log(msg);

        private float _winStart;
        private int _winFrames;
        private float _winMaxDt;
        private double _winModUsSum;
        private double _winModUsMax;

        private void Update()
        {
            long t = Safe.Enter();
            try
            {
                var gm = GameManager.UnsafeInstance; // nunca .instance: loguea errores y busca objetos si es null
                if (gm != null)
                {
                    try { if (SessionManager.Active) Clock.Tick(gm); } catch (Exception e) { Safe.Report("poll.clock", e); }
                    try { SceneRecorder.Poll(gm); } catch (Exception e) { Safe.Report("poll.scene", e); }
                }
                try { CombatRecorder.Poll(HeroController.UnsafeInstance); } catch (Exception e) { Safe.Report("poll.combat", e); }
                try { MapRecorder.Poll(gm); } catch (Exception e) { Safe.Report("poll.map", e); }
                try { PlayerDataRecorder.Poll(); } catch (Exception e) { Safe.Report("poll.pd", e); }
                try { SessionManager.Tick(); } catch (Exception e) { Safe.Report("poll.session", e); }
                Safe.Drain(LogAction);
            }
            finally { Safe.Exit(t); }

            // Tiempo del mod en este frame: hooks llamados desde el juego + este Update.
            double us = Safe.TakeTicks() * 1_000_000.0 / Stopwatch.Frequency;
            try { Perf(us); } catch (Exception e) { Safe.Report("runner.perf", e); }
        }

        /// <summary>
        /// Una vez por segundo: fps reales, frame más largo y coste total del mod por frame (µs).
        /// Sirve para descartar que el mod cause tirones o input lag.
        /// </summary>
        private void Perf(double us)
        {
            float now = Time.realtimeSinceStartup;
            _winFrames++;
            _winModUsSum += us;
            if (us > _winModUsMax) _winModUsMax = us;
            float dt = Time.unscaledDeltaTime;
            if (dt > _winMaxDt) _winMaxDt = dt;

            float span = now - _winStart;
            if (span < 1f) return;

            if (_winStart > 0f)
            {
                LastFps = _winFrames / span;
                LastDtMaxMs = _winMaxDt * 1000f;
                LastModUsAvg = _winModUsSum / _winFrames;
                LastModUsMax = _winModUsMax;
                if (SessionManager.Active)
                {
                    var w = SessionManager.Writer;
                    SessionManager.Emit("perf", Json.Obj()
                        .N("fps", LastFps)
                        .N("dt_max_ms", LastDtMaxMs)
                        .N("mod_us_avg", LastModUsAvg)
                        .N("mod_us_max", LastModUsMax)
                        .I("vsync", QualitySettings.vSyncCount)
                        .I("target_fps", Application.targetFrameRate)
                        .I("dropped", w != null ? w.TotalDropped() : 0)
                        .End());
                }
            }
            _winStart = now;
            _winFrames = 0;
            _winMaxDt = 0f;
            _winModUsSum = 0;
            _winModUsMax = 0;
        }
    }
}
