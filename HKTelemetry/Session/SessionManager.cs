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
            public RowRing<Recorders.FrameRow> Frames;
            public RowRing<Recorders.InputRow> Inputs;
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
        /// <summary>Carpeta de la sesión abierta (null si no hay). La lee el hilo de envío.</summary>
        public static volatile string CurrentDir;
        public static RowRing<Recorders.FrameRow> FrameSink => _s?.Frames;
        public static RowRing<Recorders.InputRow> InputSink => _s?.Inputs;
        /// <summary>Tipo del último evento emitido (overlay de debug).</summary>
        public static volatile string LastType;

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
            string pid = Identity.PlayerId ?? "P00";
            foreach (char c in Path.GetInvalidFileNameChars()) pid = pid.Replace(c, '_');
            string dir = Path.Combine(Root, $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{pid}_{id.Substring(0, 8)}");
            // Búferes nuevos por sesión: el escritor de la sesión anterior puede seguir vaciando los suyos.
            var frames = Recorders.FrameRecorder.CreateSink();
            var inputs = Recorders.InputRecorder.CreateSink();
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
                }, new RowSink[] { frames, inputs }),
                Frames = frames,
                Inputs = inputs,
            };
            s.Writer.OnFinished = Uploader.Poke; // enviar la sesión en cuanto esté cerrada y comprimida
            _s = s;
            CurrentDir = dir;
            Uploader.Cancel(); // si estaba subiendo algo, se corta: empieza a jugar

            var gm = GameManager.UnsafeInstance;
            s.Game = Safe.Run("session.game", GameInfo, null);
            s.Display = Safe.Run("session.display", DisplayInfo, null);
            // Una vez por arranque: leer y descifrar los saves bloquea el hilo principal (DesktopPlatform.cs:183).
            s.Experience = Safe.Run("session.experience", () => Experience.ForLaunch(gm), null) ?? new Experience();

            SceneRecorder.OnSessionStart();
            CombatRecorder.OnSessionStart();
            PlayerDataRecorder.OnSessionStart();
            MapRecorder.OnSessionStart();
            InputRecorder.OnSessionStart();
            FrameRecorder.OnSessionStart();
            Emit("session_start", Json.Obj().S("kind", kind).I("slot", slot).S("launch_id", LaunchId).End());
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
            Safe.Run("session.pd_end", PlayerDataRecorder.DumpEnd);
        }

        public static void End(string reason, bool appQuit)
        {
            var s = _s;
            if (s == null) return;
            // La foto final se toma tras el guardado de «Guardar y salir». En cualquier otro final
            // (cierre del juego, Steel Soul, nueva sesión) se vuelve a tomar: una salida cancelada
            // podría haber dejado una foto antigua. En el menú de emergencia se conserva la que haya,
            // porque QuitToMenu puede haber reiniciado ya PlayerData.
            bool retake = reason == "app_quit" || reason == "permadeath" || reason == "restarted";
            if (s.ProgressEnd == null || retake) Safe.Run("session.progress_end", TakeProgressEnd);
            Safe.Run("session.map_close", () => Recorders.MapRecorder.ForceClose("session_end"));
            Emit("session_end", Json.Obj().S("reason", reason).End());
            s.EndReason = reason;
            s.EndUtc = DateTime.UtcNow;
            s.EndTGame = Clock.TGame;
            s.EndTReal = Clock.TReal;
            WriteSessionJson();
            _s = null;
            CurrentDir = null;
            s.Writer.Stop(compress: true, joinMs: appQuit ? 3000 : 0);
            Uploader.Poke(); // otras sesiones pendientes; esta avisa ella misma al terminar (OnFinished)
        }

        /// <summary>
        /// Emite un evento a events.jsonl con los campos comunes (docs/schema.md §4).
        /// <paramref name="payload"/> es un objeto JSON ya construido (Json.End()).
        /// </summary>
        public static void Emit(string type, string payload)
        {
            var j = BeginLine(type);
            if (j == null) return;
            EndLine(j.Raw("p", payload ?? "{}"));
        }

        /// <summary>
        /// Variante sin cadena intermedia para eventos frecuentes: abre el sobre y el objeto "p" y
        /// devuelve el constructor para añadir los campos; cerrar con EmitEnd. Devuelve null sin sesión.
        /// Uso: var j = SessionManager.EmitBegin("x"); if (j != null) SessionManager.EmitEnd(j.I("a", 1));
        /// IMPORTANTE: calcular todos los valores ANTES de EmitBegin (el seq ya está asignado: si algo
        /// lanzara entre medias quedaría un hueco en seq).
        /// </summary>
        public static Json EmitBegin(string type)
        {
            var j = BeginLine(type);
            return j?.Obj("p");
        }

        public static void EmitEnd(Json j) => EndLine(j.Close());

        private static Json BeginLine(string type)
        {
            var s = _s;
            if (s == null) return null;
            LastType = type;
            var gm = GameManager.UnsafeInstance;
            bool hasGm = gm != null;
            long seq = Interlocked.Increment(ref s.Seq) - 1;
            return Json.Obj()
                .I("seq", seq)
                .N("t_game", Clock.TGame)
                .N("t_real", Clock.TReal)
                .I("frame", Clock.Frame)
                .N("play_time", hasGm ? gm.PlayTime : double.NaN)
                .S("scene", SceneRecorder.CurrentScene)
                .S("gs", hasGm ? EnumNames<GlobalEnums.GameState>.Of((int)gm.gameState) : null)
                .S("type", type);
        }

        private static void EndLine(Json j)
        {
            string line = j.End();
            var s = _s;
            if (s != null) s.Writer.Enqueue(TelemetryWriter.Events, line);
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
                    ["player_id"] = Identity.PlayerId,
                    ["identity"] = Identity.Snapshot(),
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
                        ["sample_hz"] = FrameRecorder.EffectiveHz, // fijada al empezar la sesión
                        ["auto_upload"] = Uploader.Enabled,
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
                        // dropped_*, frames, inputs y write_errors se rellenan en el hilo de escritura (abajo).
                        ["hook_errors"] = Safe.ErrorCount - s.HookErrorsAtStart,
                    },
                };
                var counters = (Dictionary<string, object>)doc["counters"];
                foreach (var kv in PlayerDataRecorder.Counters()) counters[kv.Key] = kv.Value;
                string path = Path.Combine(s.Dir, "session.json");
                long framesProduced = FrameRecorder.Produced, inputsProduced = InputRecorder.Produced;
                // La serialización (decenas de KB de basura) va al hilo de escritura: el diccionario
                // solo contiene copias y a partir de aquí solo lo toca ese hilo. Los contadores de
                // pérdidas se leen allí, en el momento de escribir. Si después del último session.json hay
                // más errores o descartes (volcado final, cierre, compresión), el escritor lo reescribe
                // (TelemetryWriter.StatusJob).
                w.StatusJob(() =>
                {
                    counters["dropped_events"] = w.Dropped(TelemetryWriter.Events);
                    counters["dropped_frames"] = w.Dropped(TelemetryWriter.Frames);
                    counters["dropped_inputs"] = w.Dropped(TelemetryWriter.Inputs);
                    // Filas que no se perdieron (seq las numera todas: cada hueco es una descartada).
                    counters["frames"] = framesProduced - w.Dropped(TelemetryWriter.Frames);
                    counters["inputs"] = inputsProduced - w.Dropped(TelemetryWriter.Inputs);
                    counters["write_errors"] = w.WriteErrors;
                    TelemetryWriter.WriteAtomic(path, JsonConvert.SerializeObject(doc, Formatting.Indented));
                });
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
                ["other_mods"] = OtherMods(),
            };
        }

        /// <summary>
        /// Otros mods ("Nombre versión"): pueden cambiar la jugabilidad, así que el análisis tiene que poder
        /// filtrar esas sesiones. Marcas: " [off]" desactivado en el menú, " [error]" cargado con error.
        /// Los que fallaron al construirse no tienen instancia ni nombre: van como "[error] x N".
        /// </summary>
        private static List<string> OtherMods()
        {
            var list = new List<string>();
            var enabled = new HashSet<IMod>();
            var ok = new HashSet<IMod>();
            foreach (var m in ModHooks.GetAllMods(onlyEnabled: true, allowLoadError: false)) if (m != null) enabled.Add(m);
            foreach (var m in ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: false)) if (m != null) ok.Add(m);
            int unnamed = 0;
            foreach (var m in ModHooks.GetAllMods(onlyEnabled: false, allowLoadError: true))
            {
                if (m == null) { unnamed++; continue; }
                if (m is HKTelemetry) continue;
                string name = null, version = null;
                try { name = m.GetName(); } catch { }
                try { version = m.GetVersion(); } catch { }
                string tag = !ok.Contains(m) ? " [error]" : !enabled.Contains(m) ? " [off]" : "";
                list.Add((name ?? m.GetType().Name) + (version != null ? " " + version : "") + tag);
            }
            list.Sort(StringComparer.Ordinal);
            if (unnamed > 0) list.Add("[error] x " + unnamed);
            return list;
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
