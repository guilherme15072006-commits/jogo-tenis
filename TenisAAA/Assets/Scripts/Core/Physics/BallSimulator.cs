using System;
using System.Collections.Generic;

namespace Tenis.Core
{
    [Serializable]
    public struct BallState
    {
        public Vec3 position;     // m
        public Vec3 velocity;     // m/s
        public Vec3 spin;         // angular velocity, rad/s (right-hand rule). Topspin for a ball travelling +x is spin.y > 0.
        public double time;       // s since launch

        public BallState(Vec3 p, Vec3 v, Vec3 w, double t = 0) { position = p; velocity = v; spin = w; time = t; }
    }

    public enum BallEventType { Bounce, NetCord, NetBody, OutOfTime }

    [Serializable]
    public struct BallEvent
    {
        public BallEventType type;
        public BallState state;   // state right AFTER the event
        public BallState before;  // state right BEFORE the event
    }

    /// <summary>
    /// Deterministic tennis-ball flight: gravity, quadratic drag, Magnus lift (spin-dependent lift coefficient),
    /// spin decay, bounce with friction/spin exchange (sliding vs. gripping), tape and net-body collisions.
    /// Fixed step + RK4 in double precision. Never replaced by canned trajectories.
    /// </summary>
    public sealed class BallSimulator
    {
        readonly BallConfig _b;
        readonly NetConfig _n;
        SurfaceConfig _surface;
        readonly double _dt, _area, _inertia;

        public BallSimulator(TennisConfig cfg)
        {
            _b = cfg.ball; _n = cfg.net; _surface = cfg.ActiveSurface;
            _dt = 1.0 / _b.stepHz;
            _area = Math.PI * _b.radius * _b.radius;
            _inertia = _b.shellInertiaFactor * _b.mass * _b.radius * _b.radius;
        }

        public double Dt => _dt;
        public SurfaceConfig Surface { get => _surface; set => _surface = value; }

        // ------------------------------------------------------------------ aerodynamics
        /// <summary>Lift coefficient from the spin parameter S = rω/v (empirical fit for tennis balls,
        /// C_L ≈ 1 / (2 + 1/S), saturating around 0.3–0.35).</summary>
        public static double LiftCoefficient(double v, double w, double r)
        {
            if (v < 1e-6 || w < 1e-6) return 0;
            double S = r * w / v;
            return 1.0 / (2.0 + 1.0 / S);
        }

        Vec3 Acceleration(Vec3 v, Vec3 w)
        {
            double speed = v.Length;
            Vec3 a = new Vec3(0, 0, -_b.gravity);
            if (speed > 1e-9)
            {
                double k = 0.5 * _b.airDensity * _area / _b.mass;
                a += v * (-k * _b.dragCoefficient * speed);                          // drag ∝ v²
                double wl = w.Length;
                if (wl > 1e-9)
                {
                    double cl = LiftCoefficient(speed, wl, _b.radius);
                    Vec3 dir = Vec3.Cross(w, v).Normalized;                          // Magnus direction ω × v
                    a += dir * (k * cl * speed * speed);
                }
            }
            return a;
        }

        /// <summary>One RK4 step in the air (no collisions).</summary>
        public BallState StepAir(BallState s)
        {
            double h = _dt; Vec3 p = s.position, v = s.velocity, w = s.spin;
            Vec3 k1v = Acceleration(v, w), k1p = v;
            Vec3 v2 = v + k1v * (h / 2); Vec3 k2v = Acceleration(v2, w), k2p = v2;
            Vec3 v3 = v + k2v * (h / 2); Vec3 k3v = Acceleration(v3, w), k3p = v3;
            Vec3 v4 = v + k3v * h;       Vec3 k4v = Acceleration(v4, w), k4p = v4;
            s.position = p + (k1p + 2 * k2p + 2 * k3p + k4p) * (h / 6);
            s.velocity = v + (k1v + 2 * k2v + 2 * k3v + k4v) * (h / 6);
            s.spin = w * Math.Exp(-_b.spinDecayPerSecond * h);
            s.time += h;
            return s;
        }

        // ------------------------------------------------------------------ bounce
        /// <summary>
        /// Rigid-body bounce: normal COR from the surface; tangential impulse from friction, limited to the
        /// impulse that would make the contact point stop slipping (ball starts rolling). This reproduces
        /// topspin "kicking" forward/high and slice skidding low.
        /// </summary>
        public BallState Bounce(BallState s)
        {
            double m = _b.mass, r = _b.radius;
            double vzIn = s.velocity.z;
            if (vzIn >= 0) return s;
            Vec3 v = s.velocity, w = s.spin;
            Vec3 rc = new Vec3(0, 0, -r);                                   // contact point from centre
            Vec3 vContact = new Vec3(v.x, v.y, 0) + Vec3.Cross(w, rc);      // tangential slip velocity
            vContact.z = 0;
            double slip = vContact.Length;
            double jn = m * (1 + _surface.restitution) * -vzIn;             // normal impulse magnitude
            Vec3 vOut = new Vec3(v.x, v.y, -_surface.restitution * vzIn);
            Vec3 wOut = w;
            if (slip > 1e-9)
            {
                double alpha = _inertia / (m * r * r);
                double jStop = m * slip / (1 + 1 / alpha);                  // impulse to reach rolling
                double jt = Math.Min(jStop, _surface.friction * jn);
                Vec3 J = vContact.Normalized * -jt;                         // friction opposes slip
                vOut += J / m;
                wOut += Vec3.Cross(rc, J) / _inertia;
            }
            wOut *= _surface.spinRetention;
            s.velocity = vOut; s.spin = wOut;
            s.position.z = r;
            return s;
        }

