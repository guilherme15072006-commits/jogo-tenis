using System;
using System.Collections.Generic;

namespace Tenis.Core
{
    /// <summary>Builds gesture results without a screen (AI, bots, replays, server validation).</summary>
    public static class GestureFactory
    {
        public static GestureResult Make(GestureShape shape, double lateral, double power01, double endTime, bool shortIntent = false)
        {
            double ang = shape switch
            {
                GestureShape.Horizontal => lateral >= 0 ? 0 : 180,
                GestureShape.DiagonalUp => lateral >= 0 ? 45 : 135,
                GestureShape.DiagonalDown => lateral >= 0 ? -45 : -135,
                GestureShape.VerticalUp => 90,
                GestureShape.VerticalDown => -90,
                _ => 0
            };
            double len = shape == GestureShape.Tap ? 0.01 : MathD.Lerp(0.12, 0.38, power01);
            double dur = shape == GestureShape.Tap ? 0.08 : MathD.Lerp(0.30, 0.09, power01);
            var f = new GestureFeatures
            {
                length = len, pathLength = len, straightness = 1, duration = dur,
                startTime = endTime - dur, endTime = endTime,
                meanSpeed = len / dur, peakSpeed = len / dur * 1.35, angleDeg = ang,
                dx = len * Math.Cos(MathD.Deg2Rad(ang)), dy = len * Math.Sin(MathD.Deg2Rad(ang))
            };
            return new GestureResult { accepted = true, shape = shape, f = f, lateralIntent = MathD.Clamp(lateral, -1, 1), shortIntent = shortIntent };
        }
    }

    /// <summary>Aim preview for the optional on-court indicator: probable landing point + 1-sigma error ellipse.</summary>
    [Serializable]
    public struct AimPreview
    {
        public bool valid;
        public Vec3 target;          // intended landing point
        public double sigmaDepth, sigmaLateral;
        public ShotDecision decision;
        public TimingResult timingIfNow;
    }

    public enum PointEndReason { None, Out, Net, DoubleBounce, DoubleFault, Ace, Whiff }

    [Serializable]
    public struct MatchMessage
    {
        public double time;
        public string text;
        public MatchMessage(double t, string s) { time = t; text = s; }
    }

    /// <summary>
    /// Complete match loop independent of any engine: serve flow, rally, AI opponent, referee and scoring.
    /// The Unity layer feeds stick + gestures for player 0 and renders the state; a server can run the very
    /// same code to validate a match from the recorded inputs. Player 0 plays the Near side.
    /// </summary>
    public sealed class MatchEngine
    {
        public readonly TennisConfig cfg;
        public readonly BallSimulator sim;
        public readonly TennisScore score;
        public readonly PlayerMotor[] players = new PlayerMotor[2];
        public readonly AthleteProfile[] profiles = new AthleteProfile[2];
        public readonly AthleteAnimation[] anims = { new AthleteAnimation(), new AthleteAnimation() };
        public readonly GestureClassifier classifier;
        readonly ShotResolver _resolver;
        readonly ServeFlow _serve;
        readonly ulong _seed;
        int _shotIndex;

        public enum Phase { PreServe, Toss, Rally, PointOver, MatchOver }
        public Phase phase { get; private set; }
        public BallState ball;
        public bool ballVisible;
        public int lastHitter = -1;
        int _bounces;
        public double now;
        double _phaseUntil;
        public readonly List<MatchMessage> messages = new List<MatchMessage>();
        public ShotOutcome lastShot; public ServeResult lastServe; public GestureResult lastGesture;
        public PointEndReason lastPointReason;
        public int rallyLength;
        public double aiSkill = 0.6;               // 0..1, timing noise and shot choice of the AI
        public double PointPause = 2.6;            // s between points: the players WALK to their next spot meanwhile

        // incoming ball plans
        ContactPlan[] _plan = new ContactPlan[2];
        bool[] _planValid = new bool[2];
        double[] _incomingAt = new double[2];
        ShotOutcome? _pending; double _pendingAt; int _pendingPlayer;
        double _aiNextServeAt = -1;
        readonly DetRandom _aiRng;

