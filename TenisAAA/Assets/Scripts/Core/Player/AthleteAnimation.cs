using System;

namespace Tenis.Core
{
    public enum BodyAction { Ready, SplitStep, Running, Preparation, Swing, Contact, FollowThrough, Recovery, Serve, Smash, Brake }

    /// <summary>Smash is broken down as the spec asks: body preparation, feet, arm raise, aerial contact,
    /// energy transfer, finish and recovery.</summary>
    public enum SmashPhase { None, TurnSideways, FootPositioning, ArmRaise, AerialContact, EnergyTransfer, Finish, Recovery }
    public enum ServeBodyPhase { None, Stance, Toss, Trophy, Drop, Contact, FollowThrough, Recovery }

    /// <summary>
    /// Engine-independent animation state of an athlete, computed every frame from the motor and the shot timeline.
    /// The view (procedural rig now, Mixamo/mocap clips later) only reads these values:
    ///   runBlend 0..1, strideHz, leanForward/leanSide (body balance), stroke phase + normalised progress, smash/serve phase.
    /// </summary>
    [Serializable]
    public class AthleteAnimation
    {
        public BodyAction action = BodyAction.Ready;
        public SmashPhase smash = SmashPhase.None;
        public ServeBodyPhase serve = ServeBodyPhase.None;
        public Stroke stroke = Stroke.Forehand;
        public bool twoHanded;
        public double phaseProgress;        // 0..1 inside the current action
        public double runBlend;             // 0 idle .. 1 sprint
        public double strideHz;             // steps per second for the legs
        public double strideTime;           // accumulated cycle (radians) for leg swing
        public double leanForward;          // radians, + = leaning towards movement
        public double leanSide;             // radians, banking into direction changes / lost balance
        public double facingYaw;            // radians in court space (0 = facing +x)
        public double contactHeight;        // m, where the racket meets the ball (for arm IK)

        // shot timeline (absolute times)
        double _prepStart, _contactAt, _followEnd, _recoveryEnd;
        double _serveToss, _serveContact;
        bool _hasShot, _serving, _smashing;
        Vec3 _prevVel;

        public const double FollowThroughTime = 0.32, RecoveryTime = 0.35, SplitStepTime = 0.18;

        // timeline, read by clip-based views to time-warp captured animations so the contact frame hits the ball
        public double PrepStart => _prepStart;
        public double ContactAt => _serving ? _serveContact : _contactAt;
        public double ServeTossAt => _serveToss;
        public bool InShot => _hasShot || _serving;

        /// <summary>Gesture started: the athlete starts the backswing.</summary>
        public void BeginPreparation(double now, double expectedContact, Stroke s, bool twoHanded, double contactHeight)
        {
            _hasShot = true; _prepStart = now; _contactAt = Math.Max(now + 0.12, expectedContact);
            stroke = s; this.twoHanded = twoHanded; this.contactHeight = contactHeight;
            _smashing = s == Stroke.Smash;
            _followEnd = _contactAt + FollowThroughTime; _recoveryEnd = _followEnd + RecoveryTime;
        }

        /// <summary>Contact really happened (or the swing was resolved) at <paramref name="t"/>.</summary>
        public void Contact(double t, Stroke s)
        {
            if (!_hasShot) BeginPreparation(t - 0.25, t, s, twoHanded, contactHeight);
            stroke = s; _smashing = s == Stroke.Smash;
            _contactAt = t; _followEnd = t + FollowThroughTime; _recoveryEnd = _followEnd + RecoveryTime;
        }

        public void BeginServe(double tossTime, double expectedContact)
        {
            _serving = true; _hasShot = false; _serveToss = tossTime; _serveContact = expectedContact;
            stroke = Stroke.Serve;
        }
        public void ServeContact(double t) { _serveContact = t; }
        public void EndServe() { _serving = false; serve = ServeBodyPhase.None; }

        public double TurnRateDeg = 540;
        /// <summary>Set by the match: true while the predicted contact is still far away (long run).</summary>
        public bool FarFromBall;
        static double WrapRad(double r) { r %= 2 * Math.PI; if (r > Math.PI) r -= 2 * Math.PI; if (r < -Math.PI) r += 2 * Math.PI; return r; }
        public double RunSpeed;          // ground speed (m/s), for stride-synced locomotion clips
        public double AccelLean, TurnLean;   // radians: + forward / into the turn (acceleration-driven, smoothed)
        public const double BrakeTime = 0.45;
        double _brakeAt = -10, _prevSpeed;
        public double MoveDirLocal;      // movement direction relative to facing (rad): 0 fwd, ±π/2 sideways, π back

        /// <summary>The opponent hit: a split-step happens as he makes contact.</summary>
        public double SplitStepAt { get; set; } = -1;

