using System;
using System.Collections.Generic;

namespace Tenis.Core
{
    public enum Stroke { Forehand, Backhand, ForehandVolley, BackhandVolley, BlockVolley, Smash, Serve }
    public enum SpinType { Flat, Topspin, Slice, Lob, DropShot }
    public enum TimingGrade { Perfect, VeryGood, Good, Late, Fail }

    /// <summary>Where and when the incoming ball will be met, predicted with the real simulator.</summary>
    [Serializable]
    public struct ContactPlan
    {
        public bool valid;
        public BallState ball;          // ball at the contact instant
        public bool bounced;            // contact after the bounce (groundstroke) or before (volley/smash)
        public double contactTime;      // absolute simulation time of the ideal contact
        public double timeAvailable;    // contactTime - now
        public double lateralOffset;    // + = on the player's dominant-hand side (forehand side)
        public double reachDistance;    // horizontal distance body→ball at contact
        public double incomingSpeed;
    }

    [Serializable]
    public struct ShotDecision
    {
        public Stroke stroke;
        public SpinType spin;
        public bool directedVolley;
        public bool twoHanded;
        public string Describe() => $"{stroke}{(stroke == Stroke.Backhand ? (twoHanded ? "(2 mãos)" : "(1 mão)") : "")} {spin}{(directedVolley ? " direcionado" : "")}";
    }

    [Serializable]
    public struct TimingResult
    {
        public TimingGrade grade;
        public double errorSeconds;     // + = late, - = early
        public double window;           // seconds of the "Bom" window for this situation
        public double quality;          // 0..1 contact quality used by power/precision
        public bool early;
    }

    [Serializable]
    public struct ShotOutcome
    {
        public bool contact;            // false = whiff (Falha / unreachable)
        public ShotDecision decision;
        public TimingResult timing;
        public double power01;
        public double racketSpeed;
        public double ballSpeed;
        public Vec3 intendedTarget;
        public Vec3 aimedTarget;        // after error
        public double errorSigma;
        public BallState launch;        // state handed to the simulator (never an animation)
        public string Describe() => contact
            ? $"{decision.Describe()} | {timing.grade} ({timing.errorSeconds * 1000:+0;-0} ms) | {ballSpeed * 3.6:0} km/h | erro σ={errorSigma:0.00} m"
            : $"FALHA ({timing.grade})";
    }

    // ===================================================================================== context
    public static class ContactPredictor
    {
        /// <summary>
        /// Predicts the ideal contact: the ball crossing the player's depth plane. Near the net the ball is met
        /// before the bounce (volley), otherwise after the first bounce. Falls back to the point after the bounce
        /// where the ball is at comfortable height if it never reaches the player's plane.
        /// </summary>
        public static ContactPlan Predict(BallSimulator sim, BallState incoming, PlayerMotor p, AthleteProfile a,
                                          ShotRulesConfig rules, TimingConfig tc, double now)
        {
            var plan = new ContactPlan();
            var path = new List<BallState>();
            sim.Simulate(incoming, 2, path);
            if (path.Count < 2) return plan;
            int s = Court.Sign(p.side);
            double distNet = Math.Abs(p.position.x);
            bool volleyZone = distNet <= rules.volleyMaxDistanceFromNet;
            bool bounced = false; int bestIdx = -1; bool bestBounced = false; double bestScore = double.MaxValue;
            for (int i = 1; i < path.Count; i++)
            {
                var prev = path[i - 1]; var cur = path[i];
                if (cur.velocity.z > 0 && prev.velocity.z < 0 && cur.position.z < 0.2) bounced = true;
                if (Court.SideOf(cur.position.x) != p.side) continue;
                // crossing of the player's depth plane
                bool crosses = (prev.position.x - p.position.x) * (cur.position.x - p.position.x) <= 0;
                if (crosses)
                {
                    bool tooHighForGround = !bounced && cur.position.z > rules.smashMinBallHeight;
                    if (!bounced && !(volleyZone || tooHighForGround)) continue;   // let it bounce
                    bestIdx = i; bestBounced = bounced; break;
                }
            }
            if (bestIdx < 0)
            {
                // ball never reaches the player's plane: choose the best point after the bounce
                bounced = false; bestScore = double.MaxValue;
                for (int i = 1; i < path.Count; i++)
                {
                    if (path[i].velocity.z > 0 && path[i - 1].velocity.z < 0 && path[i].position.z < 0.2) bounced = true;
                    if (!bounced || Court.SideOf(path[i].position.x) != p.side) continue;
                    double score = Math.Abs(path[i].position.z - tc.idealContactHeight) + 0.5 * (path[i].position - p.position).Horizontal.Length;
                    if (score < bestScore) { bestScore = score; bestIdx = i; bestBounced = true; }
                }
            }
            if (bestIdx < 0) return plan;
            var b = path[bestIdx];
            plan.valid = true;
            plan.ball = b;
            plan.bounced = bestBounced;
            plan.contactTime = b.time;
            plan.timeAvailable = b.time - now;
            double rightY = Court.RightY(p.side);
            double handSign = a.dominantHand == Hand.Right ? 1 : -1;
            plan.lateralOffset = (b.position.y - p.position.y) * rightY * handSign;
            plan.reachDistance = (b.position - p.position).Horizontal.Length;
            plan.incomingSpeed = b.velocity.Length;
            return plan;
        }
    }

