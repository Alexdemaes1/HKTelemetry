using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HKTelemetry.Util
{
    /// <summary>
    /// Constructor mínimo de objetos JSON de una línea para los eventos (camino caliente).
    /// Uso: Json.Obj().S("a","x").N("b",1.5).End()
    ///
    /// Sin basura innecesaria: los constructores se reutilizan (pool por hilo) y los números se
    /// escriben dígito a dígito, sin ToString(). La única asignación por evento es el string final.
    /// Tras End() el objeto vuelve al pool: no se puede volver a usar.
    /// </summary>
    internal sealed class Json
    {
        [ThreadStatic] private static Stack<Json> _pool;

        private readonly StringBuilder _sb = new StringBuilder(512);
        private bool _first;

        private Json() { }

        public static Json Obj()
        {
            var pool = _pool ?? (_pool = new Stack<Json>());
            var j = pool.Count > 0 ? pool.Pop() : new Json();
            j._sb.Clear();
            j._sb.Append('{');
            j._first = true;
            return j;
        }

        private void Key(string k)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            AppendString(_sb, k);
            _sb.Append(':');
        }

        public Json S(string k, string v)
        {
            Key(k);
            if (v == null) _sb.Append("null"); else AppendString(_sb, v);
            return this;
        }

        public Json I(string k, long v) { Key(k); AppendLong(_sb, v); return this; }

        public Json N(string k, double v) { Key(k); AppendNumber(_sb, v); return this; }

        public Json B(string k, bool v) { Key(k); _sb.Append(v ? "true" : "false"); return this; }

        /// <summary>Inserta JSON ya construido (p. ej. otro Json.End() o un bloque cacheado).</summary>
        public Json Raw(string k, string json) { Key(k); _sb.Append(json ?? "null"); return this; }

        /// <summary>Abre un objeto anidado: "k":{ ... (cerrarlo con Close).</summary>
        public Json Obj(string k)
        {
            Key(k);
            _sb.Append('{');
            _first = true;
            return this;
        }

        /// <summary>Cierra el objeto anidado abierto con Obj(k).</summary>
        public Json Close()
        {
            _sb.Append('}');
            _first = false;
            return this;
        }

        public string End()
        {
            _sb.Append('}');
            string s = _sb.ToString();
            // No retener buffers enormes. Primero vaciar: el setter de Capacity lanza si Capacity < Length.
            if (_sb.Capacity > 16384) { _sb.Clear(); _sb.Capacity = 512; }
            (_pool ?? (_pool = new Stack<Json>())).Push(this);
            return s;
        }

        // ---------------- números sin asignaciones ----------------

        [ThreadStatic] private static char[] _digits;

        public static void AppendLong(StringBuilder sb, long v)
        {
            if (v == 0) { sb.Append('0'); return; }
            if (v == long.MinValue) { sb.Append(v.ToString(CultureInfo.InvariantCulture)); return; }
            if (v < 0) { sb.Append('-'); v = -v; }
            var buf = _digits ?? (_digits = new char[20]);
            int n = 0;
            while (v > 0) { buf[n++] = (char)('0' + (int)(v % 10)); v /= 10; }
            while (n > 0) sb.Append(buf[--n]);
        }

        /// <summary>Hasta 4 decimales, sin ceros finales. NaN/infinito → null.</summary>
        public static void AppendNumber(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
            if (Math.Abs(v) >= 9e14)
            {
                sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }
            long scaled = (long)Math.Round(v * 10000.0, MidpointRounding.AwayFromZero);
            if (scaled < 0) { sb.Append('-'); scaled = -scaled; }
            AppendLong(sb, scaled / 10000);
            int frac = (int)(scaled % 10000);
            if (frac == 0) return;
            sb.Append('.');
            int div = 1000;
            while (frac > 0)
            {
                sb.Append((char)('0' + frac / div));
                frac %= div;
                div /= 10;
            }
        }

        public static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
