using System;
using System.Reflection;
using HKTelemetry.Recorders;
using HKTelemetry.Session;
using Modding;
using UnityEngine;

namespace HKTelemetry
{
    /// <summary>
    /// Punto de entrada del mod. Solo engancha y desengancha; la lógica vive en los recorders.
    /// Fase 0: esqueleto que carga, lee ajustes y deja constancia de la versión en ModLog.txt.
    /// </summary>
    public partial class HKTelemetry : Mod, IGlobalSettings<GlobalSettings>
    {
        /// <summary>Versión del formato de datos. Ver docs/schema.md.</summary>
        public const int SchemaVersion = 1;

        /// <summary>Versión de Unity de la build 1.5.78.11833 soportada.</summary>
        public const string ExpectedUnityVersion = "2020.2.2f1";

        internal static HKTelemetry Instance { get; private set; }

        public static GlobalSettings Settings { get; private set; } = new GlobalSettings();

        public HKTelemetry() : base("HKTelemetry") { }

        public override string GetVersion() =>
            Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public override void Initialize()
        {
            if (Instance != null) return; // Initialize debe ser idempotente.
            Instance = this;

            try
            {
                string unity = Application.unityVersion;
                bool versionOk = unity == ExpectedUnityVersion;
                Log($"HKTelemetry v{GetVersion()} (schema {SchemaVersion}) | player_id={Settings.PlayerId} | " +
                    $"api={ModHooks.ModVersion} | unity={unity} ({(versionOk ? "OK" : "VERSION NO SOPORTADA")}) | " +
                    $"data={Application.persistentDataPath}");
                if (!versionOk)
                {
                    LogWarn($"Se esperaba Unity {ExpectedUnityVersion} (Hollow Knight 1.5.78.11833). No se grabará nada.");
                    return;
                }

                SessionManager.Init();
                Runner.Create();
                SceneRecorder.Hook();
                CombatRecorder.Hook();
                EnvRecorder.Hook();
                PlayerDataRecorder.Hook();
                Log($"Grabando en {SessionManager.Root}");
            }
            catch (Exception e)
            {
                LogError($"Error al inicializar: {e}");
            }
        }

        public void OnLoadGlobal(GlobalSettings s) => Settings = s ?? new GlobalSettings();

        public GlobalSettings OnSaveGlobal() => Settings;
    }
}
