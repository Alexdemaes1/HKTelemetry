# Estado del mod HKTelemetry (22/11/2026), para el plan del ML Challenge

Actualización de la sección 1 del plan y de lo que el equipo necesita saber para el pipeline de datos.
Repo: https://github.com/Alexdemaes1/HKTelemetry (release **v1.0.0**).

## 1. Checklist «Antes de repartir el mod»

| Punto del plan | Estado |
|---|---|
| Publicar la release | ✅ **v1.0.0 publicada** (22/11). El enlace de JUGADORES.md ya descarga el kit (comprobado: el zip descargado es idéntico al probado). A partir de ahora la publica sola un workflow de GitHub Actions al subir una etiqueta `vX.Y.Z`. |
| Actualizar el receptor y probar el envío jugando | ✅ **Envío probado dentro del juego** con el kit final: el mod leyó la identidad de Steam desde Unity (`player_id=S875967793`) y subió 6 sesiones a Drive desde el menú en 50 s. ⚠️ **Único pendiente, de Alex (2 min):** publicar la versión nueva de `server/Code.gs` en Apps Script (Gestionar implementaciones → Nueva versión). Hasta entonces el receptor rechaza los ids de Steam (`S…`); no se pierde nada: las sesiones se quedan en el PC del jugador y el mod las reenvía solo en cada arranque. |
| Fase 6 (catálogo de salas) | **Decidido: va en una v1.1, no en la v1.0.0** (la gente ya tenía el enlace). Lo esencial para el clustering ya se graba: `enemy_spawn` registra **todos** los enemigos presentes al entrar en una sala (también los ya muertos, con `already_dead`), con su tipo y vida; `env_break` lo que se rompe; `scene_exit` las salidas reales; `frames.csv` el recorrido (tamaño aproximado). El catálogo completo (tamaño exacto, salidas, objetos rompibles, arenas, `scenes/<escena>.json`) es casi igual para todos los jugadores, así que basta con que lo graben los 4 del equipo con la v1.1. El mod ya avisa en el menú cuando hay versión nueva. |
| Prueba A/B del input lag | ✅ Cerrada: no hay input lag (medido: el mod cuesta ~46–63 µs por frame). |
| Piloto con una persona | Descartado: el enlace ya se ha repartido. Los primeros envíos que lleguen hacen de piloto: revisarlos con `tools/validate.py`. |
| Pruebas T1–T9 (schema §9) | Se cierran revisando los primeros datos reales (banco, sombra, hechizos, muros, arenas…). |

## 2. Qué recibimos y dónde

- **Drive** (cuenta del proyecto): `HKTelemetry_datos/<jugador>/<sesión>.zip.partNNNofMMM`. Cada sesión llega en
  trozos de 5 MB: concatenarlos en orden da un zip con la carpeta de la sesión.
- **Plan B:** `2_ENVIAR_DATOS.bat` del jugador genera `HKTelemetry_datos_<fecha>.zip` con todas sus sesiones.
- **Volumen:** ~7 MB por hora de juego (20 h ≈ 130 MB por jugador).
- **Excluir:** las carpetas `P00`/`P99` del Drive son sesiones de desarrollo de Alex (las del 16/11 son de
  versiones antiguas del formato, sin `frames.csv`).

## 3. Contenido de cada sesión (formato completo en `docs/schema.md`, schema v3)

Una carpeta por partida, desde que se carga un save hasta «Guardar y salir»:

| Fichero | Qué tiene |
|---|---|
| `session.json` | **identidad** (`player_id` = `S` + id de cuenta de Steam; `identity.steam_name`, `steam_id64`); **experiencia** (`steam_playtime_min` de la cuenta que juega, logros desbloqueados, progreso de cada save); foto de progreso al empezar y al acabar (aguijón, máscaras, alma, hechizos, habilidades, amuletos, geo, % completado…); juego (`other_mods` para filtrar sesiones con mods), pantalla, contadores de calidad (eventos/filas perdidas, errores) |
| `events.jsonl.gz` | un evento JSON por línea con `seq, t_game, t_real, frame, play_time, scene, gs, type, p`. Tipos: `scene_enter` / `scene_exit` (con motivo: gate, door, death, stag, dream, quit_menu…), `control_ready`, `hit_taken`, `hit_dealt`, `enemy_spawn`, `enemy_killed`, `enemy_hp_change`, `death`, `hazard_respawn`, `focus`, `env_break`, `boss_defeated`, `ability_gained`, `item_got`, `pd_change` (cualquier cambio de PlayerData: geo, vida, alma…), `map_open/close/pan/marker`, `device`, `game_state`, `perf`… |
| `frames.csv.gz` | 60 Hz: posición, velocidad, vida, alma, estado del héroe, 55 flags de estado (en el suelo, dash, atacando, curándose…), si acepta input, `time_scale` (hit-stop), botones pulsados desde la fila anterior, stick |
| `inputs.csv.gz` | cada pulsación y suelta de 16 acciones (saltar, atacar, dash, hechizo, curar, mapa…) y del stick, con `accepting` (si el juego la usó) y dispositivo |
| `pd_start/pd_end.json.gz` | volcado completo de PlayerData al empezar y al acabar |

**Relojes:** `t_game` excluye pausa, menús y cargas (mismo criterio que el reloj del save); `t_real` es reloj
de pared. Para duraciones de visita, usar `t_game`.

## 4. Notas para el pipeline (sección 3 del plan)

- **`visits`:** cada visita = `scene_enter` → siguiente `scene_exit` (el motivo de salida dice si fue muerte,
  puerta, ciervo, salir del juego…). Daño recibido: `hit_taken`; causado: `hit_dealt`; enemigos: `enemy_killed`;
  geo: `pd_change` del campo `geo`; features de movimiento e inputs: `frames.csv` / `inputs.csv` filtrando por
  el intervalo `t_game` de la visita.
- **`rooms`:** enemigos presentes por sala con `enemy_spawn` (agregado sobre todas las visitas), zona del mapa en
  `scene_enter.map_zone`; el resto de features estáticas con la v1.1.
- **`players`:** `session.json` → `identity` + `experience`.
- **Anonimización:** además de `steam_name`, `steam_id64` y `steam_account_id`, el id de cuenta aparece en
  `player_id`, en el nombre de cada carpeta de sesión y en la carpeta de Drive. Hay que sustituirlo en los tres.
- **Validación:** `python tools/validate.py <carpeta_sesión>` comprueba formato, huecos y coherencia de cada
  sesión y resume combate, mapa, frames e inputs.
- **Pendiente (repo del challenge):** `to_parquet.py` y la construcción de `visits` / `rooms` / `players`.
