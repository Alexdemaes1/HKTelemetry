### S1 [corrected] GameManager.BeginSceneTransition signature, SceneLoadInfo fields, SceneLoadVisualizations, is it used for all scene changes, callers
Signature is `public void BeginSceneTransition(GameManager.SceneLoadInfo info)`. The API patch runs `ModHooks.BeforeSceneLoad` on info.SceneName and then calls `orig_BeginSceneTransition`. That method sends INVENTORY CANCEL, resets the game timer if IsFirstLevelForPlayer, and starts the coroutine `BeginSceneTransitionRoutine(info)`. The coroutine returns early with no transition if a `sceneLoad` is already running.

SceneLoadInfo is a nested public class with virtual methods NotifyFetchComplete, IsReadyToActivate and NotifyFinished, which StagTravel overrides. Its fields are IsFirstLevelForPlayer, SceneName, HeroLeaveDirection, EntryGateName, EntryDelay, PreventCameraFadeOut, WaitForSceneTransitionCameraFade, Visualization, AlwaysUnloadUnusedAssets and forceWaitFetch.

The SceneLoadVisualizations enum values are Default=0, Custom=-1, Dream=1, Colosseum=2, GrimmDream=3, ContinueFromSave=4, GodsAndGlory=5.

It is NOT used for every scene change.

C# callers of BeginSceneTransition:
- TransitionPoint.OnTriggerEnter2D (edge gates)
- GameManager.WarpToDreamGate
- GameManager.ReadyForRespawn (death respawn and continue from save)
- GameManager.RunStartNewGame (scene "Opening_Sequence")
- StagTravel.Start, both branches
- CutsceneHelper (NextScene.GGReturn)
- NextSceneOnInput (GG_Atrium)
- BossDoorChallengeCompleteUI (dreamReturnScene or GG_Unlock)
- The PlayMaker action HutongGames.PlayMaker.Actions.BeginSceneTransition, used by FSM-driven doors, dream entries, stag station and so on.

Paths that do NOT use it:
- GameManager.LoadScene: Tutorial_01, PermaDeath, Intro_Cutscene, End_Credits, Menu_Credits, GG_End_Sequence and others.
- GameManager.ChangeToScene (calls LeftScene, then LoadScene or LoadSceneAdditive). Used by CinematicPlayer for VideoType.StagTravel.
- ReturnToMainMenu and EmergencyReturnToMenu load "Quit_To_Menu" directly through UnityEngine SceneManager.
- QuitToMenu loads "Menu_Title".
- RunContinueGame loads "Knight_Pickup" additively.
- OpeningSequence loads Knight_Pickup and Tutorial_01 directly.
- Hazard respawn changes no scene.
API: public void GameManager.BeginSceneTransition(GameManager.SceneLoadInfo info)
public class GameManager.SceneLoadInfo { public bool IsFirstLevelForPlayer; public string SceneName; public GatePosition? HeroLeaveDirection; public string EntryGateName; public float EntryDelay; public bool PreventCameraFadeOut; public bool WaitForSceneTransitionCameraFade; public SceneLoadVisualizations Visualization; public bool AlwaysUnloadUnusedAssets; public bool forceWaitFetch; virtual NotifyFetchComplete()/IsReadyToActivate()/NotifyFinished() }
public enum GameManager.SceneLoadVisualizations { Default=0, Custom=-1, Dream=1, Colosseum=2, GrimmDream=3, ContinueFromSave=4, GodsAndGlory=5 }
Hook: On.GameManager.BeginSceneTransition += (orig, self, info) => { record; orig(self, info); }
CAVEAT: FSM-driven callers (doors, dream nail entry, stag station, pantheon exits) cannot be listed by grep; they go through the PlayMaker action. That action always sets PreventCameraFadeOut=true and AlwaysUnloadUnusedAssets=false. If BeginSceneTransitionRoutine finds sceneLoad != null it logs an error and does nothing, so a hooked call does not always mean a real transition.

### S2 [confirmed] TransitionPoint fields and the method that starts the transition
TransitionPoint is a MonoBehaviour. Its public fields are isADoor, dontWalkOutOfDoor, entryDelay, alwaysEnterRight, alwaysEnterLeft, hardLandOnExit, targetScene, entryPoint, entryOffset, customFadeFSM, nonHazardGate, respawnMarker, the four AudioMixerSnapshot fields, sceneLoadVisualization, customFade and forceWaitFetch. It also has a private serialized alwaysUnloadUnusedAssets.

Static members: `TransitionPoints` (List), `lastEntered` (string, the gate GameObject name) and `event OnBeforeTransition`.

The transition starts in `private void OnTriggerEnter2D(Collider2D)`. OnTriggerStay2D retries it if the gate is not activated yet. It returns early if isADoor is set, if the collider layer is not 9 (the hero), or if gm.gameState != PLAYING. It then calls gm.BeginSceneTransition with:
- SceneName = targetScene
- EntryGateName = entryPoint
- HeroLeaveDirection = GetGatePosition()
- EntryDelay = entryDelay
- WaitForSceneTransitionCameraFade = true
- PreventCameraFadeOut = customFadeFSM != null
- Visualization = sceneLoadVisualization
- AlwaysUnloadUnusedAssets
- forceWaitFetch

GetGatePosition() works out top/right/left/bottom/door from the GameObject name. The exit gate's name is in `TransitionPoint.lastEntered`, which is set just before the call.

