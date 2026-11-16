#!/usr/bin/env python3
"""Valida una o varias carpetas de sesión de HKTelemetry (docs/schema.md).

Uso:
    python tools/validate.py <carpeta_sesion> [<carpeta_sesion> ...]
    python tools/validate.py --all            # todas las sesiones en la carpeta de datos del juego

Sale con código 1 si alguna sesión tiene errores. Los avisos no hacen fallar.
Solo usa la biblioteca estándar.
"""
from __future__ import annotations

import argparse
import gzip
import json
import os
import sys
from collections import Counter
from pathlib import Path

SCHEMA_VERSION = 1
DEFAULT_ROOT = Path(os.environ.get("USERPROFILE", "~")).expanduser() / "AppData/LocalLow/Team Cherry/Hollow Knight/HKTelemetry"

COMMON_FIELDS = ("seq", "t_game", "t_real", "frame", "scene", "type", "p")
EXIT_REASONS = {
    "death", "death_dream", "death_godhome", "save_load", "new_game", "new_game_godseeker", "stag", "dream_gate",
    "godhome", "dream", "gate", "door", "other", "quit_menu", "ending", "permadeath", "cutscene",
}
END_REASONS = {"quit_menu", "ending", "menu", "emergency_menu", "permadeath", "app_quit", "restarted", "recovered"}
NON_GAMEPLAY_PREFIXES = ("Cinematic_", "End_")


class Report:
    def __init__(self, name: str):
        self.name = name
        self.errors: list[str] = []
        self.warnings: list[str] = []
        self.info: list[str] = []

    def err(self, msg: str) -> None:
        self.errors.append(msg)

    def warn(self, msg: str) -> None:
        self.warnings.append(msg)

    def print(self) -> None:
        status = "ERROR" if self.errors else ("AVISOS" if self.warnings else "OK")
        print(f"\n=== {self.name}: {status}")
        # Solo ASCII en los marcadores: la consola de Windows (cp1252) no tiene ✗.
        for m in self.info:
            print(f"  - {m}")
        for m in self.errors:
            print(f"  [ERROR] {m}")
        for m in self.warnings:
            print(f"  [aviso] {m}")


def open_text(path: Path):
    return gzip.open(path, "rt", encoding="utf-8") if path.suffix == ".gz" else open(path, encoding="utf-8")


def find(folder: Path, name: str) -> Path | None:
    for p in (folder / (name + ".gz"), folder / name):
        if p.exists():
            return p
    return None


def check_session_json(folder: Path, r: Report) -> dict | None:
    path = folder / "session.json"
    if not path.exists():
        r.err("falta session.json")
        return None
    try:
        s = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as e:
        r.err(f"session.json no es JSON válido: {e}")
        return None

    if s.get("schema_version") != SCHEMA_VERSION:
        r.err(f"schema_version={s.get('schema_version')} (esperado {SCHEMA_VERSION})")
    for k in ("session_id", "launch_id", "player_id", "start_utc", "game", "save"):
        if not s.get(k):
            r.err(f"session.json sin '{k}'")
    end = s.get("end")
    if not end:
        r.warn("sesión sin 'end' (¿sigue abierta o el juego se cerró de golpe?)")
    elif end.get("reason") not in END_REASONS:
        r.warn(f"end.reason desconocido: {end.get('reason')}")
    if s.get("progress_start") is None:
        r.warn("sin progress_start (no llegó a haber control_ready)")
    if s.get("progress_end") is None and end:
        r.warn("sin progress_end")

    game = s.get("game") or {}
    if game.get("unity_version") != "2020.2.2f1":
        r.err(f"unity_version={game.get('unity_version')} (no es la 1.5.78)")

    c = s.get("counters") or {}
    for k in ("dropped_events", "write_errors", "hook_errors"):
        if c.get(k):
            r.err(f"counters.{k} = {c[k]}")

    exp = s.get("experience") or {}
    r.info.append(
        f"player={s.get('player_id')} kind={s.get('save', {}).get('start_kind')} slot={s.get('save', {}).get('slot')} "
        f"steam_min={exp.get('steam_playtime_min')} logros={len(exp.get('achievements_unlocked') or [])} "
        f"saves={len(exp.get('saves') or [])} end={(end or {}).get('reason')}"
    )
    return s


def check_events(folder: Path, s: dict | None, r: Report) -> None:
    path = find(folder, "events.jsonl")
    if path is None:
        r.err("falta events.jsonl(.gz)")
        return
    if path.suffix != ".gz" and s and s.get("end"):
        r.warn("events.jsonl sin comprimir en una sesión cerrada")

    events: list[dict] = []
    with open_text(path) as f:
        lines = [ln.rstrip("\n") for ln in f]
    while lines and not lines[-1]:
        lines.pop()
    for n, line in enumerate(lines, 1):
        if not line:
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError as e:
            # Solo la última línea puede quedar cortada (cierre brusco): aviso. Cualquier otra: error.
            (r.warn if n == len(lines) else r.err)(f"línea {n} no es JSON válido: {e}")

    if not events:
        r.err("events.jsonl vacío")
        return

    types = Counter(e.get("type") for e in events)
    r.info.append(f"{len(events)} eventos: " + ", ".join(f"{k}={v}" for k, v in types.most_common()))

    # Campos comunes, seq sin huecos, relojes monótonos.
    prev = None
    for e in events:
        missing = [k for k in COMMON_FIELDS if k not in e]
        if missing:
            r.err(f"seq={e.get('seq')} sin campos {missing}")
            continue
        if prev is not None:
            if e["seq"] != prev["seq"] + 1:
                r.err(f"hueco en seq: {prev['seq']} → {e['seq']}")
            if e["t_real"] + 1e-6 < prev["t_real"]:
                r.err(f"t_real retrocede en seq={e['seq']}")
            if e["t_game"] + 1e-6 < prev["t_game"]:
                r.err(f"t_game retrocede en seq={e['seq']}")
            if e["frame"] < prev["frame"]:
                r.err(f"frame retrocede en seq={e['seq']}")
            if e["t_game"] > e["t_real"] + 1.0:
                r.warn(f"t_game > t_real en seq={e['seq']}")
        prev = e

    if events[0].get("seq") != 0:
        r.err(f"el primer seq es {events[0].get('seq')}, no 0")
    if events[0]["type"] != "session_start":
        r.err(f"el primer evento es {events[0]['type']}, no session_start")
    if s and s.get("end") and events[-1]["type"] != "session_end":
        r.err(f"sesión cerrada pero el último evento es {events[-1]['type']}")
    if s and (s.get("counters") or {}).get("events") not in (None, len(events)):
        r.warn(f"counters.events={s['counters']['events']} pero hay {len(events)} líneas")

    check_scene_sequence(events, r)
    summarize_perf(events, r)


