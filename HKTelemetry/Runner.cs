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
    /// </summary>
    internal sealed class Runner : MonoBehaviour
    {
        public static Runner Create()
        {
            var go = new GameObject("HKTelemetry.Runner");
            DontDestroyOnLoad(go);
            return go.AddComponent<Runner>();
        }

        // --- perf: ventana de 1 s de reloj real ---
        private readonly Stopwatch _modTime = new Stopwatch();
        private float _winStart;
        private int _winFrames;
        private float _winMaxDt;
        private double _winModUsSum;
        private double _winModUsMax;

        private void Update()
        {
            _modTime.Restart();
            Safe.Run("runner", () =>
            {
                var gm = GameManager.UnsafeInstance; // nunca .instance: loguea errores y busca objetos si es null
                if (gm != null)
                {
                    if (SessionManager.Active) Clock.Tick(gm);
                    SceneRecorder.Poll(gm);
                }
                SessionManager.Tick();
            });
            Safe.Drain(msg => HKTelemetry.Instance?.Log(msg));
            _modTime.Stop();

            Safe.Run("runner.perf", Perf);
        }

        /// <summary>
        /// Una vez por segundo: fps reales, frame más largo y coste del mod en este Update (µs).
        /// Sirve para descartar que el mod cause tirones o input lag.
        /// </summary>
        private void Perf()
        {
            double us = _modTime.ElapsedTicks * 1_000_000.0 / Stopwatch.Frequency;
            float now = Time.realtimeSinceStartup;
            _winFrames++;
            _winModUsSum += us;
            if (us > _winModUsMax) _winModUsMax = us;
            float dt = Time.unscaledDeltaTime;
            if (dt > _winMaxDt) _winMaxDt = dt;

            float span = now - _winStart;
            if (span < 1f) return;

            if (SessionManager.Active && _winStart > 0f)
            {
                var w = SessionManager.Writer;
                SessionManager.Emit("perf", new Json()
                    .N("fps", _winFrames / span)
                    .N("dt_max_ms", _winMaxDt * 1000f)
                    .N("mod_us_avg", _winModUsSum / _winFrames)
                    .N("mod_us_max", _winModUsMax)
                    .I("vsync", QualitySettings.vSyncCount)
                    .I("target_fps", Application.targetFrameRate)
                    .I("dropped", w != null ? w.TotalDropped() : 0)
                    .End());
            }
            _winStart = now;
            _winFrames = 0;
            _winMaxDt = 0f;
            _winModUsSum = 0;
            _winModUsMax = 0;
        }
    }
}
