using System;
using System.Collections.Generic;
using Loamlight.Content;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.World.Generation
{
    /// <summary>A chest a structure placed, and the loot table to fill it from (filled by the game, saved with chests).</summary>
    public struct PlacedChest
    {
        public int2 origin;
        public string loot;
    }

    /// <summary>
    /// Structure templates resolved to numeric ids (from <see cref="StructureDef"/>s), and the seeded, single-threaded
    /// pass that stamps them into the world: random attempts inside each template's depth band and biomes, rejected if
    /// the spot is not mostly buried in rock (so they read as found, not floating) or overlaps another structure.
    /// Multi-tile objects (chests, tables) are written whole from their origin cell, like any placed object.
    /// </summary>
    public sealed class StructurePlan
    {
        const byte Keep = 0, Set = 1, Clear = 2;

        public sealed class Template
        {
            public string id, loot;
            public Biome[] biomes;
            public float depthMin, depthMax, ruin;
            public int count, w, h;
            public ushort[] tile, wall;
            public byte[] mode;
            public bool[] ruinable;
        }

        public readonly List<Template> Templates = new List<Template>();
        readonly Dictionary<ushort, int2> _objectSize = new Dictionary<ushort, int2>();
        readonly HashSet<ushort> _chests = new HashSet<ushort>();

        public static StructurePlan From(ContentDatabase db)
        {
            var plan = new StructurePlan();
            for (int i = 0; i < db.tiles.Length; i++)
            {
                var t = db.tiles[i];
                ushort id = (ushort)(i + 1);
                if (t.size.x > 1 || t.size.y > 1) plan._objectSize[id] = new int2(t.size.x, t.size.y);
                if (t.interact == "chest") plan._chests.Add(id);
            }
            foreach (var s in db.structures)
            {
                if (s.rows.Length == 0) continue;
                int h = s.rows.Length, w = s.rows[0].Length;
                var t = new Template
                {
                    id = s.id, loot = s.loot, depthMin = s.depthMin, depthMax = s.depthMax, ruin = s.ruin, count = s.count, w = w, h = h,
                    tile = new ushort[w * h], wall = new ushort[w * h], mode = new byte[w * h], ruinable = new bool[w * h],
                };
                var biomes = new List<Biome>();
                foreach (var b in s.biomes)
                    if (Enum.TryParse<Biome>(b, true, out var biome)) biomes.Add(biome);
                t.biomes = biomes.ToArray();
                var legend = new Dictionary<char, StructureCell>();
                foreach (var c in s.legend) if (!string.IsNullOrEmpty(c.key)) legend[c.key[0]] = c;
                for (int row = 0; row < h; row++)
                for (int x = 0; x < w; x++)
                {
                    char ch = x < s.rows[row].Length ? s.rows[row][x] : ' ';
                    int i = (h - 1 - row) * w + x;     // rows are top to bottom, the world is y-up
                    if (ch == ' ' || !legend.TryGetValue(ch, out var cell)) continue;
                    if (!string.IsNullOrEmpty(cell.tile) && db.TryTileId(cell.tile, out var tid)) { t.tile[i] = tid; t.mode[i] = Set; }
                    else if (cell.clear) t.mode[i] = Clear;
                    if (!string.IsNullOrEmpty(cell.wall))
                        for (int k = 0; k < db.walls.Length; k++)
                            if (db.walls[k].id == cell.wall) t.wall[i] = (ushort)(k + 1);
                    if (t.mode[i] == Keep && t.wall[i] != 0) t.mode[i] = Clear;
                    t.ruinable[i] = cell.ruinable;
                }
                plan.Templates.Add(t);
            }
            return plan;
        }

        /// <summary>Stamp every template into the world. Returns how many structures were placed.</summary>
        public int Place(WorldData w, BiomeField field, NativeArray<byte> columnBiome, uint seed, List<PlacedChest> chests)
        {
            var rng = new Unity.Mathematics.Random(seed * 0x2545f491u + 99u);
            var placed = new List<RectInt>();
            int total = 0;
            foreach (var t in Templates)
            {
                int done = 0;
                for (int attempt = 0; attempt < t.count * 40 && done < t.count; attempt++)
                {
                    int x0 = rng.NextInt(6, math.max(7, w.Width - t.w - 6));
                    float depth = rng.NextFloat(t.depthMin, t.depthMax);
                    int y0 = (int)((1f - depth) * w.Height) - t.h / 2;
                    if (y0 < 4 || y0 + t.h >= w.Height - 4) continue;
                    var rect = new RectInt(x0, y0, t.w, t.h);
                    if (t.biomes.Length > 0 && Array.IndexOf(t.biomes, field.At(x0 + t.w / 2, y0 + t.h / 2, columnBiome)) < 0) continue;
                    bool overlaps = false;
                    foreach (var r in placed)
                        if (r.xMin - 6 < rect.xMax && rect.xMin < r.xMax + 6 && r.yMin - 6 < rect.yMax && rect.yMin < r.yMax + 6) { overlaps = true; break; }
                    if (overlaps || !Buried(w, rect)) continue;
                    Stamp(w, t, x0, y0, rng.NextUInt(), chests);
                    placed.Add(rect);
                    done++;
                }
                total += done;
            }
            return total;
        }

        /// <summary>At least 55% of the footprint is solid ground and none of it is liquid.</summary>
        static bool Buried(WorldData w, RectInt r)
        {
            int solid = 0;
            for (int y = r.yMin; y < r.yMax; y++)
            for (int x = r.xMin; x < r.xMax; x++)
            {
                int i = w.Index(x, y);
                if (w.Liquid[i] != 0) return false;
                if (w.Blocks[i] != 0) solid++;
            }
            return solid >= r.width * r.height * 55 / 100;
        }

        void Stamp(WorldData w, Template t, int x0, int y0, uint salt, List<PlacedChest> chests)
        {
            var layout = w.Layout;
            // pass 1: terrain, air and walls; pass 2: multi-tile objects over the cleared cells
            for (int pass = 0; pass < 2; pass++)
            for (int ty = 0; ty < t.h; ty++)
            for (int tx = 0; tx < t.w; tx++)
            {
                int c = ty * t.w + tx;
                if (t.mode[c] == Keep) continue;
                int x = x0 + tx, y = y0 + ty, i = layout.Index(x, y);
                ushort id = t.tile[c];
                bool isObject = id != 0 && _objectSize.ContainsKey(id);
                if (pass == 0)
                {
                    w.Liquid[i] = 0;
                    w.LiquidKind[i] = 0;
                    if (t.wall[c] != 0) w.Walls[i] = t.wall[c];
                    w.Shapes[i] = 0;
                    w.Frames[i] = 0;
                    bool ruined = t.ruinable[c] && t.ruin > 0f && (Noise2.Hash(x, y, salt) & 0xffff) / 65535f < t.ruin;
                    w.Blocks[i] = t.mode[c] == Set && !isObject && !ruined ? id : (ushort)0;
                    continue;
                }
                if (!isObject) continue;
                var size = _objectSize[id];
                for (int oy = 0; oy < size.y; oy++)
                for (int ox = 0; ox < size.x; ox++)
                {
                    int j = layout.Index(x + ox, y + oy);
                    w.Blocks[j] = id;
                    w.Frames[j] = (byte)(ox + oy * size.x);
                }
                if (_chests.Contains(id)) chests.Add(new PlacedChest { origin = new int2(x, y), loot = t.loot });
            }
        }
    }
}
