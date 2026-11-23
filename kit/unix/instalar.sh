#!/bin/bash
# HKTelemetry - instalación en una línea para Steam Deck (modo Escritorio) y Mac:
#
#   curl -fsSL https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/instalar.sh | bash
#
# Descarga el kit de la última release a ~/HKTelemetry y ejecuta su instalador.
# No necesita navegador ni permisos de administrador.
set -eu
URL="https://github.com/Alexdemaes1/HKTelemetry/releases/latest/download/HKTelemetry.zip"
DEST="$HOME/HKTelemetry"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/hktelemetry.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

echo "  Descargando el kit de HKTelemetry..."
curl -fL --progress-bar -o "$TMP/kit.zip" "$URL" || { echo "  [X] No se ha podido descargar $URL"; exit 1; }

mkdir -p "$TMP/x"
if command -v unzip >/dev/null 2>&1; then unzip -q "$TMP/kit.zip" -d "$TMP/x"
elif command -v bsdtar >/dev/null 2>&1; then bsdtar -xf "$TMP/kit.zip" -C "$TMP/x"
elif command -v ditto >/dev/null 2>&1; then ditto -x -k "$TMP/kit.zip" "$TMP/x"
elif command -v python3 >/dev/null 2>&1; then python3 -m zipfile -e "$TMP/kit.zip" "$TMP/x"
else echo "  [X] No encuentro cómo descomprimir (unzip, bsdtar o python3)."; exit 1
fi
[ -f "$TMP/x/HKTelemetry/SteamDeck_Mac/hktelemetry.sh" ] || { echo "  [X] El kit descargado no tiene el instalador para Steam Deck / Mac."; exit 1; }

rm -rf "$DEST"
mv "$TMP/x/HKTelemetry" "$DEST"
echo "  [OK] Kit descargado en $DEST"
exec bash "$DEST/SteamDeck_Mac/hktelemetry.sh" instalar
