#!/bin/bash
# HKTelemetry - instalar / enviar datos / desinstalar en Steam Deck (Linux) y Mac.
# Equivale a 1_INSTALAR.bat, 2_ENVIAR_DATOS.bat y 3_DESINSTALAR.bat de Windows.
#
#   bash hktelemetry.sh instalar
#   bash hktelemetry.sh enviar
#   bash hktelemetry.sh desinstalar
# Opciones: --game-dir <carpeta de Hollow Knight>   --yes (responde «sí» a todo; para pruebas)
#
# Compatible con el bash 3.2 de macOS: sin arrays asociativos ni ${var,,}.

set -u
LC_ALL=C
export LC_ALL

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
KIT_ROOT="$(dirname "$SCRIPT_DIR")"
FILES="$KIT_ROOT/files"
APP_ID=367520

ACTION=""
YES=0
GAME_DIR_OVERRIDE=""
while [ $# -gt 0 ]; do
    case "$1" in
        instalar|install) ACTION=instalar ;;
        enviar|export) ACTION=enviar ;;
        desinstalar|uninstall) ACTION=desinstalar ;;
        --yes|-y) YES=1 ;;
        --game-dir) shift; GAME_DIR_OVERRIDE="${1:-}" ;;
        *) echo "Opción desconocida: $1"; exit 2 ;;
    esac
    shift
done

# ------------------------------------------------------------------ salida

if [ -t 1 ]; then
    C_T=$'\033[36m'; C_OK=$'\033[32m'; C_W=$'\033[33m'; C_B=$'\033[31m'; C_0=$'\033[0m'
else
    C_T=""; C_OK=""; C_W=""; C_B=""; C_0=""
fi
LINE="================================================================"
title() { echo; echo "${C_T}${LINE}"; echo "  $1"; echo "${LINE}${C_0}"; echo; }
info()  { echo "  $1"; }
ok()    { echo "  ${C_OK}[OK]${C_0} $1"; }
warn()  { echo "  ${C_W}[!]${C_0} $1"; }
bad()   { echo "  ${C_B}[X]${C_0} $1"; }
die()   { echo; bad "$1"; echo; exit 1; }

# Lee de la terminal aunque el script venga por una tubería (curl ... | bash).
# Sin terminal donde preguntar (EOF), devuelve error: quien llama lo trata como «no» / cancelar.
ask_line() {
    local answer="" rc
    if { : </dev/tty; } 2>/dev/null; then read -r -p "  $1" answer </dev/tty; rc=$?
    else read -r -p "  $1" answer; rc=$?; fi
    printf '%s' "$answer"
    return $rc
}
yes_no() {
    if [ "$YES" = 1 ]; then info "$1 -> S (automático)"; return 0; fi
    local a
    while true; do
        a="$(ask_line "$1 (S/N) ")" || return 1
        case "$a" in [sSyY]*) return 0 ;; [nN]*) return 1 ;; esac
    done
}

# ------------------------------------------------------------------ utilidades

OS="${HKT_TEST_OS:-$(uname -s)}"  # HKT_TEST_OS: solo para probar el script fuera de Linux/Mac
case "$OS" in
    Linux|Darwin) ;;
    *) die "Este script es para Steam Deck / Linux o Mac. En Windows usa 1_INSTALAR.bat." ;;
esac

