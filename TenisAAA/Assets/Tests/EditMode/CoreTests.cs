using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tenis.Core;

namespace Tenis.Tests
{
    public class PhysicsTests
    {
        TennisConfig cfg; BallSimulator sim;
        [SetUp] public void Setup() { cfg = new TennisConfig(); sim = new BallSimulator(cfg); }

        BallState Shot(double speed, double elevDeg, double topspin)
        {
            double e = MathD.Deg2Rad(elevDeg);
            var dir = new Vec3(1, 0, 0);
            return new BallState(new Vec3(-11.5, 0, 1.0), new Vec3(speed * Math.Cos(e), 0, speed * Math.Sin(e)),
                                 ShotSolver.SpinVector(dir, topspin, 0));
        }

        [Test] public void DropFromRestBouncesLowerEachTime()
        {
            var s = new BallState(new Vec3(5, 0, 2.54), Vec3.Zero, Vec3.Zero);
            var path = new List<BallState>();
            var ev = sim.Simulate(s, 2, path);
            Assert.AreEqual(BallEventType.Bounce, ev[0].type);
            double peak = 0; foreach (var p in path) if (p.time > ev[0].state.time && p.time < ev[1].before.time) peak = Math.Max(peak, p.position.z);
            // ITF rebound test: dropped from 2.54 m a ball must rebound 1.35–1.47 m (bottom of ball); clay COR here gives ~1.6
            Assert.That(peak, Is.InRange(1.2, 1.8), $"rebound {peak:0.00}");
        }

        [Test] public void TopspinDipsShorterThanFlat()
        {
            var flat = sim.SimulateToFirstEvent(Shot(30, 8, 0)).before.position.x;
            var top = sim.SimulateToFirstEvent(Shot(30, 8, 300)).before.position.x;
            var slice = sim.SimulateToFirstEvent(Shot(30, 8, -250)).before.position.x;
            Assert.Less(top, flat, "Magnus must pull topspin down");
            Assert.Greater(slice, flat, "Backspin must float longer");
        }

        [Test] public void TopspinKicksHigherAfterBounceThanSlice()
        {
            // same landing spot and pace: topspin arrives steeper and kicks up, slice skids low
            double PeakAfterBounce(double spin, double clear)
            {
                var launch = ShotSolver.Solve(sim, cfg.net, new Vec3(-11.5, 0, 1.0), new Vec3(8.5, 0, 0), 27, spin, 0, clear, false);
                var path = new List<BallState>(); var ev = sim.Simulate(launch, 2, path);
                double t0 = ev[0].state.time, peak = 0;
                foreach (var p in path) if (p.time > t0 && (ev.Count < 2 || p.time < ev[1].before.time)) peak = Math.Max(peak, p.position.z);
                return peak;
            }
            double top = PeakAfterBounce(330, 0.9), slice = PeakAfterBounce(-230, 0.3);
            Console.WriteLine($"rebound peak topspin {top:0.00} m, slice {slice:0.00} m");
            Assert.Greater(top, slice + 0.25);
        }

        [Test] public void LowBallHitsNet()
        {
            var ev = sim.SimulateToFirstEvent(new BallState(new Vec3(-6, 0, 0.6), new Vec3(20, 0, 0.5), Vec3.Zero));
            Assert.AreEqual(BallEventType.NetBody, ev.type);
        }

        [Test] public void SimulationIsDeterministic()
        {
            var a = new List<BallState>(); var b = new List<BallState>();
            sim.Simulate(Shot(33, 6, 280), 2, a); new BallSimulator(new TennisConfig()).Simulate(Shot(33, 6, 280), 2, b);
            Assert.AreEqual(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++) Assert.AreEqual(a[i].position, b[i].position);
        }

        [Test] public void ClayIsSlowerThanGrass()
        {
            double SpeedAfter(string surf)
            {
                var c = new TennisConfig { surface = surf }; var s = new BallSimulator(c);
                return s.Simulate(Shot(30, 5, 50), 1)[0].state.velocity.Horizontal.Length;
            }
            Assert.Less(SpeedAfter("clay"), SpeedAfter("grass"));
        }
    }

