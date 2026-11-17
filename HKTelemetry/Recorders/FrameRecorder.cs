using System;
using System.Reflection;
using System.Text;
using HKTelemetry.Session;
using HKTelemetry.Util;
using HKTelemetry.Writing;
using UnityEngine;

namespace HKTelemetry.Recorders
{
    /// <summary>Fila de frames.csv (docs/schema.md §5). Solo números y referencias a strings existentes.</summary>
    internal struct FrameRow
    {
        public long Seq;
        public double TGame, TReal, PlayTime;
        public int Frame;
        public string Scene;
        public sbyte Gs;
        public bool Gap;
        public float X, Y, Vx, Vy;
        public int Hp, HpMax, Blue, Soul, SoulReserve;
        public sbyte HeroState;
        public long CState;
        public sbyte Facing;
        public bool Accepting;
        public float TimeScale;
        public int InputMask;
        public float MoveX, MoveY;
    }

    /// <summary>
    /// Muestreo del héroe a sample_hz (fase 4), desde el Runner (late en pausa, menús y cinemáticas).
    /// - Acumulador de unscaledDeltaTime; como mucho una muestra por frame.
    /// - Sin héroe (HeroController.UnsafeInstance == null) se escribe una fila con gap=1.
    /// - Lecturas directas de campos (sin GetInt/GetBool, que pasan por los hooks de la API).
    /// - Cero asignaciones: la fila es un struct que se copia a un búfer circular; el texto se
    ///   genera en el hilo de escritura.
    /// </summary>
    internal static class FrameRecorder
    {
        public const string Header =
            "seq,t_game,t_real,frame,play_time,scene,gs,x,y,vx,vy,hp,hp_max,blue,soul,soul_reserve," +
            "hero_state,cstate,facing,accepting,time_scale,input_bitmask,move_x,move_y,gap";

        private static long _seq;
        private static float _acc;
        private static int _hz = 60;
        private static float _period = 1f / 60;
        private static HeroController _rbOwner;
        private static Rigidbody2D _rb;

        // Hz real (para el HUD de debug).
        private static float _hzWinStart;
        private static int _hzCount;
        public static float MeasuredHz { get; private set; }

        public static long Produced => _seq;

        /// <summary>Frecuencia usada en la sesión actual (ajuste fijado al empezar, limitado a 1…240).</summary>
        public static int EffectiveHz => _hz;

        public static RowRing<FrameRow> CreateSink() =>
            new RowRing<FrameRow>(TelemetryWriter.Frames, Header, 8192, Format);

        public static void OnSessionStart()
        {
            _seq = 0;
            _acc = float.MaxValue; // primera muestra en el primer frame de la sesión
            // Fija la frecuencia para toda la sesión: un cambio en el menú se aplica en la siguiente
            // (así todas las filas de un frames.csv tienen el mismo ritmo y session.json lo dice bien).
            int hz = HKTelemetry.Settings.SampleHz;
            _hz = hz < 1 ? 1 : hz > 240 ? 240 : hz;
            _period = 1f / _hz;
            _hzCount = 0;
            _hzWinStart = Time.realtimeSinceStartup;
        }

