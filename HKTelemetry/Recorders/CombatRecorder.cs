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
    /// Reglas: orig(...) siempre FUERA de Safe.Run; los hooks que devuelven valor devuelven su entrada;
    /// no se suscribe ningún hook que altere el juego (ver CLAUDE.md, «Hooks prohibidos»).
    /// </summary>
    internal static class CombatRecorder
    {
        private sealed class Enemy
        {
            public HealthManager Hm;
            public string Json;   // bloque enemy cacheado
            public int LastHp;
            public int HpMax;
        }

        private static readonly Dictionary<int, Enemy> Enemies = new Dictionary<int, Enemy>();
        private static readonly HashSet<int> KilledThisVisit = new HashSet<int>();
        private static readonly List<int> DeadKeys = new List<int>();

        private static bool _hooked;
        private static bool _deathEmitted;      // un evento death por vida
        private static string _lastHitSrc;       // fuente del último golpe recibido (para death)
        private static float _lastHazardTime = -10f;
        private static bool _inEnemyHit;         // dentro de HealthManager.TakeDamage
        private static string _pendingKill;      // enemy_killed producido durante ese TakeDamage
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
            _deathEmitted = false;
            _lastHitSrc = null;
            _focusing = false;
            _lastSpellState = null;
            _boss = null;
        }

        public static void OnSceneEnter() => KilledThisVisit.Clear();

        public static void OnControlReady() => _deathEmitted = false;

        // ---------------- jugador ----------------

        private static void OnHeroTakeDamage(On.HeroController.orig_TakeDamage orig, HeroController self,
            GameObject go, CollisionSide damageSide, int damageAmount, int hazardType)
        {
            // TakeDamage se llama en cada frame de física mientras hay contacto: solo se emite si cambia la vida.
            int hp0 = 0, blue0 = 0;
            bool ok = Safe.Run("combat.hit_taken.pre", () =>
            {
                if (!SessionManager.Active) return false;
                var pd = self.playerData;
                hp0 = pd.health;
                blue0 = pd.healthBlue;
                return true;
            }, false);

            orig(self, go, damageSide, damageAmount, hazardType);

            if (!ok) return;
            Safe.Run("combat.hit_taken", () =>
            {
                var pd = self.playerData;
                int hp1 = pd.health, blue1 = pd.healthBlue;
                if (hp1 == hp0 && blue1 == blue0) return;
                string src = go != null ? go.name : null;
                _lastHitSrc = src;
                var pos = self.transform.position;
                SessionManager.Emit("hit_taken", new Json()
                    .S("src", src)
                    .I("hazard_type", hazardType) // entero crudo: 1 enemigo, 2 pinchos, 3 ácido, 4 lava, 5 foso
                    .S("side", damageSide.ToString())
                    .I("raw_dmg", damageAmount)
                    .I("hp_before", hp0).I("hp_after", hp1)
                    .I("blue_before", blue0).I("blue_after", blue1)
                    .B("overcharmed", pd.overcharmed)
                    .I("boss_level", BossLevel())
                    .N("x", pos.x).N("y", pos.y)
                    .End());
            });
        }

        private static IEnumerator OnDieFromHazard(On.HeroController.orig_DieFromHazard orig, HeroController self,
            HazardType hazardType, float angle)
        {
            Safe.Run("combat.hazard", () =>
            {
                if (!SessionManager.Active) return;
                _lastHazardTime = Time.realtimeSinceStartup;
                var pos = self.transform.position;
                SessionManager.Emit("hazard_respawn", new Json()
                    .S("hazard", hazardType.ToString()).S("src", _lastHitSrc)
                    .N("x", pos.x).N("y", pos.y).End());
            });
            return orig(self, hazardType, angle);
        }

        /// <summary>KillOnContact llama a HazardRespawn sin pasar por DieFromHazard: se cubre aquí.</summary>
        private static IEnumerator OnHazardRespawn(On.HeroController.orig_HazardRespawn orig, HeroController self)
        {
            Safe.Run("combat.hazard_direct", () =>
            {
                if (!SessionManager.Active) return;
                if (Time.realtimeSinceStartup - _lastHazardTime < 5f) return; // ya registrado por DieFromHazard
                var pos = self.transform.position;
                SessionManager.Emit("hazard_respawn", new Json()
                    .S("hazard", "kill_on_contact").N("x", pos.x).N("y", pos.y).End());
            });
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
            SessionManager.Emit("death", new Json()
                .S("zone_kind", zoneKind)
                .S("src", _lastHitSrc)
                .I("geo_before", pd.geo)
                .S("map_zone", gm != null && gm.sm != null ? gm.sm.mapZone.ToString() : null)
                .I("boss_level", BossLevel())
                .N("x", pos.x).N("y", pos.y)
                .End());
        }

        private static void OnStartMPDrain(On.HeroController.orig_StartMPDrain orig, HeroController self, float time)
        {
            Safe.Run("combat.focus_start", () =>
            {
                if (!SessionManager.Active) return;
                _focusing = true;
                var pd = self.playerData;
                SessionManager.Emit("focus", new Json().S("phase", "start").I("mp", pd.MPCharge)
                    .I("hp", pd.health).N("time", time).End());
            });
            orig(self, time);
        }

        private static void OnStopMPDrain(On.HeroController.orig_StopMPDrain orig, HeroController self)
        {
            Safe.Run("combat.focus_stop", () =>
            {
                if (!SessionManager.Active || !_focusing) return;
                _focusing = false;
                var pd = self.playerData;
                SessionManager.Emit("focus", new Json().S("phase", "stop").I("mp", pd.MPCharge).I("hp", pd.health).End());
            });
            orig(self);
        }

        // ---------------- enemigos ----------------

        private static Enemy Track(HealthManager hm)
        {
            int iid = hm.gameObject.GetInstanceID();
            if (!Enemies.TryGetValue(iid, out var e))
            {
                e = new Enemy { Hm = hm, Json = Names.EnemyJson(hm, SceneRecorder.CurrentScene), LastHp = hm.hp, HpMax = hm.hp };
                Enemies[iid] = e;
            }
            return e;
        }

        private static void OnEnemyStart(On.HealthManager.orig_Start orig, HealthManager self)
        {
            orig(self); // Start escala la vida en Godhome: se lee después
            Safe.Run("combat.enemy_start", () => { if (SessionManager.Active) Track(self); });
        }

        private static void OnEnemyTakeDamage(On.HealthManager.orig_TakeDamage orig, HealthManager self, HitInstance hit)
        {
            int hp0 = self.hp;
            _inEnemyHit = true;
            _pendingKill = null;
            try { orig(self, hit); }
            finally { _inEnemyHit = false; }

            Safe.Run("combat.hit_dealt", () =>
            {
                if (!SessionManager.Active) return;
                var e = Track(self);
                int hp1 = self.hp;
                e.LastHp = hp1;
                if (hp0 > e.HpMax) e.HpMax = hp0;
                SessionManager.Emit("hit_dealt", HitJson("hit", e, hit, hp0, hp1));
                if (_pendingKill != null) SessionManager.Emit("enemy_killed", _pendingKill);
            });
            _pendingKill = null;
        }

        private static void OnEnemyInvincible(On.HealthManager.orig_Invincible orig, HealthManager self, HitInstance hit)
        {
            Safe.Run("combat.blocked", () =>
            {
                if (!SessionManager.Active) return;
                var e = Track(self);
                SessionManager.Emit("hit_dealt", HitJson("blocked", e, hit, self.hp, self.hp));
            });
            orig(self, hit);
        }

        /// <summary>Spore Shroom y Defender's Crest: daño sin HitInstance.</summary>
        private static void OnEnemyExtraDamage(On.HealthManager.orig_ApplyExtraDamage orig, HealthManager self, int damageAmount)
        {
            int hp0 = self.hp;
            orig(self, damageAmount);
            Safe.Run("combat.extra", () =>
            {
                if (!SessionManager.Active) return;
                var e = Track(self);
                e.LastHp = self.hp;
                SessionManager.Emit("hit_dealt", new Json()
                    .S("kind", "extra").Raw("enemy", e.Json).I("dmg_dealt", damageAmount)
                    .I("hp_before", hp0).I("hp_after", self.hp).I("hp_max", e.HpMax).End());
            });
        }

        private static string HitJson(string kind, Enemy e, HitInstance hit, int hp0, int hp1)
        {
            var src = hit.Source;
            string srcName = null, srcParent = null;
            if (src != null)
            {
                srcName = src.name;
                var p = src.transform.parent;
                if (p != null) srcParent = p.name;
            }
            return new Json()
                .S("kind", kind)
                .Raw("enemy", e.Json)
                .S("attack_type", hit.AttackType.ToString())
                .S("src", srcName)
                .S("src_parent", srcParent)
                .I("dmg_dealt", hit.DamageDealt)
                .N("mult", hit.Multiplier)
                .N("dir", hit.Direction)
                .I("hp_before", hp0)
                .I("hp_after", hp1)
                .I("hp_max", e.HpMax)
                .End();
        }

        private static void OnEnemyDie(On.HealthManager.orig_Die orig, HealthManager self, float? attackDirection,
            AttackTypes attackType, bool ignoreEvasion)
        {
            Safe.Run("combat.killed", () =>
            {
                if (!SessionManager.Active || self.isDead || self.hasSpecialDeath) return; // especiales: OnReceiveDeathEvent
                var e = Track(self);
                int iid = self.gameObject.GetInstanceID();
                if (!KilledThisVisit.Add(iid)) return;

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
                string json = new Json()
                    .Raw("enemy", e.Json)
                    .S("attack_type", attackType.ToString())
                    .I("hp_max", e.HpMax)
                    .I("geo_small", s).I("geo_med", m).I("geo_large", l)
                    .I("geo_value", s + 5 * m + 25 * l)
                    .B("special", false)
                    .N("x", pos.x).N("y", pos.y)
                    .End();
                // Si muere dentro de TakeDamage, se emite después del hit_dealt que lo mató (orden lógico).
                if (_inEnemyHit) _pendingKill = json;
                else SessionManager.Emit("enemy_killed", json);
            });
            orig(self, attackDirection, attackType, ignoreEvasion);
        }

        /// <summary>Muertes por FSM (muchos jefes con hasSpecialDeath) que no pasan por HealthManager.Die.</summary>
        private static void OnReceiveDeathEvent(EnemyDeathEffects ede, bool eventAlreadyReceived,
            ref float? attackDirection, ref bool resetDeathEvent, ref bool spellBurn, ref bool isWatery)
        {
            // Los parámetros ref NO se tocan.
            if (eventAlreadyReceived) return;
            var go = ede != null ? ede.gameObject : null;
            Safe.Run("combat.killed_special", () =>
            {
                if (!SessionManager.Active || go == null) return;
                var hm = go.GetComponent<HealthManager>();
                if (hm == null) return;
                int iid = go.GetInstanceID();
                if (!KilledThisVisit.Add(iid)) return; // ya emitido por HealthManager.Die
                var e = Track(hm);
                var pos = go.transform.position;
                SessionManager.Emit("enemy_killed", new Json()
                    .Raw("enemy", e.Json).I("hp_max", e.HpMax).B("special", true)
                    .N("x", pos.x).N("y", pos.y).End());
            });
        }

        private static bool OnEnableEnemy(GameObject enemy, bool isAlreadyDead)
        {
            Safe.Run("combat.spawn", () =>
            {
                if (!SessionManager.Active || enemy == null) return;
                var hm = enemy.GetComponent<HealthManager>();
                if (hm == null) return;
                KilledThisVisit.Remove(enemy.GetInstanceID()); // enemigo reciclado: puede volver a morir
                var e = Track(hm);
                e.LastHp = hm.hp;
                var pos = enemy.transform.position;
                SessionManager.Emit("enemy_spawn", new Json()
                    .Raw("enemy", e.Json).I("hp", hm.hp).B("already_dead", isAlreadyDead)
                    .N("x", pos.x).N("y", pos.y).End());
            });
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
                SessionManager.Emit("enemy_hp_change", new Json()
                    .Raw("enemy", e.Json).I("hp_before", e.LastHp).I("hp_after", hp).End());
                e.LastHp = hp;
            }
            foreach (int k in DeadKeys) Enemies.Remove(k);

            // Estado del FSM "Spell Control": hechizos, focus y nail arts se reconstruyen en Python.
            if (hc != null && hc.spellControl != null)
            {
                string state = hc.spellControl.ActiveStateName;
                if (state != _lastSpellState)
                {
                    SessionManager.Emit("fsm_state", new Json()
                        .S("fsm", "Spell Control").S("from", _lastSpellState).S("to", state).End());
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
                    SessionManager.Emit("boss_scene", new Json().S("phase", "start").I("boss_level", boss.BossLevel)
                        .Raw("bosses", names.ToString()).End());
                }
            }
        }

        private static void OnBossesDead() => Safe.Run("combat.boss_dead", () =>
            SessionManager.Emit("boss_scene", new Json().S("phase", "bosses_dead").I("boss_level", BossLevel()).End()));

        private static void OnBossSceneComplete() => Safe.Run("combat.boss_complete", () =>
            SessionManager.Emit("boss_scene", new Json().S("phase", "complete").I("boss_level", BossLevel()).End()));

        private static int BossLevel()
        {
            var b = BossSceneController.Instance;
            return b != null ? b.BossLevel : -1;
        }
    }
}
