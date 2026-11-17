using System;

namespace Tenis.Core
{
    /// <summary>Deterministic RNG (xorshift64*). Same seed → same sequence on client and server.
    /// Seed per shot with <see cref="ForShot"/> so a replay of the same inputs reproduces the same errors.</summary>
    public sealed class DetRandom
    {
        ulong _s;

        public DetRandom(ulong seed) { _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed; }

        public static DetRandom ForShot(ulong matchSeed, int shotIndex)
        {
            ulong z = matchSeed + 0x9E3779B97F4A7C15UL * (ulong)(shotIndex + 1);
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return new DetRandom(z ^ (z >> 31));
        }

        public ulong NextU64()
        {
            _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27;
            return _s * 0x2545F4914F6CDD1DUL;
        }

        /// <summary>Uniform in [0, 1).</summary>
        public double NextDouble() => (NextU64() >> 11) * (1.0 / (1UL << 53));

        /// <summary>Standard normal (Box–Muller).</summary>
        public double NextGaussian()
        {
            double u1 = 1.0 - NextDouble(), u2 = NextDouble();
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        }
    }
}