sha() {
    [ -f "$1" ] || { echo ""; return; }
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d' ' -f1
    else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# Hash esperado de un fichero en un SHA256SUMS ("<hash>  <nombre>").
sum_of() { awk -v n="$2" '{ h=$1; $1=""; sub(/^ +/, ""); if ($0 == n) { print h; exit } }' "$1"; }
names_in() { awk '{ $1=""; sub(/^ +/, ""); print }' "$1"; }

[ -f "$FILES/unix.conf" ] || die "No encuentro la carpeta 'files' del kit. ¿Has descomprimido el zip entero? Ejecuta el script desde la carpeta extraída."
# shellcheck disable=SC1091
. "$FILES/unix.conf"

game_running() {
    pgrep -f 'hollow_knight\.x86_64|hollow_knight\.exe|hollow_knight\.app/Contents/MacOS' >/dev/null 2>&1
}
wait_game_closed() {
    while game_running; do
        [ "$YES" = 1 ] && die "Hollow Knight está abierto."
        warn "Hollow Knight está abierto. Ciérralo del todo y pulsa Enter para seguir."
        ask_line "" >/dev/null || die "Hollow Knight está abierto."
    done
}

# ------------------------------------------------------------------ búsqueda del juego

steam_roots() {
    if [ "$OS" = Darwin ]; then
        echo "$HOME/Library/Application Support/Steam"
    else
        echo "$HOME/.local/share/Steam"
        echo "$HOME/.steam/steam"
        echo "$HOME/.steam/root"
        echo "$HOME/.var/app/com.valvesoftware.Steam/.local/share/Steam"
    fi
}

steam_libraries() {
    local root
    steam_roots | while IFS= read -r root; do
        [ -d "$root" ] || continue
        echo "$root"
        if [ -f "$root/steamapps/libraryfolders.vdf" ]; then
            sed -n 's/^[[:space:]]*"path"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$root/steamapps/libraryfolders.vdf"
        fi
    done
}

data_dir_of() {  # carpeta con Managed/ y globalgamemanagers
    if [ "$OS" = Darwin ]; then echo "$1/hollow_knight.app/Contents/Resources/Data"
    else echo "$1/hollow_knight_Data"; fi
}
is_game_dir() { [ -n "$1" ] && [ -f "$(data_dir_of "$1")/Managed/Assembly-CSharp.dll" ]; }

find_game() {
    local lib installdir acf dir
    while IFS= read -r lib; do
        [ -d "$lib" ] || continue
        installdir="Hollow Knight"
        acf="$lib/steamapps/appmanifest_$APP_ID.acf"
        if [ -f "$acf" ]; then
            dir="$(sed -n 's/^[[:space:]]*"installdir"[[:space:]]*"\(.*\)"[[:space:]]*$/\1/p' "$acf" | head -n 1)"
            [ -n "$dir" ] && installdir="$dir"
        fi
        if is_game_dir "$lib/steamapps/common/$installdir"; then
            echo "$lib/steamapps/common/$installdir"
            return 0
        fi
    done <<EOF
$(steam_libraries)
EOF
    return 1
}

resolve_game() {
    if [ -n "$GAME_DIR_OVERRIDE" ]; then
        is_game_dir "$GAME_DIR_OVERRIDE" || die "En $GAME_DIR_OVERRIDE no está Hollow Knight."
        GAME="$GAME_DIR_OVERRIDE"
        ok "Hollow Knight (ruta indicada): $GAME"
        return
    fi
    if GAME="$(find_game)"; then
        ok "Hollow Knight encontrado en: $GAME"
        return
    fi
    warn "No he encontrado Hollow Knight en las bibliotecas de Steam."
    info "En Steam: clic derecho en Hollow Knight > Administrar > Ver archivos locales."
    info "Arrastra esa carpeta a esta ventana (o escribe la ruta) y pulsa Enter."
    while true; do
        GAME="$(ask_line "Carpeta de Hollow Knight (vacío para salir): ")" || die "Cancelado."
        GAME="${GAME%/}"; GAME="${GAME#\'}"; GAME="${GAME%\'}"; GAME="${GAME#\"}"; GAME="${GAME%\"}"
        [ -z "$GAME" ] && die "Cancelado."
        case "$GAME" in */hollow_knight.app) GAME="$(dirname "$GAME")" ;; esac
        is_game_dir "$GAME" && return
        bad "En esa carpeta no está el juego. Prueba otra vez."
    done
}