        /// <summary>Cada frame desde el Runner, después de InputRecorder.Poll.</summary>
        public static void Poll(GameManager gm, HeroController hc)
        {
            if (!SessionManager.Active) return;
            var ring = SessionManager.FrameSink;
            if (ring == null) return;

            float period = _period;

            float now = Time.realtimeSinceStartup;
            if (now - _hzWinStart >= 1f)
            {
                MeasuredHz = _hzCount / (now - _hzWinStart);
                _hzCount = 0;
                _hzWinStart = now;
            }

            if (_acc == float.MaxValue) _acc = period;
            else _acc += Time.unscaledDeltaTime;
            if (_acc < period) return;
            _acc -= period;
            if (_acc >= period) _acc %= period; // fps por debajo de sample_hz: una muestra por frame, sin ráfagas

            var row = new FrameRow
            {
                TGame = Clock.TGame,
                TReal = Clock.TReal,
                Frame = Clock.Frame,
                PlayTime = gm != null ? gm.PlayTime : double.NaN,
                Scene = SceneRecorder.CurrentScene,
                Gs = gm != null ? (sbyte)gm.gameState : (sbyte)-1,
                TimeScale = Time.timeScale,
                InputMask = InputRecorder.TakeIntervalMask(),
                MoveX = InputRecorder.MoveX,
                MoveY = InputRecorder.MoveY,
            };

            var pd = hc != null ? hc.playerData : null;
            if (hc == null || pd == null)
            {
                row.Gap = true;
            }
            else
            {
                var pos = hc.transform.position;
                row.X = pos.x;
                row.Y = pos.y;
                if (!ReferenceEquals(hc, _rbOwner))
                {
                    _rbOwner = hc;
                    _rb = hc.GetComponent<Rigidbody2D>();
                }
                if (_rb != null)
                {
                    var v = _rb.velocity;
                    row.Vx = v.x;
                    row.Vy = v.y;
                }
                row.Hp = pd.health;
                row.HpMax = MaxHealth(pd);
                row.Blue = pd.healthBlue;
                row.Soul = pd.MPCharge;
                row.SoulReserve = pd.MPReserve;
                row.HeroState = (sbyte)hc.hero_state;
                var cs = hc.cState;
                if (cs != null)
                {
                    row.CState = CStateBits(cs);
                    row.Facing = (sbyte)(cs.facingRight ? 1 : -1);
                }
                row.Accepting = hc.acceptingInput && !hc.controlReqlinquished && gm != null && !gm.isPaused;
            }

            row.Seq = _seq++;
            ring.TryWrite(ref row);
            _hzCount++;
        }

