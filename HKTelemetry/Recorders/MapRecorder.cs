using System;
using System.Text;
using GlobalEnums;
using HKTelemetry.Session;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry.Recorders
{
    /// <summary>
    /// Mapa (docs/schema.md §4.7): cuándo y cómo se abre.
    /// - Mapa rápido: el FSM llama a GameMap.QuickMap&lt;Área&gt;() (14 métodos, uno por área mostrada) y
    ///   a GameMap.CloseQuickMap() al soltar el botón.
    /// - Mapa del inventario: GameMap.WorldMap(). No tiene método de cierre: se detecta cuando el FSM del
    ///   inventario vuelve al estado que tenía con el inventario cerrado.
    /// - Desplazamiento (StartPan/StopPan/SetCanPan) y marcadores (MapMarkerMenu.PlaceMarker/RemoveMarker).
    /// Todo son hooks de eventos raros; el único coste por frame es comparar una referencia de string.
    /// No se engancha CanQuickMap/CanOpenInventory (devuelven valor y el FSM los llama cada frame).
    /// </summary>
    internal static class MapRecorder
    {
        private static bool _hooked;
        private static string _openMode;           // null | "quick" | "inventory"
        private static double _openTReal, _openTGame;
        private static string _lastInvState;
        private static string _invClosedState;     // estado del FSM del inventario con el inventario cerrado
        private static bool _captureClosedState;
        private static bool _panning;
        private static bool _discoveryDone;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;

            On.GameMap.QuickMapAncientBasin += (orig, self) => { orig(self); Open(self, "quick", "AncientBasin"); };
            On.GameMap.QuickMapCity += (orig, self) => { orig(self); Open(self, "quick", "City"); };
            On.GameMap.QuickMapCliffs += (orig, self) => { orig(self); Open(self, "quick", "Cliffs"); };
            On.GameMap.QuickMapCrossroads += (orig, self) => { orig(self); Open(self, "quick", "Crossroads"); };
            On.GameMap.QuickMapCrystalPeak += (orig, self) => { orig(self); Open(self, "quick", "CrystalPeak"); };
            On.GameMap.QuickMapDeepnest += (orig, self) => { orig(self); Open(self, "quick", "Deepnest"); };
            On.GameMap.QuickMapFogCanyon += (orig, self) => { orig(self); Open(self, "quick", "FogCanyon"); };
            On.GameMap.QuickMapFungalWastes += (orig, self) => { orig(self); Open(self, "quick", "FungalWastes"); };
            On.GameMap.QuickMapGreenpath += (orig, self) => { orig(self); Open(self, "quick", "Greenpath"); };
            On.GameMap.QuickMapKingdomsEdge += (orig, self) => { orig(self); Open(self, "quick", "KingdomsEdge"); };
            On.GameMap.QuickMapQueensGardens += (orig, self) => { orig(self); Open(self, "quick", "QueensGardens"); };
            On.GameMap.QuickMapRestingGrounds += (orig, self) => { orig(self); Open(self, "quick", "RestingGrounds"); };
            On.GameMap.QuickMapDirtmouth += (orig, self) => { orig(self); Open(self, "quick", "Dirtmouth"); };
            On.GameMap.QuickMapWaterways += (orig, self) => { orig(self); Open(self, "quick", "Waterways"); };
            On.GameMap.CloseQuickMap += (orig, self) => { orig(self); Close("close_quick_map"); };
            On.GameMap.WorldMap += (orig, self) => { orig(self); Open(self, "inventory", null); };

            On.GameMap.StartPan += (orig, self) => { orig(self); Pan(true, "start_pan"); };
            On.GameMap.StopPan += (orig, self) => { orig(self); Pan(false, "stop_pan"); };
            On.GameMap.SetCanPan += (orig, self, pan) => { orig(self, pan); Pan(pan, "set_can_pan"); };

            On.MapMarkerMenu.PlaceMarker += (orig, self) => { int n0 = Markers(); orig(self); Marker("place", n0); };
            On.MapMarkerMenu.RemoveMarker += (orig, self) => { int n0 = Markers(); orig(self); Marker("remove", n0); };
        }

        public static void OnSessionStart()
        {
            _openMode = null;
            _lastInvState = null;
            _invClosedState = null;
            _captureClosedState = false;
            _panning = false;
        }

        /// <summary>control_ready: el inventario está cerrado seguro → se memoriza ese estado del FSM.</summary>
        public static void OnControlReady()
        {
            if (_invClosedState == null) _captureClosedState = true;
            if (!_discoveryDone) { _discoveryDone = true; Safe.Run("map.discovery", Discovery); }
        }

        // ---------------- apertura y cierre ----------------

        private static void Open(GameMap map, string mode, string area)
        {
            long t = Safe.Enter();
            try
            {
                if (!SessionManager.Active) return;
                if (_openMode == mode) return;            // segunda llamada del mismo modo
                if (_openMode != null) Close("reopen");    // cambio de modo

                var pd = PlayerData.instance;
                var gm = GameManager.UnsafeInstance;
                var hc = HeroController.UnsafeInstance;
                string scene = SceneRecorder.CurrentScene;
                var j = Json.Obj()
                    .S("mode", mode)
                    .S("area", area)
                    .S("map_zone", gm != null && gm.sm != null ? EnumNames<MapZone>.Of((int)gm.sm.mapZone) : null)
                    .B("has_map", pd.hasMap)
                    .Raw("has_area_map", area == null ? "null" : (HasAreaMap(pd, area) ? "true" : "false"))
                    .B("room_mapped", pd.mapAllRooms || (pd.scenesMapped != null && scene != null && pd.scenesMapped.Contains(scene)))
                    .B("has_quill", pd.hasQuill)
                    .B("compass_equipped", pd.equippedCharm_2)
                    .B("compass_shown", map != null && map.compassIcon != null && map.compassIcon.activeSelf)
                    .B("in_room", map != null && map.inRoom)
                    .I("markers_placed", Markers());
                if (hc != null)
                {
                    var pos = hc.transform.position;
                    j.N("x", pos.x).N("y", pos.y)
                     .S("hero_state", EnumNames<ActorStates>.Of((int)hc.hero_state))
                     .B("accepting", hc.acceptingInput && !hc.controlReqlinquished);
                }
                SessionManager.Emit("map_open", j.End());
                _openMode = mode;
                _openTReal = Clock.TReal;
                _openTGame = Clock.TGame;
            }
            catch (Exception e) { Safe.Report("map.open", e); }
            finally { Safe.Exit(t); }
        }

        private static void Close(string reason)
        {
            long t = Safe.Enter();
            try
            {
                if (_openMode == null || !SessionManager.Active) { _openMode = null; return; }
                SessionManager.Emit("map_close", Json.Obj()
                    .S("mode", _openMode)
                    .S("reason", reason)
                    .N("dur_real", Clock.TReal - _openTReal)
                    .N("dur_game", Clock.TGame - _openTGame)
                    .End());
                _openMode = null;
                _panning = false;
            }
            catch (Exception e) { Safe.Report("map.close", e); }
            finally { Safe.Exit(t); }
        }

        /// <summary>Cierres forzados: cambio de escena (el juego manda INVENTORY CANCEL), muerte, fin de sesión.</summary>
        public static void ForceClose(string reason)
        {
            if (_openMode != null) Close(reason);
        }

        private static void Pan(bool on, string via)
        {
            long t = Safe.Enter();
            try
            {
                if (!SessionManager.Active || on == _panning) return;
                _panning = on;
                SessionManager.Emit("map_pan", Json.Obj().B("on", on).S("via", via).S("mode", _openMode).End());
            }
            catch (Exception e) { Safe.Report("map.pan", e); }
            finally { Safe.Exit(t); }
        }

        private static void Marker(string op, int before)
        {
            long t = Safe.Enter();
            try
            {
                if (!SessionManager.Active) return;
                int after = Markers();
                if (after == before) return; // el juego no hizo nada (sin marcadores libres, etc.)
                SessionManager.Emit("map_marker", Json.Obj().S("op", op).I("before", before).I("after", after).End());
            }
            catch (Exception e) { Safe.Report("map.marker", e); }
            finally { Safe.Exit(t); }
        }

        private static int Markers()
        {
            try
            {
                var pd = PlayerData.instance;
                return Count(pd.placedMarkers_b) + Count(pd.placedMarkers_r) + Count(pd.placedMarkers_y) + Count(pd.placedMarkers_w);
            }
            catch { return -1; }
        }

        private static int Count(System.Collections.Generic.List<Vector3> l) => l != null ? l.Count : 0;

        /// <summary>Área del mapa rápido → bool de PlayerData (tabla de GameMap.WorldMap).</summary>
        private static bool HasAreaMap(PlayerData pd, string area)
        {
            switch (area)
            {
                case "AncientBasin": return pd.mapAbyss;
                case "City": return pd.mapCity;
                case "Cliffs": return pd.mapCliffs;
                case "Crossroads": return pd.mapCrossroads;
                case "CrystalPeak": return pd.mapMines;
                case "Deepnest": return pd.mapDeepnest;
                case "FogCanyon": return pd.mapFogCanyon;
                case "FungalWastes": return pd.mapFungalWastes;
                case "Greenpath": return pd.mapGreenpath;
                case "KingdomsEdge": return pd.mapOutskirts;
                case "QueensGardens": return pd.mapRoyalGardens;
                case "RestingGrounds": return pd.mapRestingGrounds;
                case "Dirtmouth": return pd.mapDirtmouth;
                case "Waterways": return pd.mapWaterways;
                default: return false;
            }
        }

        // ---------------- sondeo por frame: FSM del inventario ----------------

        public static void Poll(GameManager gm)
        {
            if (!SessionManager.Active || gm == null) return;
            var inv = gm.inventoryFSM;
            if (inv == null) return;
            string st = inv.ActiveStateName;
            if (_captureClosedState && st != null)
            {
                _invClosedState = st;
                _captureClosedState = false;
            }
            if (ReferenceEquals(st, _lastInvState) || st == _lastInvState) return;
            SessionManager.Emit("fsm_state", Json.Obj().S("fsm", "Inventory").S("from", _lastInvState).S("to", st).End());
            _lastInvState = st;
            if (_openMode == "inventory" && _invClosedState != null && st == _invClosedState) Close("inventory_closed");
        }

        // ---------------- descubrimiento (una vez por arranque, a ModLog.txt) ----------------

        /// <summary>
        /// Los nombres de los FSM del mapa y del inventario solo existen en los assets del juego.
        /// Se escriben una vez a ModLog.txt para poder nombrar estados (abierto, cerrado, paneles).
        /// </summary>
        private static void Discovery()
        {
            var sb = new StringBuilder("[HKTelemetry] Descubrimiento de FSM (una vez):");
            var hc = HeroController.UnsafeInstance;
            if (hc != null)
            {
                sb.Append("\n  Héroe:");
                foreach (var f in hc.gameObject.GetComponents<PlayMakerFSM>()) sb.Append(' ').Append(f.FsmName).Append(';');
            }
            var gm = GameManager.UnsafeInstance;
            if (gm != null && gm.inventoryFSM != null)
            {
                var inv = gm.inventoryFSM;
                sb.Append("\n  Inventario: ").Append(inv.FsmName).Append(" en ").Append(inv.gameObject.name)
                  .Append(" | estado actual: ").Append(inv.ActiveStateName);
                sb.Append("\n  Estados:");
                foreach (var s in inv.FsmStates) sb.Append(' ').Append(s.Name).Append(';');
                sb.Append("\n  Eventos:");
                foreach (var e in inv.FsmEvents) sb.Append(' ').Append(e.Name).Append(';');
                sb.Append("\n  FSM hijos del inventario:");
                foreach (var f in inv.gameObject.GetComponentsInChildren<PlayMakerFSM>(true))
                    sb.Append(' ').Append(f.gameObject.name).Append('/').Append(f.FsmName).Append(';');
            }
            if (gm != null && gm.gameMap != null)
            {
                sb.Append("\n  GameMap:");
                foreach (var f in gm.gameMap.GetComponents<PlayMakerFSM>()) sb.Append(' ').Append(f.FsmName).Append(';');
            }
            Safe.Info(sb.ToString());
        }
    }
}