    // ===================================================================================== selection
    public static class ShotSelector
    {
        public static ShotDecision Select(GestureResult g, ContactPlan c, PlayerMotor p, AthleteProfile a, ShotRulesConfig r)
        {
            var d = new ShotDecision();
            bool forehandSide = c.lateralOffset >= 0;
            d.twoHanded = a.backhand == BackhandStyle.TwoHanded;
            double h = c.ball.position.z;
            bool high = h >= r.smashMinBallHeight;

            if (!c.bounced)
            {
                if (high && (g.shape == GestureShape.VerticalDown || g.shape == GestureShape.DiagonalDown || g.shape == GestureShape.Horizontal))
                { d.stroke = Stroke.Smash; d.spin = SpinType.Flat; return d; }
                if (g.shape == GestureShape.VerticalUp) { d.stroke = forehandSide ? Stroke.ForehandVolley : Stroke.BackhandVolley; d.spin = SpinType.Lob; return d; }
                bool block = c.incomingSpeed >= r.blockVolleyIncomingSpeed || c.timeAvailable < r.blockVolleyTime;
                d.stroke = block ? Stroke.BlockVolley : (forehandSide ? Stroke.ForehandVolley : Stroke.BackhandVolley);
                d.spin = g.shape == GestureShape.Tap ? SpinType.DropShot : SpinType.Slice;
                d.directedVolley = !block && Math.Abs(g.lateralIntent) > 0.5;
                return d;
            }

            d.stroke = forehandSide ? Stroke.Forehand : Stroke.Backhand;
            switch (g.shape)
            {
                case GestureShape.Horizontal: d.spin = SpinType.Flat; break;
                case GestureShape.DiagonalUp: d.spin = SpinType.Topspin; break;
                case GestureShape.DiagonalDown: d.spin = SpinType.Slice; break;
                case GestureShape.VerticalUp: d.spin = SpinType.Lob; break;
                case GestureShape.VerticalDown: d.spin = SpinType.Slice; break;
                case GestureShape.Tap:
                    // a drop shot needs a ball that is not too fast; otherwise it becomes a short defensive slice
                    d.spin = c.incomingSpeed <= r.dropShotMaxIncomingSpeed ? SpinType.DropShot : SpinType.Slice; break;
                default: d.spin = SpinType.Flat; break;
            }
            return d;
        }
    }