unity_version_ok() {
    head -c 200 "$(data_dir_of "$GAME")/globalgamemanagers" 2>/dev/null | tr -c '[:print:]' ' ' | grep -q "$UNITY_PREFIX"
}

show_wrong_version_help() {
    info ""
    info "El mod solo funciona con la versión 1.5.78 de Hollow Knight. Para cambiarla en Steam:"
    info "  1. Biblioteca > clic derecho en Hollow Knight > Propiedades."
    info "  2. 'Versiones de juego y betas' > elige '1.5.78.11833'."
    info "  3. Espera a que termine de descargarse y vuelve a ejecutar este instalador."
    info "Tus partidas guardadas no se tocan."
}

# Carpeta de partidas y datos del mod (la del juego nativo, o la de Proton si el juego va con Proton).
user_data_root() {
    if [ "$OS" = Darwin ]; then
        echo "$HOME/Library/Application Support/unity.Team Cherry.Hollow Knight"
    elif [ "${PLATFORM:-linux}" = windows ]; then
        echo "$(cd "$GAME/../../.." && pwd)/steamapps/compatdata/$APP_ID/pfx/drive_c/users/steamuser/AppData/LocalLow/Team Cherry/Hollow Knight"
    else
        echo "${XDG_CONFIG_HOME:-$HOME/.config}/unity3d/Team Cherry/Hollow Knight"
    fi
}

# ¿Qué Modding API toca y en qué estado está el juego? Fija PLATFORM y AC_STATE (vanilla|api|other).
detect_platform() {
    local ac exe=0 elf=0
    ac="$(sha "$MANAGED/Assembly-CSharp.dll")"
    [ -f "$GAME/hollow_knight.exe" ] && exe=1
    [ -f "$GAME/hollow_knight.x86_64" ] && elf=1
    case "$ac" in
        "$WINDOWS_API_AC")     PLATFORM=windows; AC_STATE=api; return ;;
        "$WINDOWS_VANILLA_AC") PLATFORM=windows; AC_STATE=vanilla; return ;;
        "$LINUX_API_AC")       PLATFORM=linux;   AC_STATE=api; return ;;
        "$MACOS_API_AC")       PLATFORM=macos;   AC_STATE=api; return ;;
    esac
    if [ "$OS" = Darwin ]; then PLATFORM=macos
    elif [ "$elf" = 1 ]; then PLATFORM=linux
    elif [ "$exe" = 1 ]; then PLATFORM=windows
    else PLATFORM=linux; fi
    # Sin huella conocida: original si no lleva ninguna Modding API dentro (Linux/Mac: no hay huella del original).
    if [ "$PLATFORM" = windows ] || grep -q -a "ModHooks" "$MANAGED/Assembly-CSharp.dll"; then
        AC_STATE=other
    else
        AC_STATE=vanilla
    fi
}

api_dir() { if [ "$1" = windows ]; then echo "$FILES/api"; else echo "$FILES/api_$1"; fi; }

check_kit() {  # $1 = carpeta con SHA256SUMS
    local d="$1" n bad=""
    [ -f "$d/SHA256SUMS" ] || die "El kit está incompleto (falta $d/SHA256SUMS). Vuelve a descargarlo."
    while IFS= read -r n; do
        [ -n "$n" ] || continue
        [ "$(sha "$d/$n")" = "$(sum_of "$d/SHA256SUMS" "$n")" ] || bad="$bad $n"
    done <<EOF
$(names_in "$d/SHA256SUMS")
EOF
    [ -z "$bad" ] || die "El kit está incompleto o dañado:$bad. No se ha tocado nada. Vuelve a descargarlo."
}

api_mismatches() {  # ficheros de la API que faltan o no coinciden en Managed
    local d n out=""
    d="$(api_dir "$PLATFORM")"
    while IFS= read -r n; do
        [ -n "$n" ] || continue
        [ "$(sha "$MANAGED/$n")" = "$(sum_of "$d/SHA256SUMS" "$n")" ] || out="$out $n"
    done <<EOF
$(names_in "$d/SHA256SUMS")
EOF
    echo "$out"
}

