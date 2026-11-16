using System.Collections.Generic;
using HKTelemetry.Session;
using HKTelemetry.Util;
using UnityEngine;

namespace HKTelemetry.Recorders
{
    /// <summary>
    /// Entorno roto o cortado por el jugador (docs/schema.md §4.2, env_break), para el perfilado
    /// explorador / destructivo. Como mucho un evento por objeto y visita (la hierba se puede golpear
    /// muchas veces seguidas).
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

        private static void OnBreakableBreak(On.Breakable.orig_Break orig, Breakable self,
            float flingAngleMin, float flingAngleMax, float impactMultiplier)
        {
            Safe.Run("env.breakable", () => Emit("breakable", self, null, null));
            orig(self, flingAngleMin, flingAngleMax, impactMultiplier);
        }

        private static void OnPoleHit(On.BreakablePole.orig_Hit orig, BreakablePole self, HitInstance hit)
        {
            Safe.Run("env.pole", () => Emit("pole", self, hit.AttackType.ToString(), hit.Source));
            orig(self, hit);
        }

        private static void OnGrassHit(On.Grass.orig_Hit orig, Grass self, HitInstance hit)
        {
            Safe.Run("env.grass", () => Emit("grass", self, hit.AttackType.ToString(), hit.Source));
            orig(self, hit);
        }

        private static void OnGrassCut(On.GrassBehaviour.orig_CutReact orig, GrassBehaviour self, Collider2D collision)
        {
            Safe.Run("env.grass_cut", () => Emit("grass", self, null, collision != null ? collision.gameObject : null));
            orig(self, collision);
        }

        private static void OnVineCut(On.VinePlatformCut.orig_Cut orig, VinePlatformCut self)
        {
            Safe.Run("env.vine", () => Emit("vine", self, null, null));
            orig(self);
        }

        private static void Emit(string kind, Component c, string attackType, GameObject src)
        {
            if (!SessionManager.Active || c == null) return;
            if (!SeenThisVisit.Add(c.gameObject.GetInstanceID())) return;
            var pos = c.transform.position;
            SessionManager.Emit("env_break", new Json()
                .S("kind", kind)
                .S("name", Names.Normalize(c.gameObject.name))
                .N("x", pos.x).N("y", pos.y)
                .S("attack_type", attackType)
                .S("src", src != null ? src.name : null)
                .End());
        }
    }
}
