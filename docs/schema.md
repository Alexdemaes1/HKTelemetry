# HKTelemetry: esquema de datos (schema_version 3)

> Fuente de verdad del formato. Cualquier cambio incrementa `schema_version` y se anota en el
> changelog del final. Cada campo indica **de dónde sale** en el juego. Las referencias
> `archivo:línea` apuntan a `reference/decompiled/Assembly-CSharp/`. El detalle completo, con
> evidencias, está en [`api-notes/`](api-notes/).
>
> Marcas: **[test]** = depende de un FSM de PlayMaker y hay que confirmarlo jugando (lista en la
> sección 9). **[derivado]** = el mod lo emite por comodidad, pero se puede recalcular en Python
> desde datos crudos.

## 1. Carpeta de salida

`%USERPROFILE%/AppData/LocalLow/Team Cherry/Hollow Knight/HKTelemetry/<yyyyMMdd_HHmmss>_<player_id>_<8 primeros del session_id>/`
(`Application.persistentDataPath`, la misma carpeta de los saves y de `ModLog.txt`).

| Fichero | Durante la sesión | Al cerrar |
|---|---|---|
| `session.json` | reescritura atómica cada 60 s | final, con `end` |
| `events.jsonl` | append plano | `events.jsonl.gz` |
| `frames.csv` | append plano | `frames.csv.gz` |
| `inputs.csv` | append plano | `inputs.csv.gz` |
| `scenes/<scene>.json` | una vez por escena y sesión | sin cambios |

Una sesión que no llegó a cerrarse (crash) se comprime y se marca `end.reason = "recovered"`
al arrancar el juego la siguiente vez.

## 2. Relojes (en todos los ficheros)

| Campo | Tipo | Definición |
|---|---|---|
| `t_real` | float s | segundos desde `start_utc` de la sesión (`Stopwatch`) |
| `t_game` | float s | suma de `Time.unscaledDeltaTime` en los frames con `gameState ∈ {PLAYING, ENTERING_LEVEL, EXITING_LEVEL}` y `isPaused == false`. Es el mismo criterio que el reloj del juego (`GameManager.IncreaseGameTimer`), así que excluye pausa, carga, cinemáticas y menú |
| `frame` | int | `Time.frameCount` |
| `play_time` | float s | `GameManager.PlayTime` (reloj del save en vivo). Solo en eventos y frames, como control cruzado |

- **Nunca** se deduce la pausa de `Time.timeScale == 0`: el *hit-stop* al recibir o dar golpes
  también lo pone a 0 (`GameManager.FreezeMoment`). Se usan `GameManager.isPaused` y `gameState`.
- `gameState` se sondea cada frame además de engancharse a `SetState`, porque `CinematicPlayer`
  escribe el campo directamente (`CinematicPlayer.cs:254,413`).

## 3. `session.json`

```jsonc
{
  "schema_version": 1,
  "mod_version": "0.1.0",
  "session_id": "uuid4", "launch_id": "uuid4",      // launch_id: uno por arranque del juego
  "player_id": "P02",
  "experience": { /* automática, sección 3.2 */ },
  "game": {
    "api_version": "1.5.78.11833-77",   // ModHooks.ModVersion (versión de la DLL parcheada instalada)
    "unity_version": "2020.2.2f1",      // Application.unityVersion (detecta la build 12459 / Unity 6)
    "build_metadata": null,             // BuildMetadata.Embedded (BranchName, Revision) si existe
    "save_version": "1.5.78.11833",     // PlayerData.version del save cargado
    "platform": "WindowsPlayer",
    "other_mods": ["QoL 4.5.0"]         // v3: otros mods ("Nombre versión", + " [off]" desactivado, " [error]" con error; "[error] x N" sin nombre)
  },
  "save": { "slot": 1, "file_sha256": "…", "start_kind": "load|new_game|new_game_steel_soul|new_game_godseeker" },
  "display": { "width": 1920, "height": 1080, "fullscreen_mode": "…", "vsync": 1, "target_fps": 60 },
  "settings": { "sample_hz": 60, "hide_hud": false, "debug": false },
  "start_utc": "2026-11-16T19:00:00Z",
  "end": { "utc": "…", "reason": "quit_menu|ending|menu|emergency_menu|permadeath|app_quit|restarted|recovered", "t_game": 0.0, "t_real": 0.0 },
  "progress_start": { /* sección 3.1 */ },
  "progress_end":   { /* sección 3.1 */ },
  "counters": { "events": 0, "dropped_events": 0, "frames": 0, "dropped_frames": 0,
                "inputs": 0, "dropped_inputs": 0, "write_errors": 0, "hook_errors": 0, "pd_suppressed": {} }
}
```

- `slot`: viene de `SavegameLoadHook(int)`, **no** de `GameManager.profileID` dentro de
  `AfterSavegameLoadHook`, porque ahí todavía tiene el valor anterior.
- `file_sha256`: `user{slot}.dat` (el slot 0 sería `user.dat`). Se calcula en el hilo de escritura.
- `progress_end` se toma **al entrar en `ReturnToMainMenu`** (o en `ApplicationQuitHook`).
  Después, `QuitToMenu` llama a `playerData.Reset()` y los datos ya no sirven.

### 3.1 Snapshot de progreso

