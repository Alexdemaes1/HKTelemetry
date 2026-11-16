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
                Option("Indicador de grabación", new[] { "Visible", "Oculto" }, "Oculta el punto de grabación",
                    i => s.HideHud = i == 1, () => s.HideHud ? 1 : 0),

                Option("Frecuencia de muestreo", Array.ConvertAll(HzOptions, h => h + " Hz"), "Solo para pruebas",
                    i => s.SampleHz = HzOptions[i],
                    () => { int i = Array.IndexOf(HzOptions, s.SampleHz); return i < 0 ? HzOptions.Length - 1 : i; }),

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