def summarize_perf(events: list[dict], r: Report) -> None:
    perf = [e["p"] for e in events if e["type"] == "perf"]
    if not perf:
        return
    fps = sorted(p["fps"] for p in perf)
    mod_avg = sum(p["mod_us_avg"] for p in perf) / len(perf)
    mod_max = max(p["mod_us_max"] for p in perf)
    dt_max = max(p["dt_max_ms"] for p in perf)
    r.info.append(
        f"perf: fps mediana={fps[len(fps)//2]:.0f} min={fps[0]:.0f} | frame mas largo={dt_max:.0f} ms | "
        f"coste del mod: media={mod_avg:.1f} us/frame, max={mod_max:.0f} us"
    )
    if mod_avg > 200:
        r.warn(f"el mod gasta {mod_avg:.0f} us/frame de media (objetivo < 200 us)")


def is_non_gameplay(scene: str | None) -> bool:
    if not scene:
        return True
    return scene in {"Menu_Title", "Quit_To_Menu", "Knight_Pickup", "Opening_Sequence", "PermaDeath"} or scene.startswith(
        NON_GAMEPLAY_PREFIXES
    )


def check_scene_sequence(events: list[dict], r: Report) -> None:
    """scene_exit → scene_enter emparejados, motivos válidos, control_ready tras cada entrada jugable."""
    pending_exit: dict | None = None
    awaiting_control: dict | None = None
    current: str | None = events[0].get("scene")
    reasons = Counter()

    for e in events:
        t, p = e["type"], e.get("p") or {}
        if t == "scene_exit":
            reason = p.get("reason")
            reasons[reason] += 1
            if reason not in EXIT_REASONS:
                r.err(f"seq={e['seq']} motivo de salida desconocido: {reason}")
            if reason == "other":
                r.warn(f"seq={e['seq']} salida sin clasificar {p.get('from')}→{p.get('to')} "
                       f"(entry_gate={p.get('entry_gate')}, vis={p.get('vis')})")
            if pending_exit is not None and pending_exit["p"].get("reason") not in ("quit_menu", "ending"):
                r.warn(f"seq={e['seq']} scene_exit sin scene_enter del anterior (seq={pending_exit['seq']})")
            if p.get("from") != current:
                r.warn(f"seq={e['seq']} scene_exit.from={p.get('from')} pero la escena actual es {current}")
            if awaiting_control is not None and p.get("reason") not in ("quit_menu", "ending"):
                r.warn(f"scene_enter seq={awaiting_control['seq']} ({awaiting_control['p'].get('scene')}) sin control_ready")
            pending_exit = e
            awaiting_control = None
        elif t == "scene_enter":
            scene = p.get("scene")
            if pending_exit is None and not is_non_gameplay(current):
                r.warn(f"seq={e['seq']} scene_enter a {scene} sin scene_exit previo (desde {current})")
            if pending_exit is not None:
                to = pending_exit["p"].get("to")
                if to and scene != to and not is_non_gameplay(to) and not is_non_gameplay(scene):
                    r.warn(f"seq={e['seq']} entrada a {scene} pero la salida iba a {to}")
            current = scene
            pending_exit = None
            awaiting_control = None if p.get("non_gameplay") else e
        elif t == "control_ready":
            awaiting_control = None
        elif t == "session_end":
            if awaiting_control is not None:
                r.warn(f"scene_enter seq={awaiting_control['seq']} sin control_ready")

    r.info.append("motivos de salida: " + (", ".join(f"{k}={v}" for k, v in reasons.most_common()) or "ninguno"))


def validate(folder: Path) -> Report:
    r = Report(folder.name)
    s = check_session_json(folder, r)
    check_events(folder, s, r)
    return r


def main() -> int:
    # La consola de Windows puede no tener → u otros símbolos: sustituir en vez de fallar.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(errors="replace")
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("folders", nargs="*", type=Path)
    ap.add_argument("--all", action="store_true", help=f"validar todas las sesiones en {DEFAULT_ROOT}")
    args = ap.parse_args()

    folders = list(args.folders)
    if args.all:
        folders += sorted(p for p in DEFAULT_ROOT.iterdir() if p.is_dir()) if DEFAULT_ROOT.exists() else []
    if not folders:
        ap.print_help()
        return 2

    failed = 0
    for folder in folders:
        rep = validate(folder)
        rep.print()
        failed += bool(rep.errors)
    print(f"\n{len(folders)} sesiones, {failed} con errores.")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
