using System;
using Unity.Collections;
using Unity.Mathematics;

namespace Loamlight.World
{
    public enum TileShape : byte { Full = 0, HalfBottom = 1, SlopeUpRight = 2, SlopeUpLeft = 3 }

    public enum LiquidType : byte { None = 0, Water = 1, Lava = 2 }

    /// <summary>
    /// Blittable description of the chunk-major layout, safe to pass into Burst jobs.
    /// index = (chunk &lt;&lt; 10) | (ly &lt;&lt; 5) | lx, chunk = cy * ChunksX + cx.
    /// </summary>
    public readonly struct WorldLayout
    {
        public const int ChunkShift = 5;
        public const int ChunkSize = 1 << ChunkShift;      // 32
        public const int ChunkMask = ChunkSize - 1;
        public const int ChunkArea = ChunkSize * ChunkSize; // 1024

        public readonly int Width;      // playable width in tiles
        public readonly int Height;     // playable height in tiles
        public readonly int ChunksX;
        public readonly int ChunksY;

        public WorldLayout(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            Width = width;
            Height = height;
            ChunksX = (width + ChunkMask) >> ChunkShift;
            ChunksY = (height + ChunkMask) >> ChunkShift;
        }

        public int ChunkCount => ChunksX * ChunksY;
        public int StorageLength => ChunkCount * ChunkArea;

        public bool InBounds(int x, int y) => (uint)x < (uint)Width && (uint)y < (uint)Height;

        public int Index(int x, int y) =>
            ((((y >> ChunkShift) * ChunksX) + (x >> ChunkShift)) << 10) | ((y & ChunkMask) << ChunkShift) | (x & ChunkMask);

        public int2 Coord(int index)
        {
            int chunk = index >> 10;
            int local = index & (ChunkArea - 1);
            return new int2((chunk % ChunksX) * ChunkSize + (local & ChunkMask), (chunk / ChunksX) * ChunkSize + (local >> ChunkShift));
        }

        public int ChunkOf(int x, int y) => (y >> ChunkShift) * ChunksX + (x >> ChunkShift);
    }

    /// <summary>
    /// The whole world grid: structure-of-arrays NativeArrays in chunk-major order (see <see cref="WorldLayout"/>).
    /// Main-thread writes go through the setters so render/simulation dirty flags stay correct.
    /// </summary>
    public sealed class WorldData : IDisposable
    {
        public readonly WorldLayout Layout;
        public readonly uint Seed;

        public NativeArray<ushort> Blocks;
        public NativeArray<ushort> Walls;
        public NativeArray<byte> Shapes;
        public NativeArray<byte> Frames;
        public NativeArray<byte> Liquid;
        public NativeArray<byte> LiquidKind;
        /// <summary>Per chunk: bit 0 = mesh dirty, bit 1 = liquid active, bit 2 = tile sim active.</summary>
        public NativeArray<byte> ChunkFlags;

        public const byte FlagMeshDirty = 1, FlagLiquidActive = 2, FlagSimActive = 4;

        /// <summary>Raised on the main thread after a tile edit: (x, y).</summary>
        public event Action<int, int> TileChanged;

        public int Width => Layout.Width;
        public int Height => Layout.Height;

        public WorldData(int width, int height, uint seed)
        {
            Layout = new WorldLayout(width, height);
            Seed = seed;
            int n = Layout.StorageLength;
            Blocks = new NativeArray<ushort>(n, Allocator.Persistent);
            Walls = new NativeArray<ushort>(n, Allocator.Persistent);
            Shapes = new NativeArray<byte>(n, Allocator.Persistent);
            Frames = new NativeArray<byte>(n, Allocator.Persistent);
            Liquid = new NativeArray<byte>(n, Allocator.Persistent);
            LiquidKind = new NativeArray<byte>(n, Allocator.Persistent);
            ChunkFlags = new NativeArray<byte>(Layout.ChunkCount, Allocator.Persistent);
        }

        public bool InBounds(int x, int y) => Layout.InBounds(x, y);
        public int Index(int x, int y) => Layout.Index(x, y);