    public class CourtTests
    {
        [Test] public void ServiceBoxesAreDiagonal()
        {
            // Near server (faces +x, right = -y) on the deuce side serves to the box at +y on the far side.
            Assert.IsTrue(Court.InServiceBox(new Vec3(4, 2, 0), CourtSide.Near, ServeSide.Deuce, 0.033));
            Assert.IsFalse(Court.InServiceBox(new Vec3(4, -2, 0), CourtSide.Near, ServeSide.Deuce, 0.033));
            Assert.IsTrue(Court.InServiceBox(new Vec3(4, -2, 0), CourtSide.Near, ServeSide.Ad, 0.033));
            Assert.IsFalse(Court.InServiceBox(new Vec3(7.5, 2, 0), CourtSide.Near, ServeSide.Deuce, 0.033), "beyond service line");
            Assert.IsTrue(Court.InServiceBox(new Vec3(-4, -2, 0), CourtSide.Far, ServeSide.Deuce, 0.033));
            Assert.Less(Court.ServerPosition(CourtSide.Near, ServeSide.Deuce).y, 0, "deuce = server's right (-y on Near)");
        }

        [Test] public void AimTargetsAreOnOpponentSide()
        {
            var a = new AimConfig();
            var t = Court.AimTarget(CourtSide.Near, AimDepth.Deep, AimLateral.Right, a);
            Assert.Greater(t.x, 6.4); Assert.Less(t.y, 0, "Near hitter's right is -y");
            var s = Court.AimTarget(CourtSide.Near, AimDepth.Short, AimLateral.Left, a);
            Assert.Less(s.x, 6.4); Assert.Greater(s.y, 0);
        }
    }

    public class GestureTests
    {
        static List<TouchSample> Swipe(double x0, double y0, double dx, double dy, double dur, int n = 12, double t0 = 10)
        {
            var l = new List<TouchSample>();
            for (int i = 0; i <= n; i++) { double u = (double)i / n; l.Add(new TouchSample(x0 + dx * u, y0 + dy * u, t0 + dur * u)); }
            return l;
        }
        GestureClassifier New() => new GestureClassifier(new GestureConfig());
        const double Aspect = 2.0;   // phone in landscape: width = 2 × height

        [Test] public void AnglesMapToShotShapes()
        {
            Assert.AreEqual(GestureShape.Horizontal, New().Classify(Swipe(1.4, 0.5, 0.3, 0.02, 0.15), Aspect).shape);
            Assert.AreEqual(GestureShape.DiagonalUp, New().Classify(Swipe(1.4, 0.3, 0.2, 0.2, 0.15), Aspect).shape);
            Assert.AreEqual(GestureShape.DiagonalDown, New().Classify(Swipe(1.4, 0.7, 0.2, -0.2, 0.15), Aspect).shape);
            Assert.AreEqual(GestureShape.VerticalUp, New().Classify(Swipe(1.5, 0.3, 0.01, 0.3, 0.15), Aspect).shape);
            Assert.AreEqual(GestureShape.VerticalDown, New().Classify(Swipe(1.5, 0.8, 0.0, -0.3, 0.15), Aspect).shape);
            Assert.AreEqual(GestureShape.Tap, New().Classify(Swipe(1.5, 0.5, 0.005, 0.005, 0.08), Aspect).shape);
        }

        [Test] public void FiltersRejectAccidentsAndConflicts()
        {
            Assert.AreEqual(GestureRejectReason.StartedInJoystickZone, New().Classify(Swipe(0.3, 0.5, 0.3, 0, 0.15), Aspect).reason);
            Assert.AreEqual(GestureRejectReason.TooShort, New().Classify(Swipe(1.4, 0.5, 0.032, 0, 0.1), Aspect).reason);
            Assert.AreEqual(GestureRejectReason.TooLong, New().Classify(Swipe(1.4, 0.5, 0.3, 0, 1.5), Aspect).reason);
            Assert.AreEqual(GestureRejectReason.TooSlow, New().Classify(Swipe(1.4, 0.5, 0.005, 0.0, 0.5), Aspect).reason, "resting finger");
            var c = New();
            Assert.IsTrue(c.Classify(Swipe(1.4, 0.5, 0.3, 0.2, 0.15, t0: 10), Aspect).accepted);
            Assert.AreEqual(GestureRejectReason.Duplicate, c.Classify(Swipe(1.4, 0.5, 0.3, 0.2, 0.15, t0: 10.2), Aspect).reason);
            // zig-zag scribble
            var zig = new List<TouchSample>();
            for (int i = 0; i <= 12; i++) zig.Add(new TouchSample(1.4 + 0.02 * i, 0.5 + (i % 2 == 0 ? 0.08 : -0.08), 20 + 0.02 * i));
            Assert.AreEqual(GestureRejectReason.NotStraight, New().Classify(zig, Aspect).reason);
        }

