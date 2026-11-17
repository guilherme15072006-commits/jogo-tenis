using System;
using System.Collections.Generic;

namespace Tenis.Core
{
    public enum ServeType { Flat, Topspin, Slice }
    public enum ServePhase { Positioning, Toss, Preparation, Swing, Contact, Finish, Done }
    public enum ServeVerdict { None, In, Fault, Let, DoubleFault, TossCaught }

    [Serializable]
    public struct ServeResult
    {
        public ServeVerdict verdict;
        public int serveNumber;           // 1 or 2 (after the verdict is applied: the NEXT serve number)
        public ServeType type;
        public TimingResult timing;
        public double ballSpeed;
        public Vec3 intendedTarget, aimedTarget;
        public BallState launch;
        public List<BallEvent> events;
        public string Describe() => $"saque {type} {ballSpeed * 3.6:0} km/h → {verdict} (timing {timing.grade})";
    }

    /// <summary>Rules: valid service box, fault, let, first/second serve, double fault.</summary>
    public static class ServeRules
    {
        /// <summary>Judges a served ball from its simulated events (net touches and first bounce).</summary>
        public static ServeVerdict Judge(List<BallEvent> events, CourtSide server, ServeSide side, double ballRadius)
        {
            bool touchedTape = false;
            foreach (var e in events)
            {
                if (e.type == BallEventType.NetBody) return ServeVerdict.Fault;
                if (e.type == BallEventType.NetCord) { touchedTape = true; continue; }
                if (e.type == BallEventType.Bounce)
                {
                    bool inBox = Court.InServiceBox(e.before.position, server, side, ballRadius);
                    if (!inBox) return ServeVerdict.Fault;
                    return touchedTape ? ServeVerdict.Let : ServeVerdict.In;
                }
            }
            return ServeVerdict.Fault;
        }

        /// <summary>Applies the verdict to the serve counter. Returns the verdict as seen by the match
        /// (a fault on the 2nd serve becomes a double fault) and the next serve number.</summary>
        public static (ServeVerdict, int next) Apply(ServeVerdict v, int serveNumber)
        {
            switch (v)
            {
                case ServeVerdict.Let: return (v, serveNumber);                     // replay the same serve
                case ServeVerdict.TossCaught: return (v, serveNumber);
                case ServeVerdict.Fault: return serveNumber == 1 ? (ServeVerdict.Fault, 2) : (ServeVerdict.DoubleFault, 1);
                default: return (v, 1);
            }
        }
    }

    /// <summary>
    /// Serve state machine: positioning → toss (real ball physics) → preparation → swing (gesture) → contact → finish.
    /// The gesture's END is the contact moment; timing is graded against the toss apex.
    /// Shape: Horizontal / VerticalDown → flat, DiagonalUp / VerticalUp → topspin (kick), DiagonalDown → slice.
    /// Lateral intent picks T (centre line) ↔ wide.
    /// </summary>
    public sealed class ServeFlow
    {
        readonly TennisConfig _cfg; readonly BallSimulator _sim;
        public ServePhase Phase { get; private set; } = ServePhase.Positioning;
        public int ServeNumber { get; private set; } = 1;
        public CourtSide Court_ { get; private set; }
        public ServeSide Side { get; private set; }
        public BallState TossBall { get; private set; }
        public double TossStart { get; private set; }
        public double ApexTime { get; private set; }
        public Vec3 ServerPos { get; set; }

        public ServeFlow(TennisConfig cfg, BallSimulator sim) { _cfg = cfg; _sim = sim; }

        public void BeginPoint(CourtSide court, ServeSide side)
        {
            Court_ = court; Side = side; ServeNumber = 1; Phase = ServePhase.Positioning;
            ServerPos = Court.ServerPosition(court, side);
        }

        public void ResetForNextServe() { Phase = ServePhase.Positioning; }

        /// <summary>Moves along the baseline during positioning (cannot cross the centre mark).</summary>
        public void Reposition(double lateralMetres)
        {
            if (Phase != ServePhase.Positioning) return;
            double rightY = Court.RightY(Court_);
            double sideSign = Side == ServeSide.Deuce ? rightY : -rightY;   // must stay on its half
            double y = ServerPos.y + lateralMetres;
            double lo = sideSign > 0 ? 0.15 : -Court.HalfSinglesWidth, hi = sideSign > 0 ? Court.HalfSinglesWidth : -0.15;
            ServerPos = new Vec3(ServerPos.x, MathD.Clamp(y, lo, hi), 0);
        }

        /// <summary>Starts the toss: the ball is thrown up in front of the server and simulated.</summary>
        public void Toss(double now)
        {
            if (Phase != ServePhase.Positioning) return;
            double g = _cfg.ball.gravity, z0 = 1.9, apex = _cfg.serve.tossApexHeight;
            double vz = Math.Sqrt(2 * g * (apex - z0));
            int s = Court.Sign(Court_);
            Vec3 p = ServerPos + new Vec3(-s * 0.35, Court.RightY(Court_) * 0.25, z0);   // a bit inside the court and to the hitting side
            TossBall = new BallState(p, new Vec3(0, 0, vz), Vec3.Zero, now);
            TossStart = now; ApexTime = now + vz / g;
            Phase = ServePhase.Toss;
        }

