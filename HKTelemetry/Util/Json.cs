using System;
using System.Globalization;
using System.Text;

namespace HKTelemetry.Util
{
    /// <summary>
    /// Constructor mínimo de objetos JSON de una línea, sin reflexión ni dependencias, para los
    /// eventos (camino caliente). Uso: new Json().S("a","x").N("b",1.5).End()
    /// </summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder(128);
        private bool _first = true;

        public Json() { _sb.Append('{'); }

        private Json Key(string k)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            AppendString(_sb, k);
            _sb.Append(':');
            return this;
        }

        public Json S(string k, string v)
        {
            Key(k);
            if (v == null) _sb.Append("null"); else AppendString(_sb, v);
            return this;
        }

        public Json I(string k, long v) { Key(k); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

        public Json N(string k, double v) { Key(k); AppendNumber(_sb, v); return this; }

        public Json B(string k, bool v) { Key(k); _sb.Append(v ? "true" : "false"); return this; }

        /// <summary>Inserta JSON ya construido (p. ej. otro objeto Json.End()).</summary>
        public Json Raw(string k, string json) { Key(k); _sb.Append(json ?? "null"); return this; }

        public string End() { _sb.Append('}'); return _sb.ToString(); }

        public static void AppendNumber(StringBuilder sb, double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { sb.Append("null"); return; }
            sb.Append(v.ToString("0.####", CultureInfo.InvariantCulture));
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