        public MatchEngine(TennisConfig cfg, AthleteProfile me, AthleteProfile opponent, ulong seed, int firstServer = 0)
        {
            this.cfg = cfg; _seed = seed;
            sim = new BallSimulator(cfg); _resolver = new ShotResolver(cfg, sim); _serve = new ServeFlow(cfg, sim);
            classifier = new GestureClassifier(cfg.gesture) { LateralFull = cfg.aim.lateralFullDeflection };
            profiles[0] = me; profiles[1] = opponent;
            players[0] = new PlayerMotor(cfg.movement, me, new Vec3(-12, 0, 0), CourtSide.Near);
            players[1] = new PlayerMotor(cfg.movement, opponent, new Vec3(12, 0, 0), CourtSide.Far);
            score = new TennisScore(firstServer);
            _aiRng = new DetRandom(seed ^ 0x5EEDUL);
            StartPoint();
        }

        public static CourtSide SideOf(int p) => p == 0 ? CourtSide.Near : CourtSide.Far;
        public bool LocalServing => score.server == 0;
        public int ServeNumber => _serve.ServeNumber;
        public ContactPlan PlanFor(int p) => _plan[p];
        public bool HasPlan(int p) => _planValid[p];
        void Say(string s) => messages.Add(new MatchMessage(now, s));

        // ============================================================== points
        void StartPoint()
        {
            if (score.MatchOver) { phase = Phase.MatchOver; return; }
            int srv = score.server;
            _serve.BeginPoint(SideOf(srv), score.CurrentServeSide);
            NextPositions(out var np0, out var np1);
            players[0].position = np0; players[1].position = np1;     // the walk during PointOver already brought them close
            players[0].velocity = players[1].velocity = Vec3.Zero;
            foreach (var p in players) p.balance = 1;
            ballVisible = false; lastHitter = -1; _bounces = 0; rallyLength = 0;
            _planValid[0] = _planValid[1] = false; _pending = null;
            phase = Phase.PreServe;
            _aiNextServeAt = srv == 1 ? now + 1.2 : -1;
            Say(srv == 0 ? $"Seu saque ({(score.CurrentServeSide == ServeSide.Deuce ? "iguais" : "vantagem")}) — toque para lançar" : "Saque do adversário");
        }

        /// <summary>Where both players stand for the next point (server behind the baseline on the serve side,
        /// receiver diagonally opposite).</summary>
        void NextPositions(out Vec3 p0, out Vec3 p1)
        {
            int srv = score.server; var side = score.CurrentServeSide;
            var srvPos = Court.ServerPosition(SideOf(srv), side);
            int rcv = 1 - srv; var rs = SideOf(rcv);
            double ry = (side == ServeSide.Deuce ? 1 : -1) * Court.RightY(rs) * 2.2;
            var rcvPos = new Vec3(Court.Sign(rs) * (Court.HalfLength + 0.4), ry, 0);
            p0 = srv == 0 ? srvPos : rcvPos; p1 = srv == 1 ? srvPos : rcvPos;
        }

        void EndPoint(int winner, PointEndReason reason)
        {
            lastPointReason = reason;
            score.PointWonBy(winner);
            string why = reason switch
            {
                PointEndReason.Out => "fora", PointEndReason.Net => "rede", PointEndReason.DoubleBounce => "dois quiques",
                PointEndReason.DoubleFault => "dupla falta", PointEndReason.Ace => "ace", PointEndReason.Whiff => "errou a bola", _ => ""
            };
            Say($"{(winner == 0 ? "Ponto seu" : "Ponto do adversário")} — {why}  [{score.Describe()}]");
            phase = score.MatchOver ? Phase.MatchOver : Phase.PointOver;
            _phaseUntil = now + PointPause;
            _planValid[0] = _planValid[1] = false; _pending = null;
        }

