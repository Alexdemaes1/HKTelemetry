using System;
using System.Text;
using GlobalEnums;
using HKTelemetry.Session;
using HKTelemetry.Util;
using HKTelemetry.Writing;
using Modding;
using UnityEngine;
using UnityEngine.UI;
using USM = UnityEngine.SceneManagement.SceneManager;
using UScene = UnityEngine.SceneManagement.Scene;

namespace HKTelemetry.Hud
{
    /// <summary>
    /// HUD del mod (docs/BRIEF.md §6, fase 7). Sin hooks del juego: un canvas propio superpuesto que
    /// nunca captura input.
    /// - Punto de grabación arriba a la derecha: verde con parpadeo suave = grabando; gris = menú/pausa;
    ///   rojo + mensaje = error de escritura, datos descartados o versión no soportada.
    ///   «Ocultar indicador» oculta verde y gris, pero el rojo se muestra siempre (ningún dato perdido en silencio).
    /// - Línea en la pantalla de título: versión, jugador y comprobación de versión del juego.
    /// - Overlay de debug: eventos, bytes, último evento, fps y coste del mod.
    /// Coste: el texto se actualiza como mucho 4 veces por segundo; el parpadeo no reconstruye mallas.
    /// </summary>
    internal sealed class Hud : MonoBehaviour
    {
        private enum Dot { None, Green, Grey, Red }

        private static readonly Color Green = new Color(0.30f, 0.90f, 0.40f, 1f);
        private static readonly Color Grey = new Color(0.60f, 0.60f, 0.60f, 0.85f);
        private static readonly Color Red = new Color(0.95f, 0.25f, 0.20f, 1f);

        public static bool Unsupported { get; private set; }

        /// <summary>Puesto por HKTelemetry.Initialize si falla algún paso del arranque: punto rojo.</summary>
        public static volatile string InitError;

        private Image _dot;
        private Text _msg, _debug, _title;
        private Dot _state = Dot.None;
        private float _nextText, _nextFont, _nextUi;
        private int _fontTries;
        private string _activeScene;
        private UIManager _ui;
        private readonly StringBuilder _sb = new StringBuilder(512);
        private long _lastEvents;
        private float _lastEventsTime;
        private float _eventsPerSec;
        private float _nextDebug;
        private string _titleLine, _titlePid;
        private string _sessionId, _sessionShort;
        private int _errCode = -1;          // qué error hay mostrado (para no rehacer el mensaje)
        private long _errValue = -1;

        public static Hud Create(bool unsupported)
        {
            Unsupported = unsupported;
            var go = new GameObject("HKTelemetry.Hud");
            DontDestroyOnLoad(go);
            var hud = go.AddComponent<Hud>();
            hud.Build();
            return hud;
        }

        private void Build()
        {
            var canvasGo = CanvasUtil.CreateCanvas(RenderMode.ScreenSpaceOverlay, new Vector2(1920, 1080));
            canvasGo.name = "HKTelemetry.HudCanvas";
            DontDestroyOnLoad(canvasGo);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.sortingOrder = 10000;
            var cg = canvasGo.GetComponent<CanvasGroup>();
            if (cg != null) { cg.interactable = false; cg.blocksRaycasts = false; }
            var raycaster = canvasGo.GetComponent<GraphicRaycaster>();
            if (raycaster != null) Destroy(raycaster);

            Font arial = Resources.GetBuiltinResource<Font>("Arial.ttf");

            // Punto: arriba a la derecha.
            var dotGo = CanvasUtil.CreateImagePanel(canvasGo, CircleSprite(),
                new CanvasUtil.RectData(new Vector2(18, 18), new Vector2(-28, -28), Vector2.one, Vector2.one, Vector2.one));
            _dot = dotGo.GetComponent<Image>();
            _dot.raycastTarget = false;
            // Canvas propio para el punto: su parpadeo por frame no obliga a re-agrupar los textos.
            dotGo.AddComponent<Canvas>();

            // Mensaje corto a la izquierda del punto (solo en rojo).
            _msg = MakeText(canvasGo, arial, 18, TextAnchor.MiddleRight,
                new CanvasUtil.RectData(new Vector2(900, 30), new Vector2(-52, -19), Vector2.one, Vector2.one, new Vector2(1f, 0.5f)));
            _msg.color = Red;

            // Overlay de debug debajo del punto (la consola de la API ocupa abajo a la izquierda).
            Font mono = Font.CreateDynamicFontFromOSFont(new[] { "Consolas", "Courier New" }, 16) ?? arial;
            _debug = MakeText(canvasGo, mono, 16, TextAnchor.UpperRight,
                new CanvasUtil.RectData(new Vector2(560, 320), new Vector2(-20, -50), Vector2.one, Vector2.one, Vector2.one));

            // Línea de la pantalla de título: abajo, centrada.
            _title = MakeText(canvasGo, arial, 20, TextAnchor.LowerCenter,
                new CanvasUtil.RectData(new Vector2(1600, 40), new Vector2(0, 18), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f)));