state_get() { [ -f "$STATE_FILE" ] && sed -n "s/^$1=//p" "$STATE_FILE" | head -n 1; }
state_list() { [ -f "$STATE_FILE" ] && sed -n "s/^$1 //p" "$STATE_FILE"; }

other_mods() {
    local m out=""
    [ -d "$MANAGED/Mods" ] || return 0
    for m in "$MANAGED/Mods"/*; do
        [ -e "$m" ] || continue
        case "$(basename "$m")" in HKTelemetry|Disabled) ;; *) out="$out $(basename "$m")" ;; esac
    done
    echo "$out"
}

setup_paths() {
    DATA="$(data_dir_of "$GAME")"
    MANAGED="$DATA/Managed"
    STATE_DIR="$DATA/HKTelemetry_kit"
    STATE_FILE="$STATE_DIR/state_unix.txt"
}

# ------------------------------------------------------------------ instalar

do_install() {
    title "HKTelemetry $MOD_VERSION - instalación (Steam Deck / Mac)"
    info "Este mod SOLO registra cómo juegas a Hollow Knight, para un trabajo de máster de IA."
    info "No cambia nada del juego ni de tus partidas."
    info ""
    info "Qué se guarda (en tu equipo; al terminar cada partida se envía solo a la carpeta del proyecto):"
    info "  - Lo que pasa en la partida: salas, combates, vida, alma, objetos, mapa..."
    info "  - Posición del personaje y botones pulsados (60 veces por segundo)."
    info "  - Horas jugadas a Hollow Knight en Steam y logros conseguidos (para saber tu experiencia)."
    info "  - Datos técnicos: fps, resolución, versión del juego y otros mods instalados."
    info "  - Progreso de tus partidas guardadas (horas, porcentaje completado)."
    info "  - Tu nombre de Steam y el número de tu cuenta de Steam, para saber de quién es cada partida"
    info "    (no tu usuario ni tu contraseña). Alex los cambia por un código anónimo antes de analizarlos."
    info "Nada más de fuera de Hollow Knight."
    info ""
    info "Envío: al volver al menú principal, cada partida terminada se envía por internet a una"
    info "carpeta de Google Drive del proyecto (solo la ve Alex). Se puede desactivar en"
    info "Opciones > Mods > HKTelemetry y enviar los datos con: bash hktelemetry.sh enviar"
    info ""
    yes_no "¿Estás de acuerdo y quieres instalarlo?" || die "Instalación cancelada. No se ha tocado nada."

    title "Comprobando el juego"
    resolve_game
    setup_paths
    if ! unity_version_ok; then
        bad "Tu Hollow Knight no es la versión 1.5.78."
        show_wrong_version_help
        die "Instalación cancelada. No se ha tocado nada."
    fi
    detect_platform
    case "$PLATFORM" in
        windows) ok "Versión del juego correcta (1.5.78, versión de Windows con Proton)." ;;
        linux)   ok "Versión del juego correcta (1.5.78 para Linux / Steam Deck)." ;;
        macos)   ok "Versión del juego correcta (1.5.78 para Mac)." ;;
    esac
    API_DIR="$(api_dir "$PLATFORM")"
    check_kit "$API_DIR"
    check_kit "$FILES/mod"
    wait_game_closed

    local saves_root saves_bak f n
    saves_root="$(user_data_root)"
    if ls "$saves_root"/user*.dat >/dev/null 2>&1; then
        saves_bak="$saves_root/HKTelemetry_saves_backup/$(date +%Y%m%d_%H%M%S)"
        mkdir -p "$saves_bak" && cp -p "$saves_root"/user*.dat* "$saves_bak"/ \
            && ok "Copia de tus partidas en: $saves_bak" \
            || warn "No he podido copiar tus partidas (no es necesario para el mod)."
    fi

    local probe="$MANAGED/.hktelemetry_probe_$$"
    ( : > "$probe" ) 2>/dev/null || die "No tengo permiso para escribir en la carpeta del juego ($MANAGED)."
    rm -f "$probe"

    title "Modding API"
    local prev_by_kit api_by_kit=0
    prev_by_kit="$(state_get api_installed_by_kit)"
    if [ "$AC_STATE" = api ] && [ "$prev_by_kit" != 1 ]; then
        local mm; mm="$(api_mismatches)"
        if [ -n "$mm" ]; then
            warn "La Modding API instalada no coincide con la v77 oficial en:$mm"
            info "Si el juego no arranca o el mod no aparece: Steam > Propiedades > Archivos instalados >"
            info "'Verificar integridad de los archivos' y vuelve a ejecutar el instalador."
        else
            ok "La Modding API v77 ya estaba instalada (por ejemplo con Lumafly). No la toco."
        fi
    elif [ "$AC_STATE" = vanilla ] || { [ "$AC_STATE" = api ] && [ "$prev_by_kit" = 1 ]; }; then
        mkdir -p "$STATE_DIR/backup_unix" || die "No puedo crear $STATE_DIR."
        local added replaced copied=0
        added="$(state_list added)"
        replaced="$(state_list replaced)"
        while IFS= read -r n; do
            [ -n "$n" ] || continue
            if printf '%s\n%s\n' "$added" "$replaced" | grep -qxF "$n"; then continue; fi
            if [ -f "$MANAGED/$n" ]; then
                [ -f "$STATE_DIR/backup_unix/$n" ] || cp -p "$MANAGED/$n" "$STATE_DIR/backup_unix/$n" || die "No he podido hacer copia de $n. No se ha tocado nada."
                replaced="$replaced
$n"
            else
                added="$added
$n"
            fi
        done <<EOF
$(names_in "$API_DIR/SHA256SUMS")
EOF
        # El estado se escribe ANTES de copiar: si algo falla a medias, el desinstalador sabe qué restaurar.
        {
            echo "kit_version=$MOD_VERSION"
            echo "api_installed_by_kit=1"
            echo "platform=$PLATFORM"
            echo "installed_utc=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
            printf '%s\n' "$added" | sed '/^$/d; s/^/added /'
            printf '%s\n' "$replaced" | sed '/^$/d; s/^/replaced /'
        } > "$STATE_FILE.tmp" && mv "$STATE_FILE.tmp" "$STATE_FILE" || die "No he podido guardar el estado de la instalación."
        while IFS= read -r n; do
            [ -n "$n" ] || continue
            if [ "$(sha "$MANAGED/$n")" != "$(sum_of "$API_DIR/SHA256SUMS" "$n")" ]; then
                cp "$API_DIR/$n" "$MANAGED/$n" || die "No he podido copiar $n. Ejecuta 'bash hktelemetry.sh desinstalar' y avisa a Alex."
                copied=$((copied + 1))
            fi
        done <<EOF
$(names_in "$API_DIR/SHA256SUMS")
EOF
        local mm; mm="$(api_mismatches)"
        [ -z "$mm" ] || die "No se han podido copiar bien:$mm. Vuelve a ejecutar el instalador o desinstala y avisa a Alex."
        api_by_kit=1
        if [ "$AC_STATE" = vanilla ] && [ "$prev_by_kit" != 1 ]; then
            ok "Modding API v77 instalada (copia de seguridad de los ficheros originales guardada)."
        elif [ "$copied" -gt 0 ]; then
            ok "Modding API v77 reparada ($copied ficheros vueltos a copiar)."
        else
            ok "La Modding API ya estaba instalada por este kit y está completa."
        fi
    else
        bad "Los ficheros del juego no son ni los originales de la 1.5.78 ni la Modding API v77."
        info "Puede que tengas otra versión de la Modding API o un juego modificado."
        info "Solución: en Steam, clic derecho en Hollow Knight > Propiedades > Archivos instalados >"
        info "'Verificar integridad de los archivos', y vuelve a ejecutar el instalador."
        die "Instalación cancelada. No se ha tocado nada."
    fi

    title "Mod HKTelemetry"
    local mod_dir="$MANAGED/Mods/HKTelemetry" settings old_id=""
    settings="$mod_dir/HKTelemetry.GlobalSettings.json"
    mkdir -p "$mod_dir" || die "No puedo crear $mod_dir."
    # Sin Steam, el jugador se identifica con un id aleatorio guardado en los ajustes: conservarlo.
    [ -f "$settings" ] && old_id="$(sed -n 's/.*"InstallId"[[:space:]]*:[[:space:]]*"\([0-9a-f]\{8\}\)".*/\1/p' "$settings" | head -n 1)"
    while IFS= read -r n; do
        [ -n "$n" ] || continue
        cp "$FILES/mod/$n" "$mod_dir/$n" || die "No he podido copiar $n."
        [ "$(sha "$mod_dir/$n")" = "$(sum_of "$FILES/mod/SHA256SUMS" "$n")" ] || die "La copia de $n no ha salido bien."
    done <<EOF
$(names_in "$FILES/mod/SHA256SUMS")
EOF
    if [ -n "$old_id" ]; then
        sed "1s/^{/{\\
  \"InstallId\": \"$old_id\",/" "$settings" > "$settings.tmp" && mv "$settings.tmp" "$settings"
    fi
    rm -f "$mod_dir/HKTelemetry.pdb"
    if [ "$OS" = Darwin ]; then
        # Lo descargado con el navegador lleva la marca de cuarentena: macOS no cargaría la librería de la API.
        xattr -dr com.apple.quarantine "$MANAGED" 2>/dev/null || true
    fi
    if [ "$api_by_kit" != 1 ] && [ -f "$STATE_FILE" ]; then
        sed -i.bak 's/^api_installed_by_kit=.*/api_installed_by_kit=0/' "$STATE_FILE" && rm -f "$STATE_FILE.bak"
    fi
    ok "Mod instalado."

    local others; others="$(other_mods)"
    if [ -n "$others" ]; then
        warn "Tienes otros mods instalados:$others"
        info "Si alguno cambia el juego (dificultad, ayudas, mapa...), desactívalo mientras grabas,"
        info "moviendo su carpeta a Mods/Disabled. El mod deja constancia de qué mods había."
    fi

    title "Listo"
    info "Abre Hollow Knight desde Steam y juega normal (también en el modo Juego de la Steam Deck)."
    info "En el menú principal, abajo, verás:"
    info "  HKTelemetry v$MOD_VERSION - grabando como <tu nombre de Steam>"
    info "Mientras juegas, un punto verde arriba a la derecha indica que está grabando"
    info "(gris en pausa o menús; ROJO si algo falla: en ese caso avisa a Alex)."
    info ""
    info "Los datos se envían solos al volver al menú principal (necesita internet)."
}

