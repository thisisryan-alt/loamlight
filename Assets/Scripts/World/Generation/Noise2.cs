using Unity.Mathematics;

namespace Loamlight.World.Generation
{
    /// <summary>Deterministic noise helpers on top of Unity.Mathematics simplex noise (Burst-compatible).</summary>
    public static class Noise2
    {
        /// <summary>Fractal Brownian motion, roughly in [-1, 1].</summary>
        public static float Fbm(float2 p, int octaves, float lacunarity = 2f, float gain = 0.5f)
        {
            float sum = 0f, amp = 1f, norm = 0f;
            for (int i = 0; i < octaves; i++)
            {
                // offset every octave so their lattices never line up
                sum += amp * noise.snoise(p + new float2(17.13f * i, -9.71f * i));
                norm += amp;
                p *= lacunarity;
                amp *= gain;
            }
            return sum / norm;
        }

        /// <summary>1 on the noise's zero set, falling to 0 away from it: thin winding lines ("worms").</summary>
        public static float Ridge(float2 p) => 1f - math.abs(noise.snoise(p));

        /// <summary>Stable per-seed offset for a named noise channel, so channels are independent.</summary>
        public static float2 Offset(uint seed, uint channel)
        {
            var r = Random.CreateFromIndex(seed * 7919u + channel * 104729u + 1u);
            return r.NextFloat2(-20000f, 20000f);
        }

        public static uint Hash(int x, int y, uint seed)
        {
            uint h = (uint)x * 0x8da6b343u ^ (uint)y * 0xd8163841u ^ seed * 0xcb1ab31fu;
            h ^= h >> 13;
            h *= 0x5bd1e995u;
            h ^= h >> 15;
            return h;
        }
    }
}
