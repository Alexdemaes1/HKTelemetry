# Cómo participar: instalar el mod y jugar

**HKTelemetry** es un mod de Hollow Knight que solo apunta **cómo juegas** (por dónde vas, cuándo
saltas, atacas o recibes golpes…) para un trabajo de máster de IA. **No cambia nada del juego**: ni
dificultad, ni ayudas, ni tus partidas guardadas.

Necesitas **Hollow Knight** en **Windows 10/11**, **Steam Deck** o **Mac**. Son unos 5 minutos.

> 🎮 **¿Steam Deck o Mac?** Haz el paso 1 y salta a [Steam Deck y Mac](#steam-deck-y-mac)
> (los `.bat` solo funcionan en Windows).
>
> 💾 **¿Tu Hollow Knight no es de Steam** (GOG u otra copia)? Sigue
> [Si tu juego no es de Steam](#si-tu-juego-no-es-de-steam): es igual de fácil.

---

## 1. Cambia Hollow Knight a la versión 1.5.78

El mod solo funciona con esa versión (la anterior a la última actualización).

1. Abre Steam → **Biblioteca** → clic derecho en **Hollow Knight** → **Propiedades**.
2. Ve a **Versiones de juego y betas** (o «Betas»).
3. Elige **`1.5.78.11833`** (versión anterior).
4. Espera a que Steam termine de descargarla.

Tus partidas guardadas no se tocan.

## 2. Descarga el mod

👉 **[Descargar HKTelemetry.zip](https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/HKTelemetry.zip)** (unos 21 MB)

> ⚠️ **No** uses el botón verde «Code → Download ZIP» de esta página: eso descarga el código fuente,
> no el mod.

Si el navegador dice que el archivo «puede ser peligroso» o «no se descarga habitualmente», elige
**Conservar** / **Mantener**. Lo avisa porque el zip lleva programas (`.bat`) sin firma digital.

## 3. Descomprime el zip

Clic derecho en `HKTelemetry.zip` → **Extraer todo…** → **Extraer**.
(Si abres los ficheros desde dentro del zip sin extraerlo, no funcionan.)

## 4. Instala

1. **Cierra Hollow Knight** si está abierto.
2. En la carpeta extraída, doble clic en **`1_INSTALAR.bat`**.
3. Si Windows muestra **«Windows protegió su PC»**: pulsa **Más información** → **Ejecutar de todas formas**.
4. Lee el aviso de qué datos se recogen y escribe **S** para aceptar.
5. Espera a que diga **Listo**. Todas las líneas deberían salir en verde.

El instalador busca el juego solo (aunque esté en otro disco), comprueba la versión y guarda una copia
de tus partidas y de los ficheros del juego que cambia antes de tocar nada.

## 5. Juega

- Abre Hollow Knight desde Steam como siempre. En el menú principal, abajo, verás:
  **`HKTelemetry v1.0.3 - grabando como <tu nombre de Steam>`**
- **Juega como juegues normalmente**: tu partida de siempre o una nueva, el rato que quieras.
- Arriba a la derecha hay un punto pequeño:
  - 🟢 verde parpadeando = grabando
  - ⚪ gris = en pausa o en menús
  - 🔴 rojo con un mensaje = algo falla → haz una captura y avísame
- **Los datos se envían solos**: al hacer «Guardar y salir», desde el menú principal y sin afectar
  al juego. En la línea de abajo del menú verás «enviando datos…» y luego **«datos enviados»**.
  Si no hay internet, lo reintenta la próxima vez que abras el juego.

**No tienes que hacer nada más.** 🙂

---

## Si tu juego no es de Steam

El mod funciona con cualquier copia de Hollow Knight para Windows, sea de donde sea, **si es la
versión 1.5.78** (la anterior a la última actualización; la que usan casi todos los mods). Si no
estás seguro de cuál tienes, no pasa nada: el instalador lo comprueba y, si no es esa, **no toca nada**.

1. **Descarga** el mod y **descomprime** el zip (pasos 2 y 3 de arriba).
2. **Cierra el juego** y haz doble clic en **`1_INSTALAR.bat`**. Acepta el aviso con **S**.
3. Si dice **«No he encontrado Hollow Knight automáticamente»**: abre en el Explorador la carpeta
   donde tienes el juego (la que tiene `hollow_knight.exe`), **arrástrala a la ventana negra** y
   pulsa **Enter**.
4. Si dice que tu copia **«no es idéntica a la de Steam»**: escribe **S**. Es normal en copias que no
   son de Steam; antes de tocar nada guarda los ficheros originales, y `3_DESINSTALAR.bat` los
   devuelve si algo fuera mal.
5. Cuando diga **Listo**, abre el juego como siempre y juega. En el menú principal verás
   **`grabando como U…`** (un código al azar, porque no hay cuenta de Steam). Ese código eres tú; se
   guarda junto a tus partidas, así que se mantiene aunque reinstales el mod o el juego.

Los datos se envían solos, igual que en Steam (necesita internet).

**Si dice que tu versión no es la 1.5.78:** necesitas una copia de esa versión (por ejemplo, la de
GOG). Cuando la tengas, ejecuta otra vez `1_INSTALAR.bat` y elige esa carpeta.

---

## Steam Deck y Mac

Primero el **paso 1** de arriba (versión 1.5.78; en la Steam Deck: Hollow Knight → rueda dentada →
Propiedades → Versiones de juego y betas). Después:

### Steam Deck

1. Pasa al **modo Escritorio**: botón Steam → Apagar → **Cambiar al escritorio**.
2. Abre **Konsole** (menú de aplicaciones, abajo a la izquierda → Sistema → Konsole).
   Para escribir: **botón Steam + X** saca el teclado en pantalla.
3. Escribe esta línea y pulsa Enter:

   ```bash
   curl -fsSL https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/instalar.sh | bash
   ```

   Descarga el mod en la carpeta `HKTelemetry` de tu usuario y lo instala. Lee el aviso y escribe **S**.
4. Vuelve al **modo Juego** y juega normal. Funciona con la versión de Linux y con Proton.

### Mac

Abre **Terminal** (Aplicaciones → Utilidades), pega la misma línea y pulsa Enter:

```bash
curl -fsSL https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/instalar.sh | bash
```

(También vale descargar el zip, descomprimirlo y hacer doble clic en `SteamDeck_Mac/1_INSTALAR.command`;
si macOS dice «desarrollador no identificado»: clic derecho → Abrir → Abrir.)

### Enviar datos a mano y desinstalar (Steam Deck y Mac)

Los datos se envían solos igual que en Windows. Si hiciera falta, en Konsole / Terminal:

```bash
bash ~/HKTelemetry/SteamDeck_Mac/hktelemetry.sh enviar        # deja un fichero en el Escritorio
bash ~/HKTelemetry/SteamDeck_Mac/hktelemetry.sh desinstalar   # quita el mod y deja el juego como estaba
```

Para actualizar a una versión nueva, repite la línea de instalación.

---

## Preguntas frecuentes

**El instalador dice que la versión no es la correcta.**
Repite el paso 1 y espera a que Steam termine de descargar la 1.5.78.

**El juego no arranca después de instalar.**
Ejecuta `3_DESINSTALAR.bat`, o en Steam → Propiedades → Archivos instalados → **Verificar integridad
de los archivos**. Luego avísame.

**El antivirus ha borrado o bloqueado algo.**
El instalador lo detecta y no toca el juego. Vuelve a descomprimir el zip; si se repite, avísame.

**En el menú pone «N sesiones por enviar» y no baja.**
Comprueba que tienes internet y vuelve a abrir el juego. Si sigue igual: cierra el juego, ejecuta
`2_ENVIAR_DATOS.bat` (deja un zip en tu Escritorio) y envíamelo por donde te venga mejor.

**¿Puedo desactivar el envío o el punto de grabación?**
Sí: en el juego, **Opciones → Mods → HKTelemetry**.

**Hay una versión nueva del mod.**
Descárgala desde el mismo enlace, descomprímela y ejecuta `1_INSTALAR.bat` otra vez. Se instala
encima, sin perder nada.

**Ya no quiero participar.**
Cierra el juego y ejecuta **`3_DESINSTALAR.bat`**: quita el mod y deja el juego como estaba. Para volver
a la última versión de Hollow Knight: Steam → Propiedades → Versiones de juego y betas → **Ninguna**.

---

## Qué datos se recogen

- Lo que pasa en la partida: salas por las que pasas, combates, vida, alma, objetos, jefes, mapa, muertes…
- La posición del personaje y los botones que pulsas (60 veces por segundo).
- Tu experiencia previa: horas jugadas a Hollow Knight en Steam, logros y progreso de tus partidas.
- Datos técnicos: fps, resolución, versión del juego y otros mods instalados.
- Para saber de quién es cada partida: tu **nombre de Steam** y el **número de tu cuenta de Steam**
  (nunca tu usuario ni tu contraseña). Se sustituyen por un código anónimo antes de analizar los datos.

Nada más de fuera de Hollow Knight. Las sesiones se envían a una carpeta de Google Drive del proyecto a
la que solo tengo acceso yo, y solo se usan para el trabajo de máster. Si quieres que se borren tus
datos, dímelo.

¡Gracias por participar!
