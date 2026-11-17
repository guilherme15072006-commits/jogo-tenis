using System.Collections.Generic;
using Tenis.Core;
using UnityEngine;

namespace Tenis.Game
{
    /// <summary>
    /// Multitouch router: the LEFT part of the screen owns the movement joystick, the RIGHT part owns swing gestures.
    /// A finger belongs to the zone where it went down until it lifts, so one thumb can run while the other swings
    /// (and a thumb sliding across the middle never triggers the other control). Mouse = one finger in the editor.
    /// Joystick: floating (appears under the thumb anywhere on the left half), dynamic base (follows the thumb past
    /// the ring), always visible at rest, smoothed and with a response curve. Gesture coordinates are normalised by
    /// screen HEIGHT so the feel is the same on every phone.
    /// </summary>
    public class TouchInput : MonoBehaviour
    {
        [Tooltip("Joystick radius as a fraction of screen height")] public float joystickRadius = 0.13f;
        [Tooltip("Stick smoothing time constant (s): removes finger jitter without noticeable lag")] public float stickSmoothing = 0.035f;
        public GameClock clock;

        public Vector2 Stick { get; private set; }               // -1..1, y = towards the net (smoothed)
        public bool StickActive => _stickFinger != int.MinValue;
        public Vector2 StickOrigin { get; private set; }
        public Vector2 StickKnob { get; private set; }

        public System.Action<List<TouchSample>, double> OnGestureFinished;   // samples, start time
        public System.Action OnGestureStarted;
        public bool GestureInProgress => _gestureFinger != int.MinValue;
        public List<TouchSample> CurrentGesture => _samples;

        float _zoneMaxX = 0.45f;
        int _stickFinger = int.MinValue, _gestureFinger = int.MinValue;
        readonly List<TouchSample> _samples = new List<TouchSample>();
        readonly List<Vector2> _trailPts = new List<Vector2>();
        double _gestureStart;
        Vector2 _rawStick;
        float _idleAlpha = 1, _swipeFade;

        public void Configure(GestureConfig g) { _zoneMaxX = (float)g.joystickZoneMaxX; }

        /// <summary>Where the joystick rests when nobody touches it (bottom-left, at thumb height).</summary>
        Vector2 RestPos => new Vector2(Mathf.Max(Screen.height * (joystickRadius + 0.06f), Screen.width * 0.13f), Screen.height * 0.27f);