        // BossSequenceController.currentSequence es privado y estático (BossSequenceController.cs:43).
        private static readonly FieldInfo CurrentSequenceField =
            typeof(BossSequenceController).GetField("currentSequence", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>
        /// Igual que PlayerData.CurrentMaxHealth con el binding de vida de Godhome
        /// (Min(maxHealth, currentSequence.maxHealth + 2 si lleva el amuleto 23 sin romper),
        /// BossSequenceController.cs:186-192) pero leyendo campos directamente: BoundMaxHealth pasa
        /// por GetBool, es decir, por los hooks de la API y de otros mods, en cada muestra.
        /// </summary>
        private static int MaxHealth(PlayerData pd)
        {
            int max = pd.maxHealth;
            if (!BossSequenceController.BoundShell) return max;
            try
            {
                var seq = CurrentSequenceField?.GetValue(null) as BossSequence;
                if (seq == null) return max;
                int bound = seq.maxHealth + ((pd.equippedCharm_23 && !pd.brokenCharm_23) ? 2 : 0);
                return Math.Min(max, bound);
            }
            catch { return max; }
        }

        /// <summary>Bits de cState en el orden de HeroControllerStates (schema.md §5.1).</summary>
        private static long CStateBits(HeroControllerStates c)
        {
            long b = 0;
            if (c.facingRight) b |= 1L << 0;
            if (c.onGround) b |= 1L << 1;
            if (c.jumping) b |= 1L << 2;
            if (c.wallJumping) b |= 1L << 3;
            if (c.doubleJumping) b |= 1L << 4;
            if (c.nailCharging) b |= 1L << 5;
            if (c.shadowDashing) b |= 1L << 6;
            if (c.swimming) b |= 1L << 7;
            if (c.falling) b |= 1L << 8;
            if (c.dashing) b |= 1L << 9;
            if (c.superDashing) b |= 1L << 10;
            if (c.superDashOnWall) b |= 1L << 11;
            if (c.backDashing) b |= 1L << 12;
            if (c.touchingWall) b |= 1L << 13;
            if (c.wallSliding) b |= 1L << 14;
            if (c.transitioning) b |= 1L << 15;
            if (c.attacking) b |= 1L << 16;
            if (c.lookingUp) b |= 1L << 17;
            if (c.lookingDown) b |= 1L << 18;
            if (c.lookingUpAnim) b |= 1L << 19;
            if (c.lookingDownAnim) b |= 1L << 20;
            if (c.altAttack) b |= 1L << 21;
            if (c.upAttacking) b |= 1L << 22;
            if (c.downAttacking) b |= 1L << 23;
            if (c.bouncing) b |= 1L << 24;
            if (c.shroomBouncing) b |= 1L << 25;
            if (c.recoilingRight) b |= 1L << 26;
            if (c.recoilingLeft) b |= 1L << 27;
            if (c.dead) b |= 1L << 28;
            if (c.hazardDeath) b |= 1L << 29;
            if (c.hazardRespawning) b |= 1L << 30;
            if (c.willHardLand) b |= 1L << 31;
            if (c.recoilFrozen) b |= 1L << 32;
            if (c.recoiling) b |= 1L << 33;
            if (c.invulnerable) b |= 1L << 34;
            if (c.casting) b |= 1L << 35;
            if (c.castRecoiling) b |= 1L << 36;
            if (c.preventDash) b |= 1L << 37;
            if (c.preventBackDash) b |= 1L << 38;
            if (c.dashCooldown) b |= 1L << 39;
            if (c.backDashCooldown) b |= 1L << 40;
            if (c.nearBench) b |= 1L << 41;
            if (c.inWalkZone) b |= 1L << 42;
            if (c.isPaused) b |= 1L << 43;
            if (c.onConveyor) b |= 1L << 44;
            if (c.onConveyorV) b |= 1L << 45;
            if (c.inConveyorZone) b |= 1L << 46;
            if (c.spellQuake) b |= 1L << 47;
            if (c.freezeCharge) b |= 1L << 48;
            if (c.focusing) b |= 1L << 49;
            if (c.inAcid) b |= 1L << 50;
            if (c.slidingLeft) b |= 1L << 51;
            if (c.slidingRight) b |= 1L << 52;
            if (c.touchingNonSlider) b |= 1L << 53;
            if (c.wasOnGround) b |= 1L << 54;
            return b;
        }

        // ---------------- formateo (hilo de escritura) ----------------

        private static void Format(ref FrameRow r, StringBuilder sb)
        {
            Json.AppendLong(sb, r.Seq); sb.Append(',');
            Num(sb, r.TGame); sb.Append(',');
            Num(sb, r.TReal); sb.Append(',');
            Json.AppendLong(sb, r.Frame); sb.Append(',');
            Num(sb, r.PlayTime); sb.Append(',');
            Csv(sb, r.Scene); sb.Append(',');
            if (r.Gs >= 0) Json.AppendLong(sb, r.Gs);
            sb.Append(',');
            if (r.Gap)
            {
                // x … accepting vacíos (13 columnas): no hay héroe.
                sb.Append(",,,,,,,,,,,,,");
            }
            else
            {
                Num(sb, r.X); sb.Append(',');
                Num(sb, r.Y); sb.Append(',');
                Num(sb, r.Vx); sb.Append(',');
                Num(sb, r.Vy); sb.Append(',');
                Json.AppendLong(sb, r.Hp); sb.Append(',');
                Json.AppendLong(sb, r.HpMax); sb.Append(',');
                Json.AppendLong(sb, r.Blue); sb.Append(',');
                Json.AppendLong(sb, r.Soul); sb.Append(',');
                Json.AppendLong(sb, r.SoulReserve); sb.Append(',');
                Json.AppendLong(sb, r.HeroState); sb.Append(',');
                Json.AppendLong(sb, r.CState); sb.Append(',');
                Json.AppendLong(sb, r.Facing); sb.Append(',');
                sb.Append(r.Accepting ? '1' : '0'); sb.Append(',');
            }
            Num(sb, r.TimeScale); sb.Append(',');
            Json.AppendLong(sb, r.InputMask); sb.Append(',');
            Num(sb, r.MoveX); sb.Append(',');
            Num(sb, r.MoveY); sb.Append(',');
            sb.Append(r.Gap ? '1' : '0');
        }

        /// <summary>Número con hasta 4 decimales; vacío si NaN/infinito (en CSV, celda vacía = sin dato).</summary>
        private static void Num(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return;
            Json.AppendNumber(sb, v);
        }

        private static void Csv(StringBuilder sb, string s)
        {
            if (s == null) return;
            if (s.IndexOf(',') < 0 && s.IndexOf('"') < 0 && s.IndexOf('\n') < 0) { sb.Append(s); return; }
            sb.Append('"');
            foreach (char c in s) { if (c == '"') sb.Append('"'); sb.Append(c); }
            sb.Append('"');
        }
    }
}
