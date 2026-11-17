using System;

namespace Tenis.Core
{
    /// <summary>Double-precision vector used by the deterministic simulation (no UnityEngine dependency).
    /// Court axes: x = length (net at x = 0), y = width, z = up. Units: metres.</summary>
    [Serializable]
    public struct Vec3 : IEquatable<Vec3>
    {
        public double x, y, z;

        public Vec3(double x, double y, double z) { this.x = x; this.y = y; this.z = z; }

        public static readonly Vec3 Zero = new Vec3(0, 0, 0);
        public static readonly Vec3 Up = new Vec3(0, 0, 1);

        public double Length => Math.Sqrt(x * x + y * y + z * z);
        public double SqrLength => x * x + y * y + z * z;
        public Vec3 Normalized { get { double l = Length; return l > 1e-12 ? this / l : Zero; } }
        public Vec3 Horizontal => new Vec3(x, y, 0);

        public static Vec3 operator +(Vec3 a, Vec3 b) => new Vec3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vec3 operator -(Vec3 a, Vec3 b) => new Vec3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vec3 operator -(Vec3 a) => new Vec3(-a.x, -a.y, -a.z);
        public static Vec3 operator *(Vec3 a, double s) => new Vec3(a.x * s, a.y * s, a.z * s);
        public static Vec3 operator *(double s, Vec3 a) => new Vec3(a.x * s, a.y * s, a.z * s);
        public static Vec3 operator /(Vec3 a, double s) => new Vec3(a.x / s, a.y / s, a.z / s);

        public static double Dot(Vec3 a, Vec3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vec3 Cross(Vec3 a, Vec3 b) =>
            new Vec3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static Vec3 Lerp(Vec3 a, Vec3 b, double t) => a + (b - a) * t;
        public static double Distance(Vec3 a, Vec3 b) => (a - b).Length;

        public bool Equals(Vec3 o) => x == o.x && y == o.y && z == o.z;
        public override bool Equals(object obj) => obj is Vec3 v && Equals(v);
        public override int GetHashCode() => HashCode.Combine(x, y, z);
        public override string ToString() => $"({x:0.###}, {y:0.###}, {z:0.###})";
    }

    public static class MathD
    {
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
        public static double Clamp01(double v) => Clamp(v, 0, 1);
        public static double Lerp(double a, double b, double t) => a + (b - a) * t;
        public static double InverseLerp(double a, double b, double v) => Math.Abs(b - a) < 1e-12 ? 0 : Clamp01((v - a) / (b - a));
        public static double SmoothStep(double e0, double e1, double v) { double t = InverseLerp(e0, e1, v); return t * t * (3 - 2 * t); }
        /// <summary>Soft saturation: linear near 0, approaches 1 asymptotically. Used so that absurdly fast
        /// finger flicks cannot produce unrealistic racket speeds.</summary>
        public static double SoftSaturate(double v) => Math.Tanh(Math.Max(0, v));
        public static double Deg2Rad(double d) => d * Math.PI / 180.0;
        public static double Rad2Deg(double r) => r * 180.0 / Math.PI;
        /// <summary>Wraps an angle to (-180, 180].</summary>
        public static double WrapDeg(double d) { d %= 360; if (d > 180) d -= 360; if (d <= -180) d += 360; return d; }
    }
}