        public void Tick(double now, double dt, PlayerMotor m, Vec3 lookAt)
        {
            // ---------------- locomotion + balance
            Vec3 v = m.velocity.Horizontal; double sp = v.Length;
            runBlend = MathD.Clamp01(sp / Math.Max(0.1, m.MaxSpeed));
            strideHz = MathD.Lerp(1.6, 3.4, runBlend);
            strideTime += dt * strideHz * 2 * Math.PI * (sp > 0.15 ? 1 : 0);
            Vec3 acc = dt > 1e-6 ? (v - _prevVel) / dt : Vec3.Zero; _prevVel = v;
            RunSpeed = sp;
            Vec3 fwd = sp > 0.2 ? v / sp : Vec3.Zero;
            Vec3 side = new Vec3(-fwd.y, fwd.x, 0);
            double accF = Vec3.Dot(acc, fwd), accS = Vec3.Dot(acc, side);
            // body lean from acceleration (forward when starting, back when braking) and into turns
            double kl = 1 - Math.Exp(-dt * 7);
            AccelLean += (MathD.Clamp(0.035 * accF, -0.28, 0.22) - AccelLean) * kl;
            TurnLean += (MathD.Clamp(0.04 * accS, -0.30, 0.30) - TurnLean) * kl;
            // hard stop: running fast and decelerating strongly → braking step
            if (_prevSpeed > 2.3 && dt > 1e-6 && (_prevSpeed - sp) / dt > 7.0 && now - _brakeAt > 0.8) _brakeAt = now;
            _prevSpeed = sp;
            double targetLeanF = MathD.Clamp(0.06 * runBlend + 0.025 * accF, -0.25, 0.3);
            double targetLeanS = MathD.Clamp(0.03 * accS + (1 - m.balance) * 0.25 * Math.Sin(now * 9), -0.35, 0.35);
            double k = 1 - Math.Exp(-dt * 10);
            leanForward += (targetLeanF - leanForward) * k;
            leanSide += (targetLeanS - leanSide) * k;
            // facing: run "head first" on long runs, square up to the ball/net when it matters; never snap
            Vec3 toBall = (lookAt - m.position).Horizontal;
            double netYaw = m.side == CourtSide.Near ? 0 : Math.PI;                       // facing the opponent
            double target = toBall.Length > 0.1 ? Math.Atan2(toBall.y, toBall.x) : netYaw;
            // keep the ball within ±70° of the net direction so the athlete never turns his back to the court
            double off = WrapRad(target - netYaw); off = MathD.Clamp(off, -1.22, 1.22); target = netYaw + off;
            bool inShot = _hasShot || _serving;
            if (!inShot && sp > 2.6 && FarFromBall)
                target = Math.Atan2(v.y, v.x);                                          // sprint facing the run
            double maxTurn = MathD.Deg2Rad(TurnRateDeg) * dt;
            double dYaw = WrapRad(target - facingYaw);
            facingYaw += MathD.Clamp(dYaw, -maxTurn, maxTurn);
            MoveDirLocal = sp > 0.2 ? WrapRad(Math.Atan2(v.y, v.x) - facingYaw) : 0;

            // ---------------- serve
            if (_serving)
            {
                action = BodyAction.Serve;
                double tc = _serveContact;
                if (now < _serveToss) { serve = ServeBodyPhase.Stance; phaseProgress = 0; }
                else if (now < _serveToss + 0.35) { serve = ServeBodyPhase.Toss; phaseProgress = (now - _serveToss) / 0.35; }
                else if (now < tc - 0.18) { serve = ServeBodyPhase.Trophy; phaseProgress = MathD.InverseLerp(_serveToss + 0.35, tc - 0.18, now); }
                else if (now < tc) { serve = ServeBodyPhase.Drop; phaseProgress = MathD.InverseLerp(tc - 0.18, tc, now); }
                else if (now < tc + 0.05) { serve = ServeBodyPhase.Contact; phaseProgress = 1; }
                else if (now < tc + 0.45) { serve = ServeBodyPhase.FollowThrough; phaseProgress = MathD.InverseLerp(tc + 0.05, tc + 0.45, now); }
                else if (now < tc + 0.8) { serve = ServeBodyPhase.Recovery; phaseProgress = MathD.InverseLerp(tc + 0.45, tc + 0.8, now); }
                else EndServe();
                return;
            }

            // ---------------- strokes
            if (_hasShot && now < _recoveryEnd)
            {
                if (now < _contactAt - 0.14)
                {
                    action = BodyAction.Preparation; phaseProgress = MathD.InverseLerp(_prepStart, _contactAt - 0.14, now);
                    smash = _smashing ? (phaseProgress < 0.35 ? SmashPhase.TurnSideways : phaseProgress < 0.7 ? SmashPhase.FootPositioning : SmashPhase.ArmRaise) : SmashPhase.None;
                }
                else if (now < _contactAt - 0.015)
                {
                    action = BodyAction.Swing; phaseProgress = MathD.InverseLerp(_contactAt - 0.14, _contactAt, now);
                    smash = _smashing ? SmashPhase.ArmRaise : SmashPhase.None;
                }
                else if (now < _contactAt + 0.03) { action = BodyAction.Contact; phaseProgress = 1; smash = _smashing ? SmashPhase.AerialContact : SmashPhase.None; }
                else if (now < _followEnd)
                {
                    action = BodyAction.FollowThrough; phaseProgress = MathD.InverseLerp(_contactAt + 0.03, _followEnd, now);
                    smash = _smashing ? (phaseProgress < 0.5 ? SmashPhase.EnergyTransfer : SmashPhase.Finish) : SmashPhase.None;
                }
                else { action = BodyAction.Recovery; phaseProgress = MathD.InverseLerp(_followEnd, _recoveryEnd, now); smash = _smashing ? SmashPhase.Recovery : SmashPhase.None; }
                if (_smashing) action = action == BodyAction.Recovery ? BodyAction.Recovery : BodyAction.Smash;
                return;
            }
            _hasShot = false; _smashing = false; smash = SmashPhase.None;
            if (SplitStepAt > 0 && now >= SplitStepAt && now < SplitStepAt + SplitStepTime)
            { action = BodyAction.SplitStep; phaseProgress = (now - SplitStepAt) / SplitStepTime; return; }
            if (now - _brakeAt < BrakeTime) { action = BodyAction.Brake; phaseProgress = (now - _brakeAt) / BrakeTime; return; }
            action = sp > 0.35 ? BodyAction.Running : BodyAction.Ready;
            phaseProgress = 0;
        }
    }
}
