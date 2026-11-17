#!/usr/bin/env python3
"""Genera un kit de instalación por participante (docs/plan.md, fase 8).

Uso:
    python tools/make_kit.py P01 P02 P03          # un zip por código
    python tools/make_kit.py P01-P10              # rango
    python tools/make_kit.py P01 --no-build       # usar la DLL Release ya compilada

Salida: dist/HKTelemetry_<código>.zip, con una carpeta HKTelemetry_<código>/ dentro:
    1_INSTALAR.bat  2_ENVIAR_DATOS.bat  3_DESINSTALAR.bat  LEEME.txt
    scripts/*.ps1
    files/manifest.json
    files/api/      Modding API v77 (zip oficial, comprobado por hash) + licencia MIT
    files/mod/      HKTelemetry.dll (Release) + HKTelemetry.GlobalSettings.json con el player_id

La Modding API se descarga una vez a dist/cache/ (o se pasa con --api-zip).
"""
from __future__ import annotations

import argparse
import hashlib
import io
import json
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
KIT_SRC = ROOT / "kit"
DIST = ROOT / "dist"
CACHE = DIST / "cache"

API_URL = "https://github.com/hk-modding/api/releases/download/1.5.78.11833-77/moddingapi.v77.windows.zip"
API_ZIP_NAME = "moddingapi.v77.windows.zip"
API_ZIP_SHA256 = "bc9f0db3d0916b05cd5a2420bb602fb1b239ce3ff6c289fd84bffb682fb8f1d6"
API_SKIP = {"README.md"}  # documentación del zip: no va a Managed

# Huellas fijas: si cambian, el kit no se genera.
# - Assembly-CSharp.dll original de la 1.5.78.11833 de Steam (reference/vanilla-managed-backup/).
VANILLA_AC_SHA256 = "fcc01e0df1b841a8faf6b5e39f27030f63204ad02f430b3defa262134ae4e8a0"
# - Assembly-CSharp.dll parcheado por la Modding API v77 (el del zip oficial; es el que usa el equipo de desarrollo).
API_AC_SHA256 = "5944411bd93830369390a4b51766ee68c4ab26195b299e25a07b5e7d0e00086d"
UNITY_PREFIX = "2020.2.2"

PLAYER_RE = re.compile(r"^[A-Za-z0-9_]{1,16}$")


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def parse_players(args: list[str]) -> list[str]:
    out: list[str] = []
    for a in args:
        if "-" in a:
            m = re.fullmatch(r"([A-Za-z]*)(\d+)-([A-Za-z]*)(\d+)", a)
            if not m or (m.group(3) and m.group(3).lower() != m.group(1).lower()):
                sys.exit(f"Rango no válido: {a!r} (ejemplo: P01-P10)")
            prefix, lo, hi = m.group(1), m.group(2), m.group(4)
            if int(hi) < int(lo):
                sys.exit(f"Rango al revés: {a!r}")
            width = len(lo)
            out += [f"{prefix}{i:0{width}d}" for i in range(int(lo), int(hi) + 1)]
        else:
            out.append(a)
    for p in out:
        if not PLAYER_RE.fullmatch(p):
            sys.exit(f"Código de jugador no válido: {p!r} (solo letras, números, _ y -, máx. 16)")
    if not out:
        sys.exit("No hay códigos de jugador")
    if len(set(out)) != len(out):
        sys.exit("Hay códigos de jugador repetidos")
    return out


def get_api_zip(path: Path | None) -> bytes:
    if path is None:
        path = CACHE / API_ZIP_NAME
        if not path.exists():
            CACHE.mkdir(parents=True, exist_ok=True)
            print(f"Descargando la Modding API v77 de {API_URL} ...")
            with urllib.request.urlopen(API_URL, timeout=120) as r:
                data = r.read()
            path.write_bytes(data)
    data = path.read_bytes()
    if sha256(data) != API_ZIP_SHA256:
        sys.exit(f"{path} no es el zip oficial de la Modding API v77 (sha256 distinto). Bórralo y vuelve a ejecutar.")
    return data


def api_files_from_zip(data: bytes) -> dict[str, bytes]:
    files: dict[str, bytes] = {}
    with zipfile.ZipFile(io.BytesIO(data)) as z:
        for info in z.infolist():
            if info.is_dir():
                continue
            name = info.filename.replace("\\", "/")
            if name in API_SKIP:
                continue
            if "/" in name:
                sys.exit(f"El zip de la API tiene subcarpetas inesperadas ({name}); revisar make_kit.py")
            files[name] = z.read(info)
    ac = files.get("Assembly-CSharp.dll")
    if ac is None or sha256(ac) != API_AC_SHA256:
        sys.exit("El Assembly-CSharp.dll del zip de la API no es el esperado (v77 para 1.5.78.11833). Abortado.")
    return files


