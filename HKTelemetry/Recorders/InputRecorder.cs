using System;
using System.Text;
using GlobalEnums;
using HKTelemetry.Session;
using HKTelemetry.Util;
using HKTelemetry.Writing;
using InControl;

namespace HKTelemetry.Recorders
{
    /// <summary>Fila de inputs.csv (docs/schema.md §6). Solo números y strings constantes.</summary>
    internal struct InputRow
    {
        public long Seq;
        public double TGame, TReal;
        public int Frame;
        public string Action;
        public byte Ev;          // 0 down, 1 up, 2 axis
        public float X, Y;       // solo axis
        public byte Device;      // 0 none, 1 kb, 2 pad, 3 unknown
        public bool Accepting;
    }

    /// <summary>
    /// Botones del jugador (fase 5): sondeo cada frame de InputHandler.Instance.inputActions, sin hooks.
    /// - Detección de flanco propia sobre IsPressed (WasPressed dura un solo tick de InControl).
    /// - La referencia a inputActions se relee cada frame: InputHandler.Start la recrea (api-notes/hero_input.md H5).
    /// - El eje de movimiento se registra cuando cambia más de 0,2 o vuelve a / sale de cero.
    /// - Cambio de dispositivo (teclado/mando) como evento `device` en events.jsonl.
    /// - Expone la máscara «pulsado en algún momento desde la última muestra» para frames.csv.
    /// Sin asignaciones por frame: las filas son structs que se copian a un búfer circular.
    /// </summary>
    internal static class InputRecorder
    {
        public const string Header = "seq,t_game,t_real,frame,action,ev,x,y,device,accepting";

        // Orden = bits de input_bitmask (schema.md §6.1). No cambiar sin subir schema_version.
        private static readonly string[] ActionNames =
        {
            "left", "right", "up", "down", "jump", "attack", "dash", "superDash",
            "cast", "quickCast", "focus", "dreamNail", "quickMap", "openInventory", "pause", "evade",
        };

        private static readonly string[] EvNames = { "down", "up", "axis" };
        private static readonly string[] DeviceNames = { "none", "kb", "pad", "unknown" };

        private const float AxisStep = 0.2f;

        private static long _seq;
        private static int _prevMask;
        private static int _intervalMask;
        private static float _axX, _axY;          // último eje registrado
        private static int _device = -1;          // -1 = aún no registrado en esta sesión
        private static bool _haveBaseline;

        /// <summary>Eje de movimiento actual (frames.csv).</summary>
        public static float MoveX { get; private set; }
        public static float MoveY { get; private set; }

        /// <summary>Dispositivo actual (0 none, 1 kb, 2 pad, 3 unknown) para el HUD y frames.</summary>
        public static int Device => _device < 0 ? 0 : _device;

        public static RowRing<InputRow> CreateSink() =>
            new RowRing<InputRow>(TelemetryWriter.Inputs, Header, 8192, Format);

        public static void OnSessionStart()
        {
            _seq = 0;
            _prevMask = 0;      // los botones ya pulsados al empezar salen como «down»: pares down/up completos
            _intervalMask = 0;
            _axX = _axY = 0f;
            _device = -1;
            _haveBaseline = true;
        }

        /// <summary>OR de las acciones pulsadas desde la última llamada (la llama FrameRecorder al muestrear).</summary>
        public static int TakeIntervalMask()
        {
            int m = _intervalMask;
            // A cero: un botón que sigue pulsado vuelve a entrar con el Poll del siguiente frame, que siempre
            // corre antes de FrameRecorder.Poll (orden del Runner). Arrastrar _prevMask lo alargaría un frame.
            _intervalMask = 0;
            return m;
        }

