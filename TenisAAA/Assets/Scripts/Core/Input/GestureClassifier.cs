using System;
using System.Collections.Generic;

namespace Tenis.Core
{
    /// <summary>One touch sample. x/y are normalised by screen HEIGHT (y up), t in seconds.</summary>
    [Serializable]
    public struct TouchSample
    {
        public double x, y, t;
        public TouchSample(double x, double y, double t) { this.x = x; this.y = y; this.t = t; }
    }

    /// <summary>Raw shape of the swipe, before any game context.</summary>
    public enum GestureShape
    {
        None,
        Tap,            // tiny touch: drop shot candidate
        Horizontal,     // flat
        DiagonalUp,     // topspin
        DiagonalDown,   // slice
        VerticalUp,     // lob
        VerticalDown,   // smash (if context allows) / otherwise slice-like
    }

    public enum GestureRejectReason
    {
        None, TooShort, TooQuick, TooLong, TooSlow, NotStraight, Duplicate, Cooldown, StartedInJoystickZone, TapNotAllowed
    }

    [Serializable]
    public struct GestureFeatures
    {
        public double startTime, endTime, duration;
        public double length;           // chord length (screen heights)
        public double pathLength;
        public double straightness;     // chord / path
        public double meanSpeed, peakSpeed;   // screen heights / s
        public double angleDeg;         // 0 = right, 90 = up, (-180, 180]
        public double dx, dy;           // chord components
        public double startX, startY;
    }

    [Serializable]
    public struct GestureResult
    {
        public bool accepted;
        public GestureRejectReason reason;
        public GestureShape shape;
        public GestureFeatures f;
        /// <summary>-1 = hitter's left .. +1 = hitter's right (from horizontal finger travel).</summary>
        public double lateralIntent;
        /// <summary>True when the amplitude asks for a short (angled) target.</summary>
        public bool shortIntent;
        public override string ToString() => accepted ? $"{shape} ang={f.angleDeg:0} len={f.length:0.00} vmax={f.peakSpeed:0.0} lat={lateralIntent:0.00}" : $"rejected:{reason}";
    }

    /// <summary>
    /// Turns a finger path into a classified gesture using angle + distance + speed + duration, with filters
    /// against accidental touches, too-short swipes, involuntary taps, duplicates and joystick conflicts.
    /// Ball/player context is applied later by <see cref="ShotSelector"/> (this class is context-free so it can be unit tested).
    /// </summary>
    public sealed class GestureClassifier
    {
        readonly GestureConfig _c;
        double _lastAcceptedEnd = double.NegativeInfinity;
        double _cooldownUntil = double.NegativeInfinity;

        public GestureClassifier(GestureConfig cfg) { _c = cfg; }

        /// <summary>Call after a contact so a second swipe right after the hit is ignored.</summary>
        public void StartCooldown(double now) => _cooldownUntil = now + _c.cooldownAfterShot;
        public void Reset() { _lastAcceptedEnd = double.NegativeInfinity; _cooldownUntil = double.NegativeInfinity; }

        public static GestureFeatures Measure(IReadOnlyList<TouchSample> s)
        {
            var f = new GestureFeatures();
            if (s == null || s.Count == 0) return f;
            var a = s[0]; var b = s[s.Count - 1];
            f.startTime = a.t; f.endTime = b.t; f.duration = Math.Max(0, b.t - a.t);
            f.startX = a.x; f.startY = a.y;
            f.dx = b.x - a.x; f.dy = b.y - a.y;
            f.length = Math.Sqrt(f.dx * f.dx + f.dy * f.dy);
            double path = 0, peak = 0;
            for (int i = 1; i < s.Count; i++)
            {
                double ddx = s[i].x - s[i - 1].x, ddy = s[i].y - s[i - 1].y, dt = s[i].t - s[i - 1].t;
                double d = Math.Sqrt(ddx * ddx + ddy * ddy); path += d;
                // peak speed over a ~40 ms window to be robust to jittery touch timestamps
                int j = i; double acc = d, at = dt;
                while (j > 1 && at < 0.04) { j--; double ex = s[j].x - s[j - 1].x, ey = s[j].y - s[j - 1].y; acc += Math.Sqrt(ex * ex + ey * ey); at += s[j].t - s[j - 1].t; }
                if (at > 1e-4) peak = Math.Max(peak, acc / at);
            }
            f.pathLength = path;
            f.straightness = path > 1e-9 ? f.length / path : 1;
            f.meanSpeed = f.duration > 1e-6 ? f.length / f.duration : 0;
            f.peakSpeed = Math.Max(peak, f.meanSpeed);
            f.angleDeg = MathD.Rad2Deg(Math.Atan2(f.dy, f.dx));
            return f;
        }