Doors (isADoor=true) are not handled here. A PlayMaker FSM handles them, presumably through the BeginSceneTransition action.
API: public class TransitionPoint : MonoBehaviour { public bool isADoor; public float entryDelay; public bool alwaysEnterRight; public bool alwaysEnterLeft; public bool hardLandOnExit; public string targetScene; public string entryPoint; public Vector2 entryOffset; public PlayMakerFSM customFadeFSM; public bool nonHazardGate; public HazardRespawnMarker respawnMarker; public GameManager.SceneLoadVisualizations sceneLoadVisualization; public bool customFade; public bool forceWaitFetch; public static string lastEntered; public static List<TransitionPoint> TransitionPoints {get;} public GatePosition GetGatePosition(); private void OnTriggerEnter2D(Collider2D movingObj) }
CAVEAT: For doors, entering is done by a PlayMaker FSM on the door object, not by OnTriggerEnter2D. To test in-game: enter a door (for example Dirtmouth Sly shop or an elevator) and check that On.GameManager.BeginSceneTransition fires with PreventCameraFadeOut=true and that TransitionPoint.lastEntered is NOT updated. If so, the exit-gate name for doors has to come from info.EntryGateName or from the door object, not from lastEntered.

### S3 [confirmed] ReturnToMainMenu signature, SaveAndQuit path, menu scene name
Signature is `public IEnumerator ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes saveMode, Action<bool> callback = null)`. The save modes are SaveAndCancelOnFail, SaveAndContinueOnFail and DontSave.

What it does, in order:
1. Calls StoryRecord_quit and TimePasses.
2. Saves the game (SaveGame(profileID)), unless DontSave. In SaveAndCancelOnFail it aborts if the save fails.
3. Fades out over 2 s of unscaled time and spawns a loading spinner.
4. Fires the UnloadingLevel and DestroyPersonalPools events and broadcasts "QUIT TO MENU".
5. Loads "Quit_To_Menu" with UnityEngine.SceneManagement.SceneManager.LoadSceneAsync, so it does NOT go through BeginSceneTransition or LoadScene, and neither BeforeSceneLoadHook nor SceneChanged fires.
6. In "Quit_To_Menu", QuitToMenu.Start destroys UIManager, HeroController, GameCameras and the GameManager GameObject, calls playerData.Reset() and sceneData.Reset(), and loads "Menu_Title".

The pause-menu "Save & Quit" button runs UIManager.ReturnToMainMenu(). That uses SaveAndCancelOnFail, or SaveAndContinueOnFail if saving is not allowed by engagement.

The menu scene is `Constants.MENU_SCENE = "Menu_Title"`. `GameManager.IsMenuScene()` checks the same name.

