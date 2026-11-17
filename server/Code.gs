/**
 * HKTelemetry - receptor de datos en Google Drive (Apps Script).
 *
 * Qué hace: recibe trozos de las sesiones que sube el mod y los guarda en Drive, en
 *   HKTelemetry_datos/<jugador>/<sesión>.zip.partNNNofMMM
 * Nada más: no lee, no lista, no sustituye y no borra nada. Quien tenga la dirección solo puede
 * SUBIR trozos con el formato del mod, de códigos de jugador permitidos y hasta un tope de espacio.
 *
 * Instalación: docs/upload-setup.md (pegar este fichero en script.google.com e
 * "Implementar > Nueva implementación > Aplicación web", ejecutar como "Yo", acceso "Cualquier usuario").
 *
 * Protocolo (POST, cuerpo = trozo en base64, texto plano):
 *   ?v=1&player=P07&session=20261117_195746_P07_6b300850&part=1&parts=3&sha256=<hex del trozo>
 * Respuesta (siempre HTTP 200 en Apps Script): JSON {"ok":true,"status":"saved"|"duplicate"|"conflict-kept"}
 * o {"ok":false,"error":"..."}. Los errores que empiezan por "server" son temporales (el mod reintenta);
 * el resto son definitivos.
 */

// ---- Ajustes ----
// Identificadores aceptados (HKTelemetry/Session/Identity.cs): "S" + id de cuenta de Steam,
// "U" + id aleatorio de instalación (sin Steam) y P00-P99 (desarrollo/pruebas).
var ALLOWED_PLAYER = /^(S\d{1,10}|U[0-9a-f]{8}|P\d{2})$/;
var MAX_PART_BYTES = 8 * 1024 * 1024;                 // el mod sube trozos de 5 MB
var MAX_PARTS = 400;                                  // 2 GB por sesión: muy por encima de lo real
var MAX_BYTES_PER_PLAYER = 1024 * 1024 * 1024;        // 1 GB (~150 h de juego)
var MAX_BYTES_TOTAL = 12 * 1024 * 1024 * 1024;        // por debajo de los 15 GB gratis de la cuenta
var ROOT_FOLDER = 'HKTelemetry_datos';

var SESSION_RE = /^\d{8}_\d{6}_[A-Za-z0-9_]{1,16}_[0-9a-f]{8}$/;

function doPost(e) {
  try {
    var p = (e && e.parameter) || {};
    if (p.v !== '1') return reply_(false, 'version');
    var player = String(p.player || '');
    var session = String(p.session || '');
    var part = parseInt(p.part, 10);
    var parts = parseInt(p.parts, 10);
    var sha = String(p.sha256 || '').toLowerCase();
    if (!ALLOWED_PLAYER.test(player)) return reply_(false, 'player');
    if (!SESSION_RE.test(session)) return reply_(false, 'session');
    if (session.indexOf('_' + player + '_') < 0) return reply_(false, 'player/session');
    if (!(parts >= 1 && parts <= MAX_PARTS && part >= 1 && part <= parts)) return reply_(false, 'part');
    if (!/^[0-9a-f]{64}$/.test(sha)) return reply_(false, 'sha256');
    if (!e.postData || !e.postData.contents) return reply_(false, 'empty');

    var bytes = Utilities.base64Decode(e.postData.contents);
    if (bytes.length === 0 || bytes.length > MAX_PART_BYTES) return reply_(false, 'size');
    if (hex_(Utilities.computeDigest(Utilities.DigestAlgorithm.SHA_256, bytes)) !== sha) return reply_(false, 'checksum');
    // El primer trozo es el inicio de un zip ("PK\x03\x04").
    if (part === 1 && !(bytes[0] === 0x50 && bytes[1] === 0x4b && bytes[2] === 3 && bytes[3] === 4)) return reply_(false, 'zip');

    var name = session + '.zip.part' + pad_(part) + 'of' + pad_(parts);
    var lock = LockService.getScriptLock();
    if (!lock.tryLock(30000)) return reply_(false, 'server: busy');
    try {
      var folder = playerFolder_(player);
      if (!folder) return reply_(false, 'server: root folder missing or in trash');
      var conflict = false;
      var existing = folder.getFilesByName(name);
      while (existing.hasNext()) {
        var f = existing.next();
        if (f.isTrashed()) continue;
        if (f.getDescription() === sha) return reply_(true, 'duplicate');
        conflict = true; // mismo nombre, otro contenido: se guardan los dos, nunca se sustituye
      }
      if (conflict) {
        name = name + '.' + sha.substring(0, 8);
        var again = folder.getFilesByName(name);
        while (again.hasNext()) { if (!again.next().isTrashed()) return reply_(true, 'duplicate'); }
      }

      var props = PropertiesService.getScriptProperties();
      var total = Number(props.getProperty('bytes_total') || 0);
      var mine = Number(props.getProperty('bytes_' + player) || 0);
      if (total + bytes.length > MAX_BYTES_TOTAL) return reply_(false, 'quota total');
      if (mine + bytes.length > MAX_BYTES_PER_PLAYER) return reply_(false, 'quota player');

      folder.createFile(Utilities.newBlob(bytes, 'application/octet-stream', name)).setDescription(sha);
      props.setProperty('bytes_total', String(total + bytes.length));
      props.setProperty('bytes_' + player, String(mine + bytes.length));
      return reply_(true, conflict ? 'conflict-kept' : 'saved');
    } finally {
      lock.releaseLock();
    }
  } catch (err) {
    return reply_(false, 'server: ' + err);
  }
}

// Para comprobar en el navegador (en una ventana privada, sin sesión iniciada) que la implementación responde.
function doGet() {
  return reply_(true, 'HKTelemetry receptor v1');
}

/**
 * Carpeta del jugador. Las carpetas se recuerdan por su id (Script Properties), no por el nombre:
 * así una carpeta en la papelera, movida o duplicada no se "traga" las subidas en silencio.
 * Devuelve null si la carpeta raíz ya no es usable (papelera): el mod lo toma como error temporal.
 */
function playerFolder_(player) {
  var props = PropertiesService.getScriptProperties();
  var root = folderById_(props.getProperty('root_id'));
  if (!root) {
    if (props.getProperty('root_id')) return null; // existía y ya no vale: que lo arregle el dueño
    root = DriveApp.createFolder(ROOT_FOLDER);
    props.setProperty('root_id', root.getId());
  }
  var key = 'folder_' + player;
  var folder = folderById_(props.getProperty(key));
  if (!folder) {
    folder = root.createFolder(player);
    props.setProperty(key, folder.getId());
  }
  return folder;
}

function folderById_(id) {
  if (!id) return null;
  try {
    var f = DriveApp.getFolderById(id);
    return f.isTrashed() ? null : f;
  } catch (err) {
    return null;
  }
}

function reply_(ok, msg) {
  var body = ok ? { ok: true, status: msg } : { ok: false, error: msg };
  return ContentService.createTextOutput(JSON.stringify(body)).setMimeType(ContentService.MimeType.JSON);
}

function pad_(n) {
  return ('00' + n).slice(-3);
}

function hex_(bytes) {
  var s = '';
  for (var i = 0; i < bytes.length; i++) {
    var b = bytes[i] & 0xff;
    s += (b < 16 ? '0' : '') + b.toString(16);
  }
  return s;
}