    // ===================================================================================== timing
    public static class TimingModel
    {
        public static TimingResult Evaluate(double swingTime, ContactPlan c, ShotDecision d, PlayerMotor p, AthleteProfile a,
                                            TimingConfig t, double prepSeconds)
        {
            var r = new TimingResult();
            double w = t.baseWindow;
            w *= MathD.Clamp(t.referenceBallSpeed / Math.Max(5, c.incomingSpeed), 0.55, 1.6);          // speed
            double h = c.ball.position.z;
            if (h < t.awkwardHeightLow) w *= MathD.Lerp(0.6, 1, h / t.awkwardHeightLow);              // height
            else if (h > t.awkwardHeightHigh && d.stroke != Stroke.Smash) w *= MathD.Lerp(1, 0.65, MathD.Clamp01((h - t.awkwardHeightHigh) / 1.0));
            double reach = a.ReachMetres(t);
            double stretch = MathD.Clamp01((c.reachDistance - t.reachComfort) / Math.Max(0.01, reach - t.reachComfort));
            w *= MathD.Lerp(1, 0.55, stretch);                                                         // distance
            if (d.stroke == Stroke.Smash) w *= t.smashWindowFactor;                                    // type
            else if (d.stroke == Stroke.ForehandVolley || d.stroke == Stroke.BackhandVolley || d.stroke == Stroke.BlockVolley) w *= t.volleyWindowFactor;
            if (d.spin == SpinType.DropShot) w *= t.dropShotWindowFactor;
            w *= MathD.Lerp(0.75, 1.0, MathD.Clamp01(prepSeconds / 0.35));                            // preparation
            w *= Math.Max(0.5, 1 - t.runSpeedPenalty * p.Speed);                                       // moving fast
            r.window = w;
            r.errorSeconds = swingTime - c.contactTime;
            r.early = r.errorSeconds < 0;
            double e = Math.Abs(r.errorSeconds);
            if (c.reachDistance > reach) { r.grade = TimingGrade.Fail; r.quality = 0; return r; }
            if (e <= w * t.perfectFraction) r.grade = TimingGrade.Perfect;
            else if (e <= w * t.veryGoodFraction) r.grade = TimingGrade.VeryGood;
            else if (e <= w) r.grade = TimingGrade.Good;
            else if (e <= w * t.lateFraction) r.grade = TimingGrade.Late;
            else r.grade = TimingGrade.Fail;
            r.quality = r.grade == TimingGrade.Fail ? 0 : MathD.Clamp01(1 - 0.55 * (e / w) * (e / w) / (t.lateFraction * t.lateFraction) - 0.25 * stretch);
            return r;
        }
    }

    // ===================================================================================== power
    public static class PowerModel
    {
        /// <summary>Normalised swing intent 0..1 from finger speed (soft-saturated so absurd flicks don't explode)
        /// and amplitude.</summary>
        public static double SwingIntent(GestureFeatures f, PowerConfig c)
        {
            double sp = MathD.SoftSaturate(f.peakSpeed / c.fingerSpeedForFullSwing * 1.3);
            double am = MathD.SoftSaturate(f.length / c.amplitudeForFullSwing * 1.3);
            return MathD.Clamp01(c.speedWeight * sp + c.amplitudeWeight * am);
        }

        public static double MaxBallSpeed(ShotDecision d, PowerConfig c)
        {
            if (d.stroke == Stroke.Serve) return c.maxServeSpeed;
            if (d.stroke == Stroke.Smash) return c.maxSmashSpeed;
            if (d.spin == SpinType.DropShot) return c.maxDropShotSpeed;
            if (d.spin == SpinType.Lob) return c.maxLobSpeed;
            if (d.stroke == Stroke.ForehandVolley || d.stroke == Stroke.BackhandVolley || d.stroke == Stroke.BlockVolley) return c.maxVolleySpeed;
            return c.maxGroundstrokeSpeed;
        }

