using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Loamlight.World.Generation
{
    /// <summary>Numeric ids of every tile/wall the generator places (resolved from content by name).</summary>
    public struct GenIds
    {
        public ushort dirt, grass, stone, sand, clay;
        public ushort copperOre, ironOre;
        public ushort treeTrunk, treeTop, grassDecor;
        public ushort dirtWall, stoneWall;
    }

    /// <summary>Shape of the world: fractions of the height, measured from the bottom (y = 0).</summary>
    public struct GenParams
    {
        public float surfaceFrac;      // average surface height
        public float cavernFrac;       // below this caverns open up
        public float surfaceAmplitude; // tiles of large-scale hills
        public float caveDensity;      // 0..1 scaling of tunnel width

        public static GenParams Default => new GenParams
        {
            surfaceFrac = 0.72f, cavernFrac = 0.45f, surfaceAmplitude = 30f, caveDensity = 1f,
        };
    }

    public sealed class GenReport
    {
        public double ColumnsMs, FillMs, SurfaceMs, DecorateMs, TotalMs;
        public int Trees;
        public override string ToString() =>
            $"columns {ColumnsMs:0.0} ms, fill {FillMs:0.0} ms, surface {SurfaceMs:0.0} ms, decorate {DecorateMs:0.0} ms ({Trees} trees), total {TotalMs:0.0} ms";
    }

    /// <summary>
    /// Seeded, deterministic world generation. Per-column and per-chunk passes are parallel Burst jobs that only
    /// write their own column/chunk; anything that writes across chunks (later: structures, trees, liquids) runs
    /// single-threaded in a fixed order. Same seed and size always give the same <see cref="WorldHash.Compute"/>.
    /// </summary>
    public static class WorldGenerator
    {
        public static WorldData Generate(int width, int height, uint seed, GenIds ids, GenParams p, out GenReport report)
        {
            report = new GenReport();
            var total = Stopwatch.StartNew();
            var world = new WorldData(width, height, seed);
            var surface = new NativeArray<int>(width, Allocator.TempJob);
            var dirtDepth = new NativeArray<int>(width, Allocator.TempJob);
            try
            {
                var sw = Stopwatch.StartNew();
                new ColumnsJob
                {
                    seed = seed, height = height, p = p, surface = surface, dirtDepth = dirtDepth,
                }.Schedule(width, 64).Complete();
                report.ColumnsMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                new FillChunksJob
                {
                    layout = world.Layout, seed = seed, p = p, ids = ids, surface = surface, dirtDepth = dirtDepth,
                    blocks = world.Blocks, walls = world.Walls,
                }.Schedule(world.Layout.ChunkCount, 1).Complete();
                report.FillMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                new SurfaceJob
                {
                    layout = world.Layout, seed = seed, ids = ids, surface = surface, blocks = world.Blocks,
                }.Schedule(width, 64).Complete();
                report.SurfaceMs = sw.Elapsed.TotalMilliseconds;

                // cross-chunk writes: single-threaded, fixed order, seeded RNG only
                sw.Restart();
                report.Trees = Decorate(world, ids, surface, seed);
                report.DecorateMs = sw.Elapsed.TotalMilliseconds;
            }
            finally
            {
                surface.Dispose();
                dirtDepth.Dispose();
            }
            world.MarkAllChunks(WorldData.FlagMeshDirty | WorldData.FlagLiquidActive | WorldData.FlagSimActive);
            report.TotalMs = total.Elapsed.TotalMilliseconds;
            return world;
        }

        public static int SurfaceLevel(int height, GenParams p) => (int)(height * p.surfaceFrac);

        /// <summary>Trees and tall grass on exposed grass. Writes across chunks, so it runs on one thread in x order.</summary>
        static int Decorate(WorldData w, GenIds ids, NativeArray<int> surface, uint seed)
        {
            if (ids.treeTrunk == 0) return 0;
            var rng = new Unity.Mathematics.Random(seed * 2654435761u + 17u);
            var layout = w.Layout;
            int trees = 0;
            int nextTree = 4 + rng.NextInt(0, 6);
            for (int x = 3; x < w.Width - 3; x++)
            {
                int top = TopSolid(w, x, math.min(w.Height - 1, surface[x] + 3));
                if (top < 0 || w.Blocks[layout.Index(x, top)] != ids.grass) continue;
                if (x >= nextTree && CanGrowTree(w, ids, x, top))
                {
                    int height = 6 + rng.NextInt(0, 7);
                    for (int i = 1; i <= height; i++) w.Blocks[layout.Index(x, top + i)] = ids.treeTrunk;
                    // canopy: 5x4 object centred on the trunk, cell index = cx + cy * 5
                    int ox = x - 2, oy = top + height + 1;
                    if (oy + 4 < w.Height)
                        for (int cy = 0; cy < 4; cy++)
                        for (int cx = 0; cx < 5; cx++)
                        {
                            int i = layout.Index(ox + cx, oy + cy);
                            if (w.Blocks[i] != 0) continue;
                            w.Blocks[i] = ids.treeTop;
                            w.Frames[i] = (byte)(cx + cy * 5);
                        }
                    trees++;
                    nextTree = x + 5 + rng.NextInt(0, 9);
                    continue;
                }
                int above = layout.Index(x, top + 1);
                if (ids.grassDecor != 0 && w.Blocks[above] == 0 && rng.NextFloat() < 0.4f) w.Blocks[above] = ids.grassDecor;
            }
            return trees;
        }

        static int TopSolid(WorldData w, int x, int fromY)
        {
            for (int y = fromY; y > 0; y--)
            {
                ushort b = w.Blocks[w.Layout.Index(x, y)];
                if (b != 0) return y;
            }
            return -1;
        }

        static bool CanGrowTree(WorldData w, GenIds ids, int x, int top)
        {
            // flat-ish ground and clear air for trunk + canopy
            for (int dx = -2; dx <= 2; dx++)
            for (int dy = 1; dy <= 18; dy++)
            {
                if (!w.InBounds(x + dx, top + dy)) return false;
                if (w.Blocks[w.Layout.Index(x + dx, top + dy)] != 0) return false;
            }
            return w.Blocks[w.Layout.Index(x - 1, top)] != 0 && w.Blocks[w.Layout.Index(x + 1, top)] != 0;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct ColumnsJob : IJobParallelFor
        {
            public uint seed;
            public int height;
            public GenParams p;
            [WriteOnly] public NativeArray<int> surface;
            [WriteOnly] public NativeArray<int> dirtDepth;

            public void Execute(int x)
            {
                float2 o1 = Noise2.Offset(seed, 1), o2 = Noise2.Offset(seed, 2), o3 = Noise2.Offset(seed, 3);
                float hills = Noise2.Fbm(new float2(x * 0.0035f, 0f) + o1, 4) * p.surfaceAmplitude;
                float bumps = Noise2.Fbm(new float2(x * 0.03f, 0f) + o2, 3) * 4f;
                surface[x] = (int)(height * p.surfaceFrac + hills + bumps);
                dirtDepth[x] = 10 + (int)(Noise2.Fbm(new float2(x * 0.02f, 3f) + o3, 2) * 5f);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct FillChunksJob : IJobParallelFor
        {
            public WorldLayout layout;
            public uint seed;
            public GenParams p;
            public GenIds ids;
            [ReadOnly] public NativeArray<int> surface;
            [ReadOnly] public NativeArray<int> dirtDepth;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> walls;

            public void Execute(int chunk)
            {
                int cx = chunk % layout.ChunksX, cy = chunk / layout.ChunksX;
                float2 oTunnelA = Noise2.Offset(seed, 10), oTunnelB = Noise2.Offset(seed, 11), oCavern = Noise2.Offset(seed, 12);
                float2 oPocket = Noise2.Offset(seed, 13), oEntrance = Noise2.Offset(seed, 14), oMix = Noise2.Offset(seed, 15);
                float2 oClay = Noise2.Offset(seed, 16), oCopper = Noise2.Offset(seed, 20), oIron = Noise2.Offset(seed, 21);
                int cavernY = (int)(layout.Height * p.cavernFrac);
                int baseIndex = chunk << 10;

                for (int ly = 0; ly < WorldLayout.ChunkSize; ly++)
                for (int lx = 0; lx < WorldLayout.ChunkSize; lx++)
                {
                    int x = cx * WorldLayout.ChunkSize + lx, y = cy * WorldLayout.ChunkSize + ly;
                    int i = baseIndex | (ly << WorldLayout.ChunkShift) | lx;
                    if (!layout.InBounds(x, y)) { blocks[i] = 0; walls[i] = 0; continue; }

                    int s = surface[x];
                    int depth = s - y;                   // tiles below the surface (negative = sky)
                    if (depth < 0) { blocks[i] = 0; walls[i] = 0; continue; }

                    float2 pos = new float2(x, y);
                    // material layers: dirt over stone, with pockets of each in the other and clay seams
                    float mix = Noise2.Fbm(pos * 0.045f + oMix, 2);
                    bool dirtLayer = depth < dirtDepth[x];
                    ushort block;
                    if (dirtLayer) block = mix > 0.55f ? ids.stone : ids.dirt;
                    else if (y > cavernY) block = mix > 0.45f ? ids.dirt : ids.stone;
                    else block = mix > 0.62f ? ids.dirt : ids.stone;
                    if (block == ids.dirt && depth > 4 && Noise2.Fbm(pos * 0.06f + oClay, 2) > 0.6f) block = ids.clay;
                    // ore veins: small noise blobs inside depth bands (copper shallow, iron deeper)
                    if (depth > 6 && block != 0)
                    {
                        if (ids.copperOre != 0 && depth < 260 && noise.snoise(pos * 0.11f + oCopper) > 0.82f) block = ids.copperOre;
                        else if (ids.ironOre != 0 && depth > 45 && depth < 420 && noise.snoise(pos * 0.1f + oIron) > 0.84f) block = ids.ironOre;
                    }

                    // background walls start a few tiles under the surface, so sky light reaches the topsoil
                    ushort wall = depth > 3 ? (dirtLayer || block == ids.dirt ? ids.dirtWall : ids.stoneWall) : (ushort)0;

                    // caves
                    float depthFrac = math.saturate((float)depth / math.max(1, s));
                    float tunnelWidth = (0.055f + 0.03f * depthFrac) * p.caveDensity;
                    bool tunnel = Noise2.Ridge(pos * new float2(0.018f, 0.026f) + oTunnelA) > 1f - tunnelWidth
                               || Noise2.Ridge(pos * new float2(0.031f, 0.022f) + oTunnelB) > 1f - tunnelWidth * 0.8f;
                    float cavernThreshold = y < cavernY ? 0.38f - 0.12f * (1f - (float)y / cavernY) : 0.62f;
                    bool cavern = Noise2.Fbm(pos * 0.011f + oCavern, 3) > cavernThreshold;
                    bool pocket = depth > 12 && noise.snoise(pos * 0.07f + oPocket) > 0.82f;
                    bool open = tunnel || cavern || pocket;
                    if (open && depth < 14)
                    {
                        // near the surface only a few tunnels break through, forming cave mouths
                        float entrance = noise.snoise(new float2(x * 0.01f, 0f) + oEntrance);
                        open = tunnel && entrance > 0.55f && depth > 1;
                    }
                    blocks[i] = open ? (ushort)0 : block;
                    walls[i] = wall;
                }
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct SurfaceJob : IJobParallelFor
        {
            public WorldLayout layout;
            public uint seed;
            public GenIds ids;
            [ReadOnly] public NativeArray<int> surface;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;

            public void Execute(int x)
            {
                // grass on every dirt tile that sees the sky near the surface
                int s = surface[x];
                int top = math.min(layout.Height - 1, s + 2);
                for (int y = top; y >= math.max(1, s - 40); y--)
                {
                    int i = layout.Index(x, y);
                    if (blocks[i] != ids.dirt) continue;
                    bool airAbove = y + 1 >= layout.Height || blocks[layout.Index(x, y + 1)] == 0;
                    if (airAbove) blocks[i] = ids.grass;
                }
            }
        }
    }

    /// <summary>Burst FNV-style hash over every world array (determinism checks on 5M tiles).</summary>
    public static class WorldHash
    {
        public static ulong Compute(WorldData world)
        {
            var result = new NativeArray<ulong>(1, Allocator.TempJob);
            try
            {
                new HashJob
                {
                    blocks = world.Blocks, walls = world.Walls, shapes = world.Shapes, frames = world.Frames,
                    liquid = world.Liquid, liquidKind = world.LiquidKind, result = result,
                }.Run();
                return result[0];
            }
            finally { result.Dispose(); }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct HashJob : IJob
        {
            [ReadOnly] public NativeArray<ushort> blocks, walls;
            [ReadOnly] public NativeArray<byte> shapes, frames, liquid, liquidKind;
            public NativeArray<ulong> result;

            public void Execute()
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < blocks.Length; i++)
                {
                    h = (h ^ blocks[i]) * 1099511628211UL;
                    h = (h ^ ((ulong)walls[i] << 16 | (ulong)shapes[i] << 8 | frames[i])) * 1099511628211UL;
                    h = (h ^ ((ulong)liquid[i] << 8 | liquidKind[i])) * 1099511628211UL;
                }
                result[0] = h;
            }
        }
    }

    /// <summary>Proves at runtime whether jobs actually run Burst-compiled (BurstDiscard code is stripped by Burst).</summary>
    public static class BurstProbe
    {
        public static bool IsBurstCompiled()
        {
            var flag = new NativeArray<int>(1, Allocator.TempJob);
            try
            {
                new ProbeJob { flag = flag }.Run();
                return flag[0] == 1;
            }
            finally { flag.Dispose(); }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct ProbeJob : IJob
        {
            public NativeArray<int> flag;

            public void Execute()
            {
                int v = 1;
                MarkManaged(ref v);
                flag[0] = v;
            }

            [BurstDiscard]
            static void MarkManaged(ref int v) => v = 0;
        }
    }
}