        /// <summary>Ball position of the toss at time t (for the view). Drag is negligible on the toss.</summary>
        public Vec3 TossPosition(double t)
        {
            double dt = t - TossStart; var b = TossBall;
            return b.position + new Vec3(0, 0, b.velocity.z * dt - 0.5 * _cfg.ball.gravity * dt * dt);
        }

        public static ServeType TypeFromShape(GestureShape s) =>
            s == GestureShape.DiagonalUp || s == GestureShape.VerticalUp ? ServeType.Topspin :
            s == GestureShape.DiagonalDown ? ServeType.Slice : ServeType.Flat;

        /// <summary>Executes the serve from a gesture that ENDED at <paramref name="swingTime"/>.</summary>
        public ServeResult Swing(GestureResult g, double swingTime, AthleteProfile a, ulong matchSeed, int shotIndex)
        {
            var r = new ServeResult { serveNumber = ServeNumber };
            if (Phase != ServePhase.Toss && Phase != ServePhase.Preparation) { r.verdict = ServeVerdict.None; return r; }
            var sc = _cfg.serve;
            if (!g.accepted || swingTime - TossStart > sc.maxGestureAfterToss)
            {   // no valid swing: the server catches the toss, no fault
                r.verdict = ServeVerdict.TossCaught; Phase = ServePhase.Positioning; return r;
            }
            Phase = ServePhase.Contact;
            r.type = TypeFromShape(g.shape);
            // timing vs. apex
            double ideal = ApexTime - sc.idealContactBeforeApex;
            double err = swingTime - ideal; double w = sc.contactWindow;
            var t = new TimingResult { errorSeconds = err, window = w, early = err < 0 };
            double e = Math.Abs(err);
            t.grade = e <= w * 0.25 ? TimingGrade.Perfect : e <= w * 0.5 ? TimingGrade.VeryGood : e <= w ? TimingGrade.Good : e <= 2 * w ? TimingGrade.Late : TimingGrade.Fail;
            t.quality = t.grade == TimingGrade.Fail ? 0.15 : MathD.Clamp01(1 - 0.5 * (e / w) * (e / w) / 4);
            r.timing = t;
            Vec3 contact = TossPosition(swingTime);
            contact.z = Math.Max(contact.z, 2.2);
            // power
            var d = new ShotDecision { stroke = Stroke.Serve, spin = r.type == ServeType.Topspin ? SpinType.Topspin : r.type == ServeType.Slice ? SpinType.Slice : SpinType.Flat };
            var (rs, bs, p01) = PowerModel.Compute(g.f, d, 1.0, 1.0, t.quality, 0, a, _cfg);
            double typeKeep = r.type == ServeType.Flat ? 1.0 : r.type == ServeType.Slice ? 0.86 : 0.78;
            bs = bs * typeKeep;
            if (ServeNumber == 2) bs = Math.Min(bs, _cfg.power.maxServeSpeed * sc.secondServePowerCap);
            r.ballSpeed = bs;
            // target inside the correct box: lateral intent from T (0) to wide (1)
            CourtSide target = Court.Opposite(Court_);
            int ts = Court.Sign(target);
            double serverLeftY = -Court.RightY(Court_);
            double boxSign = Side == ServeSide.Deuce ? serverLeftY : -serverLeftY;   // which half of the box (in world y)
            double towardsWide = MathD.Clamp01(0.5 + 0.5 * g.lateralIntent * (Side == ServeSide.Deuce ? -1 : 1));
            double y = boxSign * MathD.Lerp(0.45, Court.HalfSinglesWidth - 0.45, towardsWide);
            r.intendedTarget = new Vec3(ts * (Court.ServiceLine - 0.9), y, 0);
            double sigma = 0.35 * a.AccuracyFactor * (1 + 1.4 * p01 * p01) * (t.grade == TimingGrade.Perfect ? 0.7 : t.grade == TimingGrade.VeryGood ? 0.9 : t.grade == TimingGrade.Good ? 1.2 : 2.4);
            var rng = DetRandom.ForShot(matchSeed, shotIndex);
            r.aimedTarget = new Vec3(r.intendedTarget.x + rng.NextGaussian() * sigma * 1.2, r.intendedTarget.y + rng.NextGaussian() * sigma, 0);
            double top = r.type == ServeType.Topspin ? sc.topspinSpin : r.type == ServeType.Slice ? 80 : sc.flatSpin;
            double side = r.type == ServeType.Slice ? sc.sliceSpin * -Court.RightY(Court_) * (a.dominantHand == Hand.Right ? 1 : -1) : 0;
            double clear = r.type == ServeType.Topspin ? sc.topspinClearance : r.type == ServeType.Slice ? sc.sliceClearance : sc.flatClearance;
            r.launch = ShotSolver.Solve(_sim, _cfg.net, contact, r.aimedTarget, bs, top, side, clear, false);
            r.launch.time = swingTime;
            r.events = _sim.Simulate(r.launch, 1);
            var verdict = ServeRules.Judge(r.events, Court_, Side, _cfg.ball.radius);
            var (v, next) = ServeRules.Apply(verdict, ServeNumber);
            r.verdict = v; ServeNumber = next; r.serveNumber = next;
            Phase = v == ServeVerdict.In ? ServePhase.Done : ServePhase.Finish;
            return r;
        }
    }
}
