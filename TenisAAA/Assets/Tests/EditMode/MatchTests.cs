using System;
using System.Collections.Generic;
using NUnit.Framework;
using Tenis.Core;

namespace Tenis.Tests
{
    public class ScoreTests
    {
        static void Win(TennisScore s, int p, int n) { for (int i = 0; i < n; i++) s.PointWonBy(p); }

        [Test] public void GameDeuceAdvantage()
        {
            var s = new TennisScore();
            Win(s, 0, 3); Win(s, 1, 3);
            Assert.AreEqual("IGUAIS", s.Describe().Split('|')[2].Trim());
            s.PointWonBy(0); Assert.AreEqual("AD", s.PointText(0));
            s.PointWonBy(1); s.PointWonBy(1); Assert.AreEqual("AD", s.PointText(1));
            s.PointWonBy(1); Assert.AreEqual(1, s.games[1]); Assert.AreEqual(1, s.server, "serve alternates each game");
        }

        [Test] public void ServeSideAlternatesEachPoint()
        {
            var s = new TennisScore();
            Assert.AreEqual(ServeSide.Deuce, s.CurrentServeSide); s.PointWonBy(0);
            Assert.AreEqual(ServeSide.Ad, s.CurrentServeSide);
        }

        [Test] public void TiebreakAtSixAllAndSetWin()
        {
            var s = new TennisScore();
            for (int g = 0; g < 6; g++) { Win(s, 0, 4); Win(s, 1, 4); }
            Assert.IsTrue(s.inTiebreak);
            int first = s.server; s.PointWonBy(0);
            Assert.AreNotEqual(first, s.server, "after 1st tiebreak point the serve changes");
            Win(s, 0, 6);
            Assert.AreEqual(1, s.sets[0]); Assert.AreEqual(7, s.finishedSets[0][0]); Assert.IsFalse(s.inTiebreak);
            for (int g = 0; g < 6; g++) Win(s, 0, 4);
            Assert.AreEqual(0, s.winner);
        }
    }

    public class AnimationTests
    {
        [Test] public void SmashGoesThroughAllPhases()
        {
            var cfg = new TennisConfig(); var m = new PlayerMotor(cfg.movement, new AthleteProfile(), new Vec3(-3, 0, 0), CourtSide.Near);
            var a = new AthleteAnimation();
            a.BeginPreparation(0, 1.0, Stroke.Smash, false, 2.8);
            var seen = new HashSet<SmashPhase>();
            for (double t = 0; t < 2.0; t += 0.01) { a.Tick(t, 0.01, m, new Vec3(-2, 0, 2.8)); seen.Add(a.smash); }
            foreach (SmashPhase p in Enum.GetValues(typeof(SmashPhase)))
                if (p != SmashPhase.None) Assert.IsTrue(seen.Contains(p), $"missing {p}");
        }

        [Test] public void RunningLeansAndBlendsWithSpeed()
        {
            var cfg = new TennisConfig(); var m = new PlayerMotor(cfg.movement, new AthleteProfile(), new Vec3(-12, 0, 0), CourtSide.Near);
            var a = new AthleteAnimation();
            for (int i = 0; i < 60; i++) { m.Tick(1 / 60.0, 1, 0); a.Tick(i / 60.0, 1 / 60.0, m, new Vec3(5, 0, 1)); }
            Assert.AreEqual(BodyAction.Running, a.action);
            Assert.Greater(a.runBlend, 0.5); Assert.Greater(a.leanForward, 0.02);
        }
    }

    public class MatchEngineTests
    {
        /// <summary>Headless match: a bot plays player 0 through the real engine (serve, rally, referee, score).</summary>
        [Test] public void BotPlaysAWholeSetWithoutGettingStuck()
        {
            var cfg = new TennisConfig();
            var eng = new MatchEngine(cfg, new AthleteProfile(), new AthleteProfile(), 99) { aiSkill = 0.6 };
            eng.score.setsToWin = 1;
            var bot = new DetRandom(7);
            double dt = 1 / 60.0; int maxRally = 0; var reasons = new HashSet<PointEndReason>();
            int pointsPlayed = 0; int lastTotal = 0; double lastProgress = 0;
            bool swungThisBall = false; double planTime = -1; int lastRally = -1; var lastPhase = eng.phase;
            for (int frame = 0; frame < 60 * 60 * 40 && !eng.score.MatchOver; frame++)
            {
                // bot movement: run to the ball
                double sx = 0, sy = 0;
                if (eng.HasPlan(0))
                {
                    var c = eng.PlanFor(0); var me = eng.players[0];
                    if (Math.Abs(c.contactTime - planTime) > 1e-6) { planTime = c.contactTime; swungThisBall = false; }
                    var goal = new Vec3(c.ball.position.x - 0.3, c.ball.position.y + 0.55, 0);   // forehand side for a right-hander (Near)
                    var to = (goal - me.position).Horizontal;
                    if (to.Length > 0.2) { var d = to.Normalized; sx = -d.y; sy = d.x; }      // Near: court x = sy, court y = -sx
                    if (!swungThisBall && eng.now >= c.contactTime - 0.01 + bot.NextGaussian() * 0.03)
                    {
                        swungThisBall = true;
                        var shape = bot.NextDouble() < 0.7 ? GestureShape.DiagonalUp : GestureShape.DiagonalDown;
                        eng.InjectLocalGesture(GestureFactory.Make(shape, bot.NextDouble() * 1.6 - 0.8, 0.55, eng.now), eng.now - 0.35);
                    }
                }
                if (eng.phase == MatchEngine.Phase.PreServe && eng.LocalServing && bot.NextDouble() < 0.02)
                    eng.InjectLocalGesture(default, eng.now);
                if (eng.phase == MatchEngine.Phase.Toss && eng.LocalServing && eng.ball.position.z > 2.95)
                    eng.InjectLocalGesture(GestureFactory.Make(GestureShape.VerticalDown, bot.NextDouble() * 2 - 1, 0.7, eng.now), eng.now - 0.15);

                eng.Tick(dt, sx, sy);
                if (eng.rallyLength != lastRally || eng.phase != lastPhase) { lastRally = eng.rallyLength; lastPhase = eng.phase; lastProgress = eng.now; }
                maxRally = Math.Max(maxRally, eng.rallyLength);
                int total = eng.score.points[0] + eng.score.points[1] + 10 * (eng.score.games[0] + eng.score.games[1]) + 100 * (eng.score.sets[0] + eng.score.sets[1]);
                if (total != lastTotal) { lastTotal = total; pointsPlayed++; reasons.Add(eng.lastPointReason); lastProgress = eng.now; }
                Assert.Less(eng.now - lastProgress, 15, $"a point lasted more than 60 s: stuck in {eng.phase} rally={eng.rallyLength} ball={eng.ball.position} v={eng.ball.velocity} hitter={eng.lastHitter} plan0={eng.HasPlan(0)} plan1={eng.HasPlan(1)}");
            }
            Console.WriteLine($"points={pointsPlayed} score={eng.score.Describe()} maxRally={maxRally} reasons={string.Join(",", reasons)}");
            foreach (var m in eng.messages.GetRange(Math.Max(0, eng.messages.Count - 6), Math.Min(6, eng.messages.Count))) Console.WriteLine("  " + m.text);
            Assert.IsTrue(eng.score.MatchOver, "a one-set match must finish");
            Assert.GreaterOrEqual(maxRally, 3, "there must be real rallies");
            Assert.GreaterOrEqual(reasons.Count, 2);
        }
    }
}
