# HKTelemetry: brief del proyecto

> Instrucción para Claude Code: este documento es el contexto completo del proyecto.
> **No escribas código todavía.** Primero léelo entero, explora `reference/` y
> devuélveme un plan de implementación por fases y la lista de decisiones abiertas
> (sección 9) con tu recomendación para cada una. Cuando las cerremos, empezamos
> por la fase 1.

---

## 1. Contexto y objetivo

Proyecto de equipo para un máster de IA (asignatura de aprendizaje supervisado y no
supervisado). En lugar de usar un dataset de Kaggle, vamos a **generar nuestro propio
dataset** grabando partidas reales de Hollow Knight con un mod que registra datos
de juego en crudo.

Uso previsto de los datos (lo hago yo en Python, no es parte del mod):

- **Regresión** a nivel de visita a sala: predecir tiempo en sala, daño recibido o
  geo obtenido a partir del progreso del jugador, su experiencia y su comportamiento.
- **Clustering de salas**: descubrir tipos de sala (plataformeo, combate, jefe,
  gauntlet, tránsito, hub) combinando características estáticas de la sala con el
  comportamiento agregado de los jugadores.
- Comparación entre **jugador experto** (yo) y **jugadores novatos** (compañeros).
- Los **inputs en crudo** se guardan también para posibles proyectos futuros.

El mod **solo registra**. Todas las features se calculan después en Python.

## 2. Restricciones técnicas

- Versión del juego: **rama de Steam 1.5.78.11833**. La actualización de febrero
  de 2026 (build 12459, Unity 6) rompió la Modding API; no damos soporte a esa versión.
- Juego en Unity 2020.2.2, C#, **.NET Framework 4.7.2**. El mod es una DLL que carga
  la Modding API (hk-modding/api, basada en MonoMod).
- Proyecto creado con la plantilla oficial: `dotnet new install HKModding.HKMod.Templates`
  y `dotnet new hksettings ...` (necesitamos ajustes globales).
- Plataformas objetivo de los participantes: Windows con Steam o GOG. Mac/Linux si
  sale gratis, sin esfuerzo extra.

## 3. Estructura del workspace

```
hk-telemetry/
├── HKTelemetry/                 proyecto del mod
├── reference/
│   ├── decompiled/              Assembly-CSharp descompilado con ILSpy (NO subir a git)
│   ├── modding-api/             clon de hk-modding/api (incluye ejemplos)
│   └── GodhomeWinLossTracker/   mod de ejemplo que ya registra golpes y estado del héroe
├── tools/                       scripts de Python (validación, conversión a parquet)
├── docs/
│   ├── BRIEF.md                 este documento
│   └── schema.md                esquema de datos definitivo (lo generamos en el plan)
└── CLAUDE.md
```

## 4. Principios que no se negocian

1. **Antes de usar cualquier clase, campo o hook del juego, búscalo en
   `reference/decompiled/` o `reference/modding-api/`.** No inventes nombres de API.
   Si algo no está claro, dímelo y lo probamos en el juego.
2. **El logger nunca puede romper ni ralentizar el juego.** try/catch en todos los
   hooks; escritura a disco en un hilo aparte o con buffer y volcado por bloques,
   nunca línea a línea en cada frame.
3. **Registrar en crudo, al nivel más fino.** La sala, la visita, la sesión y el
   jugador son agregaciones que construyo yo después.
4. **Ningún dato perdido en silencio.** Cualquier fallo de escritura se ve en el HUD
   y queda en `ModLog.txt`.
5. **Versionado del esquema.** Todo cambio de formato incrementa `schema_version`.

## 5. Datos a registrar

### 5.1 Salida en disco

En la carpeta de guardado del juego: `HKTelemetry/<session_id>/`

