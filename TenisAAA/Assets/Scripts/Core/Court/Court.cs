using System;

namespace Tenis.Core
{
    public enum CourtSide { Near = -1, Far = 1 }          // Near = x < 0 (local player), Far = x > 0
    public enum ServeSide { Deuce, Ad }
    public enum AimDepth { Deep, Short }
    public enum AimLateral { Left, Centre, Right }       // from the HITTER's point of view

    /// <summary>ITF court geometry. x = length (net at 0), y = width, z = up.</summary>
    public static class Court
    {
        public const double HalfLength = 23.77 / 2;      // 11.885
        public const double HalfDoublesWidth = 10.97 / 2;// 5.485
        public const double HalfSinglesWidth = 8.23 / 2; // 4.115
        public const double ServiceLine = 6.40;          // from the net

        public static int Sign(CourtSide s) => (int)s;
        public static CourtSide Opposite(CourtSide s) => s == CourtSide.Near ? CourtSide.Far : CourtSide.Near;
        public static CourtSide SideOf(double x) => x < 0 ? CourtSide.Near : CourtSide.Far;

        /// <summary>Singles court, lines are in (ball touching the line counts as in: we test the ball centre
        /// against the line's outer edge + ball radius).</summary>
        public static bool InSingles(Vec3 p, CourtSide side, double ballRadius)
        {
            double sx = Sign(side) * p.x;
            return sx >= -ballRadius && sx <= HalfLength + ballRadius && Math.Abs(p.y) <= HalfSinglesWidth + ballRadius;
        }

        /// <summary>Correct service box for a serve hit from <paramref name="serverSide"/> on <paramref name="side"/>.
        /// Server faces the net; the deuce court is the server's RIGHT. The serve goes diagonally.</summary>
        public static bool InServiceBox(Vec3 p, CourtSide serverCourt, ServeSide serveSide, double ballRadius)
        {
            CourtSide target = Opposite(serverCourt);
            double sx = Sign(target) * p.x;
            if (sx < -ballRadius || sx > ServiceLine + ballRadius) return false;
            // Server on Near side faces +x; his right hand side is -y. Deuce: stands at -y, serves to +y box... mirrored on Far.
            double yServerFrame = p.y * -Sign(serverCourt);      // positive = server's LEFT
            bool toServersLeft = serveSide == ServeSide.Deuce;   // diagonal from the right half lands in the left box
            double yy = toServersLeft ? yServerFrame : -yServerFrame;
            return yy >= -ballRadius && yy <= HalfSinglesWidth + ballRadius;
        }

        /// <summary>Server stand position (just behind the baseline) for a given side.</summary>
        public static Vec3 ServerPosition(CourtSide court, ServeSide side, double offsetFromCentre = 0.8)
        {
            int s = Sign(court);
            double rightOfServer = side == ServeSide.Deuce ? 1 : -1;     // deuce → stand on own right
            double y = rightOfServer * offsetFromCentre * s;             // Near: right = -y → s=-1 gives -y
            return new Vec3(s * (HalfLength + 0.25), y, 0);
        }

        /// <summary>Target point on the opponent side for a zone, from the hitter's perspective.</summary>
        public static Vec3 AimTarget(CourtSide hitterCourt, AimDepth depth, AimLateral lateral, AimConfig cfg, double lateralContinuous = double.NaN)
        {
            int s = -Sign(hitterCourt);                                  // target side sign
            double dist = depth == AimDepth.Deep ? HalfLength - cfg.deepTargetFromBaseline : cfg.shortTargetFromNet;
            double maxY = HalfSinglesWidth - cfg.lateralTargetFromSideline;
            double lat = double.IsNaN(lateralContinuous)
                ? (lateral == AimLateral.Left ? -1 : lateral == AimLateral.Right ? 1 : 0)
                : MathD.Clamp(lateralContinuous, -1, 1);
            // lat = +1 means the hitter's RIGHT. A Near hitter faces +x, so his right is -y; a Far hitter's right is +y.
            double y = RightY(hitterCourt) * lat * maxY;
            return new Vec3(s * dist, y, 0);
        }

        /// <summary>World-y sign of the right-hand side of a player standing on <paramref name="court"/> facing the net.</summary>
        public static double RightY(CourtSide court) => Sign(court);

        /// <summary>Net height (top of the tape) at lateral position y.</summary>
        public static double NetHeight(double y, NetConfig cfg)
        {
            double t = MathD.Clamp01(Math.Abs(y) / cfg.postDistanceFromCentre);
            return cfg.centreHeight + (cfg.postHeight - cfg.centreHeight) * t * t;
        }
    }
}
