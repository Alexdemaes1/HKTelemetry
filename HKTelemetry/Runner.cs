using HKTelemetry.Recorders;
using HKTelemetry.Session;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry
{
    /// <summary>
    /// MonoBehaviour propio (DontDestroyOnLoad): late en menús, cinemáticas y pausa, a diferencia de
    /// HeroUpdateHook. Lleva el reloj, los sondeos por frame y vuelca a ModLog.txt los errores pendientes.
    /// </summary>
    internal sealed class Runner : MonoBehaviour
    {
        public static Runner Create()
        {
            var go = new GameObject("HKTelemetry.Runner");
            DontDestroyOnLoad(go);
            return go.AddComponent<Runner>();
        }

        private void Update()
        {
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
        }
    }
}