        /// <summary>Cada frame desde el Runner.</summary>
        public static void Poll(GameManager gm, HeroController hc)
        {
            var ih = InputHandler.Instance;
            if (ih == null) return;
            var a = ih.inputActions;
            if (a == null) return;

            int mask = 0;
            if (a.left != null && a.left.IsPressed) mask |= 1 << 0;
            if (a.right != null && a.right.IsPressed) mask |= 1 << 1;
            if (a.up != null && a.up.IsPressed) mask |= 1 << 2;
            if (a.down != null && a.down.IsPressed) mask |= 1 << 3;
            if (a.jump != null && a.jump.IsPressed) mask |= 1 << 4;
            if (a.attack != null && a.attack.IsPressed) mask |= 1 << 5;
            if (a.dash != null && a.dash.IsPressed) mask |= 1 << 6;
            if (a.superDash != null && a.superDash.IsPressed) mask |= 1 << 7;
            if (a.cast != null && a.cast.IsPressed) mask |= 1 << 8;
            if (a.quickCast != null && a.quickCast.IsPressed) mask |= 1 << 9;
            if (a.focus != null && a.focus.IsPressed) mask |= 1 << 10;
            if (a.dreamNail != null && a.dreamNail.IsPressed) mask |= 1 << 11;
            if (a.quickMap != null && a.quickMap.IsPressed) mask |= 1 << 12;
            if (a.openInventory != null && a.openInventory.IsPressed) mask |= 1 << 13;
            if (a.pause != null && a.pause.IsPressed) mask |= 1 << 14;
            if (a.evade != null && a.evade.IsPressed) mask |= 1 << 15;

            float mx = 0f, my = 0f;
            var mv = a.moveVector;
            if (mv != null) { mx = mv.X; my = mv.Y; }
            MoveX = mx;
            MoveY = my;

            int device = DeviceOf(a.LastInputType);

            if (!SessionManager.Active || !_haveBaseline)
            {
                _prevMask = mask;
                return;
            }

            _intervalMask |= mask;

            if (device != _device)
            {
                string from = _device < 0 ? null : DeviceNames[_device];
                _device = device;
                SessionManager.Emit("device", Json.Obj()
                    .S("from", from)
                    .S("to", DeviceNames[device])
                    .S("gamepad", EnumNames<GamepadType>.Of((int)ih.activeGamepadType))
                    .End());
            }

            int changed = mask ^ _prevMask;
            bool axisMoved = Math.Abs(mx - _axX) > AxisStep || Math.Abs(my - _axY) > AxisStep
                             || ((mx == 0f && my == 0f) != (_axX == 0f && _axY == 0f));
            if (changed == 0 && !axisMoved) return;

            var ring = SessionManager.InputSink;
            if (ring == null) { _prevMask = mask; return; }

            bool accepting = hc != null && hc.acceptingInput && !hc.controlReqlinquished && gm != null && !gm.isPaused;
            var row = new InputRow
            {
                TGame = Clock.TGame,
                TReal = Clock.TReal,
                Frame = Clock.Frame,
                Device = (byte)device,
                Accepting = accepting,
            };

            for (int bit = 0; changed != 0; bit++, changed >>= 1)
            {
                if ((changed & 1) == 0) continue;
                row.Seq = _seq++;
                row.Action = ActionNames[bit];
                row.Ev = (byte)(((mask >> bit) & 1) != 0 ? 0 : 1);
                row.X = row.Y = 0f;
                ring.TryWrite(ref row);
            }
            if (axisMoved)
            {
                row.Seq = _seq++;
                row.Action = "move";
                row.Ev = 2;
                row.X = mx;
                row.Y = my;
                ring.TryWrite(ref row);
                _axX = mx;
                _axY = my;
            }
            _prevMask = mask;
        }

        private static int DeviceOf(BindingSourceType t)
        {
            switch (t)
            {
                case BindingSourceType.None: return 0;
                case BindingSourceType.KeyBindingSource:
                case BindingSourceType.MouseBindingSource: return 1;
                case BindingSourceType.DeviceBindingSource: return 2;
                default: return 3;
            }
        }

        public static long Produced => _seq;

        // ---------------- formateo (hilo de escritura) ----------------

        private static void Format(ref InputRow r, StringBuilder sb)
        {
            Json.AppendLong(sb, r.Seq); sb.Append(',');
            Json.AppendNumber(sb, r.TGame); sb.Append(',');
            Json.AppendNumber(sb, r.TReal); sb.Append(',');
            Json.AppendLong(sb, r.Frame); sb.Append(',');
            sb.Append(r.Action).Append(',');
            sb.Append(EvNames[r.Ev]).Append(',');
            if (r.Ev == 2) { Json.AppendNumber(sb, r.X); sb.Append(','); Json.AppendNumber(sb, r.Y); }
            else sb.Append(',');
            sb.Append(',');
            sb.Append(DeviceNames[r.Device]).Append(',');
            sb.Append(r.Accepting ? '1' : '0');
        }
    }
}
