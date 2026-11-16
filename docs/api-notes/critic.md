**HKTelemetry: what is still missing or unresolved for phases 1–8**

All paths are relative to `C:/Users/alexd/Desktop/MASTER IA/HKTelemetry/`. `dec/` means `reference/decompiled/Assembly-CSharp/`.

## A. Brief data fields with no confirmed source

1. **session.json: game percentage.** The field exists, but its value is stale between saves.
   - `dec/PlayerData.cs:2898` `public float completionPercentage;`
   - It is only recomputed during SaveGame: `dec/GameManager.cs:2480` `playerData.CountGameCompletion();`
   - The end-of-session snapshot is fresh after ReturnToMainMenu, which saves first. After Alt-F4 or a crash it is stale.
   - The mod must not call CountGameCompletion itself: it writes PlayerData (`dec/PlayerData.cs:5087` `SetFloatSwappedArgs(0f, "completionPercentage")`).
   - Fix: snapshot it after the save, and recompute it in Python from the snapshot fields.
2. **session.json: nail, spells and abilities snapshot.** Resolved by grep; these fields were never listed by the sweep.
   - `dec/PlayerData.cs:134` `public int nailDamage;`, `:666` `nailSmithUpgrades`, `:152/154/156` `fireballLevel/quakeLevel/screamLevel`.
   - `:158-164` `hasNailArt/hasCyclone/hasDashSlash/hasUpwardSlash`, `:168` `hasDreamNail`, `:184-194` `hasDash/hasWalljump/hasSuperDash/hasShadowDash/hasAcidArmour/hasDoubleJump`, `:220` `hasKingsBrand`, `:960` `charmsOwned`, `:41` `permadeathMode`.
3. **session.json: save hash or name.** Not covered by the sweep.
   - The file name logic is `internal`: `reference/decompiled/Assembly-CSharp/Modding/ModHooks.cs:1817` `internal static string GetSaveFileName(int saveSlot)`.
   - Proposal: build `persistentDataPath/user{slot}.dat` ourselves (per M11) and hash the file on the writer thread.
   - The save file has no "save name" field.
4. **session.json: runtime game version.** There is no reliable detector.
   - ModHooks.version and GAME_VERSION are literals baked into the installed DLL (verifier M7).
   - The best extra signals are `Application.unityVersion` (used at `dec/InControl/VersionInfo.cs:51` `Regex.Match(Application.unityVersion, ...)`) and `BuildMetadata.Embedded` (`dec/BuildMetadata.cs:39-51`, which reads `StreamingAssets/BuildMetadata.json` and may be null).
   - Phase 8 needs a decision: if the API loads at all, the DLL is 1.5.78, so the check reduces to logging unityVersion.
5. **heal / spell_cast type and soul spent.** Entirely driven by the "Spell Control" FSM.
   - `dec/HeroController.cs:830` `spellControl = FSMUtility.LocateFSM(..., "Spell Control");`
   - No C# code calls `SetCState(` (grep returned 0 hits).
   - needs_ingame_test: does fireball/dive/scream show as an MPCharge drop of 33/24 together with cState.spellQuake/casting? Can `spellControl.ActiveStateName` be polled (state names unknown)?
6. **nail_art (plan point 9).** There is no C# trigger.
   - Only `StartCyclone()/EndCyclone()` exist (`dec/HeroController.cs:3010-3018`), and nothing in code calls them (grep returned 0 hits). They are FSM-driven.
   - Charge state is visible: `cState.nailCharging` (`dec/HeroController.cs:6233` `cState.nailCharging = true;`).
   - needs_ingame_test: the HitInstance `Source.name` of Great, Dash and Cyclone Slash, which is asset data.
7. **hit_dealt "tipo de ataque (amuleto)".** Code only gives AttackType.
   - Charm sources (thorns, Grimmchild, weaverlings, Glowing Womb hatchlings) must be told apart by Source name or parent name.
   - needs_ingame_test: record the names once in debug mode.
