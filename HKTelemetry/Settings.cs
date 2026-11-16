using System;

namespace HKTelemetry
{
    /// <summary>
    /// Ajustes globales (comunes a todas las partidas). La API los guarda en
    /// HKTelemetry.GlobalSettings.json: junto a la DLL si ese fichero existe allí (así se
    /// distribuye el kit con el player_id ya puesto) o, si no, en Application.persistentDataPath.
    ///
    /// La experiencia del jugador NO se pregunta: se calcula sola (Steam y partidas guardadas),
    /// ver Session/Experience.cs.
    /// </summary>
    [Serializable]
    public class GlobalSettings
    {
        /// <summary>Pseudónimo del participante (P01, P02...). Viene en el kit; no aparece en el menú.</summary>
        public string PlayerId = "P00";

        /// <summary>Frecuencia de muestreo de frames.csv (10, 20, 30 o 60).</summary>
        public int SampleHz = 60;

        /// <summary>Ocultar el indicador de grabación.</summary>
        public bool HideHud = false;

        /// <summary>Overlay de depuración (contadores, tamaños, Hz reales).</summary>
        public bool Debug = false;
    }
}
