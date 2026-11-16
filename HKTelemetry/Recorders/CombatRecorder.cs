using System;
using System.Collections;
using System.Collections.Generic;
using GlobalEnums;
using HKTelemetry.Session;
using HKTelemetry.Util;
using Modding;
using UnityEngine;

namespace HKTelemetry.Recorders
{
    /// <summary>
    /// Combate (docs/schema.md §4.2): hit_taken, hazard_respawn, death, hit_dealt, enemy_hp_change,
    /// enemy_killed, enemy_spawn, focus, fsm_state y boss_scene.
    ///
    /// Reglas:
    /// - orig(...) siempre FUERA de las secciones protegidas; los hooks que devuelven valor devuelven su entrada.
    /// - Hooks calientes (TakeDamage se llama en cada frame de física mientras hay contacto) sin lambdas:
    ///   try/catch explícito + Safe.Enter/Exit, para no generar basura.
    /// - Orden causal: lo que el juego dispara DENTRO de un golpe (muerte del jugador, hazard, muerte del
    ///   enemigo) se guarda y se emite después del evento del golpe.
    /// </summary>
    internal static class CombatRecorder
    {
        private sealed class Enemy
        {
            public HealthManager Hm;
            public string Block;  // bloque "enemy" en JSON, cacheado
            public int LastHp;
            public int HpMax;
        }

        private static readonly Dictionary<int, Enemy> Enemies = new Dictionary<int, Enemy>();
        private static readonly HashSet<int> KilledThisVisit = new HashSet<int>();
        private static readonly List<int> DeadKeys = new List<int>();

        private static bool _hooked;
        private static bool _deathEmitted;       // un evento death por vida
        private static string _lastHitSrc;       // fuente del último golpe recibido
        private static float _lastHazardTime = -10f;

        // Golpe al jugador en curso (dentro de orig TakeDamage).
        private static bool _inHeroDamage;
        private static GameObject _curHitGo;     // nombre resuelto solo si hace falta (go.name crea un string)
        private static bool _pendingMapCloseDeath;
        private static readonly List<KeyValuePair<string, string>> PendingHero = new List<KeyValuePair<string, string>>();

        // Golpe a un enemigo en curso (TakeDamage o ApplyExtraDamage).
        private static int _inEnemyHit;
        private static string _pendingKill;

        private static bool _focusing;
        private static string _lastSpellState;
        private static BossSceneController _boss;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;

            On.HeroController.TakeDamage += OnHeroTakeDamage;
            On.HeroController.DieFromHazard += OnDieFromHazard;
            On.HeroController.HazardRespawn += OnHazardRespawn;
            On.HeroController.StartMPDrain += OnStartMPDrain;
            On.HeroController.StopMPDrain += OnStopMPDrain;

            On.HealthManager.Start += OnEnemyStart;
            On.HealthManager.TakeDamage += OnEnemyTakeDamage;
            On.HealthManager.Invincible += OnEnemyInvincible;
            On.HealthManager.ApplyExtraDamage += OnEnemyExtraDamage;
            On.HealthManager.Die += OnEnemyDie;
            ModHooks.OnReceiveDeathEventHook += OnReceiveDeathEvent;
            ModHooks.OnEnableEnemyHook += OnEnableEnemy;
        }

        // ---------------- ciclo de vida (llamado desde SceneRecorder) ----------------

        public static void OnSessionStart()
        {
            Enemies.Clear();
            KilledThisVisit.Clear();
            PendingHero.Clear();
            _pendingMapCloseDeath = false;
            _curHitGo = null;
            _deathEmitted = false;
            _lastHitSrc = null;
            _inHeroDamage = false;
            _inEnemyHit = 0;
            _pendingKill = null;
            _focusing = false;
            _lastSpellState = null;
            _boss = null;
        }

        public static void OnSceneEnter() => KilledThisVisit.Clear();

        public static void OnControlReady() => _deathEmitted = false;

        private static string CurHitSrc() => _curHitGo != null ? _curHitGo.name : null;

        /// <summary>Emite ya, o lo guarda si estamos dentro del golpe que lo provocó.</summary>
        private static void EmitHero(string type, string payload)
        {
            if (_inHeroDamage) PendingHero.Add(new KeyValuePair<string, string>(type, payload));
            else SessionManager.Emit(type, payload);
        }

        // ---------------- jugador ----------------

