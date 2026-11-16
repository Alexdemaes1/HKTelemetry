using System.Collections;
using GlobalEnums;
using HKTelemetry.Session;
using HKTelemetry.Util;
using Modding;
using UnityEngine;
using USM = UnityEngine.SceneManagement.SceneManager;
using UScene = UnityEngine.SceneManagement.Scene;

namespace HKTelemetry.Recorders
{
    /// <summary>
    /// Sesión y escenas (docs/schema.md §4.1): session_start/end, scene_exit (con motivo), scene_enter,
    /// control_ready y game_state.
    ///
    /// ModHooks.SceneChanged NO se usa: no salta en puertas, muertes, cargas, ciervos ni sueños.
    /// Todos los On.* llaman a orig FUERA de Safe.Run y devuelven su resultado sin tocar.
    /// </summary>
    internal static class SceneRecorder
    {
        private const string MenuScene = "Menu_Title";
        private const string QuitToMenuScene = "Quit_To_Menu";
        private const string PermaDeathScene = "PermaDeath";

        /// <summary>Escena base actual (sin sufijos _boss/_preload).</summary>
        public static string CurrentScene { get; private set; }

        private static bool _hooked;
        private static bool _pendingTransition;     // hubo scene_exit y falta su scene_enter
        private static bool _awaitingControl;       // hubo scene_enter y falta control_ready
        private static double _exitTReal;
        private static string _pendingDeathZone;    // puesto en HeroController.Die; lo consume el siguiente scene_exit
        private static int _gateTriggerFrame = -1;  // frame en que TransitionPoint.OnTriggerEnter2D disparó
        private static string _quitReason;          // quit_menu / ending mientras se vuelve al menú
        private static GameState? _lastState;
        private static bool _lastPaused;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;

            ModHooks.SavegameLoadHook += OnSavegameLoad;
            ModHooks.SavegameSaveHook += OnSavegameSave;
            ModHooks.ApplicationQuitHook += OnApplicationQuit;
            On.GameManager.StartNewGame += OnStartNewGame;
            On.GameManager.BeginSceneTransition += OnBeginSceneTransition;
            On.GameManager.LoadScene += OnLoadScene;
            On.GameManager.OnNextLevelReady += OnNextLevelReady;
            On.GameManager.FinishedEnteringScene += OnFinishedEnteringScene;
            On.GameManager.ReturnToMainMenu += OnReturnToMainMenu;
            On.TransitionPoint.OnTriggerEnter2D += OnTransitionTrigger;
            On.HeroController.Die += OnHeroDie;
            USM.activeSceneChanged += OnActiveSceneChanged;
        }

        public static void OnSessionStart()
        {
            CurrentScene = BaseName(USM.GetActiveScene().name);
            _pendingTransition = false;
            _awaitingControl = false;
            _pendingDeathZone = null;
            _quitReason = null;
            _lastState = null;
        }

        // ---------------- inicio y fin de sesión ----------------

        private static void OnSavegameLoad(int slot) =>
            Safe.Run("session.load", () => SessionManager.RequestStart("load", slot));

        private static void OnStartNewGame(On.GameManager.orig_StartNewGame orig, GameManager self, bool permadeathMode, bool bossRushMode)
        {
            Safe.Run("session.new", () =>
            {
                string kind = bossRushMode ? "new_game_godseeker" : permadeathMode ? "new_game_steel_soul" : "new_game";
                SessionManager.RequestStart(kind, self.profileID);
            });
            orig(self, permadeathMode, bossRushMode);
        }

        private static void OnSavegameSave(int slot) => Safe.Run("session.save", () =>
        {
            // Volviendo al menú: el juego acaba de guardar (playTime y % al día). Mejor foto final posible.
            if (_quitReason != null) SessionManager.TakeProgressEnd();
        });

        private static void OnApplicationQuit() => Safe.Run("session.quit", () =>
            SessionManager.End("app_quit", appQuit: true));

        private static IEnumerator OnReturnToMainMenu(On.GameManager.orig_ReturnToMainMenu orig, GameManager self,
            GameManager.ReturnToMainMenuSaveModes saveMode, System.Action<bool> callback)
        {
            Safe.Run("scene.quit_menu", () =>
            {
                if (!SessionManager.Active) return;
                _quitReason = IsEndingScene(CurrentScene) ? "ending" : "quit_menu";
                SessionManager.TakeProgressEnd(); // por si el guardado falla; se repite tras guardar
                SessionManager.Emit("scene_exit", new Json()
                    .S("from", CurrentScene).S("to", QuitToMenuScene).S("reason", _quitReason)
                    .S("save_mode", saveMode.ToString()).End());
            });
            return orig(self, saveMode, callback);
        }