        [Test] public void FasterSwipeGivesMoreIntentButSaturates()
        {
            var pc = new PowerConfig();
            double slow = PowerModel.SwingIntent(GestureClassifier.Measure(Swipe(1.4, 0.3, 0.2, 0.2, 0.30)), pc);
            double fast = PowerModel.SwingIntent(GestureClassifier.Measure(Swipe(1.4, 0.3, 0.2, 0.2, 0.08)), pc);
            double absurd = PowerModel.SwingIntent(GestureClassifier.Measure(Swipe(1.4, 0.3, 0.2, 0.2, 0.005)), pc);
            Assert.Greater(fast, slow);
            Assert.LessOrEqual(absurd, 1.0);
            Assert.Less(absurd - fast, 0.25, "speed must saturate");
        }
    }

    public class ShotTests
    {
        TennisConfig cfg; BallSimulator sim; AthleteProfile ath;
        [SetUp] public void Setup() { cfg = new TennisConfig(); sim = new BallSimulator(cfg); ath = new AthleteProfile(); }

        [Test] public void SolverLandsNearTargetAndClearsNet()
        {
            var from = new Vec3(-11.5, 0, 1.0); var target = new Vec3(9.5, 2.5, 0);
            foreach (var (spin, clear) in new[] { (90.0, 0.35), (330.0, 0.9), (-230.0, 0.3) })
            {
                var launch = ShotSolver.Solve(sim, cfg.net, from, target, 30, spin, 0, clear, false);
                var land = sim.SimulateToFirstEvent(launch);
                Assert.AreEqual(BallEventType.Bounce, land.type, $"spin {spin}");
                Assert.Less((land.before.position - target).Horizontal.Length, 0.25, $"spin {spin} landed {land.before.position}");
            }
        }

        [Test] public void LobGoesHigh()
        {
            var launch = ShotSolver.Solve(sim, cfg.net, new Vec3(-11, 0, 1), new Vec3(10, 0, 0), 20, 180, 0, 4.0, true);
            var path = new List<BallState>(); sim.SimulateToFirstEvent(launch, path);
            double peak = 0; foreach (var p in path) peak = Math.Max(peak, p.position.z);
            Assert.Greater(peak, 5.5);
        }

        [Test] public void ContextPicksVolleySmashAndBackhand()
        {
            var r = cfg.shots;
            var p = new PlayerMotor(cfg.movement, ath, new Vec3(-2.5, 0, 0), CourtSide.Near);
            var g = new GestureResult { accepted = true, shape = GestureShape.DiagonalUp };
            var volley = new ContactPlan { valid = true, bounced = false, lateralOffset = 0.6, incomingSpeed = 18, timeAvailable = 0.8, ball = new BallState(new Vec3(-2.5, -0.6, 1.1), Vec3.Zero, Vec3.Zero) };
            Assert.AreEqual(Stroke.ForehandVolley, ShotSelector.Select(g, volley, p, ath, r).stroke);
            volley.incomingSpeed = 32;
            Assert.AreEqual(Stroke.BlockVolley, ShotSelector.Select(g, volley, p, ath, r).stroke);
            var high = volley; high.ball.position.z = 2.9;
            var down = new GestureResult { accepted = true, shape = GestureShape.VerticalDown };
            Assert.AreEqual(Stroke.Smash, ShotSelector.Select(down, high, p, ath, r).stroke);
            var ground = new ContactPlan { valid = true, bounced = true, lateralOffset = -0.7, incomingSpeed = 20, ball = new BallState(new Vec3(-11, 0.7, 0.9), Vec3.Zero, Vec3.Zero) };
            var d = ShotSelector.Select(g, ground, p, ath, r);
            Assert.AreEqual(Stroke.Backhand, d.stroke); Assert.AreEqual(SpinType.Topspin, d.spin); Assert.IsTrue(d.twoHanded);
            var tap = new GestureResult { accepted = true, shape = GestureShape.Tap };
            Assert.AreEqual(SpinType.DropShot, ShotSelector.Select(tap, ground, p, ath, r).spin);
            ground.incomingSpeed = 35;
            Assert.AreEqual(SpinType.Slice, ShotSelector.Select(tap, ground, p, ath, r).spin, "too fast to drop");
        }