def build_mod(skip: bool) -> bytes:
    dll = ROOT / "HKTelemetry" / "bin" / "Release" / "net472" / "HKTelemetry.dll"
    if not skip:
        print("Compilando el mod en Release ...")
        r = subprocess.run(["dotnet", "build", str(ROOT / "HKTelemetry"), "-c", "Release", "-nologo", "-v", "q"],
                           capture_output=True, text=True)
        if r.returncode != 0:
            print(r.stdout[-3000:], r.stderr[-3000:])
            sys.exit("La compilación ha fallado")
    if not dll.exists():
        sys.exit(f"No existe {dll}")
    return dll.read_bytes()


def mod_version() -> str:
    csproj = (ROOT / "HKTelemetry" / "HKTelemetry.csproj").read_text(encoding="utf-8-sig")
    m = re.search(r"<AssemblyVersion>(\d+\.\d+\.\d+)", csproj)
    return m.group(1) if m else "0.0.0"


def kit_text(path: Path, player: str) -> bytes:
    """Textos del kit: .ps1 en UTF-8 con BOM (PowerShell 5.1), .bat en ASCII, .txt en UTF-8 con BOM; todos CRLF."""
    text = path.read_text(encoding="utf-8-sig").replace("{PLAYER_ID}", player)
    text = text.replace("\r\n", "\n").replace("\n", "\r\n")
    if path.suffix == ".bat":
        return text.encode("ascii")
    return b"\xef\xbb\xbf" + text.encode("utf-8")


def make_kit(player: str, api: dict[str, bytes], dll: bytes, version: str, api_zip_sha: str) -> Path:
    top = f"HKTelemetry_{player}"
    settings = json.dumps({"PlayerId": player, "SampleHz": 60, "HideHud": False, "Debug": False}, indent=2).encode()
    api_license = (ROOT / "reference" / "modding-api" / "LICENSE")
    license_bytes = api_license.read_bytes() if api_license.exists() else (
        b"MIT License - Copyright (c) 2017 seanpr96, iamwyza, firzen\r\nhttps://github.com/hk-modding/api/blob/master/LICENSE\r\n")

    manifest = {
        "kit_version": version,
        "mod_version": version,
        "player_id": player,
        "game": {
            "unity_prefix": UNITY_PREFIX,
            "vanilla_assembly_csharp_sha256": VANILLA_AC_SHA256,
            "api_assembly_csharp_sha256": API_AC_SHA256,
        },
        "api_zip": {"url": API_URL, "sha256": api_zip_sha},
        "api_files": [{"name": n, "sha256": sha256(b)} for n, b in sorted(api.items())],
        "mod_files": [
            {"name": "HKTelemetry.dll", "sha256": sha256(dll)},
            {"name": "HKTelemetry.GlobalSettings.json", "sha256": sha256(settings)},
        ],
    }

    out = DIST / f"{top}.zip"
    DIST.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for bat in sorted(KIT_SRC.glob("*.bat")):
            z.writestr(f"{top}/{bat.name}", kit_text(bat, player))
        z.writestr(f"{top}/LEEME.txt", kit_text(KIT_SRC / "LEEME.txt", player))
        for ps1 in sorted((KIT_SRC / "scripts").glob("*.ps1")):
            z.writestr(f"{top}/scripts/{ps1.name}", kit_text(ps1, player))
        z.writestr(f"{top}/files/manifest.json", json.dumps(manifest, indent=2))
        for n, b in sorted(api.items()):
            z.writestr(f"{top}/files/api/{n}", b)
        z.writestr(f"{top}/files/api/LICENSE_ModdingAPI.txt", license_bytes)
        z.writestr(f"{top}/files/mod/HKTelemetry.dll", dll)
        z.writestr(f"{top}/files/mod/HKTelemetry.GlobalSettings.json", settings)
    return out


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("players", nargs="+", help="códigos de jugador (P01 P02 ... o rangos P01-P10)")
    ap.add_argument("--api-zip", type=Path, help=f"ruta a {API_ZIP_NAME} (por defecto se descarga a dist/cache)")
    ap.add_argument("--no-build", action="store_true", help="no recompilar; usar bin/Release/net472/HKTelemetry.dll")
    args = ap.parse_args()

    players = parse_players(args.players)
    api_zip = get_api_zip(args.api_zip)
    api = api_files_from_zip(api_zip)
    dll = build_mod(args.no_build)
    version = mod_version()
    print(f"Mod {version} | API v77: {len(api)} ficheros | zip API sha256 {sha256(api_zip)[:16]}...")
    for p in players:
        out = make_kit(p, api, dll, version, sha256(api_zip))
        print(f"  {out.relative_to(ROOT)}  ({out.stat().st_size / 1e6:.1f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
