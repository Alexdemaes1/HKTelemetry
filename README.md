# HKTelemetry

Mod de Hollow Knight que **solo registra** datos crudos de partida (escenas, combate, progreso,
mapa, rendimiento…) para crear un dataset de un máster de IA. No cambia la jugabilidad.

> ### 🎮 ¿Vienes a participar?
> Sigue **[JUGADORES.md](JUGADORES.md)**: cambiar a la versión 1.5.78 en Steam, descargar
> **[HKTelemetry.zip](https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/HKTelemetry.zip)**,
> descomprimir, doble clic en `1_INSTALAR.bat` y jugar. Los datos se envían solos.
> (No uses «Code → Download ZIP»: eso es el código fuente, no el mod.)

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
| [JUGADORES.md](JUGADORES.md) | **Instrucciones para los participantes** (descargar, instalar, jugar) |
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

Un único zip para todos: cada jugador se identifica solo por su cuenta de Steam (nombre e id; se
anonimiza después, al limpiar los datos).

```bash
python tools/make_kit.py --upload-url https://script.google.com/macros/s/XXXX/exec
```

Sale en `dist/HKTelemetry.zip` (compila el mod en Release y mete la Modding API v77 oficial,
comprobada por hash). Cada participante descomprime y ejecuta:

- `1_INSTALAR.bat`: busca el juego en Steam, comprueba que es la 1.5.78, copia de seguridad de partidas y
  de los ficheros del juego que cambia, instala la API (si no estaba) y el mod.
- `2_ENVIAR_DATOS.bat`: deja en el Escritorio `HKTelemetry_datos_<fecha>.zip` con todas las sesiones y el
  `ModLog.txt` (sin la ruta de usuario de Windows).
- `3_DESINSTALAR.bat`: quita el mod y, si la puso el kit, la API, dejando el juego como estaba.

Con `--upload-url` el mod envía cada sesión solo a una carpeta de Google Drive al terminarla
(ver [docs/upload-setup.md](docs/upload-setup.md)); sin ella, los datos llegan con `2_ENVIAR_DATOS.bat`.

Para validar lo recibido: descomprimir y `python tools/validate.py <carpeta_de_sesión> ...`.

### Publicar una versión para los jugadores

El enlace de [JUGADORES.md](JUGADORES.md) apunta a
`https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/HKTelemetry.zip`, así que siempre
descarga la última release que tenga un fichero llamado exactamente `HKTelemetry.zip`:

1. Genera el kit: `python tools/make_kit.py --upload-url <dirección /exec del receptor>` → `dist/HKTelemetry.zip`.
2. En GitHub: **Releases → Draft a new release** → *Choose a tag*: escribe la versión (p. ej. `v1.0.0`)
   → *Create new tag* → título `HKTelemetry 1.0.0`.
3. Arrastra `dist/HKTelemetry.zip` a la zona de ficheros adjuntos (sin cambiarle el nombre).
4. **Publish release**. El enlace de descarga funciona al momento.

Para una versión nueva, repite con otro número (`v1.0.1`…): el enlace pasa a la nueva sola. Ojo: el zip
lleva la dirección del receptor, y una release pública la deja visible para cualquiera. Está preparado
para eso (solo admite subidas con el formato del mod y tiene topes de espacio, ver
[docs/upload-setup.md](docs/upload-setup.md)).