Lectura directa de campos de `PlayerData` (sin pasar por `GetX`, para no disparar hooks):
`nailDamage, nailSmithUpgrades, maxHealth, maxHealthBase, heartPieces, maxMP, MPReserveMax,
vesselFragments, fireballLevel, quakeLevel, screamLevel, hasNailArt, hasCyclone, hasDashSlash,
hasUpwardSlash, hasDreamNail, hasDash, hasWalljump, hasSuperDash, hasShadowDash, hasAcidArmour,
hasDoubleJump, hasKingsBrand, charmSlots, charmsOwned, gotCharm_1..40, equippedCharms[],
geo, completionPercentage, playTime, permadeathMode, mapZone (zona de respawn), respawnScene`.

`completionPercentage` solo se recalcula al guardar. Al salir al menú está al día; tras un crash
puede no estarlo, y en ese caso se recalcula en Python. **El mod nunca llama a
`CountGameCompletion`**, porque escribe en `PlayerData`.

Además, volcado completo de los 1525 campos públicos de `PlayerData` en `pd_start.json.gz` (primer `control_ready`,
que es también la línea base de `pd_change`) y `pd_end.json.gz` (al pedir volver al menú y tras su guardado):
`{schema_version, session_id, t_game, fields: {...}}`. Los tipos no primitivos se serializan solo con sus campos
públicos y profundidad máxima 3.

### 3.2 Experiencia previa (automática, no se pregunta al jugador)

Se recoge al empezar cada sesión. Todo crudo: «horas» y «se lo ha pasado» se derivan en Python.

| Campo | Fuente |
|---|---|
| `steam_playtime_min` | Horas totales del juego en Steam (minutos). `<Steam>/userdata/<id>/config/localconfig.vdf`, bloque de la app `367520`, campo `Playtime`. Solo se lee ese número. `null` si no hay Steam (GOG) |
| `steam_playtime_source`, `steam_accounts` | v3. De qué cuenta salen los minutos: `active_user` (la que está jugando, `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser`) o `max_fallback` (máximo entre las cuentas del PC); y cuántas cuentas del PC tienen el juego. No se guardan ids de cuenta |
| `achievements_unlocked` | Claves de los logros desbloqueados. Se recorre la lista de logros del propio juego (`GameManager.achievementHandler.achievementsList`) y se consulta cada uno con `Platform.Current.IsAchievementUnlocked` (Steam o GOG, solo lectura). `null` si la plataforma no responde |
| `achievements_total`, `achievements_source` | Número de logros del juego; `platform` o `unavailable` |
| `saves[]` | Por cada slot 1–4 con partida: `slot, play_time_s, completion, unlocked_completion_rate, permadeath_mode, boss_rush_mode, map_zone, max_health`. Fuente: `GameManager.GetSaveStatsForSlot`, lo mismo que usa la pantalla de selección de partida |

Derivación prevista en Python:
- **Horas:** `steam_playtime_min / 60`; si es `null`, la suma de `saves[].play_time_s / 3600`.
- **Se lo ha pasado:** logros de final (`ENDINGA`, `ENDINGB`, `ENDINGC`, `ENDINGD`…) y de Steel Soul; si no hay logros, alguna partida con `unlocked_completion_rate`.
- **Control:** del uso real (evento `device`, fase 5), no se pregunta.

## 4. `events.jsonl`

Una línea JSON por evento:

```json
{"seq":123,"t_game":81.234,"t_real":95.1,"frame":6012,"play_time":12034.5,"scene":"Crossroads_01","gs":"PLAYING","type":"hit_taken","p":{...}}
```

`seq` es un contador sin huecos que permite detectar pérdidas. `gs` es `GameManager.gameState`.
`p` es el payload específico de cada tipo.

### 4.1 Sesión y escenas

| `type` | Payload `p` | Fuente |
|---|---|---|
| `session_start` | `kind` (load/new_game/…) | `SavegameLoadHook`, `On.GameManager.StartNewGame(permadeath, bossRush)`. `NewGameHook` puede dispararse 2 veces: es idempotente |
| `session_end` | `reason` | `On.GameManager.ReturnToMainMenu` (al llamarse), `ApplicationQuitHook`, `LoadScene("PermaDeath")`, cambio a `Quit_To_Menu` sin pasar por `ReturnToMainMenu` (`EmergencyReturnToMenu`) |
| `scene_exit` | `from, to, reason, exit_gate, entry_gate, vis, info_type, prevent_fade, first_level, respawning` | `On.GameManager.BeginSceneTransition` (antes de `orig`), además de `On.GameManager.LoadScene` y `ReturnToMainMenu` para los caminos que no usan `BeginSceneTransition` |
| `scene_enter` | `scene, entry_gate, map_zone, scene_type, load_s` | `On.GameManager.OnNextLevelReady` (después de `orig`; se deduplica por escena). Es el único punto que alcanzan los tres caminos de carga. `load_s` sale de `SceneLoad.GetDuration` (vía `GameManager.SceneTransitionBegan`) |
| `control_ready` | `scene, early` | primer `On.GameManager.FinishedEnteringScene` tras `scene_exit`. Marca cuándo el héroe acepta input. En Dream Gate y en el respawn tras morir el juego lo llama **antes** de `OnNextLevelReady`: se emite justo después de `scene_enter` con `early: true`. Los que llegan por *hazard respawn* se ignoran |
| `game_state` | `from, to, paused` | sondeo de `gameState` cada frame. `pause`/`unpause` son transiciones hacia o desde `PAUSED` **[derivado]** |