8. **death "geo perdido" and shade_killed "geo recuperado".** Both FSM-driven (C7, C8).
   - Unresolved: the write order of geoPool, geo and soulLimited.
   - Fallback without a test: at the On.HeroController.Die call, record pd.geo before the death FSM runs. Recovered geo = the geo delta when soulLimited goes true→false.
9. **bench_rest.** The 'Bench Control' FSM is only located by name in code (`dec/HeroController.cs:2914` `LocateFSM(..., "Bench Control")`).
   - needs_ingame_test: does `SetPlayerBoolHook("atBench")` fire?
10. **item_pickup.** No code class exists (K9).
    - Not designed yet: a rule that derives it from pd_change (gotCharm_N, has*, heartPieces, vesselFragments, keys, trinkets, ore...).
    - Decide whether the mod emits it or Python derives it. Principle 4 favours Python.
11. **Boss and gauntlet arena entry (plan point 9).** FSM-only outside Godhome (C14). needs_ingame_test.
12. **Scene catalog: hazards.** `DamageHero` alone misses hazards defined by the "damages_hero" FSM (verifier K6, `dec/HeroBox.cs:47-58`). The catalog needs both sources.
13. **Phase 7: title-screen line.** No mechanism has been chosen yet.
    - The API's own text is built once at load: `reference/modding-api/Assembly-CSharp/ModLoader.cs:422` `modVersionDraw.drawString = builder.ToString();`. GetVersion() is therefore static and cannot show live status.
    - Use our own canvas while the scene is Menu_Title.
14. **Phase 8: zip export.** The required DLLs exist: the game's Managed folder contains `System.IO.Compression.dll` and `System.IO.Compression.FileSystem.dll` (directory listing). The csproj needs references to them.
    - This was checked while the install was at about 75%; recheck once the download completes.

## B. scene_exit reasons without a reliable signal

1. **Death in DREAM_WORLD or GODS_GLORY.**
   - Die() returns before PlayerDead (`dec/HeroController.cs:3943-3953`). The way back is an FSM transition (presumably `door_dreamReturn`), so the classifier would label it `dream` or `godhome`, not `death`.
   - Fix: set a pending_death flag in On.HeroController.Die and consume it in the next BeginSceneTransition.
2. **quit_menu can abort.**
   - `dec/GameManager.cs:3054` `if (saveMode == ReturnToMainMenuSaveModes.SaveAndCancelOnFail && !saveComplete.Value)`
   - Confirm the exit only when the next scene is `Quit_To_Menu` (`:3101` LoadSceneAsync, `:3195` EmergencyReturnToMenu).
   - ReturnToMainMenu is also called after endings (`dec/CutsceneHelper.cs:96,122`, `dec/GameManager.cs:1527`), so those need a separate `ending` reason.
3. **Emergency return to menu (engagement lost).** It never goes through ReturnToMainMenu (`dec/GameManager.cs:426`, `private void EmergencyReturnToMenu` at `:3150`). It can only be detected by a scene change to Quit_To_Menu.
4. **Steel Soul permadeath.** `dec/GameManager.cs:3348` `LoadScene("PermaDeath");`. No reason or session end is defined for it.
5. **Godseeker / boss-rush new game.** It fires neither NewGameHook nor SavegameLoadHook, so no session opens.
   - `dec/GameManager.cs:2978-2981` `if (bossRushMode) { playerData.AddGGPlayerDataOverrides(); StartCoroutine(RunContinueGame()); }`
   - Fix: hook `On.GameManager.StartNewGame(bool permadeathMode, bool bossRushMode)` (`:2967`), which covers every new game.
