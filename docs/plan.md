# HKTelemetry: plan de implementación

## Contexto

Mod de Hollow Knight (Modding API, .NET 4.7.2) que **solo registra** datos crudos de partida
para generar un dataset propio (regresión por visita a sala, clustering de salas, experto vs
novatos). Brief completo: [BRIEF.md](BRIEF.md). Formato de datos definitivo: [schema.md](schema.md).
Verificación de la API contra el código, con referencias `archivo:línea`: [api-notes/](api-notes/).

## Estado (16/11/2026)

| Elemento | Estado |
|---|---|
| Juego | ✅ 1.5.78.11833 (Unity 2020.2.2f1), versión anterior en Steam |
| Modding API v77 | ✅ instalada a mano en `Managed/`. Los originales están en `reference/vanilla-managed-backup/` |
| .NET SDK 8.0.428, ilspycmd 9.1 | ✅ |
| `reference/modding-api/` | ✅ clonado (master = 1.5.78.11833, v77) |
| `reference/decompiled/Assembly-CSharp/` | ✅ descompilado desde la DLL **parcheada** por la API: incluye los hooks y `orig_*`, e InControl |
| Verificación de todos los **[verificar]** | ✅ workflow de 11 agentes con verificación adversarial → `docs/api-notes/` y `schema.md` |
| Nombres `On.*` | ✅ comprobados en `MMHOOK_Assembly-CSharp.dll`. Las sobrecargas llevan sufijo de tipos (`SaveMyState_PersistentBoolData`) |
| `GodhomeWinLossTracker` | ⏳ falta el enlace |
| Proyecto C# | ✅ `HKTelemetry/` (plantilla `hksettings`). `dotnet build` copia la DLL a `Managed/Mods/HKTelemetry/`. Ruta del juego sobrescribible con `-p:HKManaged=...` |

### Avance por fases (17/11/2026)

| Fase | Estado |
|---|---|
| 0 | ✅ cerrada |
| 1 | ✅ probada en 3 sesiones (escenas, puertas FSM, ciervo, Dream Gate, partida nueva) |
| 2 | 🟡 código hecho y probado en 1 sesión (Falso Caballero, Chamán); revisión adversarial aplicada |
| 3 | 🟡 código hecho y probado en 1 sesión (`boss_defeated`, `ability_gained`, `item_got`) |
| Mapa (nuevo, schema v2) | ✅ probado el 17/11 (mapa rápido andando y quieto, inventario, desplazamiento) |
| 7 (HUD) | ✅ probado el 17/11: punto verde/gris, línea de título y overlay de debug |
| 4 (`frames.csv`) y 5 (`inputs.csv`) | ✅ probadas el 17/11 (sesión `20261117_185043`): 59,8 Hz reales, huecos solo en cargas de sala, 4512 inputs de mando XBOX_ONE, mod a ~46 µs/frame, sin input lag |
| 6, 8 | ⏳ pendientes |

**Rendimiento (sesión `20261117_175815`, con debug activado):** el mod cuesta ~63 µs/frame de media jugando (~1 % de un frame a 180 fps). Los picos de **3,06 ms** que salen cada ~15 s (o múltiplos) son el **recolector de basura incremental de Unity**: `boot.config` del juego tiene `gc-max-time-slice=3`, la duración es siempre la misma y caen también en segundos sin ningún evento. Es la recolección del juego entero que a veces arranca dentro de una asignación del mod; el frame dura ~6 ms en vez de 5,6, sin perder frames. Los dos picos grandes (24 ms y 14 ms) son de una sola vez, en la carga de partida (compilación JIT del mod y volcado de FSM, que ahora solo se hace en modo debug).

**Abierto:** input lag percibido. Las medidas no lo explican; falta la prueba A/B (un rato con el mod desactivado en la misma zona).

### Lo que falta para entregar el mod

1. **Fase 8, kit para participantes** (imprescindible): instalador y desinstalador `.bat` (API + mod, con copia de seguridad), `player_id` ya puesto por persona, `.bat` para enviar los datos en un zip, guía de una página (cambio a la versión anterior en Steam) y aviso de qué se recoge.
2. **Fase 6, catálogo de salas** (recomendada antes de entregar, porque no se puede reconstruir después): enemigos, objetos rompibles y tamaño de cada sala la primera vez que se entra.
3. **Versión 1.0.0** en Release y prueba completa del kit en limpio (desinstalar, instalar con el kit, jugar, enviar datos).
4. Después de entregar: `to_parquet.py` para el análisis y revisar en los primeros datos los puntos pendientes de schema.md §9.

