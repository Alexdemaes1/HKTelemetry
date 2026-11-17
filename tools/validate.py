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
import csv
import gzip
import json
import os
import sys
from collections import Counter
from pathlib import Path

SCHEMA_VERSIONS = (1, 2, 3)
DEFAULT_ROOT = Path(os.environ.get("USERPROFILE", "~")).expanduser() / "AppData/LocalLow/Team Cherry/Hollow Knight/HKTelemetry"

COMMON_FIELDS = ("seq", "t_game", "t_real", "frame", "scene", "type", "p")
EXIT_REASONS = {
    "death", "death_dream", "death_godhome", "save_load", "new_game", "new_game_godseeker", "stag", "dream_gate", "fsm_load", "unhooked",
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

    if s.get("schema_version") not in SCHEMA_VERSIONS:
        r.err(f"schema_version={s.get('schema_version')} (esperado uno de {SCHEMA_VERSIONS})")
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
    for k in ("dropped_events", "dropped_frames", "dropped_inputs", "write_errors", "hook_errors"):
        if c.get(k):
            r.err(f"counters.{k} = {c[k]}")

    exp = s.get("experience") or {}
    r.info.append(
        f"player={s.get('player_id')} kind={s.get('save', {}).get('start_kind')} slot={s.get('save', {}).get('slot')} "
        f"steam_min={exp.get('steam_playtime_min')} logros={len(exp.get('achievements_unlocked') or [])} "
        f"saves={len(exp.get('saves') or [])} end={(end or {}).get('reason')}"
    )
    return s


def check_events(folder: Path, s: dict | None, r: Report) -> list[dict]:
    path = find(folder, "events.jsonl")
    if path is None:
        r.err("falta events.jsonl(.gz)")
        return []
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
        return []

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
    check_gameplay(events, r)
    summarize_perf(events, r)
    check_pd_files(folder, s, r)
    return events


KNOWN_TYPES = {
    "session_start", "session_end", "scene_exit", "scene_enter", "control_ready", "game_state", "perf",
    "hit_taken", "hazard_respawn", "death", "hit_dealt", "enemy_hp_change", "enemy_killed", "enemy_spawn",
    "focus", "fsm_state", "boss_scene", "env_break", "pd_change", "pd_list", "boss_defeated",
    "ability_gained", "item_got", "map_open", "map_close", "map_pan", "map_marker", "device",
}


def check_gameplay(events: list[dict], r: Report) -> None:
    """Fases 2 y 3: coherencia básica de combate y PlayerData, y resumen para el perfilado."""
    unknown = Counter(e["type"] for e in events if e["type"] not in KNOWN_TYPES)
    if unknown:
        r.warn("tipos de evento desconocidos: " + ", ".join(f"{k}={v}" for k, v in unknown.items()))

    for e in events:
        t, p = e["type"], e.get("p") or {}
        if (t == "hit_dealt" and p.get("kind") == "hit" and p.get("hp_after", 0) > p.get("hp_before", 0)
                and not p.get("hp_reset") and p.get("hp_before", 0) > p.get("dmg_dealt", 0)):
            # Si el golpe dejaba la vida a 0 o menos, el FSM del enemigo puede restaurarla (fases de jefe):
            # las sesiones nuevas lo marcan con hp_reset; las antiguas se reconocen por hp_before <= dmg.
            r.warn(f"seq={e['seq']} hit_dealt con hp que sube ({p.get('hp_before')}->{p.get('hp_after')})")
        elif t == "hit_taken" and p.get("hp_after", 0) + p.get("blue_after", 0) >= p.get("hp_before", 0) + p.get("blue_before", 0):
            r.warn(f"seq={e['seq']} hit_taken sin perder vida")
        elif t == "pd_change" and p.get("old") == p.get("new"):
            r.err(f"seq={e['seq']} pd_change sin cambio en {p.get('f')}")
        elif t in ("hit_dealt", "enemy_killed", "enemy_spawn", "enemy_hp_change"):
            enemy = p.get("enemy") or {}
            if not enemy.get("type") or not enemy.get("key"):
                r.err(f"seq={e['seq']} {t} sin enemy.type/key")

    c = Counter(e["type"] for e in events)
    deaths = c["death"]
    r.info.append(
        f"combate: golpes dados={sum(1 for e in events if e['type']=='hit_dealt' and e['p'].get('kind')=='hit')} "
        f"recibidos={c['hit_taken']} enemigos muertos={c['enemy_killed']} muertes={deaths} "
        f"hazards={c['hazard_respawn']} curas={sum(1 for e in events if e['type']=='focus' and e['p'].get('phase')=='start')} "
        f"entorno roto={c['env_break']}"
    )
    r.info.append(f"PlayerData: pd_change={c['pd_change']} pd_list={c['pd_list']}")
    check_map(events, r)
    for t in ("boss_defeated", "ability_gained", "item_got"):
        found = [e["p"].get("boss") or e["p"].get("field") for e in events if e["type"] == t]
        if found:
            r.info.append(f"{t}: " + ", ".join(found))


def check_map(events: list[dict], r: Report) -> None:
    """v2: cada map_open se cierra; resumen de uso del mapa."""
    opened = None
    uses = Counter()
    total = 0.0
    for e in events:
        t, p = e["type"], e.get("p") or {}
        if t == "map_open":
            if opened is not None:
                r.warn(f"seq={e['seq']} map_open sin cerrar el anterior (seq={opened['seq']})")
            opened = e
            uses[p.get("mode")] += 1
        elif t == "map_close":
            if opened is None:
                r.warn(f"seq={e['seq']} map_close sin map_open")
            total += p.get("dur_game") or 0
            opened = None
    if opened is not None:
        r.warn(f"map_open seq={opened['seq']} sin map_close")
    if uses:
        r.info.append("mapa: " + ", ".join(f"{k}={v}" for k, v in uses.items()) + f", tiempo total {total:.0f} s")


FRAMES_HEADER = ("seq,t_game,t_real,frame,play_time,scene,gs,x,y,vx,vy,hp,hp_max,blue,soul,soul_reserve,"
                 "hero_state,cstate,facing,accepting,time_scale,input_bitmask,move_x,move_y,gap")
INPUTS_HEADER = "seq,t_game,t_real,frame,action,ev,x,y,device,accepting"
INPUT_ACTIONS = ("left", "right", "up", "down", "jump", "attack", "dash", "superDash", "cast", "quickCast",
                 "focus", "dreamNail", "quickMap", "openInventory", "pause", "evade")


def read_csv(folder: Path, name: str, header: str, s: dict | None, r: Report) -> list[list[str]] | None:
    path = find(folder, name)
    if path is None:
        # Sesiones grabadas antes de que la cabecera se escribiera siempre: sin filas no había fichero.
        c = (s or {}).get("counters") or {}
        base = name.split(".")[0]
        if c.get(base) == 0 and not c.get("dropped_" + base):
            r.warn(f"falta {name}(.gz), pero la sesión no generó filas")
            return []
        r.err(f"falta {name}(.gz)")
        return None
    if path.suffix != ".gz" and s and s.get("end"):
        r.warn(f"{name} sin comprimir en una sesión cerrada")
    with open_text(path) as f:
        text = f.read()
    # Una última línea sin '\n' es una fila cortada por un cierre brusco: se descarta con aviso.
    cut = bool(text) and not text.endswith("\n")
    rows = list(csv.reader(text.splitlines()))
    if not rows:
        r.err(f"{name} vacío (ni cabecera)")
        return None
    if ",".join(rows[0]) != header:
        r.err(f"{name}: cabecera distinta de la del esquema: {','.join(rows[0])[:120]}")
        return None
    body = rows[1:]
    if cut and body:
        r.warn(f"{name}: última fila cortada (cierre brusco), se ignora")
        body.pop()
    return body


def check_seq(name: str, rows: list[list[str]], counter: str, dropped_counter: str, s: dict | None, r: Report) -> None:
    """seq creciente; cada hueco es una fila descartada y tiene que cuadrar con los contadores."""
    gaps = 0
    prev = None
    for row in rows:
        q = int(row[0])
        if prev is not None:
            if q <= prev:
                r.err(f"{name}: seq no crece ({prev} -> {q})")
                return
            gaps += q - prev - 1
        prev = q
    if rows and int(rows[0][0]) != 0:
        gaps += int(rows[0][0])
    c = (s or {}).get("counters") or {}
    dropped = c.get(dropped_counter)
    if gaps and not dropped:
        r.err(f"{name}: {gaps} filas perdidas (huecos en seq) sin contar en counters.{dropped_counter}")
    ended = s and s.get("end") and s["end"].get("reason") != "recovered"
    if ended and c.get(counter) is not None and c[counter] != len(rows):
        r.warn(f"counters.{counter}={c[counter]} pero hay {len(rows)} filas")


def check_frames(folder: Path, s: dict | None, r: Report) -> None:
    rows = read_csv(folder, "frames.csv", FRAMES_HEADER, s, r)
    if rows is None:
        return
    if not rows:
        r.warn("frames.csv sin filas")
        return
    bad_cols = [i for i, row in enumerate(rows) if len(row) != 25]
    if bad_cols:
        r.err(f"frames.csv: {len(bad_cols)} filas sin 25 columnas (primera: fila {bad_cols[0] + 2}); se ignoran")
        rows = [row for row in rows if len(row) == 25]
        if not rows:
            return
    check_seq("frames.csv", rows, "frames", "dropped_frames", s, r)

    t_real = [float(row[2]) for row in rows]
    t_game = [float(row[1]) for row in rows]
    frame = [int(row[3]) for row in rows]
    for i in range(1, len(rows)):
        if t_real[i] + 1e-6 < t_real[i - 1] or t_game[i] + 1e-6 < t_game[i - 1]:
            r.err(f"frames.csv: reloj que retrocede en seq={rows[i][0]}")
            break
        if frame[i] <= frame[i - 1]:
            r.err(f"frames.csv: dos muestras en el mismo frame o frame que retrocede (seq={rows[i][0]})")
            break

    hz = ((s or {}).get("settings") or {}).get("sample_hz") or 60
    period = 1.0 / hz
    dts = sorted(t_real[i] - t_real[i - 1] for i in range(1, len(rows)))
    med = dts[len(dts) // 2] if dts else 0.0
    dur = t_real[-1] - t_real[0]
    real_hz = (len(rows) - 1) / dur if dur > 0 else 0.0
    late = sum(1 for d in dts if d > 2.5 * period)
    if dts and abs(med - period) > 0.35 * period:
        r.warn(f"frames.csv: intervalo mediano {med * 1000:.1f} ms (esperado {period * 1000:.1f} ms a {hz} Hz)")

    gap = [row for row in rows if row[24] == "1"]
    hero = [row for row in rows if row[24] == "0"]
    for row in gap:
        if any(row[k] for k in range(7, 20)):
            r.err(f"frames.csv: fila gap con datos de héroe (seq={row[0]})")
            break
    bad_hp = 0
    for row in hero:
        if any(row[k] == "" for k in range(7, 20)):
            r.err(f"frames.csv: fila con héroe y celdas vacías (seq={row[0]})")
            break
        hp, hp_max = int(row[11]), int(row[12])
        if hp < 0 or hp > max(hp_max, 1) + 1:
            bad_hp += 1
    if bad_hp:
        r.warn(f"frames.csv: {bad_hp} filas con hp fuera de [0, hp_max]")

    playing = [row for row in hero if row[6] == "4"]
    acc = sum(1 for row in playing if row[19] == "1")
    ts0 = sum(1 for row in playing if float(row[20]) == 0.0)
    pressed = sum(1 for row in playing if row[21] != "0")
    r.info.append(
        f"frames: {len(rows)} filas, {real_hz:.1f} Hz reales (mediana {med * 1000:.1f} ms, {late} huecos > {2.5 * period * 1000:.0f} ms), "
        f"gap={len(gap)} | jugando: {len(playing)} filas, accepting {100 * acc / max(len(playing), 1):.0f}%, "
        f"hit-stop {100 * ts0 / max(len(playing), 1):.1f}%, con botones {100 * pressed / max(len(playing), 1):.0f}%"
    )


def check_inputs(folder: Path, s: dict | None, events: list[dict], r: Report) -> None:
    rows = read_csv(folder, "inputs.csv", INPUTS_HEADER, s, r)
    if rows is None:
        return
    if not any(e.get("type") == "device" for e in events):
        r.err("events.jsonl sin evento 'device' (debe haber uno al empezar la sesión)")
    if not rows:
        r.warn("inputs.csv sin filas (¿no se pulsó nada?)")
        return
    bad_cols = [i for i, row in enumerate(rows) if len(row) != 10]
    if bad_cols:
        r.err(f"inputs.csv: {len(bad_cols)} filas sin 10 columnas (primera: fila {bad_cols[0] + 2}); se ignoran")
        rows = [row for row in rows if len(row) == 10]
        if not rows:
            return
    check_seq("inputs.csv", rows, "inputs", "dropped_inputs", s, r)

    state: dict[str, bool] = {}
    downs: Counter = Counter()
    downs_ok: Counter = Counter()
    devices: Counter = Counter()
    bad_pair = 0
    for row in rows:
        _, tg, tr, fr, action, ev, x, y, device, accepting = row
        devices[device] += 1
        if ev == "axis":
            if action != "move" or x == "" or y == "":
                r.err(f"inputs.csv: fila axis mal formada (seq={row[0]})")
                return
            continue
        if action not in INPUT_ACTIONS or ev not in ("down", "up"):
            r.err(f"inputs.csv: acción o evento desconocido '{action}/{ev}' (seq={row[0]})")
            return
        pressed = ev == "down"
        if state.get(action, False) == pressed:
            bad_pair += 1
        state[action] = pressed
        if pressed:
            downs[action] += 1
            if accepting == "1":
                downs_ok[action] += 1
    if bad_pair:
        r.warn(f"inputs.csv: {bad_pair} down/up sin pareja")

    dev_ev = [e for e in events if e.get("type") == "device"]
    pads = sorted({(e.get("p") or {}).get("gamepad") for e in dev_ev if (e.get("p") or {}).get("to") == "pad"} - {None})
    t_game = max((e.get("t_game") or 0) for e in events) if events else 0
    minutes = t_game / 60 if t_game else 0
    apm = f"{downs['attack'] / minutes:.0f}" if minutes else "-"
    top = ", ".join(f"{a}={downs[a]}({downs_ok[a]})" for a, _ in downs.most_common(8))
    r.info.append(
        f"inputs: {len(rows)} filas, dispositivos {dict(devices)}{' ' + ','.join(pads) if pads else ''} | "
        f"ataques/min de juego={apm} | pulsaciones (con heroe activo): {top}"
    )


def check_pd_files(folder: Path, s: dict | None, r: Report) -> None:
    for name in ("pd_start.json.gz", "pd_end.json.gz"):
        path = folder / name
        if not path.exists():
            if s and s.get("progress_start") is not None:
                r.warn(f"falta {name}")
            continue
        try:
            with gzip.open(path, "rt", encoding="utf-8") as f:
                n = len(json.load(f).get("fields", {}))
            if n < 1000:
                r.err(f"{name} solo tiene {n} campos")
        except (OSError, json.JSONDecodeError) as e:
            r.err(f"{name} ilegible: {e}")


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
            if reason in ("other", "unhooked") and not is_non_gameplay(p.get("from")):
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
    events = check_events(folder, s, r)
    if s and (s.get("schema_version") or 0) >= 3:
        check_frames(folder, s, r)
        check_inputs(folder, s, events, r)
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