        // ============================================================== input (player 0)
        public void OnLocalGesture(IReadOnlyList<TouchSample> samples, double startTime, double screenAspect)
        {
            if (phase == Phase.PreServe && LocalServing) { Toss(); return; }
            var g = classifier.Classify(samples, screenAspect, phase == Phase.Rally);
            lastGesture = g;
            if (!g.accepted) return;
            if (phase == Phase.Toss && LocalServing) { ServeSwing(0, g, now); return; }
            if (phase == Phase.Rally) Swing(0, g, startTime);
        }

        /// <summary>Same as a classified screen gesture, for bots, replays and server validation.</summary>
        public void InjectLocalGesture(GestureResult g, double startTime)
        {
            lastGesture = g;
            if (phase == Phase.PreServe && LocalServing) { Toss(); return; }
            if (phase == Phase.Toss && LocalServing) { ServeSwing(0, g, now); return; }
            if (phase == Phase.Rally) Swing(0, g, startTime);
        }

        /// <summary>Called when the right-hand finger touches down: the athlete starts preparing if a ball is coming.</summary>
        public void OnLocalGestureStarted()
        {
            if (phase == Phase.Rally && _planValid[0])
                anims[0].BeginPreparation(now, _plan[0].contactTime, _plan[0].lateralOffset >= 0 ? Stroke.Forehand : Stroke.Backhand,
                                          profiles[0].backhand == BackhandStyle.TwoHanded, _plan[0].ball.position.z);
        }

        public AimPreview PreviewAim(int p, GestureResult g)
        {
            var a = new AimPreview();
            if (!_planValid[p] || !g.accepted) return a;
            var c = _plan[p];
            a.decision = ShotSelector.Select(g, c, players[p], profiles[p], cfg.shots);
            a.timingIfNow = TimingModel.Evaluate(c.contactTime, c, a.decision, players[p], profiles[p], cfg.timing, 0.35);
            a.target = AimModel.IntendedTarget(SideOf(p), g, a.decision, cfg.aim);
            double p01 = MathD.Clamp01(PowerModel.SwingIntent(g.f, cfg.power));
            double s = AimModel.ErrorSigma(p01, a.timingIfNow, players[p].balance, 0, a.decision, profiles[p], cfg.aim);
            a.sigmaLateral = s; a.sigmaDepth = s * 1.3; a.valid = true;
            return a;
        }

        // ============================================================== serve
        void Toss()
        {
            if (phase != Phase.PreServe) return;
            _serve.Toss(now);
            ball = _serve.TossBall; ballVisible = true; phase = Phase.Toss;
            anims[score.server].BeginServe(now, _serve.ApexTime + 0.03);
        }

        void ServeSwing(int p, GestureResult g, double swingTime)
        {
            var r = _serve.Swing(g, swingTime, profiles[p], _seed, ++_shotIndex);
            lastServe = r;
            anims[p].ServeContact(swingTime);
            if (r.verdict == ServeVerdict.TossCaught) { Say("Bola segurada — lance de novo"); phase = Phase.PreServe; ballVisible = false; anims[p].EndServe(); _aiNextServeAt = p == 1 ? now + 0.8 : -1; return; }
            ball = r.launch; ball.time = now; ballVisible = true;
            lastHitter = p; _bounces = 0; rallyLength = 1;
            switch (r.verdict)
            {
                case ServeVerdict.In:
                    phase = Phase.Rally; Say($"Saque {r.type} {r.ballSpeed * 3.6:0} km/h");
                    PlanFor(1 - p, now); anims[1 - p].SplitStepAt = now; break;
                case ServeVerdict.Fault:
                    Say("Falta"); phase = Phase.PointOver; _phaseUntil = now + 1.3; _replayServe = true; break;
                case ServeVerdict.Let:
                    Say("Let — repete o saque"); phase = Phase.PointOver; _phaseUntil = now + 1.3; _replayServe = true; break;
                case ServeVerdict.DoubleFault:
                    EndPoint(1 - p, PointEndReason.DoubleFault); break;
            }
        }
        bool _replayServe;
        int _frame;

