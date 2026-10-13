using System;
using System.Diagnostics;
using Loamlight.Content;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Loamlight.World.Generation
{
    /// <summary>Surface biomes (per column) and the two underground ones (Glowmire caverns, the Emberdeep floor).</summary>
    public enum Biome : byte { Meadow = 0, Dunes = 1, Frostfell = 2, Glowmire = 3, Emberdeep = 4 }

    /// <summary>One data-driven ore (from tiles.json "ore"): noise blobs inside host tiles within a depth band.</summary>
    public struct OreGen
    {
        public ushort id, host0, host1, host2, host3;
        public float depthMin, depthMax, scale, threshold;
        public float2 offset;
        public bool IsHost(ushort b) => b != 0 && (b == host0 || b == host1 || b == host2 || b == host3);
    }

    /// <summary>Numeric ids of every tile/wall the generator places (resolved from content by name; 0 = absent).</summary>
    public struct GenIds
    {
        public ushort dirt, grass, stone, sand, clay, gravel, snow, ice, sandstone, ash, basalt, obsidian, mossyStone;
        public ushort treeTrunk, treeTop, pineTop, cactus, grassDecor, duneGrass, glowcap;
        public ushort dirtWall, stoneWall, sandstoneWall, basaltWall, iceWall;
        public FixedList512Bytes<OreGen> ores;

        public static GenIds From(ContentDatabase db)
        {
            ushort T(string n) => db.TryTileId(n, out var v) ? v : (ushort)0;
            ushort W(string n)
            {
                for (int i = 0; i < db.walls.Length; i++)
                    if (db.walls[i].id == n) return (ushort)(i + 1);
                return 0;
            }
            var ids = new GenIds
            {
                dirt = T("dirt"), grass = T("grass"), stone = T("stone"), sand = T("sand"), clay = T("clay"), gravel = T("gravel"),
                snow = T("snow"), ice = T("ice"), sandstone = T("sandstone"), ash = T("ash"), basalt = T("basalt"), obsidian = T("obsidian"),
                mossyStone = T("mossy_stone"), treeTrunk = T("tree_trunk"), treeTop = T("tree_top"), pineTop = T("pine_top"),
                cactus = T("cactus"), grassDecor = T("grass_decor"), duneGrass = T("dune_grass"), glowcap = T("glowcap"),
                dirtWall = W("dirt_wall"), stoneWall = W("stone_wall"), sandstoneWall = W("sandstone_wall"), basaltWall = W("basalt_wall"),
                iceWall = W("ice_wall"),
            };
            // biomes fall back to their neighbours' materials when a tile is missing (tests with trimmed content)
            if (ids.sandstone == 0) ids.sandstone = ids.stone;
            if (ids.snow == 0) ids.snow = ids.dirt;
            if (ids.ice == 0) ids.ice = ids.stone;
            if (ids.ash == 0) ids.ash = ids.stone;
            if (ids.basalt == 0) ids.basalt = ids.stone;
            if (ids.sandstoneWall == 0) ids.sandstoneWall = ids.stoneWall;
            if (ids.basaltWall == 0) ids.basaltWall = ids.stoneWall;
            if (ids.iceWall == 0) ids.iceWall = ids.stoneWall;
            foreach (var t in db.tiles)
            {
                if (!t.ore.enabled || ids.ores.Length >= ids.ores.Capacity) continue;
                var g = new OreGen
                {
                    id = T(t.id), depthMin = t.ore.depthMin, depthMax = t.ore.depthMax, scale = t.ore.scale, threshold = t.ore.threshold,
                };
                var h = t.ore.hosts;
                g.host0 = h.Length > 0 ? T(h[0]) : (ushort)0;
                g.host1 = h.Length > 1 ? T(h[1]) : (ushort)0;
                g.host2 = h.Length > 2 ? T(h[2]) : (ushort)0;
                g.host3 = h.Length > 3 ? T(h[3]) : (ushort)0;
                ids.ores.Add(g);
            }
            return ids;
        }
    }

    /// <summary>Shape of the world: fractions of the height, measured from the bottom (y = 0).</summary>
    public struct GenParams
    {
        public float surfaceFrac;      // average surface height
        public float cavernFrac;       // below this caverns open up
        public float underworldFrac;   // below this lies the Emberdeep
        public float surfaceAmplitude; // tiles of large-scale hills
        public float caveDensity;      // 0..1 scaling of tunnel width

        public static GenParams Default => new GenParams
        {
            surfaceFrac = 0.72f, cavernFrac = 0.45f, underworldFrac = 0.1f, surfaceAmplitude = 30f, caveDensity = 1f,
        };
    }

    public sealed class GenReport
    {
        public double ColumnsMs, FillMs, LakesMs, SettleMs, PoolsMs, SurfaceMs, MossMs, DecorateMs, TotalMs;
        public int Trees, Lakes, WaterPools, LavaPools, Settled;
        public override string ToString() =>
            $"columns {ColumnsMs:0.0} ms, fill {FillMs:0.0} ms, lakes {LakesMs:0.0} ms ({Lakes}), settle {SettleMs:0.0} ms ({Settled} fell), " +
            $"pools {PoolsMs:0.0} ms ({WaterPools} water, {LavaPools} lava), surface {SurfaceMs:0.0} ms, moss {MossMs:0.0} ms, " +
            $"decorate {DecorateMs:0.0} ms ({Trees} trees), total {TotalMs:0.0} ms";
    }

    /// <summary>
    /// Which biome a cell belongs to. Pure function of (seed, position, column biomes), so the fill and the later
    /// moss / glowcap passes agree without storing a per-cell biome map.
    /// </summary>
    public struct BiomeField
    {
        public int width, cavernY, underworldY;
        public float2 oJitter, oDepth, oGlow, oUnder;

        public static BiomeField Create(uint seed, int width, int height, GenParams p) => new BiomeField
        {
            width = width, cavernY = (int)(height * p.cavernFrac), underworldY = (int)(height * p.underworldFrac),
            oJitter = Noise2.Offset(seed, 30), oDepth = Noise2.Offset(seed, 31), oGlow = Noise2.Offset(seed, 32), oUnder = Noise2.Offset(seed, 33),
        };

        public Biome At(int x, int y, NativeArray<byte> columnBiome)
        {
            if (y < underworldY + (int)(Noise2.Fbm(new float2(x * 0.02f, 0f) + oUnder, 2) * 7f)) return Biome.Emberdeep;
            // surface biomes reach down to the caverns with ragged, noisy borders
            int jx = math.clamp(x + (int)(noise.snoise(new float2(x * 0.01f, y * 0.04f) + oJitter) * 14f), 0, width - 1);
            var column = (Biome)columnBiome[jx];
            if (column != Biome.Meadow && y > cavernY - 40 + (int)(noise.snoise(new float2(x * 0.02f, 0f) + oDepth) * 16f)) return column;
            if (y < cavernY + 20 && Noise2.Fbm(new float2(x, y) * 0.0055f + oGlow, 2) > 0.3f) return Biome.Glowmire;
            return Biome.Meadow;
        }
    }

    /// <summary>
    /// Seeded, deterministic world generation. Per-column and per-chunk passes are parallel Burst jobs that only
    /// write their own column/chunk; passes that write across chunks (lakes, basin pools, trees) run on one thread in a
    /// fixed order with seeded RNGs. Same seed and size always give the same <see cref="WorldHash.Compute"/>.
    /// Pass order: columns (heights, biomes) → fill (materials, ores, caves) → lakes → settle loose blocks → basin
    /// pools (water, lava) → grass → moss → glowcaps → trees and plants.
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
            var columnBiome = new NativeArray<byte>(width, Allocator.TempJob);
            var counts = new NativeArray<int>(4, Allocator.TempJob);
            for (int o = 0; o < ids.ores.Length; o++)
            {
                var g = ids.ores[o];
                g.offset = Noise2.Offset(seed, 40u + (uint)o);
                ids.ores[o] = g;
            }
            var field = BiomeField.Create(seed, width, height, p);
            try
            {
                var sw = Stopwatch.StartNew();
                var rng = Unity.Mathematics.Random.CreateFromIndex(seed ^ 0x5eedu);
                bool dunesLeft = rng.NextBool();
                float dunesC = (dunesLeft ? 0.25f : 0.75f) + rng.NextFloat(-0.03f, 0.03f);
                float frostC = (dunesLeft ? 0.75f : 0.25f) + rng.NextFloat(-0.03f, 0.03f);
                new ColumnsJob
                {
                    seed = seed, height = height, p = p, surface = surface, dirtDepth = dirtDepth, biome = columnBiome,
                    dunesCenter = dunesC * width, frostCenter = frostC * width, dunesHalf = 0.075f * width, frostHalf = 0.08f * width,
                }.Schedule(width, 64).Complete();
                report.ColumnsMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                new FillChunksJob
                {
                    layout = world.Layout, seed = seed, p = p, ids = ids, field = field, surface = surface, dirtDepth = dirtDepth,
                    columnBiome = columnBiome, blocks = world.Blocks, walls = world.Walls,
                }.Schedule(world.Layout.ChunkCount, 1).Complete();
                report.FillMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                report.Lakes = CarveLakes(world, ids, surface, columnBiome, seed);
                report.LakesMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var settled = new NativeArray<int>(width, Allocator.TempJob);
                new SettleJob { layout = world.Layout, ids = ids, blocks = world.Blocks, moved = settled }.Schedule(width, 64).Complete();
                for (int x = 0; x < width; x++) report.Settled += settled[x];
                settled.Dispose();
                report.SettleMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var mark = new NativeArray<byte>(world.Layout.StorageLength, Allocator.TempJob);
                var stack = new NativeList<int>(4096, Allocator.TempJob);
                var set = new NativeList<int>(4096, Allocator.TempJob);
                new PoolsJob
                {
                    layout = world.Layout, seed = seed, p = p, blocks = world.Blocks, liquid = world.Liquid, kind = world.LiquidKind,
                    mark = mark, stack = stack, set = set, counts = counts,
                }.Run();
                mark.Dispose(); stack.Dispose(); set.Dispose();
                report.WaterPools = counts[0];
                report.LavaPools = counts[1];
                report.PoolsMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                new SurfaceJob
                {
                    layout = world.Layout, ids = ids, surface = surface, blocks = world.Blocks, liquid = world.Liquid,
                }.Schedule(width, 64).Complete();
                report.SurfaceMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                new MossJob
                {
                    layout = world.Layout, seed = seed, ids = ids, field = field, surface = surface, columnBiome = columnBiome,
                    blocks = world.Blocks,
                }.Schedule(width, 32).Complete();
                new GlowcapJob
                {
                    layout = world.Layout, seed = seed, ids = ids, field = field, surface = surface, columnBiome = columnBiome,
                    blocks = world.Blocks, liquid = world.Liquid,
                }.Schedule(width, 32).Complete();
                report.MossMs = sw.Elapsed.TotalMilliseconds;

                // cross-chunk writes: single-threaded, fixed order, seeded RNG only
                sw.Restart();
                report.Trees = Decorate(world, ids, surface, columnBiome, seed);
                report.DecorateMs = sw.Elapsed.TotalMilliseconds;
            }
            finally
            {
                surface.Dispose();
                dirtDepth.Dispose();
                columnBiome.Dispose();
                counts.Dispose();
            }
            world.MarkAllChunks(WorldData.FlagMeshDirty | WorldData.FlagLiquidActive | WorldData.FlagSimActive);
            report.TotalMs = total.Elapsed.TotalMilliseconds;
            return world;
        }

        public static int SurfaceLevel(int height, GenParams p) => (int)(height * p.surfaceFrac);

        /// <summary>Surface biome of every column (cheap: re-runs only the columns pass). Same for a seed forever.</summary>
        public static Biome[] SurfaceBiomes(int width, int height, uint seed, GenParams p)
        {
            var surface = new NativeArray<int>(width, Allocator.TempJob);
            var dirtDepth = new NativeArray<int>(width, Allocator.TempJob);
            var biome = new NativeArray<byte>(width, Allocator.TempJob);
            try
            {
                var rng = Unity.Mathematics.Random.CreateFromIndex(seed ^ 0x5eedu);
                bool dunesLeft = rng.NextBool();
                float dunesC = (dunesLeft ? 0.25f : 0.75f) + rng.NextFloat(-0.03f, 0.03f);
                float frostC = (dunesLeft ? 0.75f : 0.25f) + rng.NextFloat(-0.03f, 0.03f);
                new ColumnsJob
                {
                    seed = seed, height = height, p = p, surface = surface, dirtDepth = dirtDepth, biome = biome,
                    dunesCenter = dunesC * width, frostCenter = frostC * width, dunesHalf = 0.075f * width, frostHalf = 0.08f * width,
                }.Schedule(width, 64).Complete();
                var result = new Biome[width];
                for (int x = 0; x < width; x++) result[x] = (Biome)biome[x];
                return result;
            }
            finally
            {
                surface.Dispose(); dirtDepth.Dispose(); biome.Dispose();
            }
        }

        /// <summary>Bowl-shaped meadow lakes, filled to just under the lower rim. Keeps clear of the spawn.</summary>
        static int CarveLakes(WorldData w, GenIds ids, NativeArray<int> surface, NativeArray<byte> columnBiome, uint seed)
        {
            var rng = new Unity.Mathematics.Random(seed * 747796405u + 2891336453u);
            var layout = w.Layout;
            int lakes = 0, count = w.Width / 420;
            for (int k = 0; k < count; k++)
            {
                int hw = rng.NextInt(8, 19), depth = rng.NextInt(4, 9);
                int cx = rng.NextInt(hw + 12, w.Width - hw - 12);
                if (math.abs(cx - w.Width / 2) < 90) continue;
                bool meadow = true;
                for (int x = cx - hw - 3; x <= cx + hw + 3 && meadow; x++) meadow = columnBiome[x] == (byte)Biome.Meadow;
                if (!meadow) continue;
                int sl = surface[cx - hw], sr = surface[cx + hw];
                if (math.abs(sl - sr) > 6) continue;
                int level = math.min(sl, sr) - 1;
                for (int x = cx - hw + 1; x < cx + hw; x++)
                {
                    float u = (x - cx) / (float)hw;
                    int bottom = level - (int)math.round(depth * math.sqrt(math.max(0f, 1f - u * u)));
                    if (bottom >= level) bottom = level - 1;
                    for (int y = bottom + 1; y <= math.min(w.Height - 1, math.max(surface[x], level) + 1); y++)
                    {
                        int i = layout.Index(x, y);
                        w.Blocks[i] = 0;
                        if (y > level - 2) w.Walls[i] = 0;
                        if (y <= level) { w.Liquid[i] = 255; w.LiquidKind[i] = (byte)LiquidType.Water; }
                    }
                    // a sandy bed
                    for (int y = bottom; y > bottom - 2 && y > 0; y--)
                    {
                        int i = layout.Index(x, y);
                        if (w.Blocks[i] != 0) w.Blocks[i] = ids.sand != 0 ? ids.sand : w.Blocks[i];
                    }
                    surface[x] = level;
                }
                lakes++;
            }
            return lakes;
        }

        /// <summary>Trees and plants, by biome. Writes across chunks, so it runs on one thread in x order.</summary>
        static int Decorate(WorldData w, GenIds ids, NativeArray<int> surface, NativeArray<byte> columnBiome, uint seed)
        {
            if (ids.treeTrunk == 0) return 0;
            var rng = new Unity.Mathematics.Random(seed * 2654435761u + 17u);
            var layout = w.Layout;
            int trees = 0;
            int nextTree = 4 + rng.NextInt(0, 6);
            for (int x = 3; x < w.Width - 3; x++)
            {
                int top = TopSolid(w, x, math.min(w.Height - 1, surface[x] + 3));
                if (top < 0 || top + 1 >= w.Height) continue;
                int ground = layout.Index(x, top);
                int above = layout.Index(x, top + 1);
                if (w.Liquid[above] != 0) continue;
                ushort g = w.Blocks[ground];
                var biome = (Biome)columnBiome[x];
                if (biome == Biome.Dunes && g == ids.sand)
                {
                    if (ids.cactus != 0 && x >= nextTree && CanGrowTree(w, x, top, 6, 1))
                    {
                        int h = 2 + rng.NextInt(0, 3);
                        for (int i = 1; i <= h; i++) w.Blocks[layout.Index(x, top + i)] = ids.cactus;
                        trees++;
                        nextTree = x + 9 + rng.NextInt(0, 16);
                    }
                    else if (ids.duneGrass != 0 && w.Blocks[above] == 0 && rng.NextFloat() < 0.14f) w.Blocks[above] = ids.duneGrass;
                    continue;
                }
                bool pine = biome == Biome.Frostfell && g == ids.snow && ids.pineTop != 0;
                if (!pine && g != ids.grass) continue;
                if (x >= nextTree && CanGrowTree(w, x, top, 18, 2))
                {
                    int height = (pine ? 7 : 6) + rng.NextInt(0, 7);
                    for (int i = 1; i <= height; i++) w.Blocks[layout.Index(x, top + i)] = ids.treeTrunk;
                    // canopy: 5x4 object centred on the trunk, cell index = cx + cy * 5
                    int ox = x - 2, oy = top + height + 1;
                    ushort canopy = pine ? ids.pineTop : ids.treeTop;
                    if (oy + 4 < w.Height)
                        for (int cy = 0; cy < 4; cy++)
                        for (int cx = 0; cx < 5; cx++)
                        {
                            int i = layout.Index(ox + cx, oy + cy);
                            if (w.Blocks[i] != 0) continue;
                            w.Blocks[i] = canopy;
                            w.Frames[i] = (byte)(cx + cy * 5);
                        }
                    trees++;
                    nextTree = x + (pine ? 7 : 5) + rng.NextInt(0, pine ? 12 : 9);
                    continue;
                }
                if (!pine && ids.grassDecor != 0 && w.Blocks[above] == 0 && rng.NextFloat() < 0.4f) w.Blocks[above] = ids.grassDecor;
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

        static bool CanGrowTree(WorldData w, int x, int top, int clearance, int halfWidth)
        {
            // flat-ish ground and clear, dry air for trunk + canopy
            for (int dx = -halfWidth; dx <= halfWidth; dx++)
            for (int dy = 1; dy <= clearance; dy++)
            {
                if (!w.InBounds(x + dx, top + dy)) return false;
                int i = w.Layout.Index(x + dx, top + dy);
                if (w.Blocks[i] != 0 || w.Liquid[i] != 0) return false;
            }
            return w.Blocks[w.Layout.Index(x - 1, top)] != 0 && w.Blocks[w.Layout.Index(x + 1, top)] != 0;
        }

        [BurstCompile(CompileSynchronously = true)]
        struct ColumnsJob : IJobParallelFor
        {
            public uint seed;
            public int height;
            public GenParams p;
            public float dunesCenter, frostCenter, dunesHalf, frostHalf;
            [WriteOnly] public NativeArray<int> surface;
            [WriteOnly] public NativeArray<int> dirtDepth;
            [WriteOnly] public NativeArray<byte> biome;

            float Weight(int x, float center, float half, float2 o)
            {
                float edge = half + Noise2.Fbm(new float2(x * 0.012f, 5f) + o, 2) * 24f;
                return math.smoothstep(edge + 28f, edge - 28f, math.abs(x - center));
            }

            public void Execute(int x)
            {
                float2 o1 = Noise2.Offset(seed, 1), o2 = Noise2.Offset(seed, 2), o3 = Noise2.Offset(seed, 3);
                float2 o4 = Noise2.Offset(seed, 4), o5 = Noise2.Offset(seed, 5), o6 = Noise2.Offset(seed, 6);
                float dunes = Weight(x, dunesCenter, dunesHalf, o4), frost = Weight(x, frostCenter, frostHalf, o5);
                float amplitude = p.surfaceAmplitude * (1f - 0.5f * dunes + 0.7f * frost);
                float hills = Noise2.Fbm(new float2(x * 0.0035f, 0f) + o1, 4) * amplitude;
                float bumps = Noise2.Fbm(new float2(x * 0.03f, 0f) + o2, 3) * 4f * (1f - dunes);
                // long rolling dunes in the desert, sharp crags in the frost
                float duneWaves = (math.sin(x * 0.045f + Noise2.Fbm(new float2(x * 0.006f, 9f) + o6, 2) * 4f) * 0.5f + 0.5f) * 7f * dunes;
                float crags = math.max(0f, Noise2.Ridge(new float2(x * 0.012f, 2f) + o6) - 0.55f) * 40f * frost;
                surface[x] = (int)(height * p.surfaceFrac + hills + bumps + duneWaves + crags);
                dirtDepth[x] = 10 + (int)(Noise2.Fbm(new float2(x * 0.02f, 3f) + o3, 2) * 5f);
                biome[x] = (byte)(dunes > 0.5f ? Biome.Dunes : frost > 0.5f ? Biome.Frostfell : Biome.Meadow);
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct FillChunksJob : IJobParallelFor
        {
            public WorldLayout layout;
            public uint seed;
            public GenParams p;
            public GenIds ids;
            public BiomeField field;
            [ReadOnly] public NativeArray<int> surface;
            [ReadOnly] public NativeArray<int> dirtDepth;
            [ReadOnly] public NativeArray<byte> columnBiome;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> walls;

            public void Execute(int chunk)
            {
                int cx = chunk % layout.ChunksX, cy = chunk / layout.ChunksX;
                float2 oTunnelA = Noise2.Offset(seed, 10), oTunnelB = Noise2.Offset(seed, 11), oCavern = Noise2.Offset(seed, 12);
                float2 oPocket = Noise2.Offset(seed, 13), oEntrance = Noise2.Offset(seed, 14), oMix = Noise2.Offset(seed, 15);
                float2 oClay = Noise2.Offset(seed, 16), oSand = Noise2.Offset(seed, 17), oGravel = Noise2.Offset(seed, 18);
                float2 oHall = Noise2.Offset(seed, 19), oObsidian = Noise2.Offset(seed, 22);
                int cavernY = field.cavernY;
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
                    var biome = field.At(x, y, columnBiome);
                    float mix = Noise2.Fbm(pos * 0.045f + oMix, 2);
                    bool topLayer = depth < dirtDepth[x];
                    ushort block, wall;
                    switch (biome)
                    {
                        case Biome.Dunes:
                            block = depth < dirtDepth[x] + 6 ? ids.sand : mix > 0.6f ? ids.sand : ids.sandstone;
                            wall = ids.sandstoneWall;
                            break;
                        case Biome.Frostfell:
                            block = topLayer ? (mix > 0.58f ? ids.ice : ids.snow) : (mix > 0.4f ? ids.ice : ids.stone);
                            wall = topLayer || block == ids.ice ? ids.iceWall : ids.stoneWall;
                            break;
                        case Biome.Emberdeep:
                            block = mix > 0.45f ? ids.ash : ids.basalt;
                            if (noise.snoise(pos * 0.09f + oObsidian) > 0.82f) block = ids.obsidian;
                            wall = ids.basaltWall;
                            break;
                        default:   // Meadow and the Glowmire share rock; the Glowmire is told apart by moss and light
                            if (topLayer) block = mix > 0.55f ? ids.stone : ids.dirt;
                            else if (y > cavernY) block = mix > 0.45f ? ids.dirt : ids.stone;
                            else block = mix > 0.62f ? ids.dirt : ids.stone;
                            if (block == ids.dirt && depth > 4 && Noise2.Fbm(pos * 0.06f + oClay, 2) > 0.6f) block = ids.clay;
                            // loose pockets that collapse when undermined: sand in the upper earth, gravel in the rock
                            if (ids.sand != 0 && depth > 8 && y > cavernY && noise.snoise(pos * 0.08f + oSand) > 0.86f) block = ids.sand;
                            else if (ids.gravel != 0 && depth > 20 && block == ids.stone && noise.snoise(pos * 0.075f + oGravel) > 0.85f) block = ids.gravel;
                            wall = topLayer || block == ids.dirt || block == ids.clay ? ids.dirtWall : ids.stoneWall;
                            break;
                    }
                    // background walls start a few tiles under the surface, so sky light reaches the topsoil
                    if (depth <= 3) wall = 0;

                    // ores from content: first matching band and host wins
                    if (depth > 6)
                    {
                        float fromTop = 1f - (float)y / layout.Height;
                        for (int o = 0; o < ids.ores.Length; o++)
                        {
                            var ore = ids.ores[o];
                            if (fromTop < ore.depthMin || fromTop > ore.depthMax || !ore.IsHost(block)) continue;
                            if (noise.snoise(pos * ore.scale + ore.offset) > ore.threshold) { block = ore.id; break; }
                        }
                    }

                    // caves
                    float depthFrac = math.saturate((float)depth / math.max(1, s));
                    float tunnelWidth = (0.055f + 0.03f * depthFrac) * p.caveDensity;
                    bool tunnel = Noise2.Ridge(pos * new float2(0.018f, 0.026f) + oTunnelA) > 1f - tunnelWidth
                               || Noise2.Ridge(pos * new float2(0.031f, 0.022f) + oTunnelB) > 1f - tunnelWidth * 0.8f;
                    bool open;
                    if (biome == Biome.Emberdeep)
                    {
                        // wide low halls over a solid floor, so lava has somewhere to pool
                        float hall = Noise2.Fbm(pos * new float2(0.008f, 0.019f) + oHall, 3);
                        int floor = 5 + (int)((noise.snoise(new float2(x * 0.04f, 1f) + oHall) * 0.5f + 0.5f) * 5f);
                        open = (hall > 0.02f || tunnel) && y > floor;
                    }
                    else
                    {
                        float cavernThreshold = y < cavernY ? 0.38f - 0.12f * (1f - (float)y / cavernY) : 0.62f;
                        if (biome == Biome.Glowmire) cavernThreshold -= 0.05f;
                        bool cavern = Noise2.Fbm(pos * 0.011f + oCavern, 3) > cavernThreshold;
                        bool pocket = depth > 12 && noise.snoise(pos * 0.07f + oPocket) > 0.82f;
                        open = tunnel || cavern || pocket;
                        if (open && depth < 14)
                        {
                            // near the surface only a few tunnels break through, forming cave mouths
                            float entrance = noise.snoise(new float2(x * 0.01f, 0f) + oEntrance);
                            open = tunnel && entrance > 0.55f && depth > 1;
                        }
                    }
                    blocks[i] = open ? (ushort)0 : block;
                    walls[i] = wall;
                }
            }
        }

        /// <summary>Loose blocks (sand, gravel) drop onto whatever holds them, so a fresh world starts settled.</summary>
        [BurstCompile(CompileSynchronously = true)]
        struct SettleJob : IJobParallelFor
        {
            public WorldLayout layout;
            public GenIds ids;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;
            [WriteOnly] public NativeArray<int> moved;

            public void Execute(int x)
            {
                int count = 0;
                int floor = -1;     // lowest empty cell above the last support, or -1
                for (int y = 0; y < layout.Height; y++)
                {
                    int i = layout.Index(x, y);
                    ushort b = blocks[i];
                    if (b == 0) { if (floor < 0) floor = y; continue; }
                    bool loose = b == ids.sand || b == ids.gravel;
                    if (loose && floor >= 0)
                    {
                        blocks[layout.Index(x, floor)] = b;
                        blocks[i] = 0;
                        floor++;
                        count++;
                        continue;
                    }
                    floor = -1;
                }
                moved[x] = count;
            }
        }

        /// <summary>
        /// Basin filling: drop from a random open cell to the floor, then raise a water line one row at a time while the
        /// flooded region (everything reachable below the line) stays small and enclosed. A line that would spill into
        /// a big cave or off the map is rejected, so every pool starts at rest.
        /// </summary>
        [BurstCompile(CompileSynchronously = true)]
        struct PoolsJob : IJob
        {
            public WorldLayout layout;
            public uint seed;
            public GenParams p;
            public NativeArray<ushort> blocks;
            public NativeArray<byte> liquid, kind;
            public NativeArray<byte> mark;
            public NativeList<int> stack, set;
            public NativeArray<int> counts;

            public void Execute()
            {
                var rng = new Unity.Mathematics.Random(seed * 0x9e3779b9u + 7u);
                int w = layout.Width, h = layout.Height;
                int cavernY = (int)(h * p.cavernFrac), underY = (int)(h * p.underworldFrac);
                int surfaceY = (int)(h * p.surfaceFrac);
                // water through the underground and caverns, lava in the deep caverns, lava lakes in the Emberdeep
                counts[0] = Scatter(ref rng, w / 5, underY + 14, surfaceY - 30, LiquidType.Water, 700, 12);
                counts[1] = Scatter(ref rng, w / 18, underY + 14, (int)(cavernY * 0.45f), LiquidType.Lava, 400, 7)
                          + Scatter(ref rng, w / 6, 3, underY + 6, LiquidType.Lava, 3000, 14);
            }

            int Scatter(ref Unity.Mathematics.Random rng, int attempts, int yMin, int yMax, LiquidType type, int maxCells, int maxRise)
            {
                if (yMax <= yMin) return 0;
                int pools = 0;
                for (int a = 0; a < attempts; a++)
                {
                    int x = rng.NextInt(2, layout.Width - 2), y = rng.NextInt(yMin, yMax);
                    if (!Open(x, y)) continue;
                    while (y > 1 && Open(x, y - 1)) y--;
                    if (y <= 1 || liquid[layout.Index(x, y - 1)] != 0) continue;
                    int best = -1;
                    for (int level = y; level < y + maxRise; level++)
                    {
                        if (Flood(x, y, level, maxCells) < 0) break;
                        best = level;
                    }
                    if (best < 0) continue;
                    if (Flood(x, y, best, maxCells) < 4) { Clear(); continue; }
                    for (int k = 0; k < set.Length; k++)
                    {
                        liquid[set[k]] = 255;
                        kind[set[k]] = (byte)type;
                    }
                    Clear();
                    pools++;
                }
                return pools;
            }

            bool Open(int x, int y)
            {
                if (!layout.InBounds(x, y)) return false;
                int i = layout.Index(x, y);
                return blocks[i] == 0 && liquid[i] == 0;
            }

            /// <summary>Cells reachable from (sx, sy) without rising above <paramref name="level"/>; -1 if it leaks or grows too big.</summary>
            int Flood(int sx, int sy, int level, int maxCells)
            {
                Clear();
                int start = layout.Index(sx, sy);
                mark[start] = 1;
                set.Add(start);
                stack.Add(start);
                while (stack.Length > 0)
                {
                    int c = stack[stack.Length - 1];
                    stack.RemoveAt(stack.Length - 1);
                    int2 xy = layout.Coord(c);
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = xy.x + (d == 0 ? -1 : d == 1 ? 1 : 0);
                        int ny = xy.y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                        if (ny > level) continue;
                        if (nx < 1 || nx >= layout.Width - 1 || ny < 1) { stack.Clear(); return -1; }
                        int n = layout.Index(nx, ny);
                        if (mark[n] != 0 || blocks[n] != 0) continue;
                        if (liquid[n] != 0) { stack.Clear(); return -1; }   // touches another pool: leave it alone
                        mark[n] = 1;
                        set.Add(n);
                        if (set.Length > maxCells) { stack.Clear(); return -1; }
                        stack.Add(n);
                    }
                }
                return set.Length;
            }

            void Clear()
            {
                for (int k = 0; k < set.Length; k++) mark[set[k]] = 0;
                set.Clear();
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        struct SurfaceJob : IJobParallelFor
        {
            public WorldLayout layout;
            public GenIds ids;
            [ReadOnly] public NativeArray<int> surface;
            [ReadOnly] public NativeArray<byte> liquid;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;

            public void Execute(int x)
            {
                // grass on every dirt tile that sees dry sky near the surface
                int s = surface[x];
                int top = math.min(layout.Height - 1, s + 2);
                for (int y = top; y >= math.max(1, s - 40); y--)
                {
                    int i = layout.Index(x, y);
                    if (blocks[i] != ids.dirt) continue;
                    bool airAbove = y + 1 >= layout.Height;
                    if (!airAbove)
                    {
                        int a = layout.Index(x, y + 1);
                        airAbove = blocks[a] == 0 && liquid[a] == 0;
                    }
                    if (airAbove) blocks[i] = ids.grass;
                }
            }
        }

        /// <summary>Moss on exposed cavern stone: nearly everywhere in the Glowmire, in patches elsewhere.</summary>
        [BurstCompile(CompileSynchronously = true)]
        struct MossJob : IJobParallelFor
        {
            public WorldLayout layout;
            public uint seed;
            public GenIds ids;
            public BiomeField field;
            [ReadOnly] public NativeArray<int> surface;
            [ReadOnly] public NativeArray<byte> columnBiome;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;

            bool Air(int x, int y) => layout.InBounds(x, y) && blocks[layout.Index(x, y)] == 0;

            public void Execute(int x)
            {
                if (ids.mossyStone == 0) return;
                float2 oPatch = Noise2.Offset(seed, 34);
                int yMax = math.min(layout.Height - 2, surface[x] - 25);
                for (int y = 2; y < yMax; y++)
                {
                    int i = layout.Index(x, y);
                    if (blocks[i] != ids.stone) continue;
                    if (!(Air(x - 1, y) || Air(x + 1, y) || Air(x, y + 1) || Air(x, y - 1))) continue;
                    var biome = field.At(x, y, columnBiome);
                    float roll = (Noise2.Hash(x, y, seed) & 0xffff) / 65535f;
                    bool moss = biome == Biome.Glowmire
                        ? roll < 0.9f
                        : biome == Biome.Meadow && y < field.cavernY && noise.snoise(new float2(x, y) * 0.03f + oPatch) > 0.55f && roll < 0.7f;
                    if (moss) blocks[i] = ids.mossyStone;
                }
            }
        }

        /// <summary>Glowcaps on dry floors: carpets in the Glowmire, the odd cluster on moss elsewhere.</summary>
        [BurstCompile(CompileSynchronously = true)]
        struct GlowcapJob : IJobParallelFor
        {
            public WorldLayout layout;
            public uint seed;
            public GenIds ids;
            public BiomeField field;
            [ReadOnly] public NativeArray<int> surface;
            [ReadOnly] public NativeArray<byte> columnBiome;
            [ReadOnly] public NativeArray<byte> liquid;
            [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;

            public void Execute(int x)
            {
                if (ids.glowcap == 0) return;
                int yMax = math.min(layout.Height - 2, surface[x] - 20);
                for (int y = 2; y < yMax; y++)
                {
                    int i = layout.Index(x, y);
                    if (blocks[i] != 0 || liquid[i] != 0) continue;
                    ushort below = blocks[layout.Index(x, y - 1)];
                    if (below != ids.mossyStone && below != ids.stone) continue;
                    float roll = (Noise2.Hash(x, y, seed ^ 0x61c88647u) & 0xffff) / 65535f;
                    float chance = field.At(x, y, columnBiome) == Biome.Glowmire ? 0.2f : below == ids.mossyStone ? 0.035f : 0f;
                    if (roll < chance) blocks[i] = ids.glowcap;
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