6. **FSM-driven doors (isADoor).** `lastEntered` is stale for them. Resolvable without an FSM test: in On.GameManager.BeginSceneTransition, find the gate in `TransitionPoint.TransitionPoints` (`dec/TransitionPoint.cs:76`) of the active scene whose `targetScene` (`:36`) == info.SceneName and `entryPoint` (`:38`) == info.EntryGateName.
7. **Dream-nail entry into dream bosses or Dream_* rooms.** No C# initiator. Classified only by Visualization or name rules, which collide with Godhome returns (`dec/BossDoorChallengeCompleteUI.cs:320-323` uses Visualization Dream). needs_ingame_test.
8. **Non-gameplay scenes** (Knight_Pickup, Opening_Sequence, Cinematic_Stag_travel, Quit_To_Menu). There is no policy yet on whether they produce scene_enter/exit.
   - IsNonGameplayScene has a bug: it checks "Knight Pickup" with a space (verifier S12, `dec/GameManager.cs:2152`). Use our own list.
9. **The plan's "bench_respawn" reason** has no distinct signal. It is the scene_enter after a death or save_load exit, not an exit reason. Redefine it as an entry reason.

## C. Contradictions or open technical points the plan must absorb

1. **Plan phase 1 relies on `ModHooks.SceneChanged`, which never fires on the BeginSceneTransition path (NF1).**
   - The proposed replacement, activeSceneChanged, needs an in-game test.
   - Better, confirmed in code: `public void OnNextLevelReady()` (`dec/GameManager.cs:1492`) is reached from all three paths:
     - BeginSceneTransition: `:575` `OnNextLevelReady();`
     - LoadSceneAdditive: `:1487`
     - LevelActivated (activeSceneChanged): `:447`
   - Use On.GameManager.OnNextLevelReady (after orig) as the scene_enter choke point and dedupe by scene name.
2. **Event subscriptions.** GameManager instance events are lost on QuitToMenu (N2). Use static hooks:
   - On.*
   - `public static event SceneTransitionBeganDelegate SceneTransitionBegan` (`dec/GameManager.cs:337`)
3. **Death signal.** The plan uses BeforePlayerDeadHook. Replace it with On.HeroController.Die, which is static and needs no re-subscription. Note that OnDeath can repeat.
4. **hit_dealt sources.** Replace the plan's HitInstanceHook/SlashHitHook with On.HealthManager.TakeDamage, Invincible, ApplyExtraDamage and Die.
   - That set still misses Flukenest damage (SpellFluke.cs:92), so add a per-HealthManager hp diff.
   - hasSpecialDeath kills need OnReceiveDeathEventHook, deduplicated by instance id.
5. **max_hp in frames.** Use `CurrentMaxHealth` (`dec/PlayerData.cs:3084`), not maxHealth, because of Godhome bindings.
6. **t_game predicate.** Use gameState in {PLAYING, ENTERING_LEVEL, EXITING_LEVEL} plus isPaused, never timeScale (hit-stop also sets timeScale to 0).
   - CinematicPlayer writes gameState directly, so poll it as well as hooking SetState.
7. **Phase 4 sampler.** Use our own DontDestroyOnLoad MonoBehaviour plus `HeroController.UnsafeInstance`, not HeroUpdateHook plus `.instance`.
8. **Phase 3 complement.** pd_change needs a baseline snapshot plus a low-rate diff, because these write PlayerData with no hook:
   - SetupNewPlayerData, AddGGPlayerDataOverrides, Reset, deserialization on load, UpgradeSaveData
   - in-place list mutations
9. **Remove `BlueHealthHook` from plan point 9.** Subscribing alters gameplay. Use SetPlayerIntHook('healthBlue') instead.
10. **Blocker for building or verifying On.* names.** `MMHOOK_Assembly-CSharp.dll` is not on disk until the Modding API is installed. That includes the private-method hooks HealthManager.TakeDamage, HealthManager.Invincible and HeroController.Die, and the overload suffixes.

## D. Still needs in-game tests (consolidated)

- Shade death and kill write order.
- atBench.
- Spell and focus FSM signals.
- Nail-art and charm Source names.
- Dream-nail entry.
- Door FSM → BeginSceneTransition action.
- Battle Control arenas.
- GeoRock hits.
- FSM wall and lever saving when the object deactivates itself.
- Whether activeSceneChanged fires on BeginSceneTransition (moot if OnNextLevelReady is used).
- HeroUpdateHook during cinematics.