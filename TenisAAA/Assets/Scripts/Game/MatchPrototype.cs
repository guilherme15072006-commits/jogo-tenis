using System.Text;
using Tenis.Core;
using UnityEngine;

namespace Tenis.Game
{
    /// <summary>
    /// Playable gray-box match (best of 3 against the AI). All rules and physics live in <see cref="MatchEngine"/>;
    /// this component only feeds touch input, draws the court, athletes, ball, aim indicator and HUD, and drives
    /// a broadcast-style camera. Starts automatically in any scene when you press Play.
    /// Controls: left thumb = joystick (or WASD), right thumb = swing gestures (mouse drag in the editor).
    /// </summary>
    public class MatchPrototype : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (FindAnyObjectByType<MatchPrototype>() != null) return;
            new GameObject("[Tenis] MatchPrototype").AddComponent<MatchPrototype>();
        }

        MatchEngine _eng; GameClock _clock; TouchInput _input; TennisConfig _cfg;
        System.Action<Vec3, AthleteAnimation> _me, _ai;   // rigged Tripo character if present, else the procedural one
        Transform _ballT, _shadowT; LineRenderer _aimLine, _aimEllipse;
        Camera _cam; Material _baseMat;
        int _msgShown; bool _mobile;

        bool _manual; float _manualDt = -1;
        public MatchEngine Engine => _eng;
        public Camera BroadcastCamera => _cam;

        void Start() { if (!_manual) Init((ulong)System.DateTime.Now.Ticks); }

        void Init(ulong seed)
        {
            // in the browser a fixed target frame rate uses setTimeout and stutters; -1 = the screen's own refresh (smooth)
            Application.targetFrameRate = Application.platform == RuntimePlatform.WebGLPlayer ? -1 : 60;
            _mobile = Application.isMobilePlatform;
            if (_mobile)
            {   // phones: lighter shadows and no MSAA so the frame rate stays steady
                QualitySettings.antiAliasing = 0; QualitySettings.shadowResolution = ShadowResolution.Low;
                QualitySettings.shadowCascades = 1; QualitySettings.shadows = ShadowQuality.HardOnly;
            }
            _cfg = ConfigLoader.Load();
            _eng = new MatchEngine(_cfg, new AthleteProfile(), new AthleteProfile { backhand = BackhandStyle.OneHanded }, seed);
            _clock = gameObject.AddComponent<GameClock>();
            _input = gameObject.AddComponent<TouchInput>(); _input.clock = _clock; _input.Configure(_cfg.gesture);
            _input.OnGestureFinished += (s, start) => _eng.OnLocalGesture(s, start, (double)Screen.width / Screen.height);
            _input.OnGestureStarted += () => _eng.OnLocalGestureStarted();
            BuildCourt();
        }

        /// <summary>Tools (gameplay video capture, bots): build the match without Play mode and step it by hand.</summary>
        public void InitManual(ulong seed) { _manual = true; Init(seed); }
        public void StepManual(float dt, double stickX, double stickY) { _manualDt = dt; Step(dt, stickX, stickY); }

        void Update() { if (!_manual) Step(Mathf.Min(Time.deltaTime, 0.05f), _input.Stick.x, _input.Stick.y); }

        void Step(double dt, double sx, double sy)
        {
            _eng.Tick(dt, sx, sy);
            _me(_eng.players[0].position, _eng.anims[0]);
            _ai(_eng.players[1].position, _eng.anims[1]);
            _ballT.gameObject.SetActive(_eng.ballVisible); _shadowT.gameObject.SetActive(_eng.ballVisible);
            _ballT.position = CourtSpace.ToUnity(_eng.ball.position);
            _shadowT.position = CourtSpace.ToUnity(new Vec3(_eng.ball.position.x, _eng.ball.position.y, 0.008));
            UpdateAim();
            UpdateCamera(dt);
        }

        // ---------------------------------------------------------------- aim indicator (optional, configurable)
        void UpdateAim()
        {
            _aimLine.positionCount = 0; _aimEllipse.positionCount = 0;
            if (_manual || !_cfg.aim.showIndicator || !_input.GestureInProgress || _input.CurrentGesture.Count < 3) return;
            var feat = GestureClassifier.Measure(_input.CurrentGesture);
            var shape = feat.length < _cfg.gesture.tapMaxLength ? GestureShape.Tap : _eng.classifier.ShapeFromAngle(feat.angleDeg);
            var gr = new GestureResult { accepted = true, shape = shape, f = feat, lateralIntent = MathD.Clamp(feat.dx / _cfg.aim.lateralFullDeflection, -1, 1), shortIntent = feat.length < _cfg.gesture.shortGestureLength };
            var pv = _eng.PreviewAim(0, gr);
            if (!pv.valid) return;
            var from = _eng.PlanFor(0).ball.position;
            _aimLine.positionCount = 2;
            _aimLine.SetPosition(0, CourtSpace.ToUnity(from)); _aimLine.SetPosition(1, CourtSpace.ToUnity(pv.target + new Vec3(0, 0, 0.02)));
            const int n = 32; _aimEllipse.positionCount = n + 1;
            for (int i = 0; i <= n; i++)
            {
                double a = i * 2 * System.Math.PI / n;
                var p = pv.target + new Vec3(System.Math.Cos(a) * pv.sigmaDepth, System.Math.Sin(a) * pv.sigmaLateral, 0.02);
                _aimEllipse.SetPosition(i, CourtSpace.ToUnity(p));
            }
        }

        // ---------------------------------------------------------------- broadcast camera
        void UpdateCamera(double dt)
        {
            var me = _eng.players[0].position;
            // TV main camera: high and far behind the baseline with a long lens (flattens the court like a broadcast)
            var target = new Vec3(-37.0, me.y * 0.30, 16.5);   // high in the stands, above the aisle rails
            var look = new Vec3(-2.5, me.y * 0.25, 0.0);
            Vector3 tp = CourtSpace.ToUnity(target);
            _cam.transform.position = Vector3.Lerp(_cam.transform.position, tp, 1 - Mathf.Exp(-(float)dt * 3));
            var rot = Quaternion.LookRotation(CourtSpace.ToUnity(look) - _cam.transform.position, Vector3.up);
            _cam.transform.rotation = Quaternion.Slerp(_cam.transform.rotation, rot, 1 - Mathf.Exp(-(float)dt * 4));
        }

        // ---------------------------------------------------------------- scene
        void BuildCourt()
        {
            _cam = Camera.main != null ? Camera.main : new GameObject("Main Camera").AddComponent<Camera>();
            _cam.tag = "MainCamera"; _cam.fieldOfView = 25; _cam.clearFlags = CameraClearFlags.SolidColor; _cam.backgroundColor = new Color(0.08f, 0.1f, 0.12f);
            _cam.transform.position = CourtSpace.ToUnity(new Vec3(-37, 0, 16.5)); _cam.transform.LookAt(CourtSpace.ToUnity(new Vec3(-2.5, 0, 0)));
            if (FindAnyObjectByType<Light>() == null)
            {
                var l = new GameObject("Sol").AddComponent<Light>(); l.type = LightType.Directional; l.intensity = 1.25f;
                l.transform.rotation = Quaternion.Euler(52, -35, 0); l.shadows = LightShadows.Soft;
            }
            var sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            _baseMat = new Material(sh);
            Material M(Color c) => new Material(_baseMat) { color = c };
            var clay = M(new Color(0.72f, 0.3f, 0.12f)); var white = M(Color.white); var dark = M(new Color(0.04f, 0.04f, 0.04f));
            var green = M(new Color(0.03f, 0.2f, 0.1f));
            bool arena = LoadArena();
            Box(new Vec3(0, 0, -0.05), arena ? new Vector3(25, 0.1f, 40) : new Vector3(22, 0.1f, 40), clay);
            double HL = Court.HalfLength, HD = Court.HalfDoublesWidth, HS = Court.HalfSinglesWidth, SV = Court.ServiceLine;
            void Line(double x0, double x1, double y0, double y1) => Box(new Vec3((x0 + x1) / 2, (y0 + y1) / 2, 0.002), new Vector3((float)(y1 - y0), 0.004f, (float)(x1 - x0)), white);
            Line(HL - 0.1, HL, -HD, HD); Line(-HL, -HL + 0.1, -HD, HD); Line(-HL, HL, HD - 0.05, HD); Line(-HL, HL, -HD, -HD + 0.05);
            Line(-HL, HL, HS - 0.05, HS); Line(-HL, HL, -HS, -HS + 0.05); Line(SV - 0.05, SV, -HS, HS); Line(-SV, -SV + 0.05, -HS, HS);
            Line(-SV, SV, -0.025, 0.025); Line(HL - 0.2, HL - 0.1, -0.025, 0.025); Line(-HL + 0.1, -HL + 0.2, -0.025, 0.025);
            Box(new Vec3(0, 0, 0.47), new Vector3(12.8f, 0.9f, 0.006f), NetMaterial()); Box(new Vec3(0, 0, 0.93), new Vector3(12.8f, 0.06f, 0.03f), white);
            foreach (var s in new[] { -1, 1 }) Box(new Vec3(0, s * 6.4, 0.535), new Vector3(0.08f, 1.07f, 0.08f), green);
            if (!arena)
                foreach (var s in new[] { -1, 1 })
                {
                    Box(new Vec3(s * 20, 0, 0.6), new Vector3(24, 1.2f, 0.2f), green);
                    Box(new Vec3(0, s * 11.5, 0.6), new Vector3(0.2f, 1.2f, 40), green);
                }
            _me = MakeAthlete("Jogador", new Color(0.2f, 0.45f, 0.95f), Color.white);
            _ai = MakeAthlete("Adversario", new Color(0.9f, 0.25f, 0.2f), new Color(0.15f, 0.15f, 0.2f));
            var b = GameObject.CreatePrimitive(PrimitiveType.Sphere); Kill(b.GetComponent<Collider>());
            b.transform.localScale = Vector3.one * 0.067f * 1.5f; b.GetComponent<Renderer>().sharedMaterial = M(new Color(0.85f, 0.95f, 0.2f)); _ballT = b.transform;
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad); Kill(q.GetComponent<Collider>());
            q.transform.localScale = Vector3.one * 0.13f; q.transform.rotation = Quaternion.Euler(90, 0, 0); q.GetComponent<Renderer>().sharedMaterial = M(new Color(0.2f, 0.08f, 0.03f)); _shadowT = q.transform;
            _aimLine = NewLine("Mira", new Color(1, 1, 1, 0.7f), 0.04f); _aimEllipse = NewLine("MiraErro", new Color(1, 0.9f, 0.2f, 0.8f), 0.03f);
        }

        /// <summary>Arena built in Blender (Resources/Arena.fbx): stands, 23k seats, suites, LED boards, roof, facade.
        /// Aligned automatically: its perimeter wall is 40 m × 25 m, the long side goes along the court.</summary>
        bool LoadArena()
        {
            var prefab = Resources.Load<GameObject>("Arena");
            if (prefab == null) return false;
            var arena = Instantiate(prefab); arena.name = "Arena";
            Bounds WallBounds()
            {
                var wall = arena.transform.Find("Muro");
                var r = wall != null ? wall.GetComponent<Renderer>() : null;
                if (r != null) return r.bounds;
                var b = new Bounds(arena.transform.position, Vector3.zero);
                foreach (var rr in arena.GetComponentsInChildren<Renderer>()) b.Encapsulate(rr.bounds);
                return b;
            }
            var wb = WallBounds();
            if (wb.size.x > wb.size.z) { arena.transform.rotation = Quaternion.Euler(0, 90, 0) * arena.transform.rotation; wb = WallBounds(); }
            arena.transform.position += new Vector3(-wb.center.x, -wb.min.y, -wb.center.z);
            foreach (var r in arena.GetComponentsInChildren<Renderer>())
            {
                r.shadowCastingMode = _mobile || r.name.StartsWith("Assentos") ?UnityEngine.Rendering.ShadowCastingMode.Off : UnityEngine.Rendering.ShadowCastingMode.On;
                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) continue;
                    string n = m.name;
                    Color e = n.Contains("Texto_LED") ? Color.white * 1.6f : n.Contains("Texto_Amarelo") ? new Color(1f, 0.75f, 0.1f) * 1.6f
                            : n.Contains("Painel_LED") ? new Color(0.01f, 0.16f, 0.08f) : n.Contains("Telao") ? new Color(0.02f, 0.05f, 0.14f)
                            : n.Contains("Refletor") ? Color.white * 2.5f : n.Contains("Vidro_Camarote") ? new Color(0.5f, 0.4f, 0.25f) : Color.black;
                    if (e != Color.black) { m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", e); }
                    if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", n.Contains("Vidro") ? 0.85f : 0.2f);
                }
            }
            // sky instead of the black void
            var sky = new Material(Shader.Find("Skybox/Procedural"));
            sky.SetFloat("_AtmosphereThickness", 0.9f); sky.SetFloat("_Exposure", 1.25f); sky.SetFloat("_SunSize", 0.035f);
            RenderSettings.skybox = sky; RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox;
            RenderSettings.ambientIntensity = 1.1f; DynamicGI.UpdateEnvironment();
            if (_cam != null) _cam.clearFlags = CameraClearFlags.Skybox;
            var sun = FindAnyObjectByType<Light>();
            if (sun != null) { sun.intensity = 1.15f; sun.color = new Color(1f, 0.96f, 0.9f); sun.shadowStrength = 0.85f; RenderSettings.sun = sun; }
            QualitySettings.shadowDistance = _mobile ? 45 : 120;
            return true;
        }

        System.Action<Vec3, AthleteAnimation> MakeAthlete(string n, Color shirt, Color shorts)
        {
            var rigged = RiggedAthlete.TryCreate(n, _baseMat, shirt);
            if (rigged != null) return (pos, an) => { rigged.Now = _eng.now; rigged.DtOverride = _manualDt; rigged.Apply(pos, an); };
            var proc = ProceduralAthlete.Create(n, shirt, shorts, _baseMat);
            return proc.Apply;
        }

        LineRenderer NewLine(string n, Color c, float w)
        {
            var l = new GameObject(n).AddComponent<LineRenderer>(); l.widthMultiplier = w; l.positionCount = 0;
            l.material = new Material(Shader.Find("Sprites/Default")); l.startColor = l.endColor = c; return l;
        }

        /// <summary>See-through net: a generated mesh texture (3.5 cm squares) with alpha cut-out.</summary>
        static Material NetMaterial()
        {
            const int n = 64; var t = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                bool cord = x < 5 || y < 5;
                t.SetPixel(x, y, cord ? new Color(0.05f, 0.05f, 0.05f, 1) : new Color(0, 0, 0, 0));
            }
            t.Apply();
            var sh = Shader.Find("Legacy Shaders/Transparent/Cutout/Diffuse") ?? Shader.Find("Standard");
            var m = new Material(sh) { mainTexture = t, mainTextureScale = new Vector2(12.8f / 0.035f, 0.9f / 0.035f) };
            if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", 0.5f);
            return m;
        }

        static void Kill(Object o) { if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }

        static void Box(Vec3 c, Vector3 size, Material m)
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Cube); Kill(g.GetComponent<Collider>());
            g.transform.position = CourtSpace.ToUnity(c); g.transform.localScale = size; g.GetComponent<Renderer>().sharedMaterial = m;
        }

        // ---------------------------------------------------------------- HUD
        void OnGUI()
        {
            int fs = Mathf.Max(14, Screen.height / 38);
            var st = new GUIStyle(GUI.skin.label) { fontSize = fs, richText = true };
            var s = _eng.score;
            var sb = new StringBuilder();
            sb.AppendLine($"<b>VOCÊ</b>  {s.sets[0]}  {s.games[0]}  {(s.inTiebreak ? s.points[0].ToString() : s.PointText(0))}{(s.server == 0 ? "  •" : "")}");
            sb.AppendLine($"<b>IA</b>      {s.sets[1]}  {s.games[1]}  {(s.inTiebreak ? s.points[1].ToString() : s.PointText(1))}{(s.server == 1 ? "  •" : "")}");
            GUI.Box(new Rect(10, 10, fs * 14, fs * 3.2f), GUIContent.none);
            GUI.Label(new Rect(18, 14, fs * 14, fs * 3), sb.ToString(), st);
            if (_mobile)
            {   // phone: only the score, the latest call ("Fora!", "Saque 2"…) and a hint for the swing side
                var big = new GUIStyle(st) { alignment = TextAnchor.UpperCenter, fontSize = (int)(fs * 1.15f) };
                if (_eng.messages.Count > 0) GUI.Label(new Rect(Screen.width * 0.25f, 12, Screen.width * 0.5f, fs * 2), "<b>" + _eng.messages[_eng.messages.Count - 1].text + "</b>", big);
                if (!_input.GestureInProgress)
                {
                    var hint = new GUIStyle(st) { alignment = TextAnchor.LowerCenter, fontSize = (int)(fs * 0.8f) };
                    GUI.color = new Color(1, 1, 1, 0.45f);
                    GUI.Label(new Rect(Screen.width * 0.55f, Screen.height - fs * 2.6f, Screen.width * 0.4f, fs * 2), "deslize aqui para bater", hint);
                    GUI.color = Color.white;
                }
                if (_eng.phase == MatchEngine.Phase.MatchOver)
                    GUI.Label(new Rect(Screen.width / 2f - fs * 6, Screen.height / 2f, fs * 14, fs * 2), s.winner == 0 ? "<b>VITÓRIA!</b>" : "<b>DERROTA</b>", st);
                return;
            }
            var info = new StringBuilder();
            for (int i = Mathf.Max(0, _eng.messages.Count - 3); i < _eng.messages.Count; i++) info.AppendLine(_eng.messages[i].text);
            if (_eng.lastShot.contact || _eng.lastShot.timing.grade == TimingGrade.Fail) info.AppendLine("golpe: " + _eng.lastShot.Describe());
            if (_eng.lastGesture.f.duration > 0) info.AppendLine("gesto: " + _eng.lastGesture);
            if (_eng.HasPlan(0))
            {
                var c = _eng.PlanFor(0);
                info.AppendLine($"bola em {System.Math.Max(0, c.contactTime - _eng.now):0.00}s · {(c.bounced ? "após quique" : "VOLEIO")} · {(c.lateralOffset >= 0 ? "forehand" : "backhand")} · altura {c.ball.position.z:0.0} m");
            }
            info.AppendLine($"equilíbrio {_eng.players[0].balance:0.00} · {_eng.players[0].Speed:0.0} m/s");
            GUI.Label(new Rect(fs * 15, 12, Screen.width - fs * 16, Screen.height * 0.4f), info.ToString(), st);
            float zx = (float)_cfg.gesture.joystickZoneMaxX * Screen.width;
            GUI.color = new Color(1, 1, 1, 0.07f); GUI.DrawTexture(new Rect(zx - 1, Screen.height * 0.3f, 2, Screen.height * 0.7f), Texture2D.whiteTexture); GUI.color = Color.white;
            if (_eng.phase == MatchEngine.Phase.MatchOver)
                GUI.Label(new Rect(Screen.width / 2f - fs * 6, Screen.height / 2f, fs * 14, fs * 2), s.winner == 0 ? "<b>VITÓRIA!</b>" : "<b>DERROTA</b>", st);
        }
    }
}