### Perfilado de jugadores (agresivo / pasivo / explorador…)

Objetivo añadido el 17/11/2026. No hace falta ningún evento «de perfil»: se calcula en Python a partir de los datos crudos.
- **Agresividad:** ataques por minuto (inputs), golpes dados/recibidos, enemigos matados frente a enemigos presentes en la sala (catálogo), tiempo cerca de enemigos, uso de hechizos.
- **Riesgo:** seguir jugando con poca vida, muertes, curaciones canceladas, *hazard respawns*.
- **Exploración y destrucción:** `env_break` (objetos y hierba), salas nuevas frente a repetidas, tiempo por sala, `scene_object` (muros secretos).
- **Progresión:** `boss_defeated`, `ability_gained`, `item_got` con su `t_game`.

## Fase 0: lo que queda

1. **Prueba en el juego:** el mod aparece en el menú de mods y `ModLog.txt` muestra la línea
   `HKTelemetry v0.1.0 (schema 1) | player_id=... | api=1.5.78.11833-77 | unity=2020.2.2f1 (OK)`.

---

## Arquitectura

```
HKTelemetry/
  HKTelemetry.cs        Mod, IGlobalSettings<GlobalSettings>, IMenuMod; solo engancha y desengancha
  Settings.cs           player_id (solo en el JSON del kit), sample_hz, hide_hud, debug
  Menu.cs               menú de ajustes (indicador, frecuencia, debug)
  Runner.cs             MonoBehaviour DontDestroyOnLoad: reloj, sondeos por frame, cola de errores → Logger/HUD
  Session/              SessionManager (ciclo de vida), ProgressSnapshot, PlayerDataTable (delegados tipados)
  Writing/              TelemetryWriter (hilo dedicado + colas), JsonlSink, CsvSink, Compressor
  Recorders/            SceneRecorder, CombatRecorder, PlayerDataRecorder, SceneObjectRecorder,
                        FrameSampler, InputRecorder, SceneCatalog
  Hud/                  RecordingIndicator, TitleLine, DebugOverlay (CanvasUtil)
  Util/                 Clock, SafeHook, Names (normalización de enemigos y escenas)
tools/  validate.py, to_parquet.py
```

Reglas (verificadas en el código):
- **Hilo del juego:** los hooks solo construyen un evento pequeño y lo encolan. Unity y
  `PlayerData.instance` solo se tocan desde el hilo principal. El hilo de escritura solo hace E/S.
- **`SafeHook`:** todo handler va envuelto en try/catch. Los errores se cuentan y se pasan a una
  cola que el `Runner` vuelca a `Logger` con límite de frecuencia. `Logger.Log` escribe en disco
  de forma síncrona bajo un lock, así que **nunca se loguea un evento por frame**.
- **Pass-through obligatorio:** los hooks que devuelven valor devuelven su entrada sin tocar
  (`Set*Hook`, `OnEnableEnemyHook`, `BeforeSceneLoadHook`…). Las corrutinas enganchadas
  (`Die`, `ReturnToMainMenu`…) devuelven `orig(...)` tal cual y se registran en el momento de la llamada.
- **Hooks prohibidos** (cambian el juego aunque se devuelva un valor «neutro»):
  `BlueHealthHook`, `FocusCostHook`, `CursorHook`, `DashPressedHook`, todos los `Get*Hook`.
- **Instancias:** `HeroController.UnsafeInstance` y `GameManager.UnsafeInstance` para los
  sondeos (`.instance` loguea errores y hace `FindObjectOfType`). No se guardan referencias entre
  sesiones: volver al menú destruye `GameManager` y `HeroController`. Se usan hooks estáticos
  `On.*` en lugar de eventos de instancia.
- **Nombres:** la clase del juego `SceneManager` choca con la de Unity; se usa siempre el alias
  `USM = UnityEngine.SceneManagement.SceneManager` y `global::SceneManager`.

---

## Fases