        private static void OnActiveSceneChanged(UScene from, UScene to) => Safe.Run("scene.active", () =>
        {
            if (!SessionManager.Active) return;
            string name = to.name;
            if (name == QuitToMenuScene)
            {
                // Sin _quitReason = no pasó por ReturnToMainMenu (EmergencyReturnToMenu, p. ej. mando desconectado).
                SessionManager.End(_quitReason ?? "emergency_menu", appQuit: false);
                _quitReason = null;
            }
            else if (name == PermaDeathScene)
            {
                SessionManager.End("permadeath", appQuit: false);
            }
            else if (name == MenuScene)
            {
                SessionManager.End(_quitReason ?? "menu", appQuit: false);
                _quitReason = null;
            }
        });

        // ---------------- salidas de escena ----------------

        private static void OnTransitionTrigger(On.TransitionPoint.orig_OnTriggerEnter2D orig, TransitionPoint self, Collider2D movingObj)
        {
            _gateTriggerFrame = Time.frameCount; // asignación trivial; no puede fallar
            orig(self, movingObj);
        }

        private static IEnumerator OnHeroDie(On.HeroController.orig_Die orig, HeroController self)
        {
            Safe.Run("scene.die", () =>
            {
                var gm = GameManager.UnsafeInstance;
                MapZone zone = MapZone.NONE;
                if (gm != null && gm.sm != null) zone = gm.sm.mapZone;
                _pendingDeathZone = zone == MapZone.DREAM_WORLD ? "death_dream"
                                  : zone == MapZone.GODS_GLORY ? "death_godhome"
                                  : "death";
            });
            return orig(self);
        }

        private static void OnBeginSceneTransition(On.GameManager.orig_BeginSceneTransition orig, GameManager self, GameManager.SceneLoadInfo info)
        {
            Safe.Run("scene.exit", () => RecordExit(self, info));
            orig(self, info);
        }

        private static void RecordExit(GameManager self, GameManager.SceneLoadInfo info)
        {
            if (!SessionManager.Active || info == null) return;

            // Si ya hay una carga en curso el juego ignora la petición (GameManager.cs:474): no hay salida real.
            if (ReflectionHelper.GetField<GameManager, SceneLoad>(self, "sceneLoad") != null) return;

            string to = info.SceneName;
            var vis = info.Visualization;
            string exitGate = null;
            string reason;

            if (_pendingDeathZone != null || (self.RespawningHero && !info.IsFirstLevelForPlayer))
            {
                reason = _pendingDeathZone ?? "death";
            }
            else if (info.IsFirstLevelForPlayer && vis == GameManager.SceneLoadVisualizations.ContinueFromSave)
            {
                reason = SessionManager.Kind == "new_game_godseeker" ? "new_game_godseeker" : "save_load";
            }
            else if (to == "Opening_Sequence")
            {
                reason = "new_game";
            }
            else if (to == "Cinematic_Stag_travel" || info.EntryGateName == "door_stagExit")
            {
                reason = "stag";
            }
            else if (vis == GameManager.SceneLoadVisualizations.GodsAndGlory
                     || StartsWith(CurrentScene, "GG_") || StartsWith(to, "GG_"))
            {
                reason = "godhome";
            }
            else if (info.EntryGateName == "dreamGate" || info.EntryGateName == "door_dreamReturn"
                     || vis == GameManager.SceneLoadVisualizations.Dream
                     || vis == GameManager.SceneLoadVisualizations.GrimmDream
                     || StartsWith(to, "Dream_"))
            {
                reason = "dream";
            }
            else if (_gateTriggerFrame == Time.frameCount)
            {
                reason = "gate";
                exitGate = TransitionPoint.lastEntered;
            }
            else if ((exitGate = FindDoor(to, info.EntryGateName)) != null)
            {
                reason = "door";
            }
            else
            {
                reason = "other";
            }

            _pendingDeathZone = null;
            SessionManager.Emit("scene_exit", new Json()
                .S("from", CurrentScene)
                .S("to", to)
                .S("reason", reason)
                .S("exit_gate", exitGate)
                .S("entry_gate", info.EntryGateName)
                .S("vis", vis.ToString())
                .S("info_type", info.GetType().Name)
                .B("prevent_fade", info.PreventCameraFadeOut)
                .B("first_level", info.IsFirstLevelForPlayer)
                .B("respawning", self.RespawningHero)
                .End());
            MarkExit();
        }