There is also a private EmergencyReturnToMenu (engagement lost), which loads Quit_To_Menu synchronously without saving.
API: public IEnumerator GameManager.ReturnToMainMenu(GameManager.ReturnToMainMenuSaveModes saveMode, Action<bool> callback = null)
public enum GameManager.ReturnToMainMenuSaveModes { SaveAndCancelOnFail, SaveAndContinueOnFail, DontSave }
public const string Constants.MENU_SCENE = "Menu_Title"
Hook: On.GameManager.ReturnToMainMenu (returns IEnumerator: wrap orig's enumerator; record at call time)
CAVEAT: The hook returns an IEnumerator. Record the session-end flag when the hook is called, and then still return orig(self, saveMode, callback) unchanged. The final progress snapshot must be read before QuitToMenu calls playerData.Reset(). The best moment is when ReturnToMainMenu is entered, or from SavegameSaveHook.

### S4 [confirmed] Death flow, respawn, hazard respawn
**Real death.** HeroController.TakeDamage starts the private coroutine HeroController.Die(). Die() does the following in order:
1. Fires the `HeroController.OnDeath` event (HeroDeathEvent), always first, even if already dead.
2. Sets disablePause.
3. If the map zone is "DREAM_WORLD" or "GODS_GLORY", it hands the death to heroDeathPrefab (FSM) and stops there. gm.PlayerDead is never called.
4. Otherwise it sets cState.dead = true and starts gm.PlayerDead(DEATH_WAIT).

GameManager.PlayerDead (API patch) fires ModHooks.OnBeforePlayerDead, runs orig_PlayerDead, then fires OnAfterPlayerDead. orig_PlayerDead freezes the camera, calls ResetSemiPersistentItems, saves the game (SaveGame(profileID)), waits, fades out, and waits for the save to finish. Then:
- If permadeathMode == 0, it calls ReadyForRespawn(false).
- If permadeathMode == 2, it calls LoadScene("PermaDeath").

ReadyForRespawn sets `RespawningHero = true` and calls BeginSceneTransition with:
- SceneName = playerData respawnScene
- EntryGateName = ""
- PreventCameraFadeOut = true
- Visualization = Default, or ContinueFromSave when it is the first level for the player
- AlwaysUnloadUnusedAssets = true

So death respawn DOES go through BeginSceneTransition. After the load, EnterHero sees RespawningHero, starts hero_ctrl.Respawn(), calls gm.FinishedEnteringScene() right away, and clears RespawningHero. Respawn() finds the GameObject tagged RespawnPoint whose name equals respawnMarkerName (LocateSpawnPoint) and handles respawnType 1 (bench). At its end it calls FinishedEnteringScene again.

AfterPlayerDeadHook fires when orig_PlayerDead returns, which is right after BeginSceneTransition starts, not after the respawn.

**Hazard respawn** (spikes, acid, pit, no death):
1. TakeDamage starts DieFromHazard(HazardType, angle), which sets cState.hazardDeath = true and starts gm.PlayerDeadFromHazard(0f).
2. That fades out, broadcasts "HAZARD RELOAD" and calls GameManager.HazardRespawn().
3. HazardRespawn sets a private hazardRespawningHero flag and calls EnterHero.
4. EnterHero starts hero_ctrl.HazardRespawn() and calls gm.FinishedEnteringScene() right away.
5. HeroController.HazardRespawn() sets cState.hazardRespawning = true, teleports to hazardRespawnLocation, plays the animation, sets hazardRespawning = false, and calls FinishedEnteringScene(setHazardMarker: false).

There is no scene change and no BeginSceneTransition. KillOnContact.cs also calls HeroController.HazardRespawn() directly.
API: private IEnumerator HeroController.Die()  // hook: On.HeroController.Die or event HeroController.OnDeath
private IEnumerator HeroController.DieFromHazard(HazardType hazardType, float angle)
public event HeroController.HeroDeathEvent OnDeath  // delegate void HeroDeathEvent()
public IEnumerator GameManager.PlayerDead(float waitTime)
public IEnumerator GameManager.PlayerDeadFromHazard(float waitTime)
public void GameManager.ReadyForRespawn(bool isFirstLevelForPlayer)
public bool GameManager.RespawningHero { get; set; }
public void GameManager.HazardRespawn()
public IEnumerator HeroController.Respawn()
public IEnumerator HeroController.HazardRespawn()
public Transform HeroController.LocateSpawnPoint()
HeroControllerStates: public bool dead; public bool hazardDeath; public bool hazardRespawning;
PlayerData: public string respawnScene; public string respawnMarkerName; public int respawnType; public Vector3 hazardRespawnLocation; public bool hazardRespawnFacingRight;
CAVEAT: Hazard deaths do NOT fire BeforePlayerDeadHook. To get a hazard_respawn event, hook On.GameManager.HazardRespawn or On.HeroController.DieFromHazard. Deaths in DREAM_WORLD or GODS_GLORY zones also do NOT fire BeforePlayerDeadHook (see new_findings).

### S5 [corrected] Save load / new game paths, entry gate, ModHooks order relative to SceneChanged
**Continue (load a save).** SaveSlotButton calls gm.LoadGameFromUI(slot), which starts LoadGameFromUIRoutine. That calls `LoadGame(int saveSlot, Action<bool> callback)`. Hooks fire in this order:
1. ModHooks.OnLoadLocalSettings, synchronously.
2. Inside the Platform.ReadSaveSlot callback, after PlayerData.instance has been replaced: ModHooks.OnAfterSaveGameLoad(saveGameData), i.e. `AfterSavegameLoadHook(SaveGameData)`.
3. ModHooks.OnSavegameLoad(saveSlot), i.e. `SavegameLoadHook(int)`.
4. The LoadGame callback.

On success, ContinueGame() runs RunContinueGame:
1. Fade out and wait 2.6 s.
2. SetState(LOADING) and isLoading = true.
3. Additively load and then unload "Knight_Pickup" through Unity SceneManager directly.
4. ReadyForRespawn(isFirstLevelForPlayer: true). This is BeginSceneTransition with SceneName = respawnScene, EntryGateName = "", Visualization = ContinueFromSave and IsFirstLevelForPlayer = true, which resets the game timer.

No ModHooks.SceneChanged fires on this path.

**New game.** UIManager or SaveSlotButton calls `GameManager.StartNewGame(bool permadeathMode = false, bool bossRushMode = false)`.
- Boss-rush mode goes through RunContinueGame.
- Otherwise RunStartNewGame calls BeginSceneTransition with SceneName = "Opening_Sequence", IsFirstLevelForPlayer = true and Visualization = Custom.

In Opening_Sequence, OpeningSequence loads Knight_Pickup and Tutorial_01 directly through Unity SceneManager and calls GameManager.OnWillActivateFirstLevel(). That method sets entryGateName = "top1" and state PLAYING, then fires ModHooks.OnNewGame (NewGameHook). After that come BeginScene and OnNextLevelReady.

The alternative path, CinematicPlayer (VideoType.OpeningCutscene) calling GameManager.LoadFirstScene(), runs OnWillActivateFirstLevel (NewGameHook), then LoadScene("Tutorial_01") (SceneChanged), then NewGameHook again. NewGameHook can fire twice there.
API: public void GameManager.LoadGame(int saveSlot, Action<bool> callback)
public void GameManager.LoadGameFromUI(int saveSlot)
public void GameManager.ContinueGame()
public IEnumerator GameManager.RunContinueGame()
public void GameManager.StartNewGame(bool permadeathMode = false, bool bossRushMode = false)
public IEnumerator GameManager.RunStartNewGame()
public void GameManager.OnWillActivateFirstLevel()
public IEnumerator GameManager.LoadFirstScene()
ModHooks: public static event Action<SaveGameData> AfterSavegameLoadHook; public static event Action<int> SavegameLoadHook; public static event Action NewGameHook;
CAVEAT: With AfterSavegameLoadHook the session starts while still in Menu_Title. The first gameplay scene_enter comes about 3 s or more later, at the end of the ReadyForRespawn transition. Do not open the session on SceneChanged. For new game, NewGameHook fires while the world scene is loading. Make session start idempotent, because NewGameHook can fire twice.

### S6 [needs_ingame_test] Stag travel initiation and code-side signals
In code, stag travel ends in the scene "Cinematic_Stag_travel" (Constants.STAG_CINEMATIC, also checked by GameManager.IsStagTravelScene()). That scene counts as a cinematic, non-gameplay scene, so BeginScene sets GameState.CUTSCENE.

The StagTravel component in that scene calls BeginSceneTransition with:
- SceneName = playerData "nextScene"
- EntryGateName = "door_stagExit"
- PreventCameraFadeOut = true
- Visualization = Custom

In async mode the info object is the private subclass StagTravelAsyncLoadInfo. The older CinematicPlayer path (VideoType.StagTravel) calls ChangeToScene(nextScene, "door_stagExit", 0f), which leads to LoadScene and so does fire SceneChanged.

No C# code writes PlayerData.nextScene, stagPosition or travelling. The station's PlayMaker FSM sets them and then starts the transition into Cinematic_Stag_travel, presumably with the PlayMaker BeginSceneTransition action.

Reliable code signals:
- (a) A BeginSceneTransition whose info.SceneName == "Cinematic_Stag_travel" means stag departure.
- (b) A BeginSceneTransition whose info.EntryGateName == "door_stagExit" means stag arrival.
- (c) SetPlayerStringHook with name "nextScene".
API: PlayerData: public bool travelling; public int stagPosition; public string nextScene;
Constants.STAG_CINEMATIC = "Cinematic_Stag_travel"
public bool GameManager.IsStagTravelScene()
StagTravel.Start -> BeginSceneTransition(EntryGateName="door_stagExit", SceneName=pd nextScene, Visualization=Custom)
CAVEAT: Test in-game: ride a stag between two stations and log every BeginSceneTransition, LoadScene and activeSceneChanged. Expected sequence: station scene, then Cinematic_Stag_travel (from FSM, Visualization unknown), then destination station with gate door_stagExit. Also check whether StagTravel or CinematicPlayer runs on PC 1.5.78 (Platform.Current.FetchScenesBeforeFade decides the StagTravel branch), and whether travelling/stagPosition are written through PlayerData.SetBool/SetInt so pd_change sees them.

### S7 [needs_ingame_test] Dream entry / dream gate in code
**Dream Gate warp.** `public void GameManager.WarpToDreamGate()` sets entryGateName = "dreamGate" and calls BeginSceneTransition with:
- SceneName = pd dreamGateScene
- EntryGateName = "dreamGate"
- PreventCameraFadeOut = true
- Visualization = Dream
- AlwaysUnloadUnusedAssets = true

On entry, FindEntryPoint uses dreamGateX/dreamGateY and EnterHero calls hero_ctrl.EnterSceneDreamGate(). No C# code calls WarpToDreamGate, so the Knight's dream-nail FSM calls it through PlayMaker.

**Dream returns.** They use the entry gate "door_dreamReturn". EnterHero swaps it for bossReturnEntryGate in the GODS_GLORY zone. BossDoorChallengeCompleteUI goes back to pd dreamReturnScene with Visualization Dream.

**Dream entry** (dream nail on a dream boss corpse or on Dream_* rooms) has no C# initiator. It is FSM-driven, presumably through the PlayMaker BeginSceneTransition action with Visualization Dream or GrimmDream.

Dream scenes are full scenes whose names start with "Dream_" (PlayerData lists Dream_01_False_Knight, Dream_Guardian_*, Dream_Abyss, Dream_Final_Boss...). The zone is MapZone.DREAM_WORLD. DreamPlant.cs is the Whispering Root and does not change scene.
API: public void GameManager.WarpToDreamGate()
PlayerData: public string dreamGateScene; public float dreamGateX; public float dreamGateY; public string dreamReturnScene; public string bossReturnEntryGate;
Entry gate names: "dreamGate", "door_dreamReturn"
SceneLoadVisualizations.Dream / GrimmDream / GodsAndGlory
GameManager.GetCurrentMapZone() == "DREAM_WORLD"
CAVEAT: Test in-game, logging BeginSceneTransition info (SceneName, EntryGateName, Visualization):
- dream-nail a dream boss entry (for example the False Knight corpse);
- die in the dream;
- warp with Dream Gate.
Check that Visualization==Dream for entry and that the return uses EntryGateName "door_dreamReturn". The reason classifier can use: Visualization in {Dream, GrimmDream}, OR SceneName starts with "Dream_", OR EntryGateName in {"dreamGate","door_dreamReturn"}.

### S8 [confirmed] Pause enter/leave, GameState enum, how to read current state
Pause is toggled by `public IEnumerator GameManager.PauseGameToggle()`. InputHandler starts it when pause is pressed while gameState is PLAYING or PAUSED and disablePause is false. PauseGameToggleByMenu() wraps it, and PauseToggleDynamicMenu(MenuScreen, bool) is a variant.

On pause it sets `isPaused = true`, SetState(GameState.PAUSED), HeroController.Pause() (cState.isPaused = true) and a time scale of 0 through TimeController.GenericTimeScale. On unpause it sets `isPaused = false`, SetState(PLAYING) and a time scale of 1.

Pause is refused while TimeSlowed (FreezeMoment in progress) or when playerData disablePause is set. disablePause is set during death, transitions, respawn and similar.

The enum is GlobalEnums.GameState: INACTIVE, MAIN_MENU, LOADING, ENTERING_LEVEL, PLAYING, PAUSED, EXITING_LEVEL, CUTSCENE, PRIMER.

To read the current state, use `GameManager.instance.gameState` (public field) and `GameManager.instance.isPaused` (public bool). The only setter is `public void SetState(GameState newState)`, so On.GameManager.SetState captures every state change, pause included.
API: public GameState GameManager.gameState; public bool GameManager.isPaused;
public void GameManager.SetState(GameState newState)
public IEnumerator GameManager.PauseGameToggle()
public IEnumerator GameManager.PauseGameToggleByMenu()
public IEnumerator GameManager.PauseToggleDynamicMenu(MenuScreen screen, bool allowUnpause = false)
public enum GlobalEnums.GameState { INACTIVE, MAIN_MENU, LOADING, ENTERING_LEVEL, PLAYING, PAUSED, EXITING_LEVEL, CUTSCENE, PRIMER }
HeroControllerStates.isPaused; TimeController.GenericTimeScale (static float)
CAVEAT: For pause/unpause events, hooking On.GameManager.SetState and emitting on transitions into or out of PAUSED is simpler than wrapping the IEnumerator. Opening the inventory or map does NOT pause: the state stays PLAYING.

### S9 [corrected] PlayerData.playTime type and where incremented
`PlayerData.playTime` is a `public float` in seconds. It is NOT incremented continuously. GameManager.Update calls `IncreaseGameTimer(ref sessionPlayTimer)` every frame, and that adds Time.unscaledDeltaTime only when gameState is PLAYING, ENTERING_LEVEL or EXITING_LEVEL and unscaledDeltaTime < 1 s. So PAUSED, LOADING, CUTSCENE and MAIN_MENU time is excluded, and so are single frames of 1 s or longer.

The live total is `GameManager.PlayTime` (public float, = sessionStartTime + sessionPlayTimer). playerData.playTime only grows inside SaveGame, which adds sessionPlayTimer and then calls ResetGameTimer.

ResetGameTimer (private) also runs in orig_BeginSceneTransition when IsFirstLevelForPlayer is true, i.e. continue or new game. That sets sessionStartTime from playerData.playTime.

BossSceneController also reuses IncreaseGameTimer for BossSequenceController.Timer.
API: public float PlayerData.playTime;
public float GameManager.PlayTime { get; }  // sessionStartTime + sessionPlayTimer
public void GameManager.IncreaseGameTimer(ref float timer)
private void GameManager.ResetGameTimer()
CAVEAT: Record GameManager.instance.PlayTime, not playerData.playTime, if a live save-clock is wanted. Note that the game's own clock counts ENTERING_LEVEL and EXITING_LEVEL (transitions) but not LOADING.

### S10 [confirmed] Reliable 'scene loaded and hero in control' and 'leaving scene' signals
**Entering signals:**
- `GameManager.FinishedEnteringScene()` (public) sets PLAYING and hasFinishedEnteringScene, then fires the instance event `GameManager.OnFinishedEnteringScene` (EnterSceneEvent). HeroController's private FinishedEnteringScene(bool, bool) calls it at the end of EnterScene, EnterSceneDreamGate, Respawn and HazardRespawn, after AcceptInput(). GameManager.EnterHero also calls it directly on respawn and hazard respawn, so it can fire twice per entry and also fires on hazard respawn with no scene change.
- `GameManager.OnFinishedSceneTransition` (instance event SceneTransitionFinishEvent) fires once per BeginSceneTransition, in sceneLoad.Finish after OnNextLevelReady, so the hero is not necessarily in control yet.
- Polling getters: `GameManager.HasFinishedEnteringScene`, `IsInSceneTransition`, `IsLoadingSceneTransition`, `HeroController.acceptingInput` (public bool), `transitionState` (HeroTransitionState: WAITING_TO_TRANSITION, EXITING_SCENE, WAITING_TO_ENTER_LEVEL, ENTERING_SCENE, DROPPING_DOWN) and `cState.transitioning`.

Recommended scene_enter: the first OnFinishedEnteringScene, or On.GameManager.FinishedEnteringScene, after a scene change, deduplicated by scene name and filtered with hazard-respawn flags.

**Leaving signals**, in order inside BeginSceneTransitionRoutine:
1. `static event GameManager.SceneTransitionBegan(SceneLoad)` fires with SceneLoad.TargetSceneName. SceneLoad also exposes GetDuration(SceneLoad.Phases), useful for load times.
2. The `UnloadingLevel` instance event fires synchronously, while the old scene is still loaded, right after SetState(EXITING_LEVEL) and hero_ctrl.LeaveScene. It also fires in LoadScene, LoadSceneAdditive and ReturnToMainMenu.
3. The on-call signals of `On.GameManager.BeginSceneTransition` and `ModHooks.BeforeSceneLoadHook` (Func<string,string>, must return its input) fire before both of those.

'NextSceneWillActivate' does NOT exist. The closest is the SceneLoad.WillActivate event on the SceneLoad passed to SceneTransitionBegan.

The Unity event `UnityEngine.SceneManagement.SceneManager.activeSceneChanged` (also used by GameManager.LevelActivated and by Mod.cs) fires on every active-scene change, including the BeginSceneTransition path when the old scene is unloaded in ActivationComplete.
API: public event GameManager.EnterSceneEvent OnFinishedEnteringScene;  // delegate void EnterSceneEvent()
public event GameManager.SceneTransitionFinishEvent OnFinishedSceneTransition;
public event GameManager.UnloadLevel UnloadingLevel;  // delegate void UnloadLevel()
public static event GameManager.SceneTransitionBeganDelegate SceneTransitionBegan;  // delegate void (SceneLoad sceneLoad)
public void GameManager.FinishedEnteringScene()
public bool GameManager.HasFinishedEnteringScene / IsInSceneTransition / IsLoadingSceneTransition {get;}
public float GameManager.CurrentLoadDuration {get;}
SceneLoad: public string TargetSceneName; events FetchComplete, WillActivate, ActivationComplete, Complete, StartCalled, BossLoaded, Finish; public float? GetDuration(SceneLoad.Phases)
HeroController: public bool acceptingInput; public HeroTransitionState transitionState; public TransitionPoint sceneEntryGate {get;}
CAVEAT: Instance events (OnFinishedEnteringScene, UnloadingLevel, OnFinishedSceneTransition) are lost when QuitToMenu destroys the GameManager (see new_findings). Prefer On.GameManager.FinishedEnteringScene, which is static and survives, or re-subscribe when GameManager.instance changes. Test in-game the exact timing of activeSceneChanged against SceneLoad.Complete, because SceneLoad loads additively and the active scene presumably changes when the old scene is unloaded.

### S11 [confirmed] What runs on application quit
The patched `private void GameManager.OnApplicationQuit()` calls orig_OnApplicationQuit (only restores gameConfig.hideLanguageOption) and then `ModHooks.OnApplicationQuit()`. That invokes every `ModHooks.ApplicationQuitHook` subscriber (Action), each in its own try/catch. Mod.cs subscribes SaveGlobalSettings to it for every mod.

The game does NOT save on quit from the desktop menu. GameManager.QuitGame() (UIManager quit button) only calls StoryRecord_quit, fades, and calls Application.Quit(). Quitting from in-game normally goes through ReturnToMainMenu (which saves) first, and only then through Menu_Title.
API: public static event Action ModHooks.ApplicationQuitHook;
private void GameManager.OnApplicationQuit()  // orig + ModHooks.OnApplicationQuit()
public IEnumerator GameManager.QuitGame()
CAVEAT: OnApplicationQuit is a Unity message on whichever GameManager is alive (the menu one, if the player returned to the menu first). It does not run on a crash or on a forced kill from Task Manager, so session.json must also be closed on ReturnToMainMenu and rewritten periodically. Also usable: Application.quitting (UnityEngine, Unity 2018.1+).

### S12 [confirmed] Scene name access, additive/boss scenes
The public string fields are `GameManager.sceneName`, `nextSceneName` and `entryGateName`.

`public string GetSceneNameString()` calls UpdateSceneName() and returns sceneName. UpdateSceneName sets sceneName = GetBaseSceneName(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name). `public static string GetBaseSceneName(string)` strips the suffixes "_boss_defeated", "_boss" and "_preload". Note: IsGameplayScene, IsMenuScene and the other Is*Scene helpers all call UpdateSceneName, which overwrites sceneName.

**Additive sub-scenes.** Scenes like X_boss and X_preload are loaded additively by SceneAdditiveLoadConditional (LoadSceneAsync additive, never set active). The active scene stays the room, and the base name maps them back anyway.

**Full scenes.** Godhome (GG_*, for example GG_Atrium and the GG_ boss rooms in Hall of Gods and the pantheons) and dream rooms (Dream_*) are ordinary single active scenes loaded through BeginSceneTransition. Pantheon steps set customExitPoint.targetScene or an FSM "To Scene" variable through BossSequenceController. The map zone is available through GameManager.GetCurrentMapZone(), i.e. the room's SceneManager component mapZone (GlobalEnums.MapZone, including DREAM_WORLD and GODS_GLORY).

Non-gameplay scenes (menu, cutscenes, Cinematic_Stag_travel, Knight_Pickup, Quit_To_Menu...) are listed in IsNonGameplayScene and IsCinematicScene. There is a name clash: the game has its own class `SceneManager` (MonoBehaviour, field `GameManager.sm`). Inside mod code, always fully qualify UnityEngine.SceneManagement.SceneManager.
API: public string GameManager.sceneName; public string GameManager.nextSceneName; public string GameManager.entryGateName;
public string GameManager.GetSceneNameString()
public void GameManager.UpdateSceneName()
public static string GameManager.GetBaseSceneName(string fullSceneName)
public bool GameManager.IsGameplayScene() / IsNonGameplayScene() / IsMenuScene() / IsCinematicScene()
public string GameManager.GetCurrentMapZone()  // sm.mapZone.ToString(); sm may be null outside gameplay
UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
CAVEAT: GetCurrentMapZone() dereferences sm, which can be null (for example in the menu), so wrap it in try/catch. For multi-scene states (boss sub-scene loaded), UnityEngine.SceneManagement.SceneManager.sceneCount/GetSceneAt can list the extra scenes if wanted.

### NEW-N1 [corrected] Does ModHooks.SceneChanged fire for normal scene transitions? (plan phase 1 relies on it)
No, it does not. The plan's assumption breaks here. ModHooks.OnSceneChanged is called only from the patched GameManager.LoadScene and GameManager.LoadSceneAdditive. BeginSceneTransition is the path for every gate, door, death respawn, continue-from-save, stag, dream and Godhome transition. It only calls BeforeSceneLoad and loads through SceneLoad (UnityEngine LoadSceneAsync), so SceneChanged NEVER fires for those.

Instead, use these for scene_enter and scene_exit:
- `UnityEngine.SceneManagement.SceneManager.activeSceneChanged` (Scene from, Scene to), for the scene change itself;
- On.GameManager.BeginSceneTransition plus GameManager.SceneTransitionBegan, for the exit and its reason;
- On.GameManager.FinishedEnteringScene or OnFinishedEnteringScene, for 'hero in control'.

SceneChanged can stay only as a secondary signal (it covers LoadScene-only paths: Tutorial_01, credits, PermaDeath, ChangeToScene). The phase-6 catalog trigger 'En SceneChanged' must also move to activeSceneChanged or FinishedEnteringScene.
API: UnityEngine.SceneManagement.SceneManager.activeSceneChanged += (Scene from, Scene to) => ...
On.GameManager.BeginSceneTransition += (orig, self, info) => {...; orig(self, info);}
CAVEAT: In LoadScene, OnSceneChanged fires right after the synchronous SceneManager.LoadScene call, before the new scene's objects exist (Unity activates it at the end of the frame). So even where it fires, it is too early for catalogs.

### NEW-N2 [confirmed] Returning to menu destroys GameManager/HeroController and resets PlayerData
QuitToMenu.Start (scene Quit_To_Menu) does the following:
- destroys the UIManager, HeroController, GameCameras and GameManager GameObjects;
- calls playerData.Reset() and sceneData.Reset();
- sets TimeController.GenericTimeScale = 1;
- loads Menu_Title.

Consequences:
- (a) Any subscription to GameManager INSTANCE events (OnFinishedEnteringScene, UnloadingLevel, OnFinishedSceneTransition) is lost when the session ends. Use static On.* hooks or the static SceneTransitionBegan, or re-subscribe each time GameManager.instance changes.
- (b) The final progress snapshot must be taken before that point, e.g. in On.GameManager.ReturnToMainMenu or in SavegameSaveHook during it. Taking it at activeSceneChanged to Menu_Title is too late.
- (c) Cached references to GameManager.instance or HeroController.instance must not be held across sessions.

### NEW-N3 [confirmed] BeforePlayerDeadHook misses deaths in dream and Godhome
In HeroController.Die(), if GetCurrentMapZone() is "DREAM_WORLD" or "GODS_GLORY", the coroutine hands off to heroDeathPrefab (FSM) and returns before StartCoroutine(gm.PlayerDead(...)). So BeforePlayerDeadHook and AfterPlayerDeadHook never fire for dream-boss, Hall of Gods or pantheon deaths, and no shade is created.

For a complete `death` event, use the `HeroController.instance.OnDeath` event (fires first in Die(), in all zones) or On.HeroController.Die. Keep BeforePlayerDeadHook only to tell apart a real death with shade and bench respawn. Do not re-subscribe OnDeath on a new HeroController instance without unsubscribing first (see N2).

### NEW-N4 [needs_ingame_test] Proposed scene_exit reason classifier based on SceneLoadInfo (replaces several [verificar] signals in plan section 6)
Inside On.GameManager.BeginSceneTransition, before calling orig, read info and the GameManager state, and classify the exit in this order:
1. `self.RespawningHero && !info.IsFirstLevelForPlayer`: death (bench respawn). ReadyForRespawn sets RespawningHero = true before the call.
2. `info.IsFirstLevelForPlayer`: save_load if Visualization==ContinueFromSave, new_game if SceneName=="Opening_Sequence".
3. `info.SceneName=="Cinematic_Stag_travel"` or `info.EntryGateName=="door_stagExit"`: stag.
4. `info.EntryGateName=="dreamGate"` or Visualization in {Dream, GrimmDream}, or SceneName starts with "Dream_", or EntryGateName=="door_dreamReturn": dream.
5. Visualization==GodsAndGlory or SceneName starts with "GG_": godhome.
6. Otherwise: door/gate. Use TransitionPoint.lastEntered for edge gates, which is set just before the call. FSM doors do not set lastEntered.

quit_menu: On.GameManager.ReturnToMainMenu. That path does not go through BeginSceneTransition.

hazard_respawn: On.GameManager.HazardRespawn. There is no scene change.

The load time per transition is available from the SceneLoad given by GameManager.SceneTransitionBegan (GetDuration per Phases), or from GameManager.CurrentLoadDuration. This covers the plan's 'tiempos de carga' item.
CAVEAT: Test each case on purpose in phase 1, as the plan already says. FSM-originated transitions (doors, stag station, dream entry) send the values chosen in the FSM, so log the raw info fields (SceneName, EntryGateName, Visualization, PreventCameraFadeOut, info.GetType().Name) in every scene_exit payload. Then the reason can be recomputed in Python if the classifier is wrong.

### NEW-N5 [confirmed] t_game definition vs game's own clock and timeScale
The plan defines t_game as unscaledDeltaTime accumulated while not paused and not loading. The game's own clock (IncreaseGameTimer) counts unscaled time in PLAYING, ENTERING_LEVEL and EXITING_LEVEL, and drops frames of 1 s or longer. To stay comparable with the save's playTime, t_game could use the same predicate (gameState in {PLAYING, ENTERING_LEVEL, EXITING_LEVEL}), with gameState and isPaused stored per event.

Gameplay slow-motion (FreezeMoment on hits) changes TimeController.GenericTimeScale, so Time.deltaTime differs from unscaledDeltaTime during hit-stop. Keep unscaled time for t_game. Optionally record TimeController.GenericTimeScale in frames.

### NEW-N6 [confirmed] Hooking value-returning / IEnumerator methods safely
Several of the targets are coroutines returning IEnumerator: GameManager.ReturnToMainMenu, PlayerDead, PauseGameToggle, and HeroController.Respawn and HazardRespawn. An On.* hook must return orig(self, ...) unchanged and record only at call time. Wrapping the enumerator would change iteration semantics.

ModHooks.BeforeSceneLoadHook is a Func<string,string>, so it must return its input unchanged (CLAUDE.md principle 3).

The repo-wide Grep tool returned nothing under reference/, probably because reference/ is gitignored and ripgrep honours .gitignore. Searches must target the subfolder explicitly or use grep -r.

## VERIFIER non-confirmed
- S1 partially_wrong: Nearly all of it holds. Confirmed: the signature (GameManager.cs:466), the patch order BeforeSceneLoad then orig (467-468), orig_BeginSceneTransition (GameManager.cs:3263-3288: INVENTORY CANCEL, ResetGameTimer when IsFirstLevelForPlayer, StartCoroutine(BeginSceneTransitionRoutine)), the early return when sceneLoad != null (472-477), the SceneLoadInfo fields and virtual methods (GameManager.cs:49-82), the enum values (38-47) and every listed C# caller. One error: ChangeToScene (GameManager.cs:646-665) calls `LeftScene();` with the default doAdditiveLoad=false, so it always goes through LoadScene, never LoadSceneAdditive. Only the deprecated TransitionScene(TransitionPoint) calls `LeftScene(doAdditiveLoad: true)` (GameManager.cs:643), and it has no callers. Omissions: (1) orig_BeginSceneTransition can also force AlwaysUnloadUnusedAssets=true for BossSequence or BossStatueLoadManager (3269-3285). (2) More LoadScene paths exist: CutsceneHelper NextScene.SpecifyScene `GameManager.instance.LoadScene(nextScene)` (CutsceneHelper.cs:93), End_Game_Completion (CutsceneHelper.cs:102), Cinematic_MrMushroom (GameManager.cs:1535) and PermaDeath_Unlock (GameManager.cs:1523). (3) The PlayMaker actions LoadLevel and RestartLevel (HutongGames/PlayMaker/Actions/LoadLevel.cs:60,71; RestartLevel.cs:14) load scenes directly through UnityEngine SceneManager, bypassing both GameManager paths. Whether any FSM uses them needs an in-game test.
- S2 partially_wrong: `OnBeforeTransition` is NOT static. It is an instance event: `public event BeforeTransitionEvent OnBeforeTransition;` (TransitionPoint.cs:78), and it fires right after lastEntered is set (135-139). `TransitionPoints` (static property, line 76) and `lastEntered` (static string, line 74) are static, as claimed. Missing condition: the transition only runs if both targetScene and entryPoint are non-empty (`if (!string.IsNullOrEmpty(targetScene) && !string.IsNullOrEmpty(entryPoint))`, line 112); otherwise it only logs an error. GetGatePosition can also return GatePosition.unknown (TransitionPoint.cs:209-210). Also, lastEntered is never reset anywhere, so it keeps a stale value across FSM-driven transitions. The rest is confirmed: fields, private [SerializeField] alwaysUnloadUnusedAssets (43-44), early-return conditions (108), SceneLoadInfo contents (140-151) and the OnTriggerStay2D retry (159-165). Whether doors (isADoor) use the PlayMaker BeginSceneTransition action is FSM-driven and needs an in-game test.
- S8 partially_wrong: SetState is NOT the only writer of gameState. CinematicPlayer writes the public field directly: `gm.gameState = GameState.CUTSCENE;` (CinematicPlayer.cs:254) and `gm.gameState = GameState.PLAYING;` (CinematicPlayer.cs:413). So On.GameManager.SetState misses those changes, and gameState should also be polled. Also, TimeSlowed blocks unpausing as well, not only pausing (`if (TimeSlowed) yield break;` at GameManager.cs:2834). disablePause is checked only on the pause branch (2838). The Modding API also patches PauseToggleDynamicMenu, which sets isPaused (modding-api/Assembly-CSharp/Patches/GameManager.cs:632-660). The rest is confirmed: GameState enum (GlobalEnums/GameState.cs:3-14), fields (GameManager.cs:105-107), PauseGameToggle (2832-2876), SetTimeScale (2889-2896), InputHandler.cs:286-288 and HeroController.Pause (3338-3344).
- S10 partially_wrong: The event order is reversed. Inside BeginSceneTransitionRoutine, UnloadingLevel fires FIRST (GameManager.cs:515-518, right after SetState(EXITING_LEVEL) at 499 and LeaveScene at 504). SceneTransitionBegan fires AFTER, once the sceneLoad delegates are wired (GameManager.cs:581-592). Other corrections: (1) SceneLoad.TargetSceneName is a read-only property (`public string TargetSceneName => targetSceneName;`, SceneLoad.cs:49), not a field. (2) AcceptInput() inside HeroController.FinishedEnteringScene is conditional on enterWithoutInput, exitedSuperDashing and exitedQuake (HeroController.cs:3900-3910). (3) activeSceneChanged on the BeginSceneTransition path relies on Unity choosing a new active scene after `SceneManager.UnloadScene(lastSceneName)` (GameManager.cs:537); no SetActiveScene call exists anywhere in Assembly-CSharp, so this needs an in-game test. The rest is confirmed: FinishedEnteringScene (1743-1752), the double call from EnterHero (1640-1647) and HeroController (3911), OnFinishedSceneTransition in Finish (569-580), the getters (271-292), the HeroTransitionState enum and acceptingInput (484). NextSceneWillActivate does not exist anywhere under reference/.
- S12 partially_wrong: The non-gameplay list is wrong in two places. IsNonGameplayScene compares against "Knight Pickup" with a SPACE (GameManager.cs:2152), but the real scene is "Knight_Pickup" (3025), so Knight_Pickup is NOT classified as non-gameplay. "Quit_To_Menu" does not appear in IsNonGameplayScene or IsCinematicScene at all (GameManager.cs:2145-2265); it is referenced only at 3101 and 3195. GetCurrentMapZone returns a string (`sm.mapZone.ToString()`, line 1976), where sm.mapZone is `public MapZone mapZone` (SceneManager.cs:19). The rest is confirmed: fields (111-115), SubSceneNameSuffixes (221), UpdateSceneName and GetBaseSceneName (2009-2026), GetSceneNameString (2030), SceneAdditiveLoadConditional additive load (351), no SetActiveScene anywhere, the game's own SceneManager class (SceneManager.cs:10) and BossSequenceController.cs:380.
- N4 partially_wrong: Gaps in the proposed classifier: (1) hazard_respawn: KillOnContact calls `StartCoroutine(component3.HazardRespawn())` on HeroController directly (KillOnContact.cs:22), bypassing GameManager.HazardRespawn. Hook On.HeroController.HazardRespawn (HeroController.cs:2971), or both. (2) TransitionPoint.lastEntered is never reset (TransitionPoint.cs:74, 135), so for FSM-driven transitions it holds a STALE gate name. Use it only when the transition was triggered from TransitionPoint.OnTriggerEnter2D, e.g. via a flag set in On.TransitionPoint.OnTriggerEnter2D. (3) Boss-rush new game goes through RunContinueGame and then ReadyForRespawn(true) with Visualization ContinueFromSave (GameManager.cs:2978-2981, 3031), so rule 2 classifies it as save_load. (4) Godhome returns from BossDoorChallengeCompleteUI use Visualization.Dream (BossDoorChallengeCompleteUI.cs:323), so rule 4 classifies them as dream before rule 5 can say godhome. (5) CurrentLoadDuration returns 0 once isLoading is false (GameManager.cs:283-292), and isLoading is cleared in sceneLoad.Finish (around 571), so it must be sampled before Finish. SceneLoad.GetDuration (SceneLoad.cs:101-108) is more reliable.