        private static void OnHeroTakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self,
            GameObject go, CollisionSide damageSide, int damageAmount, int hazardType)
        {
            int hp0 = 0, blue0 = 0;
            bool ok = false;
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active)
                {
                    var pd = self.playerData;
                    hp0 = pd.health;
                    blue0 = pd.healthBlue;
                    _curHitGo = go;
                    _inHeroDamage = true;
                    _pendingMapCloseDeath = false;
                    PendingHero.Clear();
                    ok = true;
                }
            }
            catch (Exception e) { Safe.Report("combat.hit_taken.pre", e); }
            finally { Safe.Exit(t); }

            try { orig(self, go, damageSide, damageAmount, hazardType); }
            finally { _inHeroDamage = false; }

            if (!ok) return;
            t = Safe.Enter();
            try
            {
                var pd = self.playerData;
                int hp1 = pd.health, blue1 = pd.healthBlue;
                // TakeDamage se llama en cada frame de física con contacto: solo si cambia la vida.
                if (hp1 != hp0 || blue1 != blue0)
                {
                    _lastHitSrc = CurHitSrc();
                    var pos = self.transform.position;
                    SessionManager.Emit("hit_taken", Json.Obj()
                        .S("src", _lastHitSrc)
                        .I("hazard_type", hazardType) // entero crudo: 1 enemigo, 2 pinchos, 3 ácido, 4 lava, 5 foso
                        .S("side", EnumNames<CollisionSide>.Of((int)damageSide))
                        .I("raw_dmg", damageAmount)
                        .I("hp_before", hp0).I("hp_after", hp1)
                        .I("blue_before", blue0).I("blue_after", blue1)
                        .B("overcharmed", pd.overcharmed)
                        .I("boss_level", BossLevel())
                        .N("x", pos.x).N("y", pos.y)
                        .End());
                }
                // Muerte o hazard provocados por este golpe: después del hit_taken.
                for (int i = 0; i < PendingHero.Count; i++) SessionManager.Emit(PendingHero[i].Key, PendingHero[i].Value);
                PendingHero.Clear();
                if (_pendingMapCloseDeath) { _pendingMapCloseDeath = false; MapRecorder.ForceClose("death"); }
            }
            catch (Exception e) { Safe.Report("combat.hit_taken", e); }
            finally { Safe.Exit(t); }
        }

        private static IEnumerator OnDieFromHazard(On.HeroController.orig_DieFromHazard orig, HeroController self,
            HazardType hazardType, float angle)
        {
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active)
                {
                    _lastHazardTime = Time.realtimeSinceStartup;
                    var pos = self.transform.position;
                    EmitHero("hazard_respawn", Json.Obj()
                        .S("hazard", EnumNames<HazardType>.Of((int)hazardType))
                        .S("src", _inHeroDamage ? CurHitSrc() : _lastHitSrc)
                        .N("x", pos.x).N("y", pos.y).End());
                }
            }
            catch (Exception e) { Safe.Report("combat.hazard", e); }
            finally { Safe.Exit(t); }
            return orig(self, hazardType, angle);
        }

        /// <summary>KillOnContact llama a HazardRespawn sin pasar por DieFromHazard: se cubre aquí.</summary>
        private static IEnumerator OnHazardRespawn(On.HeroController.orig_HazardRespawn orig, HeroController self)
        {
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active && Time.realtimeSinceStartup - _lastHazardTime >= 5f)
                {
                    var pos = self.transform.position;
                    SessionManager.Emit("hazard_respawn", Json.Obj()
                        .S("hazard", "kill_on_contact").N("x", pos.x).N("y", pos.y).End());
                }
            }
            catch (Exception e) { Safe.Report("combat.hazard_direct", e); }
            finally { Safe.Exit(t); }
            return orig(self);
        }

        /// <summary>Desde SceneRecorder.OnHeroDie (un solo hook sobre HeroController.Die).</summary>
        public static void RecordDeath(HeroController hc, string zoneKind)
        {
            if (!SessionManager.Active || _deathEmitted) return;
            _deathEmitted = true;
            var pd = hc.playerData;
            var pos = hc.transform.position;
            var gm = GameManager.UnsafeInstance;
            EmitHero("death", Json.Obj()
                .S("zone_kind", zoneKind)
                .S("src", _inHeroDamage ? CurHitSrc() : _lastHitSrc)
                .I("geo_before", pd.geo)
                .S("map_zone", gm != null && gm.sm != null ? EnumNames<MapZone>.Of((int)gm.sm.mapZone) : null)
                .I("boss_level", BossLevel())
                .N("x", pos.x).N("y", pos.y)
                .End());
            // El cierre del mapa va después de la muerte (y de su hit_taken si murió de un golpe).
            if (_inHeroDamage) _pendingMapCloseDeath = true;
            else MapRecorder.ForceClose("death");
        }

        private static void OnStartMPDrain(On.HeroController.orig_StartMPDrain orig, HeroController self, float time)
        {
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active)
                {
                    _focusing = true;
                    var pd = self.playerData;
                    SessionManager.Emit("focus", Json.Obj().S("phase", "start").I("mp", pd.MPCharge)
                        .I("hp", pd.health).N("time", time).End());
                }
            }
            catch (Exception e) { Safe.Report("combat.focus_start", e); }
            finally { Safe.Exit(t); }
            orig(self, time);
        }

        private static void OnStopMPDrain(On.HeroController.orig_StopMPDrain orig, HeroController self)
        {
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active && _focusing)
                {
                    _focusing = false;
                    var pd = self.playerData;
                    SessionManager.Emit("focus", Json.Obj().S("phase", "stop").I("mp", pd.MPCharge).I("hp", pd.health).End());
                }
            }
            catch (Exception e) { Safe.Report("combat.focus_stop", e); }
            finally { Safe.Exit(t); }
            orig(self);
        }

        // ---------------- enemigos ----------------

        private static Enemy Track(HealthManager hm)
        {
            int iid = hm.gameObject.GetInstanceID();
            if (!Enemies.TryGetValue(iid, out var e))
            {
                e = new Enemy { Hm = hm, Block = Names.EnemyJson(hm, SceneRecorder.CurrentScene), LastHp = hm.hp, HpMax = hm.hp };
                Enemies[iid] = e;
            }
            return e;
        }

        private static void OnEnemyStart(On.HealthManager.orig_Start orig, HealthManager self)
        {
            orig(self); // Start escala la vida en Godhome: se lee después
            long t = Safe.Enter();
            try { if (SessionManager.Active) Track(self); }
            catch (Exception e) { Safe.Report("combat.enemy_start", e); }
            finally { Safe.Exit(t); }
        }

        private static void OnEnemyTakeDamage(On.HealthManager.orig_TakeDamage orig, HealthManager self, HitInstance hit)
        {
            int hp0 = self.hp;
            string outerKill = _pendingKill;
            _inEnemyHit++;
            _pendingKill = null;
            try { orig(self, hit); }
            finally { _inEnemyHit--; }

            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active)
                {
                    var e = Track(self);
                    int hp1 = self.hp;
                    e.LastHp = hp1;
                    if (hp0 > e.HpMax) e.HpMax = hp0;
                    EmitHit("hit", e, hit, hp0, hp1);
                    if (_pendingKill != null) SessionManager.Emit("enemy_killed", _pendingKill);
                }
            }
            catch (Exception ex) { Safe.Report("combat.hit_dealt", ex); }
            finally { Safe.Exit(t); _pendingKill = outerKill; }
        }

        private static void OnEnemyInvincible(On.HealthManager.orig_Invincible orig, HealthManager self, HitInstance hit)
        {
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active)
                    EmitHit("blocked", Track(self), hit, self.hp, self.hp);
            }
            catch (Exception e) { Safe.Report("combat.blocked", e); }
            finally { Safe.Exit(t); }
            orig(self, hit);
        }

        /// <summary>Spore Shroom y Defender's Crest: daño sin HitInstance. Puede matar dentro (Die síncrono).</summary>
        private static void OnEnemyExtraDamage(On.HealthManager.orig_ApplyExtraDamage orig, HealthManager self, int damageAmount)
        {
            int hp0 = self.hp;
            string outerKill = _pendingKill;
            _inEnemyHit++;
            _pendingKill = null;
            try { orig(self, damageAmount); }
            finally { _inEnemyHit--; }

            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active)
                {
                    var e = Track(self);
                    e.LastHp = self.hp;
                    SessionManager.Emit("hit_dealt", Json.Obj()
                        .S("kind", "extra").Raw("enemy", e.Block).I("dmg_dealt", damageAmount)
                        .I("hp_before", hp0).I("hp_after", self.hp).I("hp_max", e.HpMax).End());
                    if (_pendingKill != null) SessionManager.Emit("enemy_killed", _pendingKill);
                }
            }
            catch (Exception ex) { Safe.Report("combat.extra", ex); }
            finally { Safe.Exit(t); _pendingKill = outerKill; }
        }

        private static void EmitHit(string kind, Enemy e, HitInstance hit, int hp0, int hp1)
        {
            var src = hit.Source;
            string srcName = null, srcParent = null;
            if (src != null)
            {
                srcName = src.name;
                var p = src.transform.parent;
                if (p != null) srcParent = p.name;
            }
            // Todos los valores calculados antes de abrir el sobre (ver SessionManager.EmitBegin).
            var j = SessionManager.EmitBegin("hit_dealt");
            if (j == null) return;
            SessionManager.EmitEnd(j
                .S("kind", kind)
                .Raw("enemy", e.Block)
                .S("attack_type", EnumNames<AttackTypes>.Of((int)hit.AttackType))
                .S("src", srcName)
                .S("src_parent", srcParent)
                .I("dmg_dealt", hit.DamageDealt)
                .N("mult", hit.Multiplier)
                .N("dir", hit.Direction)
                .I("hp_before", hp0)
                .I("hp_after", hp1)
                .I("hp_max", e.HpMax)
                // El FSM del enemigo puede restaurar la vida dentro del propio golpe (fases del Falso
                // Caballero: armadura a 0 → 65). No es un error: se marca.
                .B("hp_reset", hp1 > hp0));
        }

        private static void OnEnemyDie(On.HealthManager.orig_Die orig, HealthManager self, float? attackDirection,
            AttackTypes attackType, bool ignoreEvasion)
        {
            long t = Safe.Enter();
            try { RecordKill(self, attackType); }
            catch (Exception e) { Safe.Report("combat.killed", e); }
            finally { Safe.Exit(t); }
            orig(self, attackDirection, attackType, ignoreEvasion);
        }

        private static void RecordKill(HealthManager self, AttackTypes attackType)
        {
            if (!SessionManager.Active || self.isDead || self.hasSpecialDeath) return; // especiales: OnReceiveDeathEvent
            var e = Track(self);
            int iid = self.gameObject.GetInstanceID();
            if (!KilledThisVisit.Add(iid)) return;
            // Daño aplicado sin TakeDamage justo antes de morir (Flukenest: SpellFluke.cs:92 resta hp y
            // llama a Die): se registra ahora, antes de la muerte, porque el sondeo llegaría tarde o nunca.
            if (_inEnemyHit == 0 && self.hp != e.LastHp)
            {
                SessionManager.Emit("enemy_hp_change", Json.Obj()
                    .Raw("enemy", e.Block).I("hp_before", e.LastHp).I("hp_after", self.hp).End());
                e.LastHp = self.hp;
            }

            // Geo que suelta (HealthManager.cs:663-722): campos privados, +20 % con Fragile Greed (amuleto 24).
            int s = 0, m = 0, l = 0;
            if (attackType != AttackTypes.Splatter && attackType != AttackTypes.RuinsWater)
            {
                s = ReflectionHelper.GetField<HealthManager, int>(self, "smallGeoDrops");
                m = ReflectionHelper.GetField<HealthManager, int>(self, "mediumGeoDrops");
                l = ReflectionHelper.GetField<HealthManager, int>(self, "largeGeoDrops");
                var pd = PlayerData.instance;
                if (pd.equippedCharm_24 && !pd.brokenCharm_24)
                {
                    s += Mathf.CeilToInt(s * 0.2f); m += Mathf.CeilToInt(m * 0.2f); l += Mathf.CeilToInt(l * 0.2f);
                }
            }
            var pos = self.transform.position;
            string json = Json.Obj()
                .Raw("enemy", e.Block)
                .S("attack_type", EnumNames<AttackTypes>.Of((int)attackType))
                .I("hp_max", e.HpMax)
                .I("geo_small", s).I("geo_med", m).I("geo_large", l)
                .I("geo_value", s + 5 * m + 25 * l)
                .B("special", false)
                .N("x", pos.x).N("y", pos.y)
                .End();
            // Si muere dentro de un golpe, se emite después del hit_dealt que lo mató (orden causal).
            if (_inEnemyHit > 0) _pendingKill = json;
            else SessionManager.Emit("enemy_killed", json);
        }

        /// <summary>Muertes por FSM (muchos jefes con hasSpecialDeath) que no pasan por HealthManager.Die.</summary>
        private static void OnReceiveDeathEvent(EnemyDeathEffects ede, bool eventAlreadyReceived,
            ref float? attackDirection, ref bool resetDeathEvent, ref bool spellBurn, ref bool isWatery)
        {
            // Los parámetros ref NO se tocan.
            if (eventAlreadyReceived) return;
            long t = Safe.Enter();
            try
            {
                var go = ede != null ? ede.gameObject : null;
                if (!SessionManager.Active || go == null) return;
                var hm = go.GetComponent<HealthManager>();
                if (hm == null) return;
                int iid = go.GetInstanceID();
                if (!KilledThisVisit.Add(iid)) return; // ya emitido por HealthManager.Die
                var e = Track(hm);
                var pos = go.transform.position;
                SessionManager.Emit("enemy_killed", Json.Obj()
                    .Raw("enemy", e.Block).I("hp_max", e.HpMax).B("special", true)
                    .N("x", pos.x).N("y", pos.y).End());
            }
            catch (Exception ex) { Safe.Report("combat.killed_special", ex); }
            finally { Safe.Exit(t); }
        }

        private static bool OnEnableEnemy(GameObject enemy, bool isAlreadyDead)
        {
            long t = Safe.Enter();
            try
            {
                if (SessionManager.Active && enemy != null)
                {
                    var hm = enemy.GetComponent<HealthManager>();
                    if (hm != null)
                    {
                        KilledThisVisit.Remove(enemy.GetInstanceID()); // enemigo reciclado: puede volver a morir
                        var e = Track(hm);
                        e.LastHp = hm.hp;
                        var pos = enemy.transform.position;
                        SessionManager.Emit("enemy_spawn", Json.Obj()
                            .Raw("enemy", e.Block).I("hp", hm.hp).B("already_dead", isAlreadyDead)
                            .N("x", pos.x).N("y", pos.y).End());
                    }
                }
            }
            catch (Exception ex) { Safe.Report("combat.spawn", ex); }
            finally { Safe.Exit(t); }
            return isAlreadyDead; // pass-through obligatorio
        }

        // ---------------- sondeo por frame (Runner) ----------------

        public static void Poll(HeroController hc)
        {
            if (!SessionManager.Active) return;

            // Vida de enemigos cambiada sin TakeDamage (Flukenest, acciones FSM SubtractHP/SetHP).
            DeadKeys.Clear();
            foreach (var kv in Enemies)
            {
                var e = kv.Value;
                if (e.Hm == null) { DeadKeys.Add(kv.Key); continue; }
                int hp = e.Hm.hp;
                if (hp == e.LastHp) continue;
                if (hp > e.HpMax) e.HpMax = hp;
                var jh = SessionManager.EmitBegin("enemy_hp_change");
                if (jh != null) SessionManager.EmitEnd(jh.Raw("enemy", e.Block).I("hp_before", e.LastHp).I("hp_after", hp));
                e.LastHp = hp;
            }
            for (int i = 0; i < DeadKeys.Count; i++) Enemies.Remove(DeadKeys[i]);

            // Estado del FSM "Spell Control" al final del frame: hechizos, focus y nail arts se
            // reconstruyen en Python. Los estados que entran y salen en el mismo frame no se ven.
            if (hc != null && hc.spellControl != null)
            {
                string state = hc.spellControl.ActiveStateName;
                if (!ReferenceEquals(state, _lastSpellState) && state != _lastSpellState)
                {
                    var jf = SessionManager.EmitBegin("fsm_state");
                    if (jf != null) SessionManager.EmitEnd(jf.S("fsm", "Spell Control").S("from", _lastSpellState).S("to", state));
                    _lastSpellState = state;
                }
            }

            // Godhome: escenas de jefe.
            var boss = BossSceneController.Instance;
            if (boss != _boss)
            {
                if (_boss != null) { _boss.OnBossesDead -= OnBossesDead; _boss.OnBossSceneComplete -= OnBossSceneComplete; }
                _boss = boss;
                if (boss != null)
                {
                    boss.OnBossesDead += OnBossesDead;
                    boss.OnBossSceneComplete += OnBossSceneComplete;
                    var names = new System.Text.StringBuilder("[");
                    if (boss.bosses != null)
                        for (int i = 0; i < boss.bosses.Length; i++)
                        {
                            if (i > 0) names.Append(',');
                            Json.AppendString(names, boss.bosses[i] != null ? boss.bosses[i].name : "");
                        }
                    names.Append(']');
                    SessionManager.Emit("boss_scene", Json.Obj().S("phase", "start").I("boss_level", boss.BossLevel)
                        .Raw("bosses", names.ToString()).End());
                }
            }
        }

        private static void OnBossesDead() => Safe.Run("combat.boss_dead", () =>
            SessionManager.Emit("boss_scene", Json.Obj().S("phase", "bosses_dead").I("boss_level", BossLevel()).End()));

        private static void OnBossSceneComplete() => Safe.Run("combat.boss_complete", () =>
            SessionManager.Emit("boss_scene", Json.Obj().S("phase", "complete").I("boss_level", BossLevel()).End()));

        private static int BossLevel()
        {
            var b = BossSceneController.Instance;
            return b != null ? b.BossLevel : -1;
        }
    }
}
