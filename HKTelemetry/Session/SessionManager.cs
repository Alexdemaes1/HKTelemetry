using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using HKTelemetry.Recorders;
using HKTelemetry.Util;
using HKTelemetry.Writing;
using Modding;
using Newtonsoft.Json;
using UnityEngine;

namespace HKTelemetry.Session
{
    /// <summary>
    /// Ciclo de vida de una sesión (docs/plan.md, decisión 3): desde cargar o crear una partida hasta
    /// volver al menú o cerrar el juego. Todo lo que toca Unity se hace en el hilo principal; la E/S va
    /// al TelemetryWriter.
    /// </summary>
    internal static class SessionManager
    {
        /// <summary>Uno por arranque del juego: agrupa las sesiones de una misma ejecución.</summary>
        public static readonly string LaunchId = Guid.NewGuid().ToString();

        public static string Root { get; private set; }
        public static int MainThreadId { get; private set; }

        private sealed class State
        {
            public string Id, Dir, Kind;
            public int Slot;
            public DateTime StartUtc;
            public TelemetryWriter Writer;
            public long Seq;
            public Experience Experience;
            public Dictionary<string, object> Game, Display, ProgressStart, ProgressEnd;
            public volatile string SaveSha256;
            public string EndReason;
            public DateTime? EndUtc;
            public double EndTGame, EndTReal;
            public int HookErrorsAtStart;
            public float LastSessionJson;
        }

        private static State _s;
        private static (string kind, int slot)? _pendingStart;

        public static bool Active => _s != null;
        public static string SessionId => _s?.Id;
        public static string Kind => _s?.Kind;
        public static TelemetryWriter Writer => _s?.Writer;
        public static long EventCount => _s != null ? Interlocked.Read(ref _s.Seq) : 0;

        /// <summary>Desde Initialize (hilo principal).</summary>
        public static void Init()
        {
            MainThreadId = Thread.CurrentThread.ManagedThreadId;
            Root = Path.Combine(Application.persistentDataPath, "HKTelemetry");
            Directory.CreateDirectory(Root);
            string root = Root;
            ThreadPool.QueueUserWorkItem(_ => Safe.Run("recover", () => TelemetryWriter.RecoverAll(root)));
        }

        /// <summary>Pide abrir sesión. Si no estamos en el hilo principal, se abre en el siguiente frame.</summary>
        public static void RequestStart(string kind, int slot)
        {
            if (Thread.CurrentThread.ManagedThreadId == MainThreadId) Start(kind, slot);
            else _pendingStart = (kind, slot);
        }