        // ============================================================== rally
        readonly BallState[] _incomingBall = new BallState[2];

        /// <summary>Plans contact from the ball state right after the opponent's hit. The plan is always recomputed
        /// from that same state (never from the ball in flight, which may already have bounced).</summary>
        void PlanFor(int p, double t)
        {
            _incomingBall[p] = ball; _incomingAt[p] = t;
            Replan(p);
        }

        void Replan(int p)
        {
            _plan[p] = ContactPredictor.Predict(sim, _incomingBall[p], players[p], profiles[p], cfg.shots, cfg.timing, _incomingAt[p]);
            _planValid[p] = _plan[p].valid;
        }

        void Swing(int p, GestureResult g, double gestureStart)
        {
            if (!_planValid[p] || _pending.HasValue) return;
            // re-plan with the current player position (he may have moved since the ball was hit)
            Replan(p);
            if (!_planValid[p]) return;
            double prep = gestureStart - _incomingAt[p];
            var o = _resolver.Resolve(g, _plan[p], players[p], profiles[p], now, prep, _seed, ++_shotIndex);
            lastShot = o;
            classifier.StartCooldown(now);
            anims[p].Contact(Math.Max(now, _plan[p].contactTime), o.decision.stroke);
            if (!o.contact) { _planValid[p] = false; Say("Errou o tempo da bola"); return; }
            _pending = o; _pendingPlayer = p; _pendingAt = Math.Max(now, _plan[p].contactTime);
        }

        void AiThink()
        {
            const int p = 1;
            var me = players[p];
            // serve
            if (phase == Phase.PreServe && score.server == p && _aiNextServeAt > 0 && now >= _aiNextServeAt) { _aiNextServeAt = -1; Toss(); return; }
            if (phase == Phase.Toss && score.server == p && now >= _serve.ApexTime + 0.03 + (_aiRng.NextDouble() - 0.5) * 0.12 * (1.2 - aiSkill))
            {
                var shape = _aiRng.NextDouble() < 0.55 ? GestureShape.VerticalDown : (_aiRng.NextDouble() < 0.5 ? GestureShape.DiagonalUp : GestureShape.DiagonalDown);
                double pow = _serve.ServeNumber == 1 ? 0.75 + 0.2 * aiSkill : 0.5;
                ServeSwing(p, GestureFactory.Make(shape, _aiRng.NextDouble() * 2 - 1, pow, now), now);
                return;
            }
            if (phase != Phase.Rally || !_planValid[p] || _pending.HasValue) return;
            var c = _plan[p];
            // decide the swing a little before contact with skill-dependent timing noise
            double noise = (_aiRng.NextGaussian()) * 0.05 * (1.3 - aiSkill);
            if (now >= c.contactTime - 0.02 + noise)
            {
                double r = _aiRng.NextDouble();
                bool nearNet = Math.Abs(me.position.x) < cfg.shots.volleyMaxDistanceFromNet;
                GestureShape shape = c.ball.position.z > cfg.shots.smashMinBallHeight && !c.bounced ? GestureShape.VerticalDown
                                   : nearNet ? GestureShape.Horizontal
                                   : r < 0.62 ? GestureShape.DiagonalUp : r < 0.84 ? GestureShape.DiagonalDown : r < 0.94 ? GestureShape.Horizontal
                                   : r < 0.97 ? GestureShape.VerticalUp : GestureShape.Tap;
                var g = GestureFactory.Make(shape, _aiRng.NextDouble() * 1.8 - 0.9, 0.45 + 0.4 * _aiRng.NextDouble() * aiSkill, now);
                anims[p].BeginPreparation(now - 0.3, c.contactTime, c.lateralOffset >= 0 ? Stroke.Forehand : Stroke.Backhand, profiles[p].backhand == BackhandStyle.TwoHanded, c.ball.position.z);
                Swing(p, g, now - 0.4);
            }
        }

