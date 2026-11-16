using System;
using System.Collections.Generic;

namespace HKTelemetry.Util
{
    /// <summary>
    /// Nombres de enum cacheados: Enum.ToString() boxea y usa reflexión en cada llamada, y algunos
    /// (gameState) van en cada evento. Se construye una tabla por tipo la primera vez.
    /// </summary>
    internal static class EnumNames<T> where T : struct, Enum
    {
        private static readonly Dictionary<int, string> Names = Build();

        private static Dictionary<int, string> Build()
        {
            var d = new Dictionary<int, string>();
            foreach (T v in Enum.GetValues(typeof(T)))
            {
                int k = Convert.ToInt32(v);
                if (!d.ContainsKey(k)) d[k] = v.ToString();
            }
            return d;
        }

        /// <summary>El valor se pasa ya convertido a int por el llamante: (int)gs, sin boxing.</summary>
        public static string Of(int value) => Names.TryGetValue(value, out var s) ? s : value.ToString();
    }
}
