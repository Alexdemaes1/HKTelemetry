using System.Text;
using System.Text.RegularExpressions;
using Modding;
using UnityEngine;

namespace HKTelemetry.Util
{
    /// <summary>Normalización de nombres e identificación estable de enemigos (docs/schema.md §4.3).</summary>
    internal static class Names
    {
        private static readonly Regex CloneSuffix = new Regex(@"(\s*\(Clone\))+|\s*\(\d+\)$", RegexOptions.Compiled);

        /// <summary>"Crawler (3)(Clone)" → "Crawler".</summary>
        public static string Normalize(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            string prev;
            do { prev = name; name = CloneSuffix.Replace(name, "").Trim(); } while (name != prev);
            return name;
        }

        /// <summary>Ruta jerárquica dentro de la escena: "Battle Scene/Wave 1/Zombie Runner".</summary>
        public static string HierarchyPath(Transform t)
        {
            var sb = new StringBuilder(t.name);
            for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
            return sb.ToString();
        }

        /// <summary>
        /// Bloque "enemy" en JSON: type (nombre del journal o nombre normalizado), name, key estable, iid.
        /// Usa reflexión una sola vez por enemigo: el resultado se guarda en caché (CombatRecorder).
        /// </summary>
        public static string EnemyJson(HealthManager hm, string scene)
        {
            var go = hm.gameObject;
            string journal = null;
            var ede = go.GetComponent<EnemyDeathEffects>();
            if (ede != null) journal = ReflectionHelper.GetField<EnemyDeathEffects, string>(ede, "playerDataName");

            string key;
            var pbi = go.GetComponent<PersistentBoolItem>();
            if (pbi != null && pbi.persistentBoolData != null && !string.IsNullOrEmpty(pbi.persistentBoolData.id))
                key = (pbi.persistentBoolData.sceneName ?? scene) + "/" + pbi.persistentBoolData.id;
            else
                key = scene + "/" + HierarchyPath(go.transform);

            return Json.Obj()
                .S("type", string.IsNullOrEmpty(journal) ? Normalize(go.name) : journal)
                .S("name", go.name)
                .S("key", key)
                .I("iid", go.GetInstanceID())
                .End();
        }
    }
}
