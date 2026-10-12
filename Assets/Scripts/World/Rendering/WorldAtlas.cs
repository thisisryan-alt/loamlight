using System;
using Loamlight.Content;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.World.Rendering
{
    public enum AtlasLayout : byte { None = 0, Terrain = 1, Single = 2, Object = 3, Variants = 4 }

    [Serializable]
    public struct AtlasEntry
    {
        public string id;
        public AtlasLayout layout;
        public int start, count, w, h, variants;
    }

    /// <summary>Generated tile atlas description (from Tools/art/gen_atlas.py via the editor importer).</summary>
    public sealed class WorldAtlas : ScriptableObject
    {
        public const string ResourcePath = "World/WorldAtlas";

        public Material material;
        public Texture2D albedo, normal, emission;
        public int width, height;
        public AtlasEntry[] tiles = Array.Empty<AtlasEntry>();
        public AtlasEntry[] walls = Array.Empty<AtlasEntry>();
        /// <summary>Per frame: (uMin, vMin, uMax, vMax).</summary>
        public Vector4[] uvRects = Array.Empty<Vector4>();

        public static WorldAtlas Load()
        {
            var a = Resources.Load<WorldAtlas>(ResourcePath);
            if (a == null) throw new InvalidOperationException($"WorldAtlas missing at Resources/{ResourcePath}; run setup");
            return a;
        }
    }

    /// <summary>Blittable per-id render info for the mesher.</summary>
    public struct TileRenderInfo
    {
        public byte layout;
        public byte variants;
        public byte w, h;
        public int start, count;
    }

    /// <summary>NativeArray lookups the chunk mesher reads, indexed by numeric tile / wall id.</summary>
    public sealed class WorldRenderData : IDisposable
    {
        public NativeArray<TileRenderInfo> Tiles;
        public NativeArray<TileRenderInfo> Walls;
        public NativeArray<float4> Uvs;
        public NativeArray<TileProps> TileProps;
        public readonly Material Material;

        public WorldRenderData(ContentDatabase db, WorldAtlas atlas)
        {
            Material = atlas.material;
            Tiles = new NativeArray<TileRenderInfo>(db.tiles.Length + 1, Allocator.Persistent);
            Walls = new NativeArray<TileRenderInfo>(db.walls.Length + 1, Allocator.Persistent);
            foreach (var e in atlas.tiles)
                if (db.TryTileId(e.id, out var id)) Tiles[id] = Info(e);
            foreach (var e in atlas.walls)
            {
                for (int i = 0; i < db.walls.Length; i++)
                    if (db.walls[i].id == e.id) Walls[i + 1] = Info(e);
            }
            Uvs = new NativeArray<float4>(atlas.uvRects.Length, Allocator.Persistent);
            for (int i = 0; i < atlas.uvRects.Length; i++) Uvs[i] = atlas.uvRects[i];
            TileProps = new NativeArray<TileProps>(db.BuildTileProps(), Allocator.Persistent);
        }

        static TileRenderInfo Info(AtlasEntry e) => new TileRenderInfo
        {
            layout = (byte)e.layout, variants = (byte)math.max(1, e.variants), w = (byte)math.max(1, e.w), h = (byte)math.max(1, e.h),
            start = e.start, count = e.count,
        };

        public void Dispose()
        {
            if (Tiles.IsCreated) Tiles.Dispose();
            if (Walls.IsCreated) Walls.Dispose();
            if (Uvs.IsCreated) Uvs.Dispose();
            if (TileProps.IsCreated) TileProps.Dispose();
        }
    }
}
