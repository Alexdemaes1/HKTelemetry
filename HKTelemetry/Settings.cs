using System;

namespace HKTelemetry
{
    /// <summary>
    /// Ajustes globales (comunes a todas las partidas). La API los guarda en
    /// HKTelemetry.GlobalSettings.json: junto a la DLL si ese fichero existe allí (así se
    /// distribuye el kit con el player_id ya puesto) o, si no, en Application.persistentDataPath.
    /// </summary>
    [Serializable]
    public class GlobalSettings
    {
        /// <summary>Pseudónimo del participante (P01, P02...). No hay campo de texto en el menú: viene en el kit.</summary>
        public string PlayerId = "P00";

        /// <summary>Frecuencia de muestreo de frames.csv (10-60).</summary>
        public int SampleHz = 30;

        /// <summary>Ocultar el indicador de grabación.</summary>
        public bool HideHud = false;

        /// <summary>Overlay de depuración (contadores, tamaños, Hz reales).</summary>
        public bool Debug = false;

        // --- Experiencia autodeclarada (índices de opciones cerradas; ver docs/schema.md §3) ---

        /// <summary>0 = 0 h, 1 = menos de 10 h, 2 = 10-50 h, 3 = 50-200 h, 4 = más de 200 h.</summary>
        public int HoursBucket = 0;

        /// <summary>0 = no, 1 = un final, 2 = 106 % / 112 %, 3 = Steel Soul.</summary>
        public int Completed = 0;

        /// <summary>0 = ninguna, 1 = alguna, 2 = mucha.</summary>
        public int MetroidvaniaExp = 0;

        /// <summary>0 = teclado, 1 = mando.</summary>
        public int InputPref = 0;
    }
}