        void Update()
        {
            if (Input.touchSupported && Input.touchCount > 0)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    var t = Input.GetTouch(i);
                    Handle(t.fingerId, t.position, t.phase == TouchPhase.Began, t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled);
                }
            }
            else if (Input.touchSupported && Input.touchCount == 0 && (StickActive || GestureInProgress) && _stickFinger != -1 && _gestureFinger != -1)
            {
                // a finger vanished without an Ended phase (browser gesture, notification): release everything
                _stickFinger = int.MinValue; _rawStick = Vector2.zero; _gestureFinger = int.MinValue;
            }
            if (!(Input.touchSupported && Input.touchCount > 0))
            {
                if (Input.GetMouseButtonDown(0)) Handle(-1, Input.mousePosition, true, false);
                else if (Input.GetMouseButton(0)) Handle(-1, Input.mousePosition, false, false);
                else if (Input.GetMouseButtonUp(0)) Handle(-1, Input.mousePosition, false, true);
                // keyboard fallback for desktop testing (WASD / arrows)
                if (!StickActive)
                    _rawStick = Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1);
            }
            float dt = Time.unscaledDeltaTime;
            float k = 1 - Mathf.Exp(-dt / Mathf.Max(1e-3f, stickSmoothing));
            Stick = Vector2.Lerp(Stick, _rawStick, k);
            if (_rawStick == Vector2.zero && Stick.sqrMagnitude < 1e-4f) Stick = Vector2.zero;
            _idleAlpha = Mathf.MoveTowards(_idleAlpha, StickActive ? 0 : 1, dt * 4);
            _swipeFade = Mathf.MoveTowards(_swipeFade, 0, dt * 3);
        }

        void Handle(int id, Vector2 px, bool began, bool ended)
        {
            float h = Screen.height, r = joystickRadius * h;
            if (began)
            {
                bool leftZone = px.x < _zoneMaxX * Screen.width;
                if (leftZone && _stickFinger == int.MinValue)
                {
                    // the joystick appears under the thumb anywhere on the left half (every corner),
                    // nudged so the ring is never cut off by the screen edge
                    _stickFinger = id;
                    StickOrigin = new Vector2(Mathf.Clamp(px.x, r * 0.6f, Screen.width - r), Mathf.Clamp(px.y, r * 0.6f, h - r * 0.6f));
                    Move(px);
                }
                else if (!leftZone && _gestureFinger == int.MinValue)
                {
                    _gestureFinger = id; _samples.Clear(); _trailPts.Clear(); _gestureStart = clock.Now;
                    _samples.Add(new TouchSample(px.x / h, px.y / h, clock.Now)); _trailPts.Add(px);
                    OnGestureStarted?.Invoke();
                }
                return;
            }
            if (id == _stickFinger)
            {
                if (ended) { _stickFinger = int.MinValue; _rawStick = Vector2.zero; return; }
                Move(px);
            }
            else if (id == _gestureFinger)
            {
                _samples.Add(new TouchSample(px.x / h, px.y / h, clock.Now)); _trailPts.Add(px);
                if (ended)
                {
                    _gestureFinger = int.MinValue; _swipeFade = 1;
                    OnGestureFinished?.Invoke(new List<TouchSample>(_samples), _gestureStart);
                }
            }
        }

        void Move(Vector2 px)
        {
            float r = joystickRadius * Screen.height;
            Vector2 d = px - StickOrigin;
            // dynamic base: past the ring the base follows the thumb, so the stick never sticks at the edge
            if (d.magnitude > r) { StickOrigin = px - d.normalized * r; d = px - StickOrigin; }
            Vector2 n = d / r;
            // response curve: fine control near the centre, full speed a little before the edge
            float m = n.magnitude, c = Mathf.Clamp01(m / 0.85f);
            c = c * c * (3 - 2 * c) * 0.35f + c * 0.65f;
            _rawStick = m > 1e-4f ? n / m * c : Vector2.zero;
            StickKnob = StickOrigin + Vector2.ClampMagnitude(d, r);
        }

        void OnGUI()
        {
            float h = Screen.height, r = joystickRadius * h;
            // joystick: always visible (faint at rest), brighter under the thumb
            Vector2 o = StickActive ? StickOrigin : RestPos, kn = StickActive ? StickKnob : RestPos;
            float a = Mathf.Lerp(0.6f, 0.3f, _idleAlpha);
            var go = new Vector2(o.x, h - o.y); var gk = new Vector2(kn.x, h - kn.y);
            GUI.color = new Color(0, 0, 0, a * 0.35f); GUI.DrawTexture(new Rect(go.x - r, go.y - r, 2 * r, 2 * r), Disc);
            GUI.color = new Color(1, 1, 1, a); GUI.DrawTexture(new Rect(go.x - r, go.y - r, 2 * r, 2 * r), Ring);
            GUI.color = new Color(1, 1, 1, Mathf.Min(1, a + 0.3f)); GUI.DrawTexture(new Rect(gk.x - r * 0.42f, gk.y - r * 0.42f, r * 0.84f, r * 0.84f), Disc);
            // swipe trail on the right half: shows the gesture that was read
            if (_trailPts.Count > 1 && (GestureInProgress || _swipeFade > 0))
            {
                float ta = 0.7f * (GestureInProgress ? 1 : _swipeFade);
                for (int i = 0; i < _trailPts.Count; i++)
                {
                    float f = i / (float)_trailPts.Count, s = r * Mathf.Lerp(0.1f, 0.24f, f);
                    var p = _trailPts[i]; GUI.color = new Color(1, 0.95f, 0.4f, ta * (0.3f + 0.7f * f));
                    GUI.DrawTexture(new Rect(p.x - s, h - p.y - s, 2 * s, 2 * s), Disc);
                }
            }
            GUI.color = Color.white;
        }

        static Texture2D _disc, _ring;
        static Texture2D Disc => _disc != null ? _disc : (_disc = MakeCircle(false));
        static Texture2D Ring => _ring != null ? _ring : (_ring = MakeCircle(true));
        static Texture2D MakeCircle(bool ring)
        {
            const int n = 128; var t = new Texture2D(n, n) { filterMode = FilterMode.Bilinear };
            for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                float aOut = Mathf.Clamp01((1 - d) * 40), aIn = ring ? Mathf.Clamp01((d - 0.9f) * 40) : 1;
                t.SetPixel(x, y, new Color(1, 1, 1, aOut * aIn));
            }
            t.Apply(); return t;
        }
    }

    /// <summary>Single time base shared by input and simulation (seconds, double).</summary>
    public class GameClock : MonoBehaviour
    {
        public double Now { get; private set; }
        public float timeScale = 1f;
        void Update() => Now += Time.unscaledDeltaTime * timeScale;
    }
}
