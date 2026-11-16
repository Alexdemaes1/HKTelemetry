using System.Collections.Generic;
using System.Reflection;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Foto del progreso del jugador (docs/schema.md §3.1). Lectura directa de campos de PlayerData:
    /// desde nuestra DLL es un ldfld normal, no pasa por los Get*Hook de la API ni altera nada.
    /// NUNCA llamar a CountGameCompletion u otros métodos que escriben en PlayerData.
    /// </summary>
    internal static class ProgressSnapshot
    {
        private static FieldInfo[] _gotCharm;

        public static Dictionary<string, object> Take(PlayerData pd)
        {
            if (pd == null) return null;

            if (_gotCharm == null)
            {
                var f = new FieldInfo[41];
                for (int i = 1; i <= 40; i++) f[i] = typeof(PlayerData).GetField("gotCharm_" + i);
                _gotCharm = f;
            }
            var charmsGot = new List<int>();
            for (int i = 1; i <= 40; i++)
                if (_gotCharm[i] != null && (bool)_gotCharm[i].GetValue(pd)) charmsGot.Add(i);

            return new Dictionary<string, object>
            {
                ["nailDamage"] = pd.nailDamage,
                ["nailSmithUpgrades"] = pd.nailSmithUpgrades,
                ["maxHealth"] = pd.maxHealth,
                ["maxHealthBase"] = pd.maxHealthBase,
                ["heartPieces"] = pd.heartPieces,
                ["maxMP"] = pd.maxMP,
                ["MPReserveMax"] = pd.MPReserveMax,
                ["vesselFragments"] = pd.vesselFragments,
                ["fireballLevel"] = pd.fireballLevel,
                ["quakeLevel"] = pd.quakeLevel,
                ["screamLevel"] = pd.screamLevel,
                ["hasNailArt"] = pd.hasNailArt,
                ["hasCyclone"] = pd.hasCyclone,
                ["hasDashSlash"] = pd.hasDashSlash,
                ["hasUpwardSlash"] = pd.hasUpwardSlash,
                ["hasDreamNail"] = pd.hasDreamNail,
                ["hasDash"] = pd.hasDash,
                ["hasWalljump"] = pd.hasWalljump,
                ["hasSuperDash"] = pd.hasSuperDash,
                ["hasShadowDash"] = pd.hasShadowDash,
                ["hasAcidArmour"] = pd.hasAcidArmour,
                ["hasDoubleJump"] = pd.hasDoubleJump,
                ["hasKingsBrand"] = pd.hasKingsBrand,
                ["charmSlots"] = pd.charmSlots,
                ["charmsOwned"] = pd.charmsOwned,
                ["charms_got"] = charmsGot,
                ["equippedCharms"] = pd.equippedCharms != null ? new List<int>(pd.equippedCharms) : null,
                ["geo"] = pd.geo,
                ["completionPercentage"] = pd.completionPercentage,
                ["playTime"] = pd.playTime,
                ["permadeathMode"] = pd.permadeathMode,
                ["bossRushMode"] = pd.bossRushMode,
                ["mapZone"] = pd.mapZone.ToString(),
                ["respawnScene"] = pd.respawnScene,
                ["version"] = pd.version,
            };
        }
    }
}