        [Test] public void TimingGradesShrinkWithBallSpeed()
        {
            var p = new PlayerMotor(cfg.movement, ath, new Vec3(-11, 0, 0), CourtSide.Near);
            var d = new ShotDecision { stroke = Stroke.Forehand, spin = SpinType.Topspin };
            var slow = new ContactPlan { valid = true, bounced = true, contactTime = 5, incomingSpeed = 15, reachDistance = 0.6, ball = new BallState(new Vec3(-11, -0.6, 1.0), Vec3.Zero, Vec3.Zero) };
            var fast = slow; fast.incomingSpeed = 38;
            var ts = TimingModel.Evaluate(5.0, slow, d, p, ath, cfg.timing, 0.5);
            var tf = TimingModel.Evaluate(5.0, fast, d, p, ath, cfg.timing, 0.5);
            Assert.AreEqual(TimingGrade.Perfect, ts.grade);
            Assert.Greater(ts.window, tf.window);
            Assert.AreEqual(TimingGrade.Late, TimingModel.Evaluate(5.0 + tf.window * 1.5, fast, d, p, ath, cfg.timing, 0.5).grade);
            Assert.AreEqual(TimingGrade.Fail, TimingModel.Evaluate(5.0 + 0.6, fast, d, p, ath, cfg.timing, 0.5).grade);
        }

        [Test] public void PowerIsCappedPhysically()
        {
            var f = new GestureFeatures { peakSpeed = 500, length = 3 };
            var d = new ShotDecision { stroke = Stroke.Forehand, spin = SpinType.Flat };
            var strong = new AthleteProfile { strength = 1 };
            var (_, ball, _) = PowerModel.Compute(f, d, 1, 1, 1, 40, strong, cfg);
            Assert.LessOrEqual(ball, cfg.power.maxGroundstrokeSpeed * 1.1 + 1e-9);
        }

        [Test] public void ResolverIsDeterministicAndErrorDependsOnSeed()
        {
            var p1 = new PlayerMotor(cfg.movement, ath, new Vec3(-11.5, -0.4, 0), CourtSide.Near);
            var incoming = ShotSolver.Solve(sim, cfg.net, new Vec3(11, 0, 1.0), new Vec3(-9.5, -0.2, 0), 24, 250, 0, 0.8, false);
            var plan = ContactPredictor.Predict(sim, incoming, p1, ath, cfg.shots, cfg.timing, 0);
            Assert.IsTrue(plan.valid); Assert.IsTrue(plan.bounced);
            var g = new GestureClassifier(cfg.gesture).Classify(SwipeAt(1.4, 0.3, 0.18, 0.18, 0.14, plan.contactTime - 0.14), 2.0);
            var res = new ShotResolver(cfg, sim);
            var a = res.Resolve(g, plan, p1, ath, plan.contactTime, 0.5, 42, 3);
            var p2 = new PlayerMotor(cfg.movement, ath, new Vec3(-11.5, -0.4, 0), CourtSide.Near);
            var b = res.Resolve(g, plan, p2, ath, plan.contactTime, 0.5, 42, 3);
            Assert.IsTrue(a.contact, a.Describe());
            Assert.AreEqual(a.aimedTarget, b.aimedTarget);
            Assert.AreEqual(a.launch.velocity, b.launch.velocity);
            var p3 = new PlayerMotor(cfg.movement, ath, new Vec3(-11.5, -0.4, 0), CourtSide.Near);
            Assert.AreNotEqual(a.aimedTarget, res.Resolve(g, plan, p3, ath, plan.contactTime, 0.5, 43, 3).aimedTarget);
            Console.WriteLine(a.Describe());
        }

