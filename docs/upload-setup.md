# Envío automático de datos a Google Drive

El mod sube cada sesión al terminarla (desde el menú principal, en segundo plano) a un pequeño
receptor hecho con Google Apps Script, que la guarda en tu Google Drive. Código del receptor:
[`server/Code.gs`](../server/Code.gs). Código del mod: `HKTelemetry/Session/Uploader.cs`.

## Qué viaja en el kit y qué no

- En el kit solo va la **dirección web** del receptor (`https://script.google.com/macros/s/.../exec`).
  No va ninguna contraseña, clave ni token.
- Con esa dirección solo se puede **subir** trozos con el formato del mod: identificadores de jugador
  válidos (`ALLOWED_PLAYER` en `Code.gs`: `S` + id de cuenta de Steam, `U` + id de instalación, o
  P00–P99 para pruebas), código de sesión válido, zip, sha256
  correcto, máximo 8 MB por trozo. No se puede leer, listar, sustituir ni borrar nada: si llega un trozo
  con el mismo nombre y otro contenido, se guardan los dos.
- Topes de espacio: 1 GB por jugador y **12 GB en total** (por debajo de los 15 GB gratis), así que ni
  un abuso puede llenar la cuenta.
- Si la dirección se filtra y alguien abusa: se crea una implementación nueva (otra dirección), se
  archiva la antigua y se generan kits nuevos. Los datos ya recibidos no se tocan.
- Recomendado: una **cuenta de Google personal nueva (gmail.com) solo para el proyecto**, no la tuya ni
  una de la universidad (las cuentas de Google Workspace suelen no permitir el acceso "Cualquier usuario").
  Tiene 15 GB gratis; 20 h de juego son ~130 MB por persona.

## Puesta en marcha (unos 10 minutos)

1. Entra con la cuenta del proyecto en <https://script.google.com> y pulsa **Nuevo proyecto**.
2. Ponle nombre (arriba a la izquierda), por ejemplo `HKTelemetry receptor`.
3. Borra el contenido de `Código.gs` y pega **todo** el contenido de `server/Code.gs`. Guarda (Ctrl+S).
4. **Implementar → Nueva implementación**:
   - Tipo (rueda dentada): **Aplicación web**.
   - Descripción: `v1`.
   - Ejecutar como: **Yo** (la cuenta del proyecto).
   - Quién tiene acceso: **Cualquier usuario** (en inglés, *Anyone*). Hace falta para que el mod pueda
     enviar sin iniciar sesión; solo permite subir con el formato del mod. **No** elijas "Cualquier
     usuario con una cuenta de Google": el mod no inicia sesión y todas las subidas fallarían.
5. Pulsa **Implementar**. Google pedirá **autorizar el acceso**: elige la cuenta del proyecto. Saldrá
   "Google no ha verificado esta aplicación": **Configuración avanzada → Ir a HKTelemetry receptor
   (no seguro)** → **Permitir**. Es normal en scripts propios; el permiso es para crear ficheros en tu Drive.
6. Copia la **URL de la aplicación web** (termina en `/exec`).
7. Compruébala **sin sesión iniciada**: ábrela en una ventana privada / de incógnito. Debe responder
   `{"ok":true,"status":"HKTelemetry receptor v1"}`. Si sale la pantalla de inicio de sesión de Google, el
   acceso no está en "Cualquier usuario": vuelve al paso 4 (Gestionar implementaciones → editar).
8. Genera los kits con ella:

   ```bash
   python tools/make_kit.py --upload-url https://script.google.com/macros/s/XXXX/exec
   ```

Los datos aparecen en tu Drive en `HKTelemetry_datos/<jugador>/<sesión>.zip.part001of002`, etc.
No muevas `HKTelemetry_datos` a la papelera: el receptor la recuerda por su id y, si está en la
papelera, rechaza las subidas (el mod lo reintenta y los datos siguen en los PCs) hasta que la
restaures. Cada jugador tiene su carpeta con el id de su cuenta de Steam (`S875967793`); su nombre de
Steam va dentro de cada sesión (`session.json` → `identity.steam_name`).

## Si cambias el receptor

Si editas `Code.gs` después: **Implementar → Gestionar implementaciones → editar (lápiz) →
Versión: Nueva versión → Implementar**. Así la dirección no cambia. Una **Nueva implementación**
crea otra dirección, y los kits ya repartidos seguirían enviando a la antigua.

## Recoger los datos

Descarga la carpeta `HKTelemetry_datos` de Drive (clic derecho → Descargar). Cada sesión llega en
trozos de 5 MB; para unirlos basta concatenarlos en orden (`part001`, `part002`…) y se obtiene un zip
con la carpeta de la sesión, igual que la que hay en el PC del jugador. El script de procesado
(`tools/build_dataset.py`, pendiente) lo hará solo.

## Cómo se comporta el mod

- Solo envía sesiones **cerradas y comprimidas**, y solo **mientras no se está jugando** (menú
  principal). Si empieza una partida, termina el trozo en curso y espera.
- Sin internet o con el receptor caído: lo reintenta más tarde (y en el siguiente arranque). Los datos
  siguen en el PC; `2_ENVIAR_DATOS.bat` sirve como plan B.
- Cada sesión enviada queda marcada con `upload.json` en su carpeta. Reenviar es seguro: el receptor
  descarta trozos repetidos (mismo nombre y sha256).
- En el menú principal, la línea de HKTelemetry dice "datos enviados", "N sesiones por enviar" o
  "enviando datos...". El jugador puede desactivarlo en Opciones → Mods → HKTelemetry.