        public GestureShape ShapeFromAngle(double angleDeg)
        {
            double a = MathD.WrapDeg(angleDeg);
            if (Math.Abs(a - 90) < _c.verticalHalfWidth) return GestureShape.VerticalUp;
            if (Math.Abs(a + 90) < _c.verticalHalfWidth) return GestureShape.VerticalDown;
            if (Math.Abs(a) < _c.horizontalHalfWidth || Math.Abs(Math.Abs(a) - 180) < _c.horizontalHalfWidth) return GestureShape.Horizontal;
            return a > 0 ? GestureShape.DiagonalUp : GestureShape.DiagonalDown;
        }

        /// <param name="screenAspect">width / height, used for the joystick-zone test.</param>
        public GestureResult Classify(IReadOnlyList<TouchSample> samples, double screenAspect, bool tapAllowed = true)
        {
            var r = new GestureResult { f = Measure(samples) };
            var f = r.f;
            if (samples == null || samples.Count == 0) { r.reason = GestureRejectReason.TooShort; return r; }
            if (f.startX < _c.joystickZoneMaxX * screenAspect) { r.reason = GestureRejectReason.StartedInJoystickZone; return r; }
            if (f.endTime < _cooldownUntil) { r.reason = GestureRejectReason.Cooldown; return r; }
            if (f.startTime - _lastAcceptedEnd < _c.duplicateWindow) { r.reason = GestureRejectReason.Duplicate; return r; }

            // tap → drop-shot candidate
            if (f.length <= _c.tapMaxLength)
            {
                if (f.duration > _c.tapMaxDuration) { r.reason = GestureRejectReason.TooSlow; return r; }   // resting finger
                if (f.duration < 0.02) { r.reason = GestureRejectReason.TooQuick; return r; }              // involuntary brush
                if (!tapAllowed) { r.reason = GestureRejectReason.TapNotAllowed; return r; }
                return Accept(r, GestureShape.Tap, 0, true);
            }
            if (f.length < _c.minLength) { r.reason = GestureRejectReason.TooShort; return r; }
            if (f.duration < _c.minDuration) { r.reason = GestureRejectReason.TooQuick; return r; }
            if (f.duration > _c.maxDuration) { r.reason = GestureRejectReason.TooLong; return r; }
            if (f.peakSpeed < _c.minPeakSpeed) { r.reason = GestureRejectReason.TooSlow; return r; }
            if (f.straightness < _c.minStraightness) { r.reason = GestureRejectReason.NotStraight; return r; }

            var shape = ShapeFromAngle(f.angleDeg);
            // lateral intent: horizontal travel, full deflection at lateralFullDeflection (set from AimConfig by caller)
            double lat = MathD.Clamp(f.dx / Math.Max(1e-6, LateralFull), -1, 1);
            return Accept(r, shape, lat, f.length < _c.shortGestureLength);
        }

        /// <summary>Horizontal finger travel (screen heights) that means "full left/right". Set from AimConfig.</summary>
        public double LateralFull { get; set; } = 0.20;

        GestureResult Accept(GestureResult r, GestureShape shape, double lat, bool shortIntent)
        {
            r.accepted = true; r.shape = shape; r.lateralIntent = lat; r.shortIntent = shortIntent;
            _lastAcceptedEnd = r.f.endTime;
            return r;
        }
    }
}
