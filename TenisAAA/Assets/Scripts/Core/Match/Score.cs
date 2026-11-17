using System;

namespace Tenis.Core
{
    /// <summary>
    /// Tennis scoring: 0/15/30/40/deuce/advantage (or no-ad), games, sets (6 with 2 clear, tiebreak at 6-6 to 7 by 2),
    /// server rotation (tiebreak: 1 then 2-2) and the serve side (deuce/ad) for every point.
    /// Player index 0 = local player, 1 = opponent.
    /// </summary>
    [Serializable]
    public class TennisScore
    {
        public int setsToWin = 2;          // best of 3
        public int gamesPerSet = 6;
        public bool noAd = false;
        public int tiebreakTo = 7;

        public int[] points = new int[2];  // points inside the current game / tiebreak
        public int[] games = new int[2];
        public int[] sets = new int[2];
        public System.Collections.Generic.List<int[]> finishedSets = new System.Collections.Generic.List<int[]>();
        public int server;                 // who serves the current point
        public bool inTiebreak;
        public int winner = -1;
        int _tiebreakFirstServer;

        public TennisScore(int firstServer = 0) { server = firstServer; }

        public bool MatchOver => winner >= 0;

        /// <summary>Deuce side when the total points played in the game/tiebreak is even.</summary>
        public ServeSide CurrentServeSide => (points[0] + points[1]) % 2 == 0 ? ServeSide.Deuce : ServeSide.Ad;

        public void PointWonBy(int p)
        {
            if (MatchOver) return;
            int o = 1 - p;
            points[p]++;
            if (inTiebreak)
            {
                if (points[p] >= tiebreakTo && points[p] - points[o] >= 2) { WinGame(p, true); return; }
                int played = points[0] + points[1];
                if (played % 2 == 1) server = 1 - server;     // 1, then every 2 points
                return;
            }
            if (noAd)
            {
                if (points[p] >= 4) WinGame(p, false);
                return;
            }
            if (points[p] >= 4 && points[p] - points[o] >= 2) WinGame(p, false);
        }

        void WinGame(int p, bool tiebreak)
        {
            games[p]++;
            points[0] = points[1] = 0;
            if (tiebreak)
            {
                inTiebreak = false;
                WinSet(p);
                server = 1 - _tiebreakFirstServer;           // the player who received first in the tiebreak serves next set
                return;
            }
            server = 1 - server;
            int o = 1 - p;
            if (games[p] >= gamesPerSet && games[p] - games[o] >= 2) { WinSet(p); return; }
            if (games[0] == gamesPerSet && games[1] == gamesPerSet) { inTiebreak = true; _tiebreakFirstServer = server; }
        }

        void WinSet(int p)
        {
            finishedSets.Add(new[] { games[0], games[1] });
            sets[p]++;
            games[0] = games[1] = 0;
            if (sets[p] >= setsToWin) winner = p;
        }

        public string PointText(int p)
        {
            if (inTiebreak) return points[p].ToString();
            int a = points[p], b = points[1 - p];
            if (a >= 3 && b >= 3)
            {
                if (a == b) return "40";
                return a > b ? "AD" : "40";
            }
            return a switch { 0 => "0", 1 => "15", 2 => "30", _ => "40" };
        }

        public string Describe()
        {
            var sb = new System.Text.StringBuilder();
            foreach (var s in finishedSets) sb.Append($"{s[0]}-{s[1]} ");
            sb.Append($"| {games[0]}-{games[1]} | ");
            if (inTiebreak) sb.Append($"TB {points[0]}-{points[1]}");
            else if (points[0] >= 3 && points[1] >= 3 && points[0] == points[1]) sb.Append("IGUAIS");
            else sb.Append($"{PointText(0)}-{PointText(1)}");
            if (MatchOver) sb.Append(winner == 0 ? "  VITÓRIA" : "  DERROTA");
            return sb.ToString();
        }
    }
}