        /// <summary>Racket-head speed (m/s) from intent × preparation × balance × athlete, and ball speed via the racket impact model.</summary>
        public static (double racketSpeed, double ballSpeed, double power01) Compute(
            GestureFeatures f, ShotDecision d, double prepQuality01, double balance01, double contactQuality01,
            double incomingSpeed, AthleteProfile a, TennisConfig cfg)
        {
            var c = cfg.power;
            double intent = SwingIntent(f, c);
            double prep = MathD.Lerp(c.prepMinFactor, 1, prepQuality01);
            double bal = MathD.Lerp(c.balanceMinFactor, 1, balance01);
            double strength = d.stroke == Stroke.Serve ? 0.85 + 0.30 * a.serve : a.StrengthFactor;
            double typeFactor = d.spin == SpinType.Topspin ? 0.92 : d.spin == SpinType.Slice ? 0.78 : d.spin == SpinType.Lob ? 0.55
                              : d.spin == SpinType.DropShot ? 0.25 : d.stroke == Stroke.BlockVolley ? 0.30 : 1.0;
            double racket = MathD.Lerp(c.minRacketSpeed, c.maxRacketSpeed, intent) * prep * bal * strength * typeFactor;
            double ball = RacketImpact.OutgoingSpeed(racket, incomingSpeed, contactQuality01, cfg.racket);
            double cap = MaxBallSpeed(d, c) * (d.stroke == Stroke.Serve ? 0.9 + 0.2 * a.serve : 0.9 + 0.2 * a.strength);
            ball = Math.Min(ball, cap);
            return (racket, ball, MathD.Clamp01(ball / cap));
        }
    }

    public static class RacketImpact
    {
        /// <summary>Apparent-COR racket model: v_out = e_A·v_in + (1+e_A)·v_racket (along the swing line).
        /// Off-centre/poorly timed hits lower e_A, so blocked volleys use the incoming pace and mishits die.</summary>
        public static double OutgoingSpeed(double racketSpeed, double incomingSpeed, double contactQuality01, RacketConfig rc)
        {
            double eA = rc.apparentRestitution * (1 - rc.offCentreRestitutionLoss * (1 - contactQuality01));
            return eA * incomingSpeed + (1 + eA) * racketSpeed * MathD.Lerp(0.75, 1, contactQuality01);
        }
    }

    // ===================================================================================== aim + solver
    public static class AimModel
    {
        public static Vec3 IntendedTarget(CourtSide hitter, GestureResult g, ShotDecision d, AimConfig c)
        {
            AimDepth depth = (d.spin == SpinType.DropShot || g.shortIntent) ? AimDepth.Short : AimDepth.Deep;
            if (d.stroke == Stroke.BlockVolley && g.shortIntent) depth = AimDepth.Short;
            return Court.AimTarget(hitter, depth, AimLateral.Centre, c, g.lateralIntent);
        }

        public static double ErrorSigma(double power01, TimingResult t, double balance01, double stretch01, ShotDecision d, AthleteProfile a, AimConfig c)
        {
            double s = c.baseErrorMetres * a.AccuracyFactor;
            s *= 1 + c.powerErrorGrowth * power01 * power01;                                // intensity
            s *= t.grade == TimingGrade.Perfect ? 0.7 : t.grade == TimingGrade.VeryGood ? 0.9 : t.grade == TimingGrade.Good ? 1.2 : 2.2; // contact
            s *= MathD.Lerp(1.6, 1.0, balance01);                                            // body position / physical state
            s *= 1 + 0.8 * stretch01;
            if (d.spin == SpinType.DropShot) s *= 0.8 + 0.6 * (1 - a.touch);
            if (d.stroke == Stroke.BlockVolley) s *= 1.3;
            return s;
        }

        /// <summary>Deterministic gaussian landing error plus a systematic pull for early/late contact
        /// (late forehand of a right-hander goes to his right, early pulls left; inverted on the backhand).</summary>
        public static Vec3 ApplyError(Vec3 target, double sigma, TimingResult t, ShotDecision d, CourtSide hitter, AthleteProfile a, DetRandom rng)
        {
            double ex = rng.NextGaussian() * sigma * 1.3;   // depth error is larger than lateral in real tennis
            double ey = rng.NextGaussian() * sigma;
            double handSign = a.dominantHand == Hand.Right ? 1 : -1;
            bool backhandFamily = d.stroke == Stroke.Backhand || d.stroke == Stroke.BackhandVolley;
            double pull = MathD.Clamp(t.errorSeconds / Math.Max(0.02, t.window), -2, 2) * 0.9 * (backhandFamily ? -1 : 1) * handSign;
            ey += pull * Court.RightY(hitter);
            return new Vec3(target.x + ex, target.y + ey, 0);
        }
    }

