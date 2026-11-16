# HKTelemetry

Mod de Hollow Knight que **solo registra** datos crudos de partida para un dataset de un
máster de IA. Contexto completo: [docs/BRIEF.md](docs/BRIEF.md). Plan por fases y decisiones
cerradas: [docs/plan.md](docs/plan.md). Esquema de datos: `docs/schema.md` (se crea al final de la fase 0).

## Objetivo técnico

- Juego: **rama de Steam 1.5.78.11833** (Unity 2020.2.2, .NET Framework 4.7.2).
  No hay soporte para la build 12459 / Unity 6.
- Modding API: `hk-modding/api` v77 (`reference/modding-api/hollowknight.version` = 1.5.78.11833).
- Ruta del juego en el equipo de desarrollo:
  `C:\Program Files (x86)\Steam\steamapps\common\Hollow Knight\hollow_knight_Data\Managed`

## Principios que no se negocian

1. **Antes de usar una clase, campo o hook del juego, búscalo en `reference/decompiled/` o
   `reference/modding-api/`.** No inventes nombres de API. Si no está claro, se prueba en el juego.
2. **El logger nunca rompe ni ralentiza el juego.** try/catch en todos los hooks (`SafeHook`).
   Sin E/S en el hilo de Unity: los hooks encolan y un hilo dedicado escribe por bloques.
3. **Los hooks que devuelven valor siempre devuelven la entrada sin tocar.** Cero cambios de jugabilidad.
4. **Registrar en crudo, al nivel más fino.** Las agregaciones se hacen en Python.
5. **Ningún dato perdido en silencio.** Fallos de escritura o pérdidas se ven en el HUD y en `ModLog.txt`.
6. **Versionado del esquema.** Todo cambio de formato incrementa `schema_version` y actualiza `docs/schema.md`.

## Trampas verificadas (detalle en docs/api-notes/ y docs/plan.md)

- **Hooks prohibidos** (alteran el juego aunque devuelvan un valor «neutro»): `BlueHealthHook`,
  `FocusCostHook`, `CursorHook`, `DashPressedHook` y cualquier `Get*Hook` de PlayerData.
- `ModHooks.SceneChanged` no salta en transiciones normales: usar `On.GameManager.BeginSceneTransition`
  y `On.GameManager.OnNextLevelReady`.
- Sondeos con `HeroController.UnsafeInstance` y `GameManager.UnsafeInstance`, nunca con `.instance`.
- La clase `SceneManager` del juego choca con la de Unity: usar `global::SceneManager` y un alias para la de Unity.
- `reference/` está en `.gitignore`, así que Grep/ripgrep la ignora por defecto: pasar la ruta explícita
  (`path: reference/decompiled/Assembly-CSharp`).
- `reference/decompiled/` es la DLL **ya parcheada** por la API (incluye `Modding.*` y `orig_*`).
  Los originales del juego están en `reference/vanilla-managed-backup/`.

## Estructura

```
HKTelemetry/     proyecto del mod (C#)
reference/       material de consulta, NO versionado (decompiled/, modding-api/, ...)
tools/           scripts de Python (validate.py, to_parquet.py)
docs/            BRIEF.md, plan.md, schema.md
```

## Flujo por fase

`dotnet build` sin errores → el usuario prueba en el juego → pasa `ModLog.txt` y la carpeta de
sesión → `python tools/validate.py <carpeta_sesión>` → commit.
