using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Índice de los campos públicos de PlayerData con lectores tipados compilados (sin boxing).
    /// Se construye una vez en segundo plano al arrancar (≈1460 campos escalares); hasta que
    /// Ready es true, el comodín pd_change no se activa.
    /// Solo LEE: nunca escribe en PlayerData.
    /// </summary>
    internal static class PdTable
    {
        public enum Kind : byte { Bool, Int, Float, Str, V3, List, Var }

        public sealed class Field
        {
            public string Name;
            public Kind Kind;
            public int Idx;      // índice dentro de su tipo
            public int Gid;      // índice global (límite por campo)
            public FieldInfo Fi;
        }

        public static volatile bool Ready;
        public static Dictionary<string, Field> ByName { get; private set; }
        public static int Count { get; private set; }

        public static string[] BoolNames, IntNames, FloatNames, StrNames, V3Names;
        public static Func<PlayerData, bool>[] BoolGet;
        public static Func<PlayerData, int>[] IntGet;
        public static Func<PlayerData, float>[] FloatGet;
        public static Func<PlayerData, string>[] StrGet;
        public static Func<PlayerData, Vector3>[] V3Get;
        public static Field[] Lists, Vars;

        /// <summary>En un hilo de fondo. Solo reflexión y compilación de expresiones (sin Unity).</summary>
        public static void Build()
        {
            var byName = new Dictionary<string, Field>();
            var b = new List<FieldInfo>(); var i = new List<FieldInfo>(); var f = new List<FieldInfo>();
            var s = new List<FieldInfo>(); var v = new List<FieldInfo>();
            var lists = new List<Field>(); var vars = new List<Field>();
            int gid = 0;

            foreach (var fi in typeof(PlayerData).GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                var t = fi.FieldType;
                Kind k;
                int idx;
                if (t == typeof(bool)) { k = Kind.Bool; idx = b.Count; b.Add(fi); }
                else if (t == typeof(int)) { k = Kind.Int; idx = i.Count; i.Add(fi); }
                else if (t == typeof(float)) { k = Kind.Float; idx = f.Count; f.Add(fi); }
                else if (t == typeof(string)) { k = Kind.Str; idx = s.Count; s.Add(fi); }
                else if (t == typeof(Vector3)) { k = Kind.V3; idx = v.Count; v.Add(fi); }
                else if (typeof(IList).IsAssignableFrom(t)) { k = Kind.List; idx = lists.Count; }
                else { k = Kind.Var; idx = vars.Count; }

                var field = new Field { Name = fi.Name, Kind = k, Idx = idx, Gid = gid++, Fi = fi };
                if (k == Kind.List) lists.Add(field);
                if (k == Kind.Var) vars.Add(field);
                byName[fi.Name] = field;
            }

            BoolNames = Names(b); IntNames = Names(i); FloatNames = Names(f); StrNames = Names(s); V3Names = Names(v);
            BoolGet = Getters<bool>(b); IntGet = Getters<int>(i); FloatGet = Getters<float>(f);
            StrGet = Getters<string>(s); V3Get = Getters<Vector3>(v);
            Lists = lists.ToArray();
            Vars = vars.ToArray();
            Count = gid;
            ByName = byName;
            Ready = true;
            Safe.Info($"[HKTelemetry] Tabla de PlayerData lista: {b.Count} bool, {i.Count} int, {f.Count} float, " +
                      $"{s.Count} string, {v.Count} Vector3, {lists.Count} listas, {vars.Count} otros");
        }

        private static string[] Names(List<FieldInfo> fs)
        {
            var r = new string[fs.Count];
            for (int k = 0; k < r.Length; k++) r[k] = fs[k].Name;
            return r;
        }

        private static Func<PlayerData, T>[] Getters<T>(List<FieldInfo> fs)
        {
            var r = new Func<PlayerData, T>[fs.Count];
            for (int k = 0; k < r.Length; k++) r[k] = Getter<T>(fs[k]);
            return r;
        }

        private static Func<PlayerData, T> Getter<T>(FieldInfo fi)
        {
            try
            {
                var p = Expression.Parameter(typeof(PlayerData), "pd");
                return Expression.Lambda<Func<PlayerData, T>>(Expression.Field(p, fi), p).Compile();
            }
            catch
            {
                // Sin compilación de expresiones: reflexión (más lento, pero funciona).
                return pd => (T)fi.GetValue(pd);
            }
        }

        /// <summary>Lista de PlayerData como cadenas comparables (List&lt;string&gt;, List&lt;int&gt;, List&lt;Vector3&gt;).</summary>
        public static List<string> ListAsStrings(object value)
        {
            var r = new List<string>();
            if (value is IList list)
                foreach (var item in list)
                    r.Add(item is Vector3 v ? FormatV3(v) : item?.ToString());
            return r;
        }

        public static string FormatV3(Vector3 v) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "[{0:0.###},{1:0.###},{2:0.###}]", v.x, v.y, v.z);
    }
}