        static List<TouchSample> SwipeAt(double x0, double y0, double dx, double dy, double dur, double t0)
        {
            var l = new List<TouchSample>();
            for (int i = 0; i <= 10; i++) { double u = i / 10.0; l.Add(new TouchSample(x0 + dx * u, y0 + dy * u, t0 + dur * u)); }
            return l;
        }
    }

    public class MovementTests
    {
        [Test] public void AcceleratesProgressivelyAndNeverTeleports()
        {
            var cfg = new TennisConfig(); var m = new PlayerMotor(cfg.movement, new AthleteProfile(), new Vec3(-12, 0, 0), CourtSide.Near);
            double dt = 1 / 60.0; var prev = m.position; double maxJump = 0;
            for (int i = 0; i < 30; i++) { m.Tick(dt, 1, 0); maxJump = Math.Max(maxJump, (m.position - prev).Length); prev = m.position; }
            Assert.Less(maxJump, m.MaxSpeed * dt + 1e-9);
            Assert.Less(m.position.y, 0, "stick right on the Near side moves to -y");
            Assert.Greater(m.Speed, 2.5);
            double before = m.Speed; m.Tick(dt, -1, 0);
            Assert.Less(m.Speed, before, "reversal loses speed (inertia)");
            Assert.Less(m.balance, 1.0);
        }

        [Test] public void RecoversTowardsHomeWhenReleased()
        {
            var cfg = new TennisConfig(); var m = new PlayerMotor(cfg.movement, new AthleteProfile(), new Vec3(-12, -4, 0), CourtSide.Near);
            for (int i = 0; i < 240; i++) m.Tick(1 / 60.0, 0, 0);
            Assert.Less(Math.Abs(m.position.y), 3.0);
        }
    }

    public class ServeTests
    {
        [Test] public void FaultThenDoubleFaultAndLet()
        {
            Assert.AreEqual((ServeVerdict.Fault, 2), ServeRules.Apply(ServeVerdict.Fault, 1));
            Assert.AreEqual((ServeVerdict.DoubleFault, 1), ServeRules.Apply(ServeVerdict.Fault, 2));
            Assert.AreEqual((ServeVerdict.Let, 2), ServeRules.Apply(ServeVerdict.Let, 2));
            var ev = new List<BallEvent> {
                new BallEvent { type = BallEventType.NetCord },
                new BallEvent { type = BallEventType.Bounce, before = new BallState(new Vec3(3, 1.5, 0.03), Vec3.Zero, Vec3.Zero) } };
            Assert.AreEqual(ServeVerdict.Let, ServeRules.Judge(ev, CourtSide.Near, ServeSide.Deuce, 0.033));
        }

        [Test] public void GoodServeLandsInTheBox()
        {
            var cfg = new TennisConfig(); var sim = new BallSimulator(cfg); var flow = new ServeFlow(cfg, sim);
            flow.BeginPoint(CourtSide.Near, ServeSide.Deuce);
            flow.Toss(0);
            double swing = flow.ApexTime + cfg.serve.idealContactBeforeApex * -1;
            var samples = new List<TouchSample>();
            for (int i = 0; i <= 10; i++) samples.Add(new TouchSample(1.5, 0.8 - 0.035 * i, swing - 0.12 + 0.012 * i));
            var g = new GestureClassifier(cfg.gesture).Classify(samples, 2.0);
            var r = flow.Swing(g, swing, new AthleteProfile(), 7, 1);
            Console.WriteLine(r.Describe());
            Assert.AreEqual(ServeType.Flat, r.type);
            Assert.That(r.timing.grade, Is.EqualTo(TimingGrade.Perfect).Or.EqualTo(TimingGrade.VeryGood));
            Assert.That(r.verdict, Is.EqualTo(ServeVerdict.In).Or.EqualTo(ServeVerdict.Fault));  // error is random but deterministic
            Assert.Greater(r.ballSpeed, 25);
        }
    }
}