    /// <summary>
    /// Finds launch direction so that a ball with given speed and spin lands on a target, using the real
    /// simulator (so Magnus/drag are honoured). Lob uses the high branch; others the low branch with a
    /// minimum net clearance. If the requested speed cannot land in (too fast for the spin), speed is reduced.
    /// </summary>
    public static class ShotSolver
    {
        public static Vec3 SpinVector(Vec3 dirHoriz, double topspin, double sidespin)
        {
            // topspin axis = horizontal, perpendicular to travel: ω = (up × dir) * topspin  → for dir = +x gives +y
            Vec3 axis = Vec3.Cross(Vec3.Up, dirHoriz.Normalized);
            return axis * topspin + Vec3.Up * sidespin;
        }

        public static BallState Solve(BallSimulator sim, NetConfig net, Vec3 from, Vec3 target, double speed,
                                      double topspin, double sidespin, double clearance, bool highBranch)
        {
            BallState best = default; double bestErr = double.MaxValue;
            Vec3 aimPoint = target;
            for (int pass = 0; pass < 3; pass++)
            {
                Vec3 dir = (aimPoint - from).Horizontal.Normalized;
                Vec3 spin = SpinVector(dir, topspin, sidespin);
                double targetDist = (target - from).Horizontal.Length;
                double v = speed;
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (TrySolveElevation(sim, net, from, dir, spin, v, targetDist, clearance, highBranch, out var st, out var landing))
                    {
                        double err = (landing - target).Horizontal.Length;
                        if (err < bestErr) { bestErr = err; best = st; }
                        // correct azimuth for side drift
                        Vec3 miss = (target - landing).Horizontal;
                        aimPoint = aimPoint + new Vec3(miss.x, miss.y, 0);
                        break;
                    }
                    v *= 0.88;                                    // cannot keep it in at this pace → hit softer
                }
                if (bestErr < 0.05) break;
            }
            return best;
        }

        static bool TrySolveElevation(BallSimulator sim, NetConfig net, Vec3 from, Vec3 dir, Vec3 spin, double v,
                                      double targetDist, double clearance, bool highBranch, out BallState launch, out Vec3 landing)
        {
            launch = default; landing = default;
            double Range(double elevDeg, out BallState l, out Vec3 land, out bool cleared)
            {
                double e = MathD.Deg2Rad(elevDeg);
                Vec3 vel = dir * (v * Math.Cos(e)) + Vec3.Up * (v * Math.Sin(e));
                l = new BallState(from, vel, spin, 0);
                var path = new List<BallState>();
                var ev = sim.SimulateToFirstEvent(l, path);
                land = ev.before.position;
                cleared = ev.type == BallEventType.Bounce && ClearsNet(path, net, clearance, sim);
                if (ev.type != BallEventType.Bounce) return -1;   // net or timeout → treat as short
                return (land - from).Horizontal.Length * (Vec3.Dot((land - from).Horizontal, dir) >= 0 ? 1 : -1);
            }
            // coarse scan
            double lo = double.NaN, hi = double.NaN; double prevR = double.NaN, prevE = double.NaN;
            double start = highBranch ? 75 : -12, end = highBranch ? 20 : 55, step = highBranch ? -1.5 : 1.5;
            for (double e = start; highBranch ? e >= end : e <= end; e += step)
            {
                double r = Range(e, out _, out _, out bool cl);
                bool ok = r > 0 && cl;
                if (!double.IsNaN(prevR) && ok && prevR > 0 && ((prevR - targetDist) * (r - targetDist) <= 0)) { lo = prevE; hi = e; break; }
                if (ok) { prevR = r; prevE = e; } else { prevR = double.NaN; }
            }
            if (double.IsNaN(lo)) return false;
            for (int i = 0; i < 28; i++)
            {
                double mid = (lo + hi) / 2;
                double r = Range(mid, out _, out _, out bool cl);
                double rlo = Range(lo, out _, out _, out _);
                if (!cl || r <= 0) { lo = mid; continue; }
                if ((rlo - targetDist) * (r - targetDist) <= 0) hi = mid; else lo = mid;
            }
            double fin = Range((lo + hi) / 2, out launch, out landing, out bool okc);
            return fin > 0 && okc;
        }