| Fase | Contenido | Hooks principales (verificados) | Criterio de "hecho" |
|---|---|---|---|
| 1 | Writer, `Runner`, sesión, `session.json`, `scene_exit`/`scene_enter`/`control_ready`/`game_state`, `validate.py` mínimo | `SavegameLoadHook`, `On.GameManager.StartNewGame`, `On.GameManager.BeginSceneTransition`, `On.GameManager.OnNextLevelReady`, `On.GameManager.FinishedEnteringScene`, `On.GameManager.ReturnToMainMenu`, `On.GameManager.LoadScene`, `On.TransitionPoint.OnTriggerEnter2D`, `On.HeroController.Die` (flag), `ApplicationQuitHook` | 5 min de juego (gate, puerta, banco, muerte, ciervo, sueño, salir al menú): la secuencia y los motivos cuadran. Pruebas T1–T3 |
| 2 | `hit_taken`, `hazard_respawn`, `death`, `hit_dealt`, `enemy_hp_change`, `enemy_killed`, `enemy_spawn`, `focus`, `fsm_state`, `boss_scene` + **entorno roto (`env_break`)**: objetos rompibles, postes, hierba cortada, enredaderas | `On.HeroController.TakeDamage/DieFromHazard/HazardRespawn/Die/StartMPDrain/StopMPDrain`, `On.HealthManager.TakeDamage/Invincible/ApplyExtraDamage/Die`, `OnReceiveDeathEventHook`, `RecordKillForJournalHook`, `OnEnableEnemyHook`; entorno: `On.Breakable.Break`, `On.BreakablePole.Hit`, `On.Grass.Hit`, `On.GrassBehaviour.CutReact`, `On.VinePlatformCut.Cut` | Golpes, muertes y objetos rotos cuadran con un vídeo del mismo tramo. Pruebas T5, T6 |
| 3 | `pd_change` (hooks + diff a 1 Hz) y `pd_list`. Snapshots de progreso. **Eventos derivados en el propio mod:** `boss_defeated` (bools de PlayerData que pasan a true y cuyo nombre es de jefe: `*Defeated`, `defeated*`, `killedFinalBoss`…, ~30 campos detectados por patrón), `ability_gained` (`has*` de habilidades, niveles de hechizo `fireballLevel/quakeLevel/screamLevel`, nail arts, aguijón onírico), `item_got` (amuletos `gotCharm_N`, fragmentos de máscara y de vaso, mejoras de aguijón, llaves, reliquias) | `SetPlayer{Bool,Int,Float,String,Vector3,Variable}Hook`, tabla de ~1460 campos con delegados tipados | Aparecen geo, amuletos, mapas, llaves… con old/new correctos; el Falso Caballero sale como `boss_defeated` y Vengeful Spirit como `ability_gained`. Prueba T4. Se decide la lista negra |
| 4 | `frames` | Muestreo desde `Runner`; `cState` como máscara de bits; `Rigidbody2D` en caché | Trayectorias coherentes, Hz reales ≈ objetivo, sin bajada de FPS medible |
| 5 | `inputs` + `device` | `InputHandler.Instance.inputActions` sondeado cada frame, flancos propios | Reconstruyo las pulsaciones de un tramo grabado |
| 6 | `scenes/<scene>.json` + `scene_object` | `On.GameManager.OnNextLevelReady` (+1 frame), `On.SceneData.SaveMyState_*` con diccionario sombra | JSON por escena con enemigos (incluidos inactivos), gates y objetos. Grafo de Dirtmouth y Crossroads correcto. Pruebas T7, T9 |
| 7 | HUD y modo debug | `CanvasUtil.CreateCanvas/CreateTextPanel`; línea propia en `Menu_Title` | Indicador correcto al grabar, en pausa y con un error forzado |
| 8 | Menú, exportar zip, comprobación de versión, kit de instalación, `to_parquet.py`, guía | `IMenuMod.GetMenuData`, `Application.unityVersion` + `ModHooks.ModVersion`, `System.IO.Compression` | Un compañero lo instala con la guía sin ayuda. < 25 MB/h |

Al final de cada fase: `dotnet build` sin errores → pruebas en el juego → me pasas `ModLog.txt`
y la carpeta de sesión → `tools/validate.py` → commit.

---

## Decisiones cerradas