        public ushort GetBlock(int x, int y) => InBounds(x, y) ? Blocks[Layout.Index(x, y)] : (ushort)0;
        public ushort GetWall(int x, int y) => InBounds(x, y) ? Walls[Layout.Index(x, y)] : (ushort)0;
        public TileShape GetShape(int x, int y) => InBounds(x, y) ? (TileShape)Shapes[Layout.Index(x, y)] : TileShape.Full;
        public byte GetFrame(int x, int y) => InBounds(x, y) ? Frames[Layout.Index(x, y)] : (byte)0;
        public byte GetLiquid(int x, int y) => InBounds(x, y) ? Liquid[Layout.Index(x, y)] : (byte)0;
        public LiquidType GetLiquidType(int x, int y) => InBounds(x, y) ? (LiquidType)LiquidKind[Layout.Index(x, y)] : LiquidType.None;

        public void SetBlock(int x, int y, ushort block, TileShape shape = TileShape.Full, byte frame = 0)
        {
            if (!InBounds(x, y)) return;
            int i = Layout.Index(x, y);
            Blocks[i] = block;
            Shapes[i] = (byte)shape;
            Frames[i] = frame;
            Touch(x, y, FlagMeshDirty | FlagLiquidActive | FlagSimActive);
        }

        public void SetShape(int x, int y, TileShape shape)
        {
            if (!InBounds(x, y)) return;
            Shapes[Layout.Index(x, y)] = (byte)shape;
            Touch(x, y, FlagMeshDirty | FlagLiquidActive);
        }

        public void SetFrame(int x, int y, byte frame)
        {
            if (!InBounds(x, y)) return;
            Frames[Layout.Index(x, y)] = frame;
            Touch(x, y, FlagMeshDirty);
        }

        public void SetWall(int x, int y, ushort wall)
        {
            if (!InBounds(x, y)) return;
            Walls[Layout.Index(x, y)] = wall;
            Touch(x, y, FlagMeshDirty);
        }

        public void SetLiquid(int x, int y, byte amount, LiquidType type)
        {
            if (!InBounds(x, y)) return;
            int i = Layout.Index(x, y);
            Liquid[i] = amount;
            LiquidKind[i] = (byte)(amount == 0 ? LiquidType.None : type);
            Touch(x, y, FlagMeshDirty | FlagLiquidActive);
        }

        /// <summary>Mark the chunk containing (x, y) and any chunk whose autotiling reads this tile.</summary>
        public void Touch(int x, int y, byte flags)
        {
            MarkChunk(x, y, flags);
            int lx = x & WorldLayout.ChunkMask, ly = y & WorldLayout.ChunkMask;
            if (lx == 0) MarkChunk(x - 1, y, flags);
            if (lx == WorldLayout.ChunkMask) MarkChunk(x + 1, y, flags);
            if (ly == 0) MarkChunk(x, y - 1, flags);
            if (ly == WorldLayout.ChunkMask) MarkChunk(x, y + 1, flags);
            TileChanged?.Invoke(x, y);
        }

        void MarkChunk(int x, int y, byte flags)
        {
            if (x < 0 || y < 0 || x >= Layout.ChunksX * WorldLayout.ChunkSize || y >= Layout.ChunksY * WorldLayout.ChunkSize) return;
            int c = Layout.ChunkOf(x, y);
            ChunkFlags[c] = (byte)(ChunkFlags[c] | flags);
        }

        public void MarkAllChunks(byte flags)
        {
            for (int i = 0; i < ChunkFlags.Length; i++) ChunkFlags[i] = (byte)(ChunkFlags[i] | flags);
        }

        public void Dispose()
        {
            if (Blocks.IsCreated) Blocks.Dispose();
            if (Walls.IsCreated) Walls.Dispose();
            if (Shapes.IsCreated) Shapes.Dispose();
            if (Frames.IsCreated) Frames.Dispose();
            if (Liquid.IsCreated) Liquid.Dispose();
            if (LiquidKind.IsCreated) LiquidKind.Dispose();
            if (ChunkFlags.IsCreated) ChunkFlags.Dispose();
        }
    }
}