> `ModHooks.SceneChanged` **no se usa**: no salta en puertas, muertes, cargas, ciervos ni
> sueños (solo en `LoadScene`).

**Clasificador de `scene_exit.reason`**, en `On.GameManager.BeginSceneTransition`, en este orden.
El payload guarda siempre los campos crudos de `SceneLoadInfo`, así que el motivo se puede
recalcular en Python si el clasificador se equivoca:

| Orden | Condición | `reason` |
|---|---|---|
| 1 | flag `pending_death` (puesto en `On.HeroController.Die`), o `self.RespawningHero && !info.IsFirstLevelForPlayer` | `death` (en sueño o Godhome: `death_dream` / `death_godhome`) |
| 2 | `info.IsFirstLevelForPlayer` y `Visualization == ContinueFromSave` | `save_load` (o `new_game_godseeker` si la sesión empezó así) |
| 3 | `info.SceneName == "Opening_Sequence"` | `new_game` |
| 4 | `SceneName == "Cinematic_Stag_travel"` o `EntryGateName == "door_stagExit"` | `stag` |
| 4b | `EntryGateName == "dreamGate"` (Dream Gate, `GameManager.WarpToDreamGate`; también sirve para salir de Godhome) | `dream_gate` |
| 5 | `Visualization == GodsAndGlory`, o la escena de origen o destino empieza por `GG_` | `godhome` |
| 6 | `EntryGateName == door_dreamReturn`, `Visualization ∈ {Dream, GrimmDream}` o el destino empieza por `Dream_` | `dream` |
| 7 | flag puesto en `On.TransitionPoint.OnTriggerEnter2D` en este mismo frame | `gate`, con `exit_gate = TransitionPoint.lastEntered` |
| 8 | existe un `TransitionPoint` en la escena activa con `targetScene == info.SceneName` y `entryPoint == info.EntryGateName` | `door` (puertas por FSM), con `exit_gate =` su nombre |
| 8b | la acción PlayMaker `BeginSceneTransition` se ejecutó en este mismo frame (puertas y ascensores sin `TransitionPoint`: estación de ciervo, cabaña del Chamán…) | `door`, con `exit_gate =` el GameObject dueño del FSM |
| 9 | ninguna de las anteriores | `other` (se loguea una vez para revisarlo) |

Y fuera de `BeginSceneTransition`: `stag` / `fsm_load` cuando un FSM usa la acción PlayMaker `LoadLevel`, que carga con Unity directamente (así sale la estación de ciervo hacia `Cinematic_Stag_travel`); `unhooked` (con `synthetic: true`) si llega un `scene_enter` a otra sala sin salida registrada: se emite la salida en ese momento para que todo `scene_enter` tenga su `scene_exit`; `quit_menu` (`ReturnToMainMenu`, con confirmación al llegar
a `Quit_To_Menu`, porque el guardado puede abortar), `ending` (`ReturnToMainMenu` desde
`CutsceneHelper`, o las escenas de créditos) y `permadeath`.

Notas:
- `TransitionPoint.lastEntered` **nunca se reinicia**. Para un cambio de escena hecho por FSM
  contiene un valor viejo y solo es fiable con el flag de la regla 7.
- Las escenas `Knight_Pickup`, `Opening_Sequence`, `Cinematic_Stag_travel` y `Quit_To_Menu`
  generan enter y exit con `scene_type = "non_gameplay"`. Se usa una lista propia, porque
  `IsNonGameplayScene` tiene un error: busca `"Knight Pickup"` con espacio (`GameManager.cs:2152`).
- El nombre de escena se normaliza con `GameManager.GetBaseSceneName`, que quita los sufijos
  `_boss` y `_preload` de las subescenas aditivas.

### 4.2 Combate

`hazard_type` es **el entero crudo** que usa el juego, que **no** coincide con el enum
`HazardType` (este va desplazado en uno): 0 = buffer/otro, **1 = enemigo**, 2 = pinchos,
3 = ácido, 4 = lava (solo genera un `Debug.Log`), 5 = foso.

