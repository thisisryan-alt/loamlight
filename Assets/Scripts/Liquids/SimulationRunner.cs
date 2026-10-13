using Loamlight.Content;
using Loamlight.World;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace Loamlight.Liquids
{
    /// <summary>
    /// Pillar 2 on a fixed tick, independent of frame rate: liquids at 30 Hz (gravity pass then levelling pass),
    /// falling sand/gravel at 20 Hz, grass/moss spread random ticks at 4 Hz. Only a region around the camera is
    /// simulated; a tick is skipped entirely while no chunk in that region is flagged liquid/sim-active, and chunks
    /// whose cells stopped changing go back to sleep. Jobs complete inside the tick, so main-thread edits never race.
    /// </summary>
    public sealed class SimulationRunner : MonoBehaviour
    {
        public const float LiquidHz = 30f, FallHz = 20f, SpreadHz = 4f;
        static readonly ProfilerMarker TickMarker = new ProfilerMarker(ProfilerCategory.Scripts, "Loamlight.SimTick");

        public int halfWidth = 100, halfHeight = 70;
        public int spreadSamples = 600;
        public float TimeScale { get; set; } = 1f;
        public bool Paused { get; set; }

        WorldData _world;
        NativeArray<TileProps> _props;
        Camera _camera;
        ushort _obsidian, _grass, _dirt;
        NativeArray<byte> _changed;
        NativeArray<int> _perLine, _perLine2, _single;
        float _liquidAcc, _fallAcc, _spreadAcc;
        int _parity;
        uint _seed = 1;

        public long LiquidTicks { get; private set; }
        public long SkippedTicks { get; private set; }
        public long Reactions { get; private set; }
        public long BlocksFallen { get; private set; }
        public long Spreads { get; private set; }
        public long Evaporated { get; private set; }
        public double LastTickMs { get; private set; }

        public void Init(WorldData world, NativeArray<TileProps> props, ContentDatabase db, Camera cam)
        {
            Dispose();
            _world = world;
            _props = props;
            _camera = cam;
            _obsidian = db.TileId("obsidian");
            _grass = db.TileId("grass");
            _dirt = db.TileId("dirt");
            _changed = new NativeArray<byte>(world.Layout.ChunkCount, Allocator.Persistent);
            _perLine = new NativeArray<int>(4096, Allocator.Persistent);
            _perLine2 = new NativeArray<int>(4096, Allocator.Persistent);
            _single = new NativeArray<int>(1, Allocator.Persistent);
        }

        void OnDestroy() => Dispose();

        void Dispose()
        {
            if (_changed.IsCreated) _changed.Dispose();
            if (_perLine.IsCreated) _perLine.Dispose();
            if (_perLine2.IsCreated) _perLine2.Dispose();
            if (_single.IsCreated) _single.Dispose();
        }

        public SimRegion RegionAround(Vector2 center)
        {
            int cx = Mathf.FloorToInt(center.x), cy = Mathf.FloorToInt(center.y);
            return new SimRegion
            {
                x0 = math.max(0, cx - halfWidth), x1 = math.min(_world.Width, cx + halfWidth),
                y0 = math.max(0, cy - halfHeight), y1 = math.min(_world.Height, cy + halfHeight),
            };
        }

        void Update()
        {
            if (_world == null || Paused) return;
            float dt = Mathf.Min(Time.deltaTime * TimeScale, 0.1f);
            _liquidAcc += dt;
            _fallAcc += dt;
            _spreadAcc += dt;
            var region = RegionAround(_camera.transform.position);
            int guard = 0;
            while (_liquidAcc >= 1f / LiquidHz && guard++ < 3) { _liquidAcc -= 1f / LiquidHz; TickLiquids(region); }
            guard = 0;
            while (_fallAcc >= 1f / FallHz && guard++ < 3) { _fallAcc -= 1f / FallHz; TickFalling(region); }
            if (_spreadAcc >= 1f / SpreadHz) { _spreadAcc = 0f; TickSpread(region); }
        }

        bool AnyActive(SimRegion r, byte flag)
        {
            var l = _world.Layout;
            int c0x = r.x0 >> WorldLayout.ChunkShift, c1x = (r.x1 - 1) >> WorldLayout.ChunkShift;
            int c0y = r.y0 >> WorldLayout.ChunkShift, c1y = (r.y1 - 1) >> WorldLayout.ChunkShift;
            for (int cy = c0y; cy <= c1y; cy++)
            for (int cx = c0x; cx <= c1x; cx++)
                if ((_world.ChunkFlags[cy * l.ChunksX + cx] & flag) != 0) return true;
            return false;
        }

        /// <summary>One liquid tick over a region (public for tests and scenarios).</summary>
        public void TickLiquids(SimRegion r)
        {
            if (!AnyActive(r, WorldData.FlagLiquidActive)) { SkippedTicks++; return; }
            using var _ = TickMarker.Auto();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ClearChanged(r);
            for (int i = 0; i < r.Width; i++) _perLine[i] = 0;
            for (int i = 0; i < r.Height; i++) { _perLine2[i] = 0; }
            var reactionsRows = new NativeArray<int>(r.Height, Allocator.TempJob);
            var evap = new NativeArray<int>(r.Height, Allocator.TempJob);
            var fall = new LiquidFallJob
            {
                layout = _world.Layout, region = r, obsidian = _obsidian, props = _props, blocks = _world.Blocks,
                liquid = _world.Liquid, kind = _world.LiquidKind, changed = _changed, reactions = _perLine,
            }.Schedule(r.Width, 16);
            new LiquidSpreadJob
            {
                layout = _world.Layout, region = r, parity = _parity++, obsidian = _obsidian, props = _props, blocks = _world.Blocks,
                liquid = _world.Liquid, kind = _world.LiquidKind, changed = _changed, reactions = reactionsRows, evaporated = evap,
            }.Schedule(r.Height, 8, fall).Complete();
            int react = 0, ev = 0;
            for (int i = 0; i < r.Width; i++) react += _perLine[i];
            for (int i = 0; i < r.Height; i++) { react += reactionsRows[i]; ev += evap[i]; }
            reactionsRows.Dispose();
            evap.Dispose();
            Reactions += react;
            Evaporated += ev;
            ApplyChanged(r, WorldData.FlagLiquidActive, true);
            LiquidTicks++;
            LastTickMs = sw.Elapsed.TotalMilliseconds;
        }

        public void TickFalling(SimRegion r)
        {
            if (!AnyActive(r, WorldData.FlagSimActive)) return;
            ClearChanged(r);
            for (int i = 0; i < r.Width; i++) _perLine[i] = 0;
            new FallingBlocksJob
            {
                layout = _world.Layout, region = r, props = _props, blocks = _world.Blocks, shapes = _world.Shapes, frames = _world.Frames,
                liquid = _world.Liquid, kind = _world.LiquidKind, changed = _changed, moved = _perLine,
            }.Schedule(r.Width, 16).Complete();
            int moved = 0;
            for (int i = 0; i < r.Width; i++) moved += _perLine[i];
            BlocksFallen += moved;
            ApplyChanged(r, WorldData.FlagSimActive, false);
        }

        public void TickSpread(SimRegion r)
        {
            ClearChanged(r);
            _single[0] = 0;
            new SpreadJob
            {
                layout = _world.Layout, region = r, seed = _seed++, samples = spreadSamples, grass = _grass, dirt = _dirt, props = _props,
                blocks = _world.Blocks, changed = _changed, conversions = _single,
            }.Run();
            Spreads += _single[0];
            ApplyChanged(r, 0, false);
        }

        void ClearChanged(SimRegion r)
        {
            var l = _world.Layout;
            for (int cy = r.y0 >> 5; cy <= (r.y1 - 1) >> 5; cy++)
            for (int cx = r.x0 >> 5; cx <= (r.x1 - 1) >> 5; cx++)
                _changed[cy * l.ChunksX + cx] = 0;
        }

        /// <summary>Changed chunks get a mesh rebuild and stay active; unchanged ones lose <paramref name="activeFlag"/> (sleep).</summary>
        void ApplyChanged(SimRegion r, byte activeFlag, bool liquidChanged)
        {
            var l = _world.Layout;
            for (int cy = r.y0 >> 5; cy <= (r.y1 - 1) >> 5; cy++)
            for (int cx = r.x0 >> 5; cx <= (r.x1 - 1) >> 5; cx++)
            {
                int c = cy * l.ChunksX + cx;
                byte f = _world.ChunkFlags[c];
                if (_changed[c] != 0)
                {
                    // neighbours too: their autotiling / liquid edges read across the border
                    f |= (byte)(WorldData.FlagMeshDirty | WorldData.FlagLiquidActive | WorldData.FlagSimActive);
                    _world.ChunkFlags[c] = f;
                    Wake(cx - 1, cy); Wake(cx + 1, cy); Wake(cx, cy - 1); Wake(cx, cy + 1);
                }
                else if (activeFlag != 0) _world.ChunkFlags[c] = (byte)(f & ~activeFlag);
            }
            if (liquidChanged) LiquidChanged?.Invoke();
        }

        void Wake(int cx, int cy)
        {
            var l = _world.Layout;
            if (cx < 0 || cy < 0 || cx >= l.ChunksX || cy >= l.ChunksY) return;
            int c = cy * l.ChunksX + cx;
            _world.ChunkFlags[c] = (byte)(_world.ChunkFlags[c] | WorldData.FlagLiquidActive | WorldData.FlagSimActive);
        }

        public event System.Action LiquidChanged;

        /// <summary>Total liquid in a region (conservation checks).</summary>
        public long TotalLiquid(SimRegion r, LiquidType type)
        {
            long sum = 0;
            for (int y = r.y0; y < r.y1; y++)
            for (int x = r.x0; x < r.x1; x++)
            {
                int i = _world.Layout.Index(x, y);
                if (_world.LiquidKind[i] == (byte)type) sum += _world.Liquid[i];
            }
            return sum;
        }
    }
}