| Fichero | Contenido |
|---|---|
| `session.json` | Metadatos de la sesión |
| `events.jsonl` | Un evento por línea, campos comunes + `payload` libre |
| `inputs.jsonl` o `.csv.gz` | Pulsaciones y sueltas de botones (por cambio) |
| `frames.csv.gz` | Muestreo de estado a frecuencia fija |
| `scenes/<scene>.json` | Catálogo estático de cada escena (una vez por escena y versión) |

### 5.2 `session.json`

- `schema_version`, versión del mod, versión del juego (comprobada en tiempo de ejecución)
- `session_id` (UUID), `player_id` (pseudónimo, viene de los ajustes del mod)
- Hora de inicio y fin en tiempo real, slot de guardado, hash o nombre del save de origen
- Dispositivo de entrada (mando o teclado), resolución
- Experiencia autodeclarada: la pondré en los ajustes (horas jugadas, si se lo ha
  pasado). Propón cómo.
- Snapshot de progreso al inicio y al final: aguijón, máscaras, vasos de alma,
  amuletos equipados y conseguidos, hechizos, habilidades, geo, porcentaje de juego.

### 5.3 Eventos (`events.jsonl`)

Campos comunes en todos: `t_game` (tiempo de juego, sin pausas), `t_real`,
`frame`, `scene`, `type`, `payload`.

Tipos mínimos (ampliar si en el código del juego ves algo útil):

| Tipo | Payload orientativo |
|---|---|
| `scene_enter` / `scene_exit` | gate de entrada/salida, escena destino, **motivo** (puerta, muerte, banco, estación de ciervo, sueño, carga de partida, salir al menú) |
| `hit_dealt` | enemigo, daño, tipo de ataque (aguijón, dirección, hechizo, amuleto), vida restante |
| `hit_taken` | fuente (enemigo o trampa), daño, vida resultante, estado del héroe |
| `enemy_killed` | enemigo, geo soltado si se puede leer |
| `death` | geo perdido en la sombra |
| `shade_killed` | geo recuperado |
| `heal` / `spell_cast` | tipo, alma gastada |
| `bench_rest`, `pause`, `unpause`, `quit` | — |
| `pd_change` | **comodín**: cualquier cambio en `PlayerData` (campo, valor anterior, valor nuevo) |
| `scene_object` | cambio de estado persistente de la escena (paredes rotas, palancas, rocas de geo, zonas ocultas…) |
| `item_pickup` | objeto, amuleto, llave, etc. |

El comodín `pd_change` es clave: queremos capturar cientos de datos que no hemos
enumerado. Revisa qué hooks de la API permiten interceptar los cambios de
`PlayerData` (bool, int, float, string…) y si conviene filtrar campos muy ruidosos.

### 5.4 Inputs

Objetivo: poder reconstruir exactamente qué pulsó el jugador y cuándo.

- Propuesta: registrar **eventos de cambio** (botón pulsado/soltado con timestamp
  y frame) en vez de solo muestrear, y además el estado de ejes analógicos cuando
  cambie por encima de un umbral.
- Acciones lógicas del juego (saltar, atacar, dash, hechizo, curar, super dash,
  mapa, inventario, direcciones), no teclas físicas. Busca cómo lo gestiona el juego.

### 5.5 Frames (`frames.csv.gz`)

Frecuencia configurable (por defecto 20–30 Hz). Columnas:
`t_game, frame, scene, x, y, vx, vy, hp, max_hp, soul, hero_state, grounded,
wall_sliding, dashing, facing, invulnerable, input_bitmask`

### 5.6 Catálogo de escenas (`scenes/<scene>.json`)

Generado al cargar cada escena por primera vez:

- Enemigos presentes al cargar: tipo, vida, posición, geo que sueltan
- Gates/transiciones: nombre, posición, escena destino
- Tamaño/límites de la escena, bancos, trampas (pinchos, ácido) si es viable
- Objetos persistentes de la escena (paredes rompibles, palancas, rocas de geo)

Con esto construyo el grafo de salas y las features estáticas sin scrapear la wiki.

## 6. HUD en el juego

