using Loamlight.Content;
using Loamlight.World;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace Loamlight.Liquids
{
    /// <summary>Rectangle of tiles a simulation tick works on (clamped to the world).</summary>
    public struct SimRegion
    {
        public int x0, y0, x1, y1;   // x1/y1 exclusive
        public int Width => x1 - x0;
        public int Height => y1 - y0;
    }

    static class Cell
    {
        public static bool Blocks(NativeArray<ushort> blocks, NativeArray<TileProps> props, int i)
        {
            ushort b = blocks[i];
            if (b == 0) return false;
            var p = props[b];
            return p.solid != 0 || p.kind == (byte)TileKind.Door;
        }
    }

    /// <summary>
    /// Liquid pass 1, gravity: per column (parallel), bottom-up, liquid falls into the cell below as far as it has room.
    /// Water reaching lava (or lava reaching water) turns the lower cell into obsidian. Writes stay inside the column.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct LiquidFallJob : IJobParallelFor
    {
        public WorldLayout layout;
        public SimRegion region;
        public ushort obsidian;
        [ReadOnly] public NativeArray<TileProps> props;
        [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;
        [NativeDisableParallelForRestriction] public NativeArray<byte> liquid, kind;
        [NativeDisableParallelForRestriction] public NativeArray<byte> changed;   // per chunk
        [NativeDisableParallelForRestriction] public NativeArray<int> reactions;  // per column

        public void Execute(int col)
        {
            int x = region.x0 + col;
            int y0 = math.max(1, region.y0);
            for (int y = y0; y < region.y1; y++)
            {
                int i = layout.Index(x, y);
                int a = liquid[i];
                if (a == 0) continue;
                int j = layout.Index(x, y - 1);
                if (Cell.Blocks(blocks, props, j)) continue;
                int b = liquid[j];
                byte ta = kind[i], tb = kind[j];
                if (b > 0 && tb != ta)
                {
                    // water + lava: the lower cell solidifies, the falling liquid is consumed (steam)
                    blocks[j] = obsidian;
                    liquid[j] = 0;
                    kind[j] = 0;
                    int used = math.min(a, 96);
                    liquid[i] = (byte)(a - used);
                    if (liquid[i] == 0) kind[i] = 0;
                    reactions[col]++;
                    Mark(x, y); Mark(x, y - 1);
                    continue;
                }
                int move = math.min(a, 255 - b);
                if (move <= 0) continue;
                liquid[j] = (byte)(b + move);
                kind[j] = ta;
                liquid[i] = (byte)(a - move);
                if (liquid[i] == 0) kind[i] = 0;
                Mark(x, y); Mark(x, y - 1);
            }
        }

        void Mark(int x, int y) => changed[layout.ChunkOf(x, y)] = 1;
    }

    /// <summary>
    /// Liquid pass 2, levelling: per row (parallel), liquid resting on something spreads sideways toward lower
    /// neighbours, equalising pairs. Scan direction alternates per tick and per row so there is no drift.
    /// Writes stay inside the row, so the pass is exactly mass-conserving apart from evaporation of thin films.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct LiquidSpreadJob : IJobParallelFor
    {
        public WorldLayout layout;
        public SimRegion region;
        public int parity;
        public ushort obsidian;
        [ReadOnly] public NativeArray<TileProps> props;
        [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;
        [NativeDisableParallelForRestriction] public NativeArray<byte> liquid, kind;
        [NativeDisableParallelForRestriction] public NativeArray<byte> changed;
        [NativeDisableParallelForRestriction] public NativeArray<int> reactions;   // per row
        [NativeDisableParallelForRestriction] public NativeArray<int> evaporated;  // per row

        public void Execute(int row)
        {
            int y = region.y0 + row;
            bool rightward = ((parity + y) & 1) == 0;
            int w = region.Width;
            for (int k = 0; k < w; k++)
            {
                int x = rightward ? region.x0 + k : region.x1 - 1 - k;
                int i = layout.Index(x, y);
                int a = liquid[i];
                if (a == 0) continue;
                // only liquid that is supported spreads; falling liquid is handled by the gravity pass
                if (y > 0)
                {
                    int below = layout.Index(x, y - 1);
                    if (!Cell.Blocks(blocks, props, below) && liquid[below] < 255 && (liquid[below] == 0 || kind[below] == kind[i])) continue;
                }
                byte t = kind[i];
                int dir = rightward ? 1 : -1;
                for (int side = 0; side < 2; side++)
                {
                    int nx = side == 0 ? x + dir : x - dir;
                    if (nx < region.x0 || nx >= region.x1 || !layout.InBounds(nx, y)) continue;
                    int n = layout.Index(nx, y);
                    if (Cell.Blocks(blocks, props, n)) continue;
                    int nb = liquid[n];
                    if (nb > 0 && kind[n] != t)
                    {
                        // sideways contact: the lava side solidifies
                        int lavaCell = t == (byte)LiquidType.Lava ? i : n;
                        blocks[lavaCell] = obsidian;
                        liquid[lavaCell] = 0;
                        kind[lavaCell] = 0;
                        reactions[row]++;
                        changed[layout.ChunkOf(x, y)] = 1;
                        changed[layout.ChunkOf(nx, y)] = 1;
                        if (lavaCell == i) { a = 0; break; }
                        continue;
                    }
                    int diff = a - nb;
                    if (diff < 2) continue;
                    int move = side == 0 ? diff / 3 : diff / 2;
                    if (move <= 0) move = 1;
                    a -= move;
                    liquid[n] = (byte)(nb + move);
                    kind[n] = t;
                    changed[layout.ChunkOf(x, y)] = 1;
                    changed[layout.ChunkOf(nx, y)] = 1;
                }
                // thin films that cannot go anywhere evaporate (keeps the world from filling with 1-unit puddles)
                if (a > 0 && a < 4)
                {
                    bool settled = y == 0 || Cell.Blocks(blocks, props, layout.Index(x, y - 1)) || liquid[layout.Index(x, y - 1)] == 255;
                    if (settled && (parity & 7) == 0)
                    {
                        evaporated[row] += a;
                        a = 0;
                        changed[layout.ChunkOf(x, y)] = 1;
                    }
                }
                liquid[i] = (byte)a;
                if (a == 0) kind[i] = 0;
            }
        }
    }

    /// <summary>Sand and gravel: per column, bottom-up, a falling tile drops one cell into empty space (liquid moves up).</summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct FallingBlocksJob : IJobParallelFor
    {
        public WorldLayout layout;
        public SimRegion region;
        [ReadOnly] public NativeArray<TileProps> props;
        [NativeDisableParallelForRestriction] public NativeArray<ushort> blocks;
        [NativeDisableParallelForRestriction] public NativeArray<byte> shapes, frames, liquid, kind, changed;
        [NativeDisableParallelForRestriction] public NativeArray<int> moved;

        public void Execute(int col)
        {
            int x = region.x0 + col;
            for (int y = math.max(1, region.y0); y < region.y1; y++)
            {
                int i = layout.Index(x, y);
                ushort b = blocks[i];
                if (b == 0 || props[b].falls == 0) continue;
                int j = layout.Index(x, y - 1);
                if (blocks[j] != 0) continue;
                blocks[j] = b;
                shapes[j] = 0;
                frames[j] = 0;
                blocks[i] = 0;
                shapes[i] = 0;
                // displaced liquid swaps upward
                byte l = liquid[j], k = kind[j];
                liquid[j] = liquid[i];
                kind[j] = kind[i];
                liquid[i] = l;
                kind[i] = k;
                moved[col]++;
                changed[layout.ChunkOf(x, y)] = 1;
                changed[layout.ChunkOf(x, y - 1)] = 1;
            }
        }
    }

    /// <summary>
    /// Random-tick spreading (data-driven: a tile with spreadsOnto = X converts neighbouring X tiles): grass onto dirt
    /// that sees air, moss onto stone that touches open cave air. Grass buried under a solid block reverts to dirt.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public struct SpreadJob : IJob
    {
        public WorldLayout layout;
        public SimRegion region;
        public uint seed;
        public int samples;
        public ushort grass, dirt;
        [ReadOnly] public NativeArray<TileProps> props;
        public NativeArray<ushort> blocks;
        public NativeArray<byte> changed;
        public NativeArray<int> conversions;

        public void Execute()
        {
            var rng = Random.CreateFromIndex(seed);
            for (int s = 0; s < samples; s++)
            {
                int x = rng.NextInt(region.x0, region.x1), y = rng.NextInt(math.max(1, region.y0), math.min(layout.Height - 1, region.y1));
                int i = layout.Index(x, y);
                ushort b = blocks[i];
                if (b == 0) continue;
                if (b == grass && Solid(x, y + 1))
                {
                    blocks[i] = dirt;
                    changed[layout.ChunkOf(x, y)] = 1;
                    conversions[0]++;
                    continue;
                }
                if (!Exposed(x, y)) continue;
                // look for a neighbour that spreads onto this tile type
                for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if ((dx | dy) == 0 || !layout.InBounds(x + dx, y + dy)) continue;
                    ushort n = blocks[layout.Index(x + dx, y + dy)];
                    if (n == 0 || props[n].spreadsOnto != b) continue;
                    if (rng.NextFloat() > 0.5f) continue;
                    blocks[i] = n;
                    changed[layout.ChunkOf(x, y)] = 1;
                    conversions[0]++;
                    dx = 2; dy = 2;
                }
            }
        }

        bool Solid(int x, int y)
        {
            if (!layout.InBounds(x, y)) return false;
            ushort b = blocks[layout.Index(x, y)];
            return b != 0 && props[b].solid != 0;
        }

        bool Exposed(int x, int y) => !Solid(x, y + 1) || !Solid(x - 1, y) || !Solid(x + 1, y) || !Solid(x, y - 1);
    }
}
