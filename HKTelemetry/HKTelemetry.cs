using System;
using System.Reflection;
using HKTelemetry.Recorders;
using HKTelemetry.Session;
using HKTelemetry.Util;
using Modding;
using UnityEngine;

// Pruebas fuera del juego (búferes de frames/inputs, escritor). No afecta al mod.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HKTelemetry.RingTest")]

namespace HKTelemetry
{
    /// <summary>
    /// Punto de entrada del mod. Solo engancha y desengancha; la lógica vive en los recorders.
    /// Fase 0: esqueleto que carga, lee ajustes y deja constancia de la versión en ModLog.txt.
    /// </summary>
    public partial class HKTelemetry : Mod, IGlobalSettings<GlobalSettings>
    {
        /// <summary>Versión del formato de datos. Ver docs/schema.md.</summary>
        public const int SchemaVersion = 3;

        /// <summary>Versión de Unity de la build 1.5.78.11833 soportada.</summary>
        public const string ExpectedUnityVersion = "2020.2.2f1";

        internal static HKTelemetry Instance { get; private set; }

        public static GlobalSettings Settings { get; private set; } = new GlobalSettings();

        public HKTelemetry() : base("HKTelemetry") { }

        /// <summary>Versión del mod, calculada una vez (GetExecutingAssembly().GetName() asigna memoria).</summary>
        public static readonly string VersionString = Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public override string GetVersion() => VersionString;

        public override void Initialize()
        {
            if (Instance != null) return; // Initialize debe ser idempotente.
            Instance = this;

            try
            {
                string unity = Application.unityVersion;
                bool versionOk = unity == ExpectedUnityVersion;
                Safe.Run("identity", () => Identity.Resolve(Settings, SaveGlobalSettings));
                Log($"HKTelemetry v{GetVersion()} (schema {SchemaVersion}) | player_id={Identity.PlayerId} ({Identity.Source}) | " +
                    $"api={ModHooks.ModVersion} | unity={unity} ({(versionOk ? "OK" : "VERSION NO SOPORTADA")}) | " +
                    $"data={Application.persistentDataPath}");
                Safe.SetMainThread();
                // El HUD se crea antes de la comprobación: con versión no soportada muestra el punto rojo.
                Safe.Run("hud.create", () => global::HKTelemetry.Hud.Hud.Create(unsupported: !versionOk));
                if (!versionOk)
                {
                    LogWarn($"Se esperaba Unity {ExpectedUnityVersion} (Hollow Knight 1.5.78.11833). No se grabará nada.");
                    return;
                }

                // Cada paso por separado: si uno falla, los demás siguen y el HUD se pone en rojo.
                Step("init.session", SessionManager.Init);
                Step("init.runner", () => Runner.Create());
                Step("hook.scene", SceneRecorder.Hook);
                Step("hook.combat", CombatRecorder.Hook);
                Step("hook.env", EnvRecorder.Hook);
                Step("hook.playerdata", PlayerDataRecorder.Hook);
                Step("hook.map", MapRecorder.Hook);
                Step("init.upload", () => Uploader.Start(SessionManager.Root));
                Log($"Grabando en {SessionManager.Root}");
            }
            catch (Exception e)
            {
                global::HKTelemetry.Hud.Hud.InitError = "Error al iniciar: " + e.GetType().Name;
                LogError($"Error al inicializar: {e}");
            }
        }

        private void Step(string name, Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                global::HKTelemetry.Hud.Hud.InitError = $"Error al iniciar ({name}): {e.GetType().Name}";
                LogError($"Error al inicializar {name}: {e}");
            }
        }

        public void OnLoadGlobal(GlobalSettings s) => Settings = s ?? new GlobalSettings();

        public GlobalSettings OnSaveGlobal() => Settings;
    }
}