        static bool ClearsNet(List<BallState> path, NetConfig net, double clearance, BallSimulator sim)
        {
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i - 1].position; var b = path[i].position;
                if (Math.Sign(a.x) != Math.Sign(b.x))
                {
                    double t = MathD.Clamp01(-a.x / (b.x - a.x));
                    var p = Vec3.Lerp(a, b, t);
                    return p.z >= Court.NetHeight(p.y, net) + clearance;
                }
            }
            return true;   // never crossed (should not happen for a valid shot)
        }
    }

    // ===================================================================================== orchestration
    /// <summary>Gesture + context → full shot outcome, deterministic for a given (seed, shotIndex).</summary>
    public sealed class ShotResolver
    {
        readonly TennisConfig _cfg; readonly BallSimulator _sim;
        public ShotResolver(TennisConfig cfg, BallSimulator sim) { _cfg = cfg; _sim = sim; }

        public ShotOutcome Resolve(GestureResult g, ContactPlan c, PlayerMotor p, AthleteProfile a,
                                   double swingTime, double prepSeconds, ulong matchSeed, int shotIndex)
        {
            var o = new ShotOutcome();
            o.decision = ShotSelector.Select(g, c, p, a, _cfg.shots);
            o.timing = TimingModel.Evaluate(swingTime, c, o.decision, p, a, _cfg.timing, prepSeconds);
            if (!c.valid || o.timing.grade == TimingGrade.Fail) { o.contact = false; return o; }
            o.contact = true;
            double stretch = MathD.Clamp01((c.reachDistance - _cfg.timing.reachComfort) / Math.Max(0.01, a.ReachMetres(_cfg.timing) - _cfg.timing.reachComfort));
            double prepQ = MathD.Clamp01(prepSeconds / 0.35);
            var (rs, bs, p01) = PowerModel.Compute(g.f, o.decision, prepQ, p.balance, o.timing.quality, c.incomingSpeed, a, _cfg);
            o.racketSpeed = rs; o.ballSpeed = bs; o.power01 = p01;
            o.intendedTarget = AimModel.IntendedTarget(p.side, g, o.decision, _cfg.aim);
            o.errorSigma = AimModel.ErrorSigma(p01, o.timing, p.balance, stretch, o.decision, a, _cfg.aim);
            var rng = DetRandom.ForShot(matchSeed, shotIndex);
            o.aimedTarget = AimModel.ApplyError(o.intendedTarget, o.errorSigma, o.timing, o.decision, p.side, a, rng);
            var (top, clear, high) = SpinFor(o.decision);
            o.launch = ShotSolver.Solve(_sim, _cfg.net, c.ball.position, o.aimedTarget, bs, top, 0, clear, high);
            o.launch.time = c.contactTime;
            p.OnShot(stretch);
            return o;
        }

        (double spin, double clearance, bool high) SpinFor(ShotDecision d)
        {
            var r = _cfg.shots;
            if (d.stroke == Stroke.Smash) return (r.smashSpin, r.flatClearance * 0.5, false);
            if (d.stroke == Stroke.ForehandVolley || d.stroke == Stroke.BackhandVolley || d.stroke == Stroke.BlockVolley)
                return d.spin == SpinType.DropShot ? (r.dropSpin, r.dropClearance, false)
                     : d.spin == SpinType.Lob ? (r.lobSpin, r.lobClearance, true) : (r.volleySpin, r.volleyClearance, false);
            switch (d.spin)
            {
                case SpinType.Topspin: return (r.topspinSpin, r.topspinClearance, false);
                case SpinType.Slice: return (r.sliceSpin, r.sliceClearance, false);
                case SpinType.Lob: return (r.lobSpin, r.lobClearance, true);
                case SpinType.DropShot: return (r.dropSpin, r.dropClearance, false);
                default: return (r.flatSpin, r.flatClearance, false);
            }
        }
    }
}