- **Indicador de grabación** en una esquina, discreto:
  - verde (parpadeo suave): grabando
  - gris: pausado / en menú
  - rojo + mensaje corto: error de escritura, disco lleno, versión incorrecta
- **Línea en la pantalla de título**: `HKTelemetry vX.Y · grabando como P02 · juego 1.5.78 OK`
- Opción en el menú del mod para ocultar el indicador (que no condicione al jugador).
- **Modo debug** (solo para mí, activable en ajustes): contador de eventos de la
  sesión, tamaño de ficheros, último evento registrado, Hz reales de muestreo.

## 7. Menú del mod y distribución

- Ajustes globales: `player_id`, frecuencia de muestreo, ocultar HUD, modo debug,
  experiencia autodeclarada.
- Botón **"Exportar datos"**: comprime todas las sesiones en
  `Escritorio/HKTelemetry_<player_id>_<fecha>.zip`.
- Aviso al arrancar si la versión del juego no es la 1.5.78.
- Distribución: zip para instalación manual con Lumafly o en
  `hollow_knight_Data/Managed/Mods/HKTelemetry/`. No se publica en ModLinks.
- Cada participante recibe un kit: mod con su `player_id` ya configurado, guía de
  instalación en PDF y, posiblemente, partidas guardadas en puntos concretos del juego.
- Los participantes me envían el zip por correo. Tamaño objetivo: < 25 MB por hora.

## 8. Herramientas en Python (`tools/`)

- `validate.py`: comprueba una carpeta de sesión (JSON mal formado, saltos de tiempo,
  `scene_enter` sin `scene_exit`, frames con huecos, versión del esquema).
- `to_parquet.py`: convierte sesiones a parquet para el análisis.
- Más adelante: script que construye la tabla de visitas a sala.

## 9. Decisiones abiertas (quiero tu recomendación)

1. Frecuencia de muestreo de frames y si frames va en CSV gzip o en binario.
2. Inputs: solo eventos de cambio, solo bitmask en frames, o ambos.
3. Qué define el inicio y fin de una sesión (abrir juego, cargar partida, cada
   vuelta al menú…).
4. Cómo identificar enemigos de forma estable (nombre del GameObject, tipo, id).
5. Si `pd_change` necesita filtro o lista negra de campos ruidosos.
6. Cómo detectar el motivo de cada `scene_exit` de forma fiable.
7. Cómo registrar la experiencia autodeclarada del jugador.
8. Estrategia de escritura (hilo dedicado, cola, tamaño de bloque, qué pasa si el
   juego se cierra de golpe).
9. Cualquier dato útil que veas en el código del juego y no esté en este brief.

## 10. Fases (orientativo, ajústalo en el plan)

| Fase | Contenido | Criterio de "hecho" |
|---|---|---|
| 1 | Carga, sesión, `scene_enter`/`scene_exit` con gates y motivo | Juego 5 min y la secuencia de escenas es correcta |
| 2 | Daño, muertes, enemigos muertos, bancos, pausas | Los golpes cuadran con lo que vi |
| 3 | Comodín `pd_change` | Aparecen cambios de geo, amuletos, mapas… |
| 4 | Frames | Trayectorias y velocidades coherentes, sin tirones |
| 5 | Inputs | Puedo reconstruir pulsaciones de un tramo grabado |
| 6 | Catálogo de escenas y objetos persistentes | JSON por escena con enemigos y gates |
| 7 | HUD y modo debug | Indicador correcto en grabación, pausa y error forzado |
| 8 | Ajustes, exportar, comprobación de versión, empaquetado | Un compañero lo instala con la guía sin ayuda |

Después de cada fase: `dotnet build` sin errores, yo pruebo en el juego y te paso
`ModLog.txt` y la carpeta de sesión, y pasamos `tools/validate.py`.

## 11. Fuera de alcance

- Subida automática de datos a un servidor.
- Soporte para la versión 1.5.12459 del juego.
- Cualquier cosa que modifique la jugabilidad.