| `type` | Payload `p` | Fuente |
|---|---|---|
| `hit_taken` | `src` (nombre del GO o null), `hazard_type`, `side`, `raw_dmg`, `hp_before/after`, `blue_before/after`, `overcharmed`, `boss_level` | `On.HeroController.TakeDamage`, con lectura antes y después de `orig`. **Solo se emite si cambian `health` o `healthBlue`**: `TakeDamage` se llama en cada frame de física mientras hay contacto |
| `hp_change_other` | `field, old, new` | `SetPlayerIntHook('health'/'healthBlue')` fuera de `TakeDamage`, por ejemplo cuando un FSM llama a `HeroController.TakeHealth` **[derivado]** |
| `hazard_respawn` | `hazard` (enum `HazardType`), `x, y` | `On.HeroController.DieFromHazard`. `KillOnContact` llama directamente a `HeroController.HazardRespawn`, así que también se engancha `On.HeroController.HazardRespawn` y se deduplica |
| `death` | `zone_kind` (normal/dream/godhome), `x, y`, `geo_before`, `map_zone` | `On.HeroController.Die`, deduplicado: se emite una vez por vida (`OnDeath` puede repetirse). Captura también las muertes en sueño y en Godhome, que `BeforePlayerDeadHook` no ve |
| `shade_killed` | `geo_recovered` | `soulLimited` pasa de true a false en `SetPlayerBoolHook`, con el delta de geo **[derivado][test]** |
| `hit_dealt` | `enemy` (bloque 4.3), `kind` (`hit`/`blocked`/`extra`), `attack_type` (AttackTypes: Nail, Generic, Spell, Acid, Splatter, RuinsWater, SharpShadow, NailBeam), `src`, `src_parent`, `dmg_dealt`, `mult`, `hp_before`, `hp_after`, `dir` | `On.HealthManager.TakeDamage` (`hit`), `On.HealthManager.Invincible` (`blocked`), `On.HealthManager.ApplyExtraDamage` (`extra`: Spore Shroom o Defender's Crest) |
| `enemy_hp_change` | `enemy`, `hp_before`, `hp_after` | sondeo del `hp` de los `HealthManager` vivos de la sala: cubre Flukenest (`SpellFluke.cs:92`) y FSM (`SubtractHP`/`SetHP`), que restan sin pasar por `TakeDamage` |
| `enemy_killed` | `enemy`, `attack_type`, `geo_small/med/large`, `geo_value`, `special` | `On.HealthManager.Die` (antes de `orig`, solo si `!isDead && !hasSpecialDeath` y el ataque no es Splatter ni RuinsWater). Los enemigos con `hasSpecialDeath` (muchos jefes) se recogen con `OnReceiveDeathEventHook`, deduplicado por iid, con `special: true`. Geo: `ReflectionHelper.GetField<HealthManager,int>` sobre los campos privados `smallGeoDrops`, `mediumGeoDrops` y `largeGeoDrops`, aplicando el +20% de Fragile Greed |
| `enemy_spawn` | `enemy`, `already_dead` | `OnEnableEnemyHook` (devuelve `isAlreadyDead` sin tocar) |
| `focus` | `phase` (`start`/`stop`), `mp` | `On.HeroController.StartMPDrain` y `StopMPDrain` **[test]** |
| `fsm_state` | `fsm` (`Spell Control`/…), `from`, `to` | sondeo cada frame de `HeroController.spellControl.ActiveStateName`. Con esto se detectan después hechizos, focus y nail arts **[test]** |
| `env_break` | `kind` (`breakable`/`pole`/`grass`/`vine`), `name`, `x, y`, `attack_type`, `src` | Entorno roto o cortado por el jugador: `On.Breakable.Break`, `On.BreakablePole.Hit`, `On.Grass.Hit`, `On.GrassBehaviour.CutReact`, `On.VinePlatformCut.Cut`. Para el perfilado (explorador / destructivo). La hierba se agrupa: como mucho un evento por objeto y visita |
| `boss_scene` | `phase` (`start`/`bosses_dead`/`complete`), `boss_level`, `bosses[]` | `BossSceneController.Instance` (solo Godhome): `OnBossesDead`, `OnBossSceneComplete` |

### 4.3 Bloque `enemy`

| Campo | Definición |
|---|---|
| `type` | `EnemyDeathEffects.playerDataName` (nombre del journal; campo privado). Si es vacío (`string.IsNullOrEmpty`), el nombre del GameObject normalizado (sin `(Clone)` ni ` (n)`) |
| `name` | `gameObject.name` crudo |
| `key` | si tiene `PersistentBoolItem`: `persistentBoolData.sceneName + "/" + id` (la misma clave que usa el juego para guardar). Si no: `escena + "/" + ruta jerárquica` |
| `iid` | `GetInstanceID()`. Solo vale dentro de una visita y se repite si el enemigo se recicla |
| `hp_max` | el `hp` máximo observado en la visita. No existe un campo de vida máxima: el `hp` cambia por FSM. Para los jefes de Godhome se usa `BossSceneController.BossHealthLookup` |

### 4.4 Comodín `pd_change`

| `type` | Payload `p` | Fuente |
|---|---|---|
| `pd_change` | `f` (campo), `t` (b/i/f/s/v3/var), `old`, `new`, `src` (`hook`/`diff`/`list`) | `SetPlayer{Bool,Int,Float,String,Vector3,Variable}Hook`. Dentro del hook el campo todavía guarda el valor anterior, así que se lee directamente (`PlayerData.instance.<campo>` compila a `ldfld`, sin hooks). **Solo se emite si el valor cambia.** Los hooks devuelven siempre su entrada sin tocar |
| `pd_change` (`src: diff`) | igual | diff a 1 Hz de los ~1460 campos escalares contra una copia en caché, con delegados tipados compilados en `Initialize` (sin boxing). Cubre lo que no pasa por hooks: `SetupNewPlayerData`, `AddGGPlayerDataOverrides`, `Reset`, `UpgradeSaveData` y la deserialización al cargar |
| `boss_defeated` | `field`, `boss` | bool de PlayerData que pasa de false a true y cuyo nombre es de jefe (`*Defeated`, `defeated*`, `killedFinalBoss`, `*Killed` de PNJ), detectado por patrón al arrancar (~30 campos). Ejemplo: `falseKnightDefeated` |
| `ability_gained` | `field`, `old`, `new` | `has*` de habilidades (`hasDash`, `hasWalljump`, `hasSuperDash`, `hasShadowDash`, `hasAcidArmour`, `hasDoubleJump`, `hasDreamNail`, `hasDreamGate`, `hasNailArt`, `hasCyclone`, `hasDashSlash`, `hasUpwardSlash`, `hasLantern`…) o subida de `fireballLevel`/`quakeLevel`/`screamLevel` |
| `item_got` | `field`, `old`, `new`, `kind` (`charm`/`mask_shard`/`vessel_fragment`/`nail`/`key`/`relic`/`other`) | `gotCharm_N`, `heartPieces`, `vesselFragments`, `nailSmithUpgrades`, llaves (`has*Key`, `simpleKeys`), reliquias (`trinket1-4`), `ore`, `rancidEggs`… |
| `pd_list` | `f`, `added[]`, `removed[]` | diff a 1 Hz de las 15 listas que se modifican in situ sin hook: `scenesVisited`, `scenesMapped`, `equippedCharms`, `unlockedBossScenes`, `scenesEncountered*`, `scenesGrubRescued`, `scenesFlameCollected`… |

- **Nunca** se suscribe un `Get*Hook`: todas las lecturas del juego pasan por ellos.
- El nombre se valida contra la tabla de campos: los FSM pueden escribir nombres que no existen.
  Se cuentan una vez y no se emiten.
- Límite por campo: 30 cambios por segundo, con contador en `session.counters.pd_suppressed`
  (p. ej. `MPCharge` durante una curación). Nombres inexistentes: `session.counters.pd_unknown_fields`.
- Coste medido fuera del juego: barrido de 40 campos por frame ≈ 0,7 µs; línea base completa ≈ 14 µs.
  La lista negra se decide al final de la fase 3, con datos reales.
- Campos con trampa: `killsX` **cuenta hacia abajo** (lo que falta para completar el journal),
  y `mapZone` es la zona **de respawn**, no la actual.
- `boss_defeated`, `ability_gained` e `item_got` los emite el mod (además del `pd_change` crudo) para que el análisis
  no dependa de conocer los nombres de campos. `bench_rest` y `shade_killed` se derivan en Python (`atBench`, `soulLimited`).

### 4.5 Objetos persistentes

| `type` | Payload `p` | Fuente |
|---|---|---|
| `scene_object` | `kind` (`bool`/`int`/`georock`), `id`, `obj_scene`, `old`, `new`, `trigger` | diccionario sombra `(escena, id, tipo) → valor`, sembrado desde `SceneData.instance` al cargar o empezar partida, y comparado en `On.SceneData.SaveMyState_PersistentBoolData`, `SaveMyState_PersistentIntData` y `SaveMyState_GeoRockData` |

⚠ **`t_*` es el momento observado, no el de la interacción.** El juego guarda en bloque al salir
de la escena, al guardar o tras un *hazard respawn*. La excepción son los enemigos, que se
guardan al morir. Para tener el momento exacto: `breakable` (en vivo) y la muerte del enemigo.
Los muros y palancas por FSM solo tienen resolución de salida de sala. **[test]**: comprobar que
los objetos que se desactivan solos se guardan.

### 4.7 Mapa (v2)

Cuándo y cómo se abre el mapa. Fuente: los métodos públicos de `GameMap` que llaman los FSM del juego.

| `type` | Payload `p` | Fuente |
|---|---|---|
| `map_open` | `mode` (`quick`/`inventory`), `area` (área mostrada en el mapa rápido: `Crossroads`, `Greenpath`…; null en el inventario), `map_zone`, `has_map`, `has_area_map` (si tiene el mapa de esa área), `room_mapped` (sala con sprite completo: `scenesMapped` o `mapAllRooms`), `has_quill`, `compass_equipped` (amuleto 2), `compass_shown`, `in_room`, `markers_placed`, `x, y, hero_state, accepting` | Mapa rápido: `On.GameMap.QuickMap<Área>` (14 métodos). Inventario: `On.GameMap.WorldMap` |
| `map_close` | `mode`, `reason` (`close_quick_map`/`inventory_closed`/`scene_transition`/`death`/`quit_menu`/`session_end`/`reopen`), `dur_real`, `dur_game` | `On.GameMap.CloseQuickMap`; inventario: el FSM del inventario vuelve al estado que tenía cerrado (capturado en el primer `control_ready`); forzados desde escena, muerte y fin de sesión |
| `map_pan` | `on`, `via` (`start_pan`/`stop_pan`/`set_can_pan`), `mode` | `On.GameMap.StartPan/StopPan/SetCanPan`: el jugador recorre el mapa del inventario |
| `map_marker` | `op` (`place`/`remove`), `before`, `after` | `On.MapMarkerMenu.PlaceMarker/RemoveMarker`, solo si cambia el número de marcadores |
| `fsm_state` (`fsm: "Inventory"`) | `from, to` | sondeo de `GameManager.inventoryFSM.ActiveStateName`: paneles del inventario. Los nombres de estado solo existen en los assets: se escriben una vez por arranque a `ModLog.txt` |

**Comprobado en el juego (sesión `20261117_175815`):**
- El Caballero **puede andar con el mapa rápido abierto**. Si cruza a otra sala sin soltar el botón, el juego oculta
  el mapa sin llamar a `CloseQuickMap` y el cierre sale con `reason: scene_transition` (es un cierre real, no una pérdida).
- En el inventario, `CloseQuickMap` salta al salir de la vista detallada del mapa (primer «cancelar»), antes de
  que se cierre el inventario. Por eso un `map_close` de `mode: inventory` suele llevar `reason: close_quick_map`;
  `inventory_closed` queda para cuando el inventario se cierra directamente.
- Estados del FSM `Inventory Control` vistos: `Closed` → `Open Current Pane` → `Opened`; `Tween Panes` al cambiar de
  panel; `Close` → `Closed` al cerrar. La lista completa sale en `ModLog.txt` con el modo debug activado.
- El FSM `Spell Control` pasa a `Button Down` también con el botón de cancelar dentro del inventario (en mando, el
  mismo botón que lanza hechizos): filtrar esos `fsm_state` mientras el inventario está abierto.

El tiempo con el mapa abierto cuenta como tiempo de juego (`t_game`): el juego no se pausa. Las pulsaciones de
`quickMap` y `openInventory` irán además en `inputs.csv` (fase 5).

### 4.6 Sistema

| `type` | Payload `p` | Fuente |
|---|---|---|
| `device` | `from` (null en el primero de la sesión), `to` (`none`/`kb`/`pad`/`unknown`), `gamepad` (`InputHandler.activeGamepadType`: `XBOX_ONE`, `PS4`, `SWITCH_PRO_CONTROLLER`…) | sondeo cada frame de `inputActions.LastInputType` (`KeyBindingSource`/`MouseBindingSource` = `kb`, `DeviceBindingSource` = `pad`, `None` = `none` hasta la primera pulsación). Siempre hay uno al empezar la sesión |
| `perf` | `fps, dt_max_ms, mod_us_avg, mod_us_max, vsync, target_fps, dropped` | cada 1 s de tiempo real, desde el `Runner`. **v2:** `mod_us_*` = tiempo TOTAL del mod en el hilo del juego por frame (µs): hooks + sondeos + HUD (v1 solo medía el `Update` del Runner). Sirve para descartar que el mod cause tirones o input lag |
| `writer` | `status` (`ok`/`error`/`dropping`), `msg` | hilo de escritura |

## 5. `frames.csv`

Muestreo a `sample_hz` (60 por defecto; se fija al empezar la sesión, así que un cambio en el menú se
aplica en la siguiente, y `session.json` guarda la frecuencia usada) desde el `Runner` (`MonoBehaviour` propio con `DontDestroyOnLoad`,
late también en pausa, menús y cinemáticas) con un acumulador de `unscaledDeltaTime`. Como mucho una
muestra por frame; la primera, en el primer frame de la sesión. Si el juego va por debajo de `sample_hz`
se muestrea cada frame, sin ráfagas para recuperar.

Implementación (v3): el hilo del juego copia un struct a un búfer circular de 8192 filas sin asignar
memoria; el texto se genera en el hilo de escritura, que se despierta antes si el búfer llega a la mitad.
La cabecera se escribe al abrir la sesión, aunque luego no haya filas. Si una escritura a disco falla,
el lote entero (como mucho 500 ms) cuenta como descartado y el fichero se recorta a la última longitud
buena: nunca queda una fila a medias. Si el búfer se llena (disco atascado ~2 min), la
fila se descarta y se cuenta en `counters.dropped_frames`; `seq` la cuenta igualmente, así que cada
hueco en `seq` es una fila descartada.

Cabecera exacta:
`seq,t_game,t_real,frame,play_time,scene,gs,x,y,vx,vy,hp,hp_max,blue,soul,soul_reserve,hero_state,cstate,facing,accepting,time_scale,input_bitmask,move_x,move_y,gap`

| Columna | Tipo | Fuente |
|---|---|---|
| `seq` | int | contador propio del fichero, desde 0 en cada sesión |
| `t_game, t_real, frame, play_time` | | sección 2. `play_time` vacío si no hay `GameManager` |
| `scene` | str | escena base actual (la misma de `events.jsonl`) |
| `gs` | int | `gameState` (sección 8: 4 = PLAYING, 5 = PAUSED…). Vacío si no hay `GameManager` |
| `x, y` | float | `hc.transform.position` |
| `vx, vy` | float | `Rigidbody2D.velocity` del héroe (velocidad física actual). El componente se guarda en caché con `GetComponent` y se renueva cuando cambia la instancia del héroe |
| `hp, hp_max, blue` | int | `pd.health`; `pd.maxHealth` limitado por el *binding* de vida de Godhome (mismo cálculo que `CurrentMaxHealth`, sin pasar por `GetInt`); `pd.healthBlue` |
| `soul, soul_reserve` | int | `pd.MPCharge`, `pd.MPReserve` |
| `hero_state` | int | `hc.hero_state` (ActorStates: 0 grounded, 1 idle, 2 running, 3 airborne, 4 wall_sliding, 5 hard_landing, 6 dash_landing, 7 no_input, 8 previous) |
| `cstate` | int64 | máscara de bits de los 55 bools de `cState`, en el orden de la sección 5.1 |
| `facing` | int | 1 si `cState.facingRight`, −1 si no |
| `accepting` | 0/1 | `hc.acceptingInput && !hc.controlReqlinquished && !gm.isPaused` |
| `time_scale` | float | `Time.timeScale` (0 en el *hit-stop* y en pausa; ver sección 2) |
| `input_bitmask` | int | acciones pulsadas en algún frame desde la muestra anterior (OR de cada frame; bits de la sección 6.1). Un botón mantenido aparece en todas las muestras |
| `move_x, move_y` | float | `inputActions.moveVector.X/Y` en el frame de la muestra |
| `gap` | 0/1 | 1 si no hay héroe (`HeroController.UnsafeInstance == null`): `x` … `accepting` van vacíos y el resto se rellena |

Las columnas `hp`/`soul` se reconstruyen fácilmente a partir de `pd_change`, pero se mantienen
porque son lo que más se usa en el análisis.

### 5.1 Bits de `cstate` (orden de `HeroControllerStates`)

0 facingRight, 1 onGround, 2 jumping, 3 wallJumping, 4 doubleJumping, 5 nailCharging,
6 shadowDashing, 7 swimming, 8 falling, 9 dashing, 10 superDashing, 11 superDashOnWall,
12 backDashing, 13 touchingWall, 14 wallSliding, 15 transitioning, 16 attacking, 17 lookingUp,
18 lookingDown, 19 lookingUpAnim, 20 lookingDownAnim, 21 altAttack, 22 upAttacking,
23 downAttacking, 24 bouncing, 25 shroomBouncing, 26 recoilingRight, 27 recoilingLeft, 28 dead,
29 hazardDeath, 30 hazardRespawning, 31 willHardLand, 32 recoilFrozen, 33 recoiling,
34 invulnerable, 35 casting, 36 castRecoiling, 37 preventDash, 38 preventBackDash, 39 dashCooldown,
40 backDashCooldown, 41 nearBench, 42 inWalkZone, 43 isPaused, 44 onConveyor, 45 onConveyorV,
46 inConveyorZone, 47 spellQuake, 48 freezeCharge, 49 focusing, 50 inAcid, 51 slidingLeft,
52 slidingRight, 53 touchingNonSlider, 54 wasOnGround.

## 6. `inputs.csv`

Sondeo **cada frame** (no a `sample_hz`) de `InputHandler.Instance.inputActions` desde el `Runner`, sin
hooks. La referencia se vuelve a leer en cada frame, porque `InputHandler.Start` la recrea. Se emite una
fila en cada transición de `IsPressed`; la detección de flanco es propia, sin depender de `WasPressed`,
que dura un solo tick de InControl. Mismo búfer circular que `frames.csv` (contadores `inputs` y
`dropped_inputs`; huecos en `seq` = filas descartadas).

- Se graba durante toda la sesión, también en pausa, inventario y cinemáticas: la columna `accepting`
  separa las pulsaciones que el héroe no podía usar.
- Los botones que ya están pulsados al empezar la sesión salen como `down` en la primera fila (por
  ejemplo, el botón con el que se cargó la partida): todos los `up` tienen su `down`.
- El eje: una fila `action=move, ev=axis` cuando `moveVector` cambia más de 0,2 en X o en Y respecto a
  la última fila de eje, y siempre que pasa de cero a no cero o al revés.
- Las direcciones del stick también activan `left`/`right`/`up`/`down` (son acciones de InControl con
  zona muerta propia).

Cabecera exacta: `seq,t_game,t_real,frame,action,ev,x,y,device,accepting`

| Columna | Definición |
|---|---|
| `seq` | contador propio del fichero, desde 0 en cada sesión |
| `t_game, t_real, frame` | sección 2 (frame en el que el mod vio el cambio) |
| `action` | nombre de la acción (sección 6.1) o `move` para el eje |
| `ev` | `down`, `up` o `axis` |
| `x, y` | solo en `axis` (−1 … 1); vacíos en `down`/`up` |
| `device` | `none`, `kb`, `pad` o `unknown`, según `LastInputType` en ese frame |
| `accepting` | 1 si el héroe estaba aceptando input (mismo criterio que en `frames.csv`) |

### 6.1 Acciones y bits de `input_bitmask`

0 left, 1 right, 2 up, 3 down, 4 jump, 5 attack, 6 dash, 7 superDash, 8 cast, 9 quickCast,
10 focus, 11 dreamNail, 12 quickMap, 13 openInventory, 14 pause, 15 evade.
(Campos de `HeroActions`. InControl está compilado dentro de `Assembly-CSharp`.)

## 7. `scenes/<scene>.json`

Se genera en el primer `scene_enter` de cada escena en la sesión, desde
`On.GameManager.OnNextLevelReady` (después de `orig`: ya son válidos `sm`, `tilemap` y
`sceneWidth`), con un frame de retraso.

```jsonc
{
  "schema_version": 1, "scene": "Crossroads_01", "captured_t_game": 81.2,
  "size": { "w": 120, "h": 60 },                       // GameManager.sceneWidth/Height (tiles = unidades; origen abajo-izquierda)
  "scene_manager": { "map_zone": "CROSSROADS", "scene_type": "GAMEPLAY", "is_windy": false, "is_tremor": false,
                     "environment": 0, "darkness": 0, "no_lantern": false },  // global::SceneManager
  "gates": [ { "name": "right1", "pos": [x,y], "target_scene": "…", "entry_point": "…", "is_door": false,
               "position": "right" } ],                // TransitionPoint.TransitionPoints filtrado por escena activa
  "enemies": [ { "name": "…", "type": "…", "key": "…", "pos": [x,y], "hp": 15, "active": true,
                 "is_dead": false, "persistent_id": "…|null", "geo": [s,m,l] } ],
  "hazards": [ { "name": "…", "kind": "damage_hero|fsm", "hazard_type": 2, "damage": 1,
                 "bounds": [xmin,ymin,xmax,ymax] } ],
  "benches": [ { "name": "…", "pos": [x,y] } ],         // RestBench
  "respawn_points": [ { "name": "…", "pos": [x,y] } ],  // tag RespawnPoint
  "hazard_respawn_triggers": [ { "pos": [x,y] } ],
  "persistent": [ { "kind": "bool|int|georock", "id": "…", "name": "…", "pos": [x,y], "value": 0, "fsm": "…|null" } ],
  "breakables": [ { "name": "…", "pos": [x,y] } ],
  "battle_scenes": [ { "name": "…", "pos": [x,y] } ],   // PlayMakerFSM con FsmName "Battle Control"
  "scene_data_saved": { "bool": {"id": true}, "int": {"id": 2}, "georock": {"id": 0} }  // SceneData.instance para esta escena
}
```

- Enemigos y objetos se buscan **incluidos los inactivos** (`FindObjectsOfType<T>(true)`,
  filtrando por `gameObject.scene`). Los enemigos ya muertos y los de arena empiezan inactivos.
- Peligros: `DamageHero` con `hazardType >= 2` **y** los GameObjects con un FSM `damages_hero`,
  del que se leen sus enteros `hazardType` y `damageDealt` (`HeroBox.cs:47-58`).
- Una escena se cataloga una vez por sesión. En Python se deduplica por escena y versión de esquema.

## 8. Mapas de enums usados

- `GameState`: 0 INACTIVE, 1 MAIN_MENU, 2 LOADING, 3 ENTERING_LEVEL, 4 PLAYING, 5 PAUSED,
  6 EXITING_LEVEL, 7 CUTSCENE, 8 PRIMER.
- `SceneLoadVisualizations`: −1 Custom, 0 Default, 1 Dream, 2 Colosseum, 3 GrimmDream,
  4 ContinueFromSave, 5 GodsAndGlory.
- `HazardType` (enum, solo en `hazard_respawn.hazard`): 0 NON_HAZARD, 1 SPIKES, 2 ACID, 3 LAVA, 4 PIT.
- `CollisionSide`: top, left, right, bottom, other.

## 9. Pendiente de prueba en el juego

| # | Qué | Cómo |
|---|---|---|
| T1 | Las puertas (FSM) llegan por `BeginSceneTransition` y la regla 8 identifica la gate | entrar en la tienda de Sly o en un ascensor |
| T2 | Ciervo: secuencia de transiciones y motivo | viajar entre dos estaciones |
| T3 | Sueño: entrada con el aguijón onírico, muerte dentro, Dream Gate | cadáver del Falso Caballero |
| T4 | Banco: `atBench` true/false y `respawn*` en `pd_change` | sentarse y levantarse |
| T5 | Muerte, sombra y su recuperación: orden de `geoPool`, `soulLimited` y `geo` | morir y matar la sombra |
| T6 | Hechizos, focus y nail arts: nombres de estado de `Spell Control` y `Source.name` de cada ataque | lanzar cada hechizo y cada nail art; curarse y cancelar |
| T7 | Muros y palancas: `scene_object` al salir de la sala | romper un muro en Crossroads |
| ~~T8~~ | ✅ `FindObjectsOfType<T>(true)` compila contra Unity 2020.2 (comprobado el 16/11/2026) | — |
| T9 | Arenas: estados del FSM `Battle Control` | arena de aspids de Crossroads |

## Changelog

- **v3** (17/11/2026): `frames.csv` (§5) e `inputs.csv` (§6) implementados, con la cabecera exacta y búferes circulares sin asignaciones; evento `device` con `gamepad` y uno siempre al empezar; `game.other_mods` en session.json; `experience.steam_playtime_source` y `steam_accounts`; contadores `frames`/`dropped_frames`/`inputs`/`dropped_inputs`. Sin cambios en `events.jsonl` salvo `device`.
- **v2** (17/11/2026): eventos de mapa (§4.7); `perf.mod_us_*` mide todo el tiempo del mod (hooks incluidos); `hit_dealt.hp_reset`; `death`/`hazard_respawn` llevan la fuente del golpe letal y se emiten después de su `hit_taken`; el `scene_exit` de «Guardar y salir» se emite al llegar a `Quit_To_Menu` (una salida cancelada ya no deja rastro); `env_break` solo cuando el objeto se rompe de verdad; motivo `new_game` para `Opening_Sequence → Tutorial_01`.
- **v1** (16/11/2026): esquema inicial, verificado contra el código descompilado de la 1.5.78. Experiencia automática (§3.2) en lugar de autodeclarada; 60 Hz por defecto.