1. **Frames:** 60 Hz en CSV (gzip al cerrar la sesión), configurable a 10/20/30 Hz solo para pruebas. Estimado ≈ 22 MB/h sin comprimir, ≈ 5 MB/h en gzip.
2. **Inputs:** ambos. Eventos de cambio con precisión de frame (`inputs.csv`) y `input_bitmask` en frames.
3. **Sesión:** desde cargar o crear una partida hasta volver al menú o cerrar el juego. Más un
   `launch_id` por arranque. Las sesiones de Godseeker y Steel Soul también se cubren (`StartNewGame`).
4. **Enemigos:** `type` = nombre del journal (`EnemyDeathEffects.playerDataName`) o el nombre
   normalizado. `key` = la clave de `PersistentBoolItem` (la misma que usa el juego) o la ruta
   jerárquica. `iid` solo dentro de la visita. Detalle en `schema.md` §4.3.
5. **`pd_change`:** hooks de `Set*` (que ven el valor anterior), emitiendo solo si hay cambio,
   más un diff a 1 Hz para lo que no pasa por hooks (cargas, reset, listas). Límite por campo.
   La lista negra se decide con datos de la fase 3.
6. **Motivo de `scene_exit`:** clasificador sobre `SceneLoadInfo` en `BeginSceneTransition`,
   guardando siempre los campos crudos para poder recalcularlo. Tabla en `schema.md` §4.1.
   `bench_respawn` deja de ser un motivo de salida: es la entrada tras una muerte o una carga.
7. **Experiencia:** automática, sin preguntar (`schema.md` §3.2): horas de Steam (o suma de las partidas), logros desbloqueados (o datos de las partidas) y el control que se use de verdad. El `player_id` va en `Mods/HKTelemetry/HKTelemetry.GlobalSettings.json` del kit y no aparece en el menú. El menú solo tiene: indicador de grabación, frecuencia de muestreo (pruebas) y modo debug.
8. **Escritura:** hilo dedicado, volcado cada 500 ms o 64 KB, texto plano en append durante la
   sesión, gzip al cerrar y recuperación al siguiente arranque. Si hay presión se descartan
   frames antes que eventos, siempre con contador.
9. **Datos extra incorporados:** `hazard_respawn`, `control_ready`, `game_state`, tiempo de carga
   por transición, `play_time` del juego, `time_scale`, `accepting`, `fsm_state` de Spell Control,
   `breakable`, `boss_scene` en Godhome, `perf` y `device`. `BlueHealthHook` queda descartado:
   la sangre vital se recoge con `pd_change`.

## Correcciones respecto al primer plan (por la verificación)

- `ModHooks.SceneChanged` **no salta** en puertas, muertes, cargas, ciervos ni sueños → se usan `BeginSceneTransition` y `OnNextLevelReady`.
- `BeforePlayerDeadHook` no ve las muertes en sueño ni en Godhome → `On.HeroController.Die`.
- `HitInstanceHook` y `SlashHitHook` no dan un evento por golpe → `On.HealthManager.TakeDamage`, más sondeo de `hp` para Flukenest y FSM.
- `TakeDamage` del héroe se llama en cada frame de contacto → solo se emite si cambia la vida.
- `hazardType` (entero) va desplazado en uno respecto al enum `HazardType`.
- La pausa no se puede deducir de `timeScale` (el *hit-stop* también lo pone a 0).
- `Constants.GAME_VERSION` y `ModHooks.version` son literales compilados → la versión se comprueba con `Application.unityVersion`.
- `AfterSavegameLoadHook` llega antes de actualizar `profileID` → el slot se toma de `SavegameLoadHook`.
- `scene_object` solo tiene resolución de salida de sala (el juego guarda en bloque).

## Verificación

- `dotnet build` en cada fase. La DLL se copia a `Managed/Mods/HKTelemetry/`.
- Prueba manual guiada por fase (criterios de la tabla y pruebas T1–T9 de `schema.md` §9), contrastada con vídeo.
- `python tools/validate.py <carpeta_sesión>`: JSON válido, `seq` sin huecos, `t_game` monótono,
  enter/exit emparejados, Hz de frames, `schema_version`.
- Robustez (fase 7): matar el proceso a mitad de sesión y recuperar los datos; forzar un error
  de escritura y ver el HUD en rojo.
- Rendimiento (fase 4): FPS con y sin el mod en la misma sala.
