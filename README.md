# HKTelemetry

Mod de Hollow Knight que **solo registra** datos crudos de partida (escenas, combate, progreso,
mapa, rendimiento…) para crear un dataset de un máster de IA. No cambia la jugabilidad.

- Juego: rama de Steam **1.5.78.11833** («previous version»), Modding API v77.
- Datos: `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight\HKTelemetry\`, una carpeta por sesión.

## Documentación

| Documento | Contenido |
|---|---|
| [docs/plan.md](docs/plan.md) | Plan por fases, **estado actual** y **próxima prueba pendiente** |
| [docs/schema.md](docs/schema.md) | Formato de los datos (schema v3) |
| [docs/BRIEF.md](docs/BRIEF.md) | Brief original del proyecto |
| [docs/api-notes/](docs/api-notes/) | Verificación de la API del juego, con referencias al código |
| [CLAUDE.md](CLAUDE.md) | Reglas del proyecto y trampas conocidas |
| [kit/LEEME.txt](kit/LEEME.txt) | Guía que reciben los participantes |

## Compilar y validar

```bash
cd HKTelemetry
dotnet build
```

`dotnet build` copia la DLL a `Managed/Mods/HKTelemetry/` (con el juego cerrado). Después de jugar:

```bash
python tools/validate.py --all
```

## Kit para participantes

Un zip por persona, con su código de jugador ya puesto:

```bash
python tools/make_kit.py P01-P10
```

Sale en `dist/HKTelemetry_P01.zip`, etc. (compila el mod en Release y mete la Modding API v77 oficial,
comprobada por hash). Cada participante descomprime y ejecuta:

- `1_INSTALAR.bat`: busca el juego en Steam, comprueba que es la 1.5.78, copia de seguridad de partidas y
  de los ficheros del juego que cambia, instala la API (si no estaba) y el mod.
- `2_ENVIAR_DATOS.bat`: deja en el Escritorio `HKTelemetry_P01_<fecha>.zip` con todas las sesiones y el
  `ModLog.txt` (sin la ruta de usuario de Windows).
- `3_DESINSTALAR.bat`: quita el mod y, si la puso el kit, la API, dejando el juego como estaba.

Para validar lo recibido: descomprimir y `python tools/validate.py <carpeta_de_sesión> ...`.
