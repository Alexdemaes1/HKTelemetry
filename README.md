# HKTelemetry

Mod de Hollow Knight que **solo registra** datos crudos de partida (escenas, combate, progreso,
mapa, rendimiento…) para crear un dataset de un máster de IA. No cambia la jugabilidad.

- Juego: rama de Steam **1.5.78.11833** («previous version»), Modding API v77.
- Datos: `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight\HKTelemetry\`, una carpeta por sesión.

## Documentación

| Documento | Contenido |
|---|---|
| [docs/plan.md](docs/plan.md) | Plan por fases, **estado actual** y **próxima prueba pendiente** |
| [docs/schema.md](docs/schema.md) | Formato de los datos (schema v2) |
| [docs/BRIEF.md](docs/BRIEF.md) | Brief original del proyecto |
| [docs/api-notes/](docs/api-notes/) | Verificación de la API del juego, con referencias al código |
| [CLAUDE.md](CLAUDE.md) | Reglas del proyecto y trampas conocidas |

## Compilar y validar

```bash
cd HKTelemetry
dotnet build
```

`dotnet build` copia la DLL a `Managed/Mods/HKTelemetry/` (con el juego cerrado). Después de jugar:

```bash
python tools/validate.py --all
```
