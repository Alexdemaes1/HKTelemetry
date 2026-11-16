using System;
using System.Collections.Generic;
using Modding;

namespace HKTelemetry
{
    /// <summary>
    /// Menú de ajustes (IMenuMod). Además de servir para cambiar opciones, es lo que hace que el mod
    /// aparezca en la lista "Mods": esa lista solo incluye mods con menú o que se pueden desactivar.
    /// El mod NO es desactivable a propósito, para que nadie corte una grabación a medias.
    /// </summary>
    public partial class HKTelemetry : IMenuMod
    {
        public bool ToggleButtonInsideMenu => false;

        private static readonly int[] HzOptions = { 10, 20, 30, 60 };

        public List<IMenuMod.MenuEntry> GetMenuData(IMenuMod.MenuEntry? toggleButtonEntry)
        {
            var s = Settings;
            return new List<IMenuMod.MenuEntry>
            {
                // player_id: solo lectura (no hay campo de texto; viene en el kit).
                new IMenuMod.MenuEntry("Jugador", new[] { s.PlayerId }, "ID del participante (viene en el kit)",
                    _ => { }, () => 0),

                Option("Indicador de grabación", new[] { "Visible", "Oculto" }, "Oculta el punto de grabación",
                    i => s.HideHud = i == 1, () => s.HideHud ? 1 : 0),

                Option("Horas jugadas", new[] { "0", "Menos de 10", "10-50", "50-200", "Más de 200" },
                    "Horas totales en Hollow Knight antes del estudio",
                    i => s.HoursBucket = i, () => s.HoursBucket),

                Option("¿Te lo has pasado?", new[] { "No", "Un final", "106% / 112%", "Steel Soul" }, null,
                    i => s.Completed = i, () => s.Completed),

                Option("Experiencia en metroidvanias", new[] { "Ninguna", "Alguna", "Mucha" }, null,
                    i => s.MetroidvaniaExp = i, () => s.MetroidvaniaExp),

                Option("Control preferido", new[] { "Teclado", "Mando" }, null,
                    i => s.InputPref = i, () => s.InputPref),

                Option("Frecuencia de muestreo", Array.ConvertAll(HzOptions, h => h + " Hz"), "Solo para pruebas",
                    i => s.SampleHz = HzOptions[i], () => Math.Max(0, Array.IndexOf(HzOptions, s.SampleHz))),

                Option("Modo debug", new[] { "No", "Sí" }, "Muestra contadores en pantalla",
                    i => s.Debug = i == 1, () => s.Debug ? 1 : 0),
            };
        }

        /// <summary>Entrada de menú cuyo cambio se guarda al momento (si no, solo se guarda al salir del juego).</summary>
        private IMenuMod.MenuEntry Option(string name, string[] values, string description, Action<int> set, Func<int> get) =>
            new IMenuMod.MenuEntry(name, values, description, i =>
            {
                try { set(i); SaveGlobalSettings(); }
                catch (Exception e) { LogError($"Error guardando ajuste '{name}': {e}"); }
            }, get);
    }
}