        private static void Start(string kind, int slot)
        {
            if (_s != null) End("restarted", appQuit: false);

            Clock.Reset();
            string id = Guid.NewGuid().ToString();
            string pid = HKTelemetry.Settings.PlayerId ?? "P00";
            foreach (char c in Path.GetInvalidFileNameChars()) pid = pid.Replace(c, '_');
            string dir = Path.Combine(Root, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{pid}_{id.Substring(0, 8)}");
            var s = new State
            {
                Id = id,
                Dir = dir,
                Kind = kind,
                Slot = slot,
                StartUtc = Clock.StartUtc,
                HookErrorsAtStart = Safe.ErrorCount,
                Writer = new TelemetryWriter(dir, new[]
                {
                    (TelemetryWriter.Events, (string)null, 500_000),
                }),
                Experience = new Experience(),
            };
            _s = s;

            var gm = GameManager.UnsafeInstance;
            s.Game = Safe.Run("session.game", GameInfo, null);
            s.Display = Safe.Run("session.display", DisplayInfo, null);
            Safe.Run("session.experience", () => s.Experience.Collect(gm));

            SceneRecorder.OnSessionStart();
            Emit("session_start", new Json().S("kind", kind).I("slot", slot).S("launch_id", LaunchId).End());
            WriteSessionJson();

            string savePath = Path.Combine(Application.persistentDataPath, slot == 0 ? "user.dat" : $"user{slot}.dat");
            s.Writer.Job(() => { if (File.Exists(savePath)) s.SaveSha256 = Sha256(savePath); });
        }

        /// <summary>Cada frame desde el Runner.</summary>
        public static void Tick()
        {
            if (_pendingStart is (string kind, int slot))
            {
                _pendingStart = null;
                Start(kind, slot);
            }
            if (_s != null && Time.realtimeSinceStartup - _s.LastSessionJson > 60f) WriteSessionJson();
        }

        /// <summary>Primer control_ready de la sesión: todo cargado (incluida la mejora de save).</summary>
        public static void TakeProgressStartIfNeeded()
        {
            if (_s == null || _s.ProgressStart != null) return;
            _s.ProgressStart = ProgressSnapshot.Take(PlayerData.instance);
            WriteSessionJson();
        }

        /// <summary>Foto final. Se llama al pedir volver al menú y otra vez justo después de guardar.</summary>
        public static void TakeProgressEnd()
        {
            if (_s == null) return;
            _s.ProgressEnd = ProgressSnapshot.Take(PlayerData.instance);
        }

        public static void End(string reason, bool appQuit)
        {
            var s = _s;
            if (s == null) return;
            if (s.ProgressEnd == null) Safe.Run("session.progress_end", TakeProgressEnd);
            Emit("session_end", new Json().S("reason", reason).End());
            s.EndReason = reason;
            s.EndUtc = DateTime.UtcNow;
            s.EndTGame = Clock.TGame;
            s.EndTReal = Clock.TReal;
            WriteSessionJson();
            _s = null;
            s.Writer.Stop(compress: true, joinMs: appQuit ? 3000 : 0);
        }

        /// <summary>
        /// Emite un evento a events.jsonl con los campos comunes (docs/schema.md §4).
        /// <paramref name="payload"/> es un objeto JSON ya construido (Json.End()).
        /// </summary>
        public static void Emit(string type, string payload)
        {
            var s = _s;
            if (s == null) return;
            var gm = GameManager.UnsafeInstance;
            bool hasGm = gm != null;
            long seq = Interlocked.Increment(ref s.Seq) - 1;
            string line = new Json()
                .I("seq", seq)
                .N("t_game", Clock.TGame)
                .N("t_real", Clock.TReal)
                .I("frame", Clock.Frame)
                .N("play_time", hasGm ? gm.PlayTime : double.NaN)
                .S("scene", SceneRecorder.CurrentScene)
                .S("gs", hasGm ? gm.gameState.ToString() : null)
                .S("type", type)
                .Raw("p", payload ?? "{}")
                .End();
            s.Writer.Enqueue(TelemetryWriter.Events, line);
        }

        /// <summary>Construye session.json en el hilo principal y lo escribe de forma atómica en el writer.</summary>
        public static void WriteSessionJson()
        {
            var s = _s;
            if (s == null) return;
            s.LastSessionJson = Time.realtimeSinceStartup;
            Safe.Run("session.json", () =>
            {
                var w = s.Writer;
                var doc = new Dictionary<string, object>
                {
                    ["schema_version"] = HKTelemetry.SchemaVersion,
                    ["mod_version"] = HKTelemetry.Instance?.GetVersion(),
                    ["session_id"] = s.Id,
                    ["launch_id"] = LaunchId,
                    ["player_id"] = HKTelemetry.Settings.PlayerId,
                    ["experience"] = s.Experience.Snapshot(),
                    ["game"] = s.Game,
                    ["save"] = new Dictionary<string, object>
                    {
                        ["slot"] = s.Slot,
                        ["file_sha256"] = s.SaveSha256,
                        ["start_kind"] = s.Kind,
                    },
                    ["display"] = s.Display,
                    ["settings"] = new Dictionary<string, object>
                    {
                        ["sample_hz"] = HKTelemetry.Settings.SampleHz,
                        ["hide_hud"] = HKTelemetry.Settings.HideHud,
                        ["debug"] = HKTelemetry.Settings.Debug,
                    },
                    ["start_utc"] = s.StartUtc.ToString("o"),
                    ["end"] = s.EndUtc.HasValue
                        ? new Dictionary<string, object>
                        {
                            ["utc"] = s.EndUtc.Value.ToString("o"),
                            ["reason"] = s.EndReason,
                            ["t_game"] = s.EndTGame,
                            ["t_real"] = s.EndTReal,
                        }
                        : null,
                    ["progress_start"] = s.ProgressStart,
                    ["progress_end"] = s.ProgressEnd,
                    ["counters"] = new Dictionary<string, object>
                    {
                        ["events"] = Interlocked.Read(ref s.Seq),
                        ["dropped_events"] = w.Dropped(TelemetryWriter.Events),
                        ["write_errors"] = w.WriteErrors,
                        ["hook_errors"] = Safe.ErrorCount - s.HookErrorsAtStart,
                    },
                };
                string text = JsonConvert.SerializeObject(doc, Formatting.Indented);
                string path = Path.Combine(s.Dir, "session.json");
                w.Job(() => TelemetryWriter.WriteAtomic(path, text));
            });
        }

        private static Dictionary<string, object> GameInfo()
        {
            var bm = BuildMetadata.Embedded;
            var pd = PlayerData.instance;
            return new Dictionary<string, object>
            {
                ["api_version"] = ModHooks.ModVersion,
                ["unity_version"] = Application.unityVersion,
                ["build_branch"] = bm?.BranchName,
                ["build_revision"] = bm?.Revision,
                ["save_version"] = pd?.version,
                ["platform"] = Application.platform.ToString(),
            };
        }

        private static Dictionary<string, object> DisplayInfo() => new Dictionary<string, object>
        {
            ["width"] = Screen.width,
            ["height"] = Screen.height,
            ["fullscreen_mode"] = Screen.fullScreenMode.ToString(),
            ["refresh_rate"] = Screen.currentResolution.refreshRate,
            ["vsync"] = QualitySettings.vSyncCount,
            ["target_fps"] = Application.targetFrameRate,
        };

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }
    }
}
