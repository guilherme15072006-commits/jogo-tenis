using System.Collections.Generic;
using Tenis.Core;
using UnityEngine;

namespace Tenis.Game
{
    /// <summary>
    /// Multitouch router: the LEFT part of the screen owns the movement joystick, the RIGHT part owns swing gestures.
    /// A finger belongs to the zone where it went down until it lifts, so one thumb can run while the other swings
    /// (and a thumb sliding across the middle never triggers the other control). Mouse = one finger in the editor.
    /// Gesture coordinates are normalised by screen HEIGHT so the feel is the same on every phone.
    /// </summary>
    public class TouchInput : MonoBehaviour
    {
        [Tooltip("Floating joystick radius as a fraction of screen height")] public float joystickRadius = 0.11f;
        public GameClock clock;

        public Vector2 Stick { get; private set; }               // -1..1, y = towards the net
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
        double _gestureStart;

        public void Configure(GestureConfig g) { _zoneMaxX = (float)g.joystickZoneMaxX; }

        void Update()
        {
            if (Input.touchSupported && Input.touchCount > 0)
                for (int i = 0; i < Input.touchCount; i++)
                {
                    var t = Input.GetTouch(i);
                    Handle(t.fingerId, t.position, t.phase == TouchPhase.Began, t.phase == TouchPhase.Ended || t.phase == TouchPhase.Canceled);
                }
            else
            {
                if (Input.GetMouseButtonDown(0)) Handle(-1, Input.mousePosition, true, false);
                else if (Input.GetMouseButton(0)) Handle(-1, Input.mousePosition, false, false);
                else if (Input.GetMouseButtonUp(0)) Handle(-1, Input.mousePosition, false, true);
                // keyboard fallback for desktop testing (WASD)
                if (!StickActive)
                    Stick = Vector2.ClampMagnitude(new Vector2(Input.GetAxisRaw("Horizontal"), Input.GetAxisRaw("Vertical")), 1);
            }
        }

        void Handle(int id, Vector2 px, bool began, bool ended)
        {
            float h = Screen.height;
            if (began)
            {
                bool leftZone = px.x < _zoneMaxX * Screen.width;
                if (leftZone && _stickFinger == int.MinValue) { _stickFinger = id; StickOrigin = px; StickKnob = px; Stick = Vector2.zero; }
                else if (!leftZone && _gestureFinger == int.MinValue)
                {
                    _gestureFinger = id; _samples.Clear(); _gestureStart = clock.Now;
                    _samples.Add(new TouchSample(px.x / h, px.y / h, clock.Now));
                    OnGestureStarted?.Invoke();
                }
                return;
            }
            if (id == _stickFinger)
            {
                if (ended) { _stickFinger = int.MinValue; Stick = Vector2.zero; return; }
                Vector2 d = (px - StickOrigin) / (joystickRadius * h);
                Stick = Vector2.ClampMagnitude(d, 1);
                StickKnob = StickOrigin + Stick * joystickRadius * h;
            }
            else if (id == _gestureFinger)
            {
                _samples.Add(new TouchSample(px.x / h, px.y / h, clock.Now));
                if (ended)
                {
                    _gestureFinger = int.MinValue;
                    OnGestureFinished?.Invoke(new List<TouchSample>(_samples), _gestureStart);
                }
            }
        }

        void OnGUI()
        {
            if (!StickActive) return;
            float h = Screen.height, r = joystickRadius * h;
            var o = new Vector2(StickOrigin.x, h - StickOrigin.y); var k = new Vector2(StickKnob.x, h - StickKnob.y);
            GUI.color = new Color(1, 1, 1, 0.25f); GUI.DrawTexture(new Rect(o.x - r, o.y - r, 2 * r, 2 * r), Circle);
            GUI.color = new Color(1, 1, 1, 0.6f); GUI.DrawTexture(new Rect(k.x - r * 0.4f, k.y - r * 0.4f, r * 0.8f, r * 0.8f), Circle);
            GUI.color = Color.white;
        }

        static Texture2D _circle;
        static Texture2D Circle
        {
            get
            {
                if (_circle != null) return _circle;
                const int n = 64; _circle = new Texture2D(n, n);
                for (int y = 0; y < n; y++) for (int x = 0; x < n; x++)
                {
                    float d = Vector2.Distance(new Vector2(x, y), new Vector2(n / 2f, n / 2f)) / (n / 2f);
                    _circle.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01((1 - d) * 8)));
                }
                _circle.Apply(); return _circle;
            }
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
