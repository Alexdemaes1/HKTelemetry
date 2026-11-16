using System;
using System.Collections.Generic;
using GlobalEnums;
using HKTelemetry.Session;
using HKTelemetry.Util;
using Modding;
using UnityEngine;

namespace HKTelemetry.Recorders
{
    /// <summary>
    /// Entorno roto o cortado por el jugador (docs/schema.md §4.2, env_break), para el perfilado
    /// explorador / destructivo. Solo se emite si el objeto se rompió DE VERDAD (se comprueba el estado
    /// antes y después de orig), y como mucho una vez por objeto y visita.
    /// </summary>
    internal static class EnvRecorder
    {
        private static readonly HashSet<int> SeenThisVisit = new HashSet<int>();
        private static bool _hooked;

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            On.Breakable.Break += OnBreakableBreak;
            On.BreakablePole.Hit += OnPoleHit;
            On.Grass.Hit += OnGrassHit;
            On.GrassBehaviour.CutReact += OnGrassCut;
            On.VinePlatformCut.Cut += OnVineCut;
        }

        public static void OnSceneEnter() => SeenThisVisit.Clear();

        /// <summary>Breakable.Break siempre rompe (pots, postes de madera...).</summary>
        private static void OnBreakableBreak(On.Breakable.orig_Break orig, Breakable self,
            float flingAngleMin, float flingAngleMax, float impactMultiplier)
        {
            orig(self, flingAngleMin, flingAngleMax, impactMultiplier);
            Emit("breakable", self, null, null);
        }

        /// <summary>Solo rompe con golpes horizontales (BreakablePole.cs:55); al romper se desactiva (:67).</summary>
        private static void OnPoleHit(On.BreakablePole.orig_Hit orig, BreakablePole self, HitInstance hit)
        {
            bool wasEnabled = self != null && self.enabled;
            orig(self, hit);
            if (wasEnabled && self != null && !self.enabled)
                Emit("pole", self, EnumNames<AttackTypes>.Of((int)hit.AttackType), hit.Source);
        }

        /// <summary>No corta si DamageDealt &lt;= 0 o ya estaba cortada (Grass.cs:187): se mira isCut antes y después.</summary>
        private static void OnGrassHit(On.Grass.orig_Hit orig, Grass self, HitInstance hit)
        {
            bool before = IsCut(self);
            orig(self, hit);
            if (!before && IsCut(self))
                Emit("grass", self, EnumNames<AttackTypes>.Of((int)hit.AttackType), hit.Source);
        }

        private static bool IsCut(Grass g)
        {
            try { return g != null && ReflectionHelper.GetField<Grass, bool>(g, "isCut"); }
            catch (Exception e) { Safe.Report("env.grass_state", e); return true; } // ante la duda, no emitir
        }

        /// <summary>La primera llamada siempre es un corte real (GrassBehaviour.cs:274); las repetidas las filtra la visita.</summary>
        private static void OnGrassCut(On.GrassBehaviour.orig_CutReact orig, GrassBehaviour self, Collider2D collision)
        {
            orig(self, collision);
            Emit("grass", self, null, collision != null ? collision.gameObject : null);
        }

        private static void OnVineCut(On.VinePlatformCut.orig_Cut orig, VinePlatformCut self)
        {
            orig(self);
            Emit("vine", self, null, null);
        }

        private static void Emit(string kind, Component c, string attackType, GameObject src)
        {
            long t = Safe.Enter();
            try
            {
                if (!SessionManager.Active || c == null) return;
                if (!SeenThisVisit.Add(c.gameObject.GetInstanceID())) return;
                var pos = c.transform.position;
                SessionManager.Emit("env_break", Json.Obj()
                    .S("kind", kind)
                    .S("name", Names.Normalize(c.gameObject.name))
                    .N("x", pos.x).N("y", pos.y)
                    .S("attack_type", attackType)
                    .S("src", src != null ? src.name : null)
                    .End());
            }
            catch (Exception e) { Safe.Report("env." + kind, e); }
            finally { Safe.Exit(t); }
        }
    }
}