        /// <summary>Screen-stick that drives player <paramref name="p"/> to his ideal hitting spot: beside the ball on
        /// the dominant-hand side, a little behind the contact so the swing has room.</summary>
        void HitPositionStick(int p, out double sx, out double sy)
        {
            sx = sy = 0; if (!_planValid[p]) return;
            var me = players[p]; var c = _plan[p];
            double hand = profiles[p].dominantHand == Hand.Right ? 1 : -1;
            double side = c.lateralOffset >= -0.15 ? 1 : -1;                 // forehand unless the ball is clearly on the other side
            double off = 0.62 * Court.RightY(me.side) * hand * side;
            var goal = new Vec3(c.ball.position.x + Court.Sign(me.side) * 0.35, c.ball.position.y - off, 0);
            var to = (goal - me.position).Horizontal;
            if (to.Length < 0.12) return;
            double t = Math.Max(0.15, c.contactTime - now);
            double need = to.Length / t;                                     // speed needed to arrive in time
            var d = to.Normalized * MathD.Clamp(Math.Max(need / Math.Max(0.1, me.MaxSpeed), to.Length / 1.2), 0, 1);
            var st = CourtToStick(me.side, d); sx = st.x; sy = st.y;
        }

        static Vec3 CourtToStick(CourtSide side, Vec3 d)
        {   // inverse of PlayerMotor.StickToCourt: court = (-s*sy, s*sx)
            int s = Court.Sign(side); return new Vec3(d.y / s, -d.x / s, 0);
        }

        /// <summary>Stick for the AI: run to the predicted contact (with the hitting-side offset), then recover.</summary>
        void AiStick(out double sx, out double sy)
        {
            sx = sy = 0;
            var me = players[1];
            Vec3 goal;
            if (phase == Phase.Rally && _planValid[1])
            {
                var c = _plan[1];
                double off = 0.55 * Court.RightY(me.side) * (profiles[1].dominantHand == Hand.Right ? 1 : -1);
                goal = new Vec3(c.ball.position.x + Court.Sign(me.side) * 0.3, c.ball.position.y - off, 0);
            }
            else if (phase == Phase.Rally) goal = me.recoveryPoint;
            else return;
            Vec3 to = (goal - me.position).Horizontal;
            if (to.Length < 0.2) return;
            Vec3 d = to.Normalized * MathD.Clamp01(to.Length / 0.8);
            // court → screen stick for the Far side: court x = -sy, court y = sx
            sx = d.y; sy = -d.x;
        }

        // ============================================================== tick
        public void Tick(double dt, double stickX, double stickY)
        {
            now += dt;
            if (phase == Phase.PointOver && now >= _phaseUntil)
            {
                if (_replayServe) { _replayServe = false; _serve.ResetForNextServe(); phase = Phase.PreServe; ballVisible = false; anims[score.server].EndServe(); _aiNextServeAt = score.server == 1 ? now + 0.9 : -1; players[score.server].position = _serve.ServerPos; Say($"Saque {_serve.ServeNumber}"); }
                else StartPoint();
            }
            if (phase == Phase.PointOver && !_replayServe && now > _phaseUntil - PointPause + 0.6)
            {
                NextPositions(out var g0, out var g1);
                for (int p = 0; p < 2; p++)
                {
                    var goal = p == 0 ? g0 : g1; var to = (goal - players[p].position).Horizontal;
                    var d = to.Length > 0.15 ? to.Normalized * MathD.Clamp(to.Length / 1.5, 0.25, 0.42) : Vec3.Zero;   // walking pace
                    var st = CourtToStick(players[p].side, d); players[p].Tick(dt, st.x, st.y);
                }
                AdvanceBallAndAnims(dt); return;
            }
            bool locked0 = phase == Phase.PreServe || phase == Phase.Toss;
            if (phase == Phase.PreServe && LocalServing) { _serve.Reposition(stickX * dt * -Court.RightY(CourtSide.Near) * 2); players[0].position = _serve.ServerPos; }
            else
            {
                // movement assist: the athlete runs towards the hitting position by himself; the stick adds/overrides
                double ux = locked0 ? 0 : stickX, uy = locked0 ? 0 : stickY;
                double um = Math.Sqrt(ux * ux + uy * uy);
                if (phase == Phase.Rally && cfg.movement.assist > 0 && _planValid[0] && now - _incomingAt[0] > cfg.movement.assistStartDelay)
                {
                    HitPositionStick(0, out var gx, out var gy);
                    double w = cfg.movement.assist * (1 - MathD.Clamp01(um));
                    ux += gx * w; uy += gy * w;
                }
                players[0].Tick(dt, ux, uy);
            }
            AiStick(out var ax, out var ay);
            players[1].Tick(dt, phase == Phase.Rally ? ax : 0, phase == Phase.Rally ? ay : 0);
            if (phase == Phase.Rally && (_frame++ % 6) == 0) for (int p = 0; p < 2; p++) if (_planValid[p] && !_pending.HasValue && now < _plan[p].contactTime - 0.05) Replan(p);
            AiThink();

            AdvanceBallAndAnims(dt);
        }