            _activeScene = USM.GetActiveScene().name;
            USM.activeSceneChanged += OnActiveSceneChanged;
            Apply(Dot.Grey);
        }

        private static Text MakeText(GameObject parent, Font font, int size, TextAnchor anchor, CanvasUtil.RectData rd)
        {
            var go = CanvasUtil.CreateTextPanel(parent, "", size, anchor, rd, font);
            var t = go.GetComponent<Text>();
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = Color.white;
            var outline = go.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.8f);
            go.SetActive(false);
            return t;
        }

        /// <summary>Círculo blanco con borde suavizado (se tiñe con Image.color).</summary>
        private static Sprite CircleSprite()
        {
            const int n = 32;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false);
            var px = new Color32[n * n];
            float r = n / 2f - 1f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x + 0.5f - n / 2f, dy = y + 0.5f - n / 2f;
                    float a = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(a * 255));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f));
        }

        private void OnActiveSceneChanged(UScene from, UScene to) => _activeScene = to.name;

        private void OnDestroy() => USM.activeSceneChanged -= OnActiveSceneChanged;

        private void Update()
        {
            long t = Safe.Enter();
            try { Tick(); }
            catch (Exception e) { Safe.Report("hud", e); }
            finally { Safe.Exit(t); }
        }

        private void Tick()
        {
            var settings = HKTelemetry.Settings; // no se cachea: OnLoadGlobal lo sustituye
            var w = SessionManager.Writer;
            var gm = GameManager.UnsafeInstance;

            // --- estado del punto, solo con enteros (sin crear strings cada frame) ---
            long writeErrors = w != null ? w.WriteErrors : 0;
            long dropped = w != null ? w.TotalDropped() : 0;
            int errCode = Unsupported ? 1 : InitError != null ? 2 : writeErrors > 0 ? 3 : dropped > 0 ? 4 : 0;

            Dot state;
            if (errCode != 0) state = Dot.Red;
            else if (SessionManager.Active && gm != null && !gm.isPaused) state = Dot.Green;
            else state = Dot.Grey;
            if (state != Dot.Red && settings.HideHud) state = Dot.None;
            if (state != _state) Apply(state);
            if (state == Dot.Green)
                _dot.canvasRenderer.SetAlpha(0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * Mathf.PI));

            float now = Time.unscaledTime;
            if (now < _nextText) return;
            _nextText = now + 0.25f;

            // --- mensaje de error: se rehace solo si cambia el error ---
            SetActive(_msg, errCode != 0);
            long errValue = errCode == 3 ? writeErrors : errCode == 4 ? dropped : 0;
            if (errCode != 0 && (errCode != _errCode || errValue != _errValue))
            {
                _msg.text = errCode == 1 ? "Versión del juego no soportada: no se graba"
                          : errCode == 2 ? InitError
                          : errCode == 3 ? "Error de escritura: " + w?.LastError
                          : "Datos descartados: " + dropped;
            }
            _errCode = errCode;
            _errValue = errValue;

            // --- línea del título: se construye una vez (y si cambia el jugador) ---
            bool onTitle = _activeScene == "Menu_Title" && UiState() == UIState.MAIN_MENU_HOME;
            SetActive(_title, onTitle);
            if (onTitle)
            {
                RetryTrajan(now);
                if (_titleLine == null || !ReferenceEquals(_titlePid, settings.PlayerId))
                {
                    _titlePid = settings.PlayerId;
                    _titleLine = $"HKTelemetry v{HKTelemetry.VersionString}  -  grabando como {settings.PlayerId}  -  " +
                                 $"juego 1.5.78 {(Unsupported ? "NO SOPORTADO" : "OK")}";
                    _title.text = _titleLine;
                }
            }

            // --- overlay de debug (2 Hz) ---
            SetActive(_debug, settings.Debug);
            if (settings.Debug && now >= _nextDebug)
            {
                _nextDebug = now + 0.5f;
                _debug.text = DebugText(w, now);
            }
        }

        private void Apply(Dot state)
        {
            _state = state;
            _dot.gameObject.SetActive(state != Dot.None);
            if (state == Dot.None) return;
            _dot.color = state == Dot.Green ? Green : state == Dot.Red ? Red : Grey; // solo al cambiar de estado
            _dot.canvasRenderer.SetAlpha(1f);
        }

        private static void SetActive(Text t, bool on)
        {
            if (t.gameObject.activeSelf != on) t.gameObject.SetActive(on);
        }

        /// <summary>UIManager.instance hace FindObjectOfType si es null: se consulta como mucho una vez por segundo.</summary>
        private UIState UiState()
        {
            if (_ui == null && Time.unscaledTime >= _nextUi)
            {
                _nextUi = Time.unscaledTime + 1f;
                _ui = UIManager.instance;
            }
            return _ui != null ? _ui.uiState : UIState.INACTIVE;
        }

        /// <summary>Trajan solo existe cuando el menú la ha cargado; mientras tanto, Arial.</summary>
        private void RetryTrajan(float now)
        {
            if (_fontTries >= 20 || now < _nextFont) return;
            _nextFont = now + 1f;
            _fontTries++;
            var trajan = CanvasUtil.TrajanNormal;
            if (trajan != null)
            {
                _title.font = trajan;
                _msg.font = trajan;
                _fontTries = int.MaxValue;
            }
        }

        private string DebugText(TelemetryWriter w, float now)
        {
            long events = SessionManager.EventCount;
            if (now - _lastEventsTime >= 1f)
            {
                _eventsPerSec = (events - _lastEvents) / Mathf.Max(0.001f, now - _lastEventsTime);
                _lastEvents = events;
                _lastEventsTime = now;
            }
            string sid = SessionManager.SessionId;
            if (!ReferenceEquals(sid, _sessionId)) { _sessionId = sid; _sessionShort = sid?.Substring(0, 8); }

            // Números con los escritores sin asignaciones de Json (StringBuilder.Append(long) crea strings en Mono).
            const char NL = '\n';
            var sb = _sb;
            sb.Length = 0;
            sb.Append("HKTelemetry debug").Append(NL);
            sb.Append("sesión: ").Append(_sessionShort ?? "-").Append("  ").Append(SessionManager.Kind ?? "").Append(NL);
            sb.Append("escena: ").Append(Recorders.SceneRecorder.CurrentScene ?? _activeScene).Append(NL);
            sb.Append("eventos: "); Json.AppendLong(sb, events);
            sb.Append("  ("); Json.AppendNumber(sb, Math.Round(_eventsPerSec, 1)); sb.Append("/s)").Append(NL);
            sb.Append("último: ").Append(SessionManager.LastType ?? "-").Append(NL);
            if (w != null)
            {
                sb.Append("events.jsonl: "); Json.AppendNumber(sb, Math.Round(w.Bytes(TelemetryWriter.Events) / 1024.0, 1));
                sb.Append(" KB  líneas "); Json.AppendLong(sb, w.Lines(TelemetryWriter.Events)); sb.Append(NL);
                sb.Append("frames: "); Json.AppendLong(sb, w.Lines(TelemetryWriter.Frames));
                sb.Append(" ("); Json.AppendNumber(sb, Math.Round(Recorders.FrameRecorder.MeasuredHz, 1)); sb.Append(" Hz)");
                sb.Append("  inputs: "); Json.AppendLong(sb, w.Lines(TelemetryWriter.Inputs)); sb.Append(NL);
                sb.Append("descartados: "); Json.AppendLong(sb, w.TotalDropped());
                sb.Append("  errores escritura: "); Json.AppendLong(sb, w.WriteErrors); sb.Append(NL);
            }
            sb.Append("fps: "); Json.AppendLong(sb, (long)Runner.LastFps);
            sb.Append("  frame máx: "); Json.AppendNumber(sb, Math.Round(Runner.LastDtMaxMs, 1)); sb.Append(" ms").Append(NL);
            sb.Append("mod: "); Json.AppendNumber(sb, Math.Round(Runner.LastModUsAvg, 1));
            sb.Append(" µs/frame  máx "); Json.AppendLong(sb, (long)Runner.LastModUsMax); sb.Append(" µs").Append(NL);
            sb.Append("errores del mod: "); Json.AppendLong(sb, Safe.ErrorCount);
            if (Safe.LastError != null) sb.Append(NL).Append(Safe.LastError);
            return sb.ToString();
        }
    }
}
