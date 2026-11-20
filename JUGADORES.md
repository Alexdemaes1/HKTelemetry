# Cómo participar: instalar el mod y jugar

**HKTelemetry** es un mod de Hollow Knight que solo apunta **cómo juegas** (por dónde vas, cuándo
saltas, atacas o recibes golpes…) para un trabajo de máster de IA. **No cambia nada del juego**: ni
dificultad, ni ayudas, ni tus partidas guardadas.

Necesitas **Windows 10 u 11** y **Hollow Knight en Steam**. Son unos 5 minutos.

---

## 1. Cambia Hollow Knight a la versión 1.5.78

El mod solo funciona con esa versión (la anterior a la última actualización).

1. Abre Steam → **Biblioteca** → clic derecho en **Hollow Knight** → **Propiedades**.
2. Ve a **Versiones de juego y betas** (o «Betas»).
3. Elige **`1.5.78.11833`** (versión anterior).
4. Espera a que Steam termine de descargarla.

Tus partidas guardadas no se tocan.

## 2. Descarga el mod

👉 **[Descargar HKTelemetry.zip](https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/HKTelemetry.zip)** (unos 7 MB)

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
  **`HKTelemetry v1.0.0 - grabando como <tu nombre de Steam>`**
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