        void AdvanceBallAndAnims(double dt)
        {
            // physics (fixed step inside the frame)
            double t = now - dt;
            int steps = Math.Max(1, (int)Math.Round(dt / sim.Dt));
            for (int i = 0; i < steps; i++)
            {
                t += dt / steps;
                if (_pending.HasValue && t >= _pendingAt) Launch(t);
                if (phase == Phase.Toss) { ball.position = _serve.TossPosition(t); ball.velocity = Vec3.Zero; continue; }
                if (phase != Phase.Rally && phase != Phase.PointOver) continue;
                if (!ballVisible) continue;
                ball = sim.Step(ball, out var ev);
                if (ev.HasValue && phase == Phase.Rally) Referee(ev.Value);
            }
            // a toss that nobody hits is caught
            if (phase == Phase.Toss && now > _serve.TossStart + cfg.serve.maxGestureAfterToss)
            { phase = Phase.PreServe; _serve.ResetForNextServe(); ballVisible = false; anims[score.server].EndServe(); Say("Bola segurada — lance de novo"); _aiNextServeAt = score.server == 1 ? now + 0.8 : -1; }
            // ball passed the player without a swing
            for (int p = 0; p < 2; p++)
                if (_planValid[p] && !_pending.HasValue && now > _plan[p].contactTime + 0.4) _planValid[p] = false;
            for (int p = 0; p < 2; p++)
            {
                anims[p].TurnRateDeg = cfg.movement.turnRateDeg;
                anims[p].FarFromBall = _planValid[p] && (_plan[p].ball.position - players[p].position).Horizontal.Length > cfg.movement.faceRunDistance;
            }
            Vec3 ballXY = ballVisible ? ball.position : new Vec3(0, 0, 0);
            for (int p = 0; p < 2; p++) anims[p].Tick(now, dt, players[p], ballXY);
        }

        void Launch(double t)
        {
            var o = _pending.Value; int p = _pendingPlayer; _pending = null; _planValid[p] = false;
            ball = o.launch; ball.time = t; ballVisible = true;
            lastHitter = p; _bounces = 0; rallyLength++;
            PlanFor(1 - p, t);
            anims[1 - p].SplitStepAt = t;
        }

        void Referee(BallEvent e)
        {
            if (e.type == BallEventType.NetBody) { EndPoint(1 - lastHitter, PointEndReason.Net); return; }
            if (e.type != BallEventType.Bounce) return;
            _bounces++;
            var receiverSide = SideOf(1 - lastHitter);
            if (_bounces == 1)
            {
                if (!Court.InSingles(e.before.position, receiverSide, cfg.ball.radius)) { EndPoint(1 - lastHitter, PointEndReason.Out); return; }
            }
            else if (_bounces == 2)
            {
                bool ace = rallyLength == 1;
                EndPoint(lastHitter, ace ? PointEndReason.Ace : PointEndReason.DoubleBounce);
            }
        }
    }
}