        // ------------------------------------------------------------------ net
        /// <summary>Checks the segment a→b against the net plane x = 0. Returns true and the adjusted state if the ball
        /// touched the net. Tape touch (net cord) keeps the ball going with a deterministic upward kick;
        /// body touch kills the ball on the hitter's side.</summary>
        bool NetCollision(BallState a, ref BallState b, out BallEventType type)
        {
            type = BallEventType.NetBody;
            if (Math.Sign(a.position.x) == Math.Sign(b.position.x) && Math.Abs(b.position.x) > _b.radius) return false;
            double dx = b.position.x - a.position.x;
            double t = Math.Abs(dx) < 1e-12 ? 0 : MathD.Clamp01((0 - a.position.x) / dx);
            Vec3 p = Vec3.Lerp(a.position, b.position, t);
            if (Math.Abs(p.y) > _n.postDistanceFromCentre + _b.radius) return false; // passed outside the posts
            double top = Court.NetHeight(p.y, _n);
            double bottom = p.z - _b.radius;
            if (bottom > top) return false;                                  // clean over the tape
            Vec3 v = b.velocity;
            int dir = Math.Sign(a.velocity.x == 0 ? -a.position.x : a.velocity.x);
            if (p.z > top - _n.tapeThickness)
            {
                // net cord: lose speed, get pushed up; it can still dribble over or fall back depending on speed
                type = BallEventType.NetCord;
                double keep = _n.cordSpeedKeep;
                double hit = MathD.Clamp01((top - bottom) / (2 * _b.radius)); // 0 = grazing, 1 = full on the tape
                double vx = v.x * keep * (1 - 0.8 * hit);
                double vz = Math.Abs(v.Length) * _n.cordUpwardBias * (1 - hit) + Math.Max(v.z, 0) * 0.3;
                b.position = new Vec3(dir * _b.radius * 1.05 * (vx * dir >= 0 ? 1 : -1), p.y, top + _b.radius);
                b.velocity = new Vec3(vx, v.y * keep, vz);
                b.spin = a.spin * 0.3;
            }
            else
            {
                type = BallEventType.NetBody;
                b.position = new Vec3(-dir * (_b.radius + 0.01), p.y, p.z);
                b.velocity = new Vec3(-v.x * _n.netBodyDamping, v.y * 0.3, Math.Min(v.z, 0) * 0.3);
                b.spin = Vec3.Zero;
            }
            return true;
        }

        // ------------------------------------------------------------------ public API
        /// <summary>Advances one fixed step, resolving net and ground collisions. Returns event if one happened.</summary>
        public BallState Step(BallState s, out BallEvent? evt)
        {
            evt = null;
            BallState next = StepAir(s);
            if (NetCollision(s, ref next, out var nt))
            {
                evt = new BallEvent { type = nt, before = s, state = next };
                return next;
            }
            if (next.position.z <= _b.radius && next.velocity.z < 0)
            {
                // interpolate to the exact contact time inside the step for an accurate landing point
                double z0 = s.position.z - _b.radius, z1 = next.position.z - _b.radius;
                double f = Math.Abs(z0 - z1) < 1e-12 ? 1 : MathD.Clamp01(z0 / (z0 - z1));
                BallState contact = next;
                contact.position = Vec3.Lerp(s.position, next.position, f);
                contact.velocity = Vec3.Lerp(s.velocity, next.velocity, f);
                contact.time = s.time + _dt * f;
                BallState after = Bounce(contact);
                // finish the remainder of the step with the post-bounce velocity
                after.position += after.velocity * (_dt * (1 - f));
                after.position.z = Math.Max(after.position.z, _b.radius);
                after.time = next.time;
                evt = new BallEvent { type = BallEventType.Bounce, before = contact, state = after };
                return after;
            }
            return next;
        }

        /// <summary>Simulates until the first bounce (or net/timeout). Returns the landing state and records the path.</summary>
        public BallEvent SimulateToFirstEvent(BallState s, List<BallState> path = null, double maxTime = -1)
        {
            if (maxTime < 0) maxTime = _b.maxFlightSeconds;
            double t0 = s.time;
            path?.Add(s);
            while (s.time - t0 < maxTime)
            {
                s = Step(s, out var e);
                path?.Add(s);
                if (e.HasValue) return e.Value;
            }
            return new BallEvent { type = BallEventType.OutOfTime, before = s, state = s };
        }

        /// <summary>Simulates a full rally ball: through net touches and up to <paramref name="maxBounces"/> bounces.</summary>
        public List<BallEvent> Simulate(BallState s, int maxBounces, List<BallState> path = null, double maxTime = -1)
        {
            var events = new List<BallEvent>();
            if (maxTime < 0) maxTime = _b.maxFlightSeconds;
            double t0 = s.time; int bounces = 0;
            path?.Add(s);
            while (s.time - t0 < maxTime && bounces < maxBounces)
            {
                s = Step(s, out var e);
                path?.Add(s);
                if (e.HasValue)
                {
                    events.Add(e.Value);
                    if (e.Value.type == BallEventType.Bounce) bounces++;
                    if (e.Value.type == BallEventType.NetBody && Math.Abs(s.velocity.x) < 0.5 && s.position.z < 0.2) break;
                }
            }
            return events;
        }
    }
}
