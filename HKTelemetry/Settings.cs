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
        /// <summary>
        /// "auto" (el del kit): identidad sacada de Steam (Session/Identity.cs). Cualquier otro valor se usa
        /// tal cual (p. ej. "P00" en desarrollo). No aparece en el menú.
        /// </summary>
        public string PlayerId = "auto";

        /// <summary>Id aleatorio de esta instalación, solo si no se puede leer Steam ("U" + esto).</summary>
        public string InstallId = "";

        /// <summary>Frecuencia de muestreo de frames.csv (10, 20, 30 o 60).</summary>
        public int SampleHz = 60;

        /// <summary>Ocultar el indicador de grabación.</summary>
        public bool HideHud = false;

        /// <summary>Overlay de depuración (contadores, tamaños, Hz reales).</summary>
        public bool Debug = false;

        /// <summary>
        /// Dirección del receptor de datos (Apps Script, server/Code.gs). Viene en el kit; vacía = sin envío
        /// automático (desarrollo). No es una clave: solo permite SUBIR trozos con el formato del mod.
        /// </summary>
        public string UploadUrl = "";

        /// <summary>Enviar las sesiones automáticamente al terminar (si hay UploadUrl).</summary>
        public bool AutoUpload = true;
    }
}