        /// <summary>Caminos que no usan BeginSceneTransition: cinemáticas, créditos, PermaDeath, ciervo antiguo.</summary>
        private static void OnLoadScene(On.GameManager.orig_LoadScene orig, GameManager self, string destScene)
        {
            Safe.Run("scene.exit_load", () =>
            {
                if (!SessionManager.Active) return;
                string reason = destScene == PermaDeathScene ? "permadeath"
                              : self.entryGateName == "door_stagExit" ? "stag"
                              : IsEndingScene(destScene) ? "ending"
                              : "cutscene";
                SessionManager.Emit("scene_exit", new Json()
                    .S("from", CurrentScene).S("to", destScene).S("reason", reason)
                    .S("entry_gate", self.entryGateName).S("info_type", "LoadScene").End());
                MarkExit();
            });
            orig(self, destScene);
        }

        private static void MarkExit()
        {
            _pendingTransition = true;
            _awaitingControl = false;
            _exitTReal = Clock.TReal;
        }

        // ---------------- entradas de escena ----------------

        /// <summary>Único punto que alcanzan los tres caminos de carga (BeginSceneTransition, LoadScene, LevelActivated).</summary>
        private static void OnNextLevelReady(On.GameManager.orig_OnNextLevelReady orig, GameManager self)
        {
            orig(self);
            Safe.Run("scene.enter", () =>
            {
                if (!SessionManager.Active) return;
                string scene = BaseName(USM.GetActiveScene().name);
                if (!_pendingTransition && scene == CurrentScene) return; // duplicado del mismo cambio

                string mapZone = null, sceneType = null;
                if (self.sm != null)
                {
                    mapZone = self.sm.mapZone.ToString();
                    sceneType = self.sm.sceneType.ToString();
                }
                CurrentScene = scene;
                SessionManager.Emit("scene_enter", new Json()
                    .S("scene", scene)
                    .S("entry_gate", self.entryGateName)
                    .S("map_zone", mapZone)
                    .S("scene_type", sceneType)
                    .B("non_gameplay", IsNonGameplay(scene))
                    .N("load_s", _pendingTransition ? Clock.TReal - _exitTReal : double.NaN)
                    .End());
                _pendingTransition = false;
                _awaitingControl = true;
            });
        }

        private static void OnFinishedEnteringScene(On.GameManager.orig_FinishedEnteringScene orig, GameManager self)
        {
            orig(self);
            Safe.Run("scene.control", () =>
            {
                if (!SessionManager.Active || !_awaitingControl) return;
                _awaitingControl = false; // los siguientes (hazard respawn, doble llamada de EnterHero) se ignoran
                SessionManager.Emit("control_ready", new Json().S("scene", CurrentScene).End());
                SessionManager.TakeProgressStartIfNeeded();
            });
        }

        // ---------------- sondeo por frame (desde el Runner) ----------------

        /// <summary>
        /// gameState se sondea además de los hooks porque CinematicPlayer lo escribe directamente.
        /// Emite game_state en cada cambio de estado o de pausa.
        /// </summary>
        public static void Poll(GameManager gm)
        {
            if (!SessionManager.Active || gm == null) return;
            GameState gs = gm.gameState;
            bool paused = gm.isPaused;
            if (_lastState == gs && _lastPaused == paused) return;
            SessionManager.Emit("game_state", new Json()
                .S("from", _lastState?.ToString())
                .S("to", gs.ToString())
                .B("paused", paused)
                .End());
            _lastState = gs;
            _lastPaused = paused;
        }

        // ---------------- utilidades ----------------

        private static string FindDoor(string targetScene, string entryPoint)
        {
            var active = USM.GetActiveScene();
            foreach (var tp in TransitionPoint.TransitionPoints)
            {
                if (tp == null || tp.gameObject.scene != active) continue;
                if (tp.targetScene == targetScene && tp.entryPoint == entryPoint) return tp.gameObject.name;
            }
            return null;
        }

        public static string BaseName(string scene) =>
            string.IsNullOrEmpty(scene) ? scene : GameManager.GetBaseSceneName(scene);

        private static bool StartsWith(string s, string prefix) =>
            s != null && s.StartsWith(prefix, System.StringComparison.Ordinal);

        private static bool IsEndingScene(string s) =>
            s != null && (s.StartsWith("Cinematic_Ending", System.StringComparison.Ordinal)
                          || s.StartsWith("End_", System.StringComparison.Ordinal)
                          || s.Contains("Credits"));

        /// <summary>Lista propia: IsNonGameplayScene del juego busca "Knight Pickup" con espacio (GameManager.cs:2152).</summary>
        private static bool IsNonGameplay(string s) =>
            s == MenuScene || s == QuitToMenuScene || s == "Knight_Pickup" || s == "Opening_Sequence"
            || s == "Cinematic_Stag_travel" || s == PermaDeathScene || StartsWith(s, "Cinematic_")
            || IsEndingScene(s) || s == "Intro_Cutscene" || s == "Prologue_Cutscene";
    }
}
