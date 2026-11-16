using System;
using System.Diagnostics;
using GlobalEnums;
using UnityEngine;

namespace HKTelemetry.Util
{
    /// <summary>
    /// Relojes de la sesión (docs/schema.md §2). Solo se usa desde el hilo principal.
    /// - t_real: segundos de reloj real desde el inicio de la sesión.
    /// - t_game: tiempo de juego con el mismo criterio que GameManager.IncreaseGameTimer:
    ///   suma unscaledDeltaTime en PLAYING / ENTERING_LEVEL / EXITING_LEVEL, sin pausa y descartando
    ///   frames de 1 s o más. NUNCA se deduce la pausa de Time.timeScale (el hit-stop también lo pone a 0).
    /// </summary>
    internal static class Clock
    {
        private static readonly Stopwatch Watch = new Stopwatch();

        public static DateTime StartUtc { get; private set; }
        public static double TGame { get; private set; }
        public static double TReal => Watch.Elapsed.TotalSeconds;
        public static int Frame => Time.frameCount;

        public static void Reset()
        {
            StartUtc = DateTime.UtcNow;
            TGame = 0;
            Watch.Restart();
        }

        /// <summary>Una vez por frame desde el Runner.</summary>
        public static void Tick(GameManager gm)
        {
            if (gm == null || gm.isPaused) return;
            var gs = gm.gameState;
            if (gs != GameState.PLAYING && gs != GameState.ENTERING_LEVEL && gs != GameState.EXITING_LEVEL) return;
            float dt = Time.unscaledDeltaTime;
            if (dt < 1f) TGame += dt;
        }
    }
}