# ------------------------------------------------------------------ enviar datos (plan B)

do_export() {
    title "HKTelemetry - preparar los datos para enviar"
    resolve_game
    setup_paths
    detect_platform
    wait_game_closed
    local root out_dir stamp out
    root="$(user_data_root)/HKTelemetry"
    [ -d "$root" ] || die "No hay datos grabados todavía ($root)."
    out_dir="$HOME/Desktop"; [ -d "$out_dir" ] || out_dir="$HOME"
    stamp="$(date +%Y%m%d_%H%M%S)"
    if command -v zip >/dev/null 2>&1; then
        out="$out_dir/HKTelemetry_datos_$stamp.zip"
        ( cd "$root/.." && zip -rq "$out" HKTelemetry -x 'HKTelemetry/_upload_tmp/*' ) || die "No he podido crear $out."
    else
        out="$out_dir/HKTelemetry_datos_$stamp.tar.gz"
        ( cd "$root/.." && tar -czf "$out" --exclude 'HKTelemetry/_upload_tmp' HKTelemetry ) || die "No he podido crear $out."
    fi
    ok "Datos guardados en: $out"
    info "Envíaselo a Alex por WhatsApp, Drive, correo... como te venga mejor."
}

# ------------------------------------------------------------------ desinstalar

do_uninstall() {
    title "HKTelemetry - desinstalación"
    resolve_game
    setup_paths
    detect_platform
    wait_game_closed
    local root; root="$(user_data_root)/HKTelemetry"
    if [ -d "$root" ] && [ -n "$(ls -A "$root" 2>/dev/null)" ]; then
        warn "Tienes datos grabados. Si aún no se han enviado, cancela y ejecuta antes: bash hktelemetry.sh enviar"
        yes_no "¿Seguir con la desinstalación?" || die "Cancelado. No se ha tocado nada."
    fi

    if [ -d "$MANAGED/Mods/HKTelemetry" ]; then
        rm -rf "$MANAGED/Mods/HKTelemetry" && ok "Mod HKTelemetry eliminado."
    else
        info "El mod no estaba instalado."
    fi

    if [ "$(state_get api_installed_by_kit)" != 1 ]; then
        info "La Modding API no la instaló este kit: no la toco."
        rm -f "$STATE_FILE"
        return
    fi
    local others; others="$(other_mods)"
    if [ -n "$others" ]; then
        warn "Hay otros mods que necesitan la Modding API:$others"
        yes_no "¿Quitar también la Modding API? (esos mods dejarán de funcionar)" || { info "Dejo la Modding API."; return; }
    fi
    PLATFORM="$(state_get platform)"
    local api n bak cur want problems=0
    api="$(api_dir "$PLATFORM")"
    if ! unity_version_ok; then
        warn "Steam ha cambiado el juego de versión desde la instalación: no restauro los ficheros antiguos."
    else
        while IFS= read -r n; do
            [ -n "$n" ] || continue
            bak="$STATE_DIR/backup_unix/$n"; cur="$(sha "$MANAGED/$n")"; want="$(sum_of "$api/SHA256SUMS" "$n")"
            if [ ! -f "$bak" ]; then warn "Falta la copia de seguridad de $n"; problems=$((problems + 1))
            elif [ "$cur" = "$(sha "$bak")" ]; then :
            elif [ "$cur" = "$want" ]; then
                cp "$bak" "$MANAGED/$n" || { warn "No he podido restaurar $n"; problems=$((problems + 1)); }
            else warn "$n ha cambiado desde la instalación (¿Steam?): lo dejo como está."; problems=$((problems + 1)); fi
        done <<EOF
$(state_list replaced)
EOF
    fi
    while IFS= read -r n; do
        [ -n "$n" ] || continue
        # Solo se borra si sigue siendo el fichero que puso el kit.
        [ "$(sha "$MANAGED/$n")" = "$(sum_of "$api/SHA256SUMS" "$n")" ] && rm -f "$MANAGED/$n"
    done <<EOF
$(state_list added)
EOF
    if [ "$problems" -eq 0 ]; then
        rm -rf "$STATE_DIR/backup_unix" "$STATE_FILE"
        rmdir "$STATE_DIR" 2>/dev/null || true
        ok "Modding API quitada: el juego vuelve a estar como antes."
    else
        warn "No he podido dejarlo todo como antes; la copia de seguridad sigue en $STATE_DIR."
        info "Solución segura: Steam > Propiedades > Archivos instalados > 'Verificar integridad de los archivos'."
    fi
    info ""
    info "Para volver a la última versión del juego: Steam > Propiedades > 'Versiones de juego y betas' > 'Ninguna'."
}

case "$ACTION" in
    instalar) do_install ;;
    enviar) do_export ;;
    desinstalar) do_uninstall ;;
    *) echo "Uso: bash $(basename "$0") instalar | enviar | desinstalar"; exit 2 ;;
esac
echo
