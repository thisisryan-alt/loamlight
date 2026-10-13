using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.Content
{
    /// <summary>
    /// All game content, generated from Assets/Content/Data/*.json by the editor importer.
    /// Runtime numeric tile ids are 1-based indexes into <see cref="tiles"/> (0 = air), assigned in sorted-id
    /// order; saves store names and remap, so adding content never invalidates a save.
    /// </summary>
    public sealed class ContentDatabase : ScriptableObject
    {
        public const string ResourcePath = "Content/ContentDatabase";

        public TileDef[] tiles = Array.Empty<TileDef>();
        public WallDef[] walls = Array.Empty<WallDef>();
        public ItemDef[] items = Array.Empty<ItemDef>();
        public RecipeDef[] recipes = Array.Empty<RecipeDef>();
        public EnemyDef[] enemies = Array.Empty<EnemyDef>();
        public LootTableDef[] lootTables = Array.Empty<LootTableDef>();
        public StructureDef[] structures = Array.Empty<StructureDef>();

        Dictionary<string, ushort> _tileIds;
        Dictionary<string, ushort> _wallIds;
        Dictionary<string, int> _itemIds;
        Dictionary<string, EnemyDef> _enemies;
        Dictionary<string, LootTableDef> _loot;

        static ContentDatabase _instance;

        /// <summary>Loaded from Resources on first use.</summary>
        public static ContentDatabase Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = Resources.Load<ContentDatabase>(ResourcePath);
                    if (_instance == null) throw new InvalidOperationException($"ContentDatabase missing at Resources/{ResourcePath}; run setup");
                }
                return _instance;
            }
        }

        void OnEnable() => _tileIds = null;

        void BuildLookups()
        {
            _tileIds = new Dictionary<string, ushort>(StringComparer.Ordinal);
            for (int i = 0; i < tiles.Length; i++) _tileIds[tiles[i].id] = (ushort)(i + 1);
            _wallIds = new Dictionary<string, ushort>(StringComparer.Ordinal);
            for (int i = 0; i < walls.Length; i++) _wallIds[walls[i].id] = (ushort)(i + 1);
            _itemIds = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < items.Length; i++) _itemIds[items[i].id] = i + 1;
            _enemies = new Dictionary<string, EnemyDef>(StringComparer.Ordinal);
            foreach (var e in enemies) _enemies[e.id] = e;
            _loot = new Dictionary<string, LootTableDef>(StringComparer.Ordinal);
            foreach (var l in lootTables) _loot[l.id] = l;
        }

        // ---------------------------------------------------------------------------------------------- tiles
        public ushort TileId(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            if (_tileIds == null) BuildLookups();
            return _tileIds.TryGetValue(id, out var v) ? v : throw new KeyNotFoundException("unknown tile '" + id + "'");
        }

        public bool TryTileId(string id, out ushort value)
        {
            if (_tileIds == null) BuildLookups();
            value = 0;
            return !string.IsNullOrEmpty(id) && _tileIds.TryGetValue(id, out value);
        }

        public TileDef Tile(ushort numericId) => numericId == 0 || numericId > tiles.Length ? null : tiles[numericId - 1];

        public ushort WallId(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            if (_tileIds == null) BuildLookups();
            return _wallIds.TryGetValue(id, out var v) ? v : throw new KeyNotFoundException("unknown wall '" + id + "'");
        }

        public WallDef Wall(ushort numericId) => numericId == 0 || numericId > walls.Length ? null : walls[numericId - 1];

        // ---------------------------------------------------------------------------------------------- items
        public int ItemId(string id)
        {
            if (string.IsNullOrEmpty(id)) return 0;
            if (_tileIds == null) BuildLookups();
            return _itemIds.TryGetValue(id, out var v) ? v : throw new KeyNotFoundException("unknown item '" + id + "'");
        }

        public bool TryItemId(string id, out int value)
        {
            if (_tileIds == null) BuildLookups();
            value = 0;
            return !string.IsNullOrEmpty(id) && _itemIds.TryGetValue(id, out value);
        }

        public ItemDef Item(int numericId) => numericId <= 0 || numericId > items.Length ? null : items[numericId - 1];
        public ItemDef Item(string id) => Item(ItemId(id));

        public EnemyDef Enemy(string id)
        {
            if (_tileIds == null) BuildLookups();
            return _enemies.TryGetValue(id, out var e) ? e : null;
        }

        public LootTableDef Loot(string id)
        {
            if (_tileIds == null) BuildLookups();
            return id != null && _loot.TryGetValue(id, out var l) ? l : null;
        }

        /// <summary>Blittable per-tile properties for Burst jobs, indexed by numeric tile id (index 0 = air).</summary>
        public TileProps[] BuildTileProps()
        {
            var props = new TileProps[tiles.Length + 1];
            for (int i = 0; i < tiles.Length; i++)
            {
                var t = tiles[i];
                props[i + 1] = new TileProps
                {
                    kind = (byte)t.kind,
                    solid = (byte)(t.solid ? 1 : 0),
                    falls = (byte)(t.falls ? 1 : 0),
                    tier = (byte)Mathf.Clamp(t.tier, 0, 255),
                    light = new float3(t.light.r, t.light.g, t.light.b),
                    lightRadius = t.lightRadius,
                    width = (byte)Mathf.Max(1, t.size.x),
                    height = (byte)Mathf.Max(1, t.size.y),
                    spreadsOnto = TryTileId(t.spreadsOnto, out var s) ? s : (ushort)0,
                };
            }
            return props;
        }
    }

    public struct TileProps
    {
        public byte kind;
        public byte solid;
        public byte falls;
        public byte tier;
        public byte width;
        public byte height;
        public ushort spreadsOnto;
        public float3 light;
        public float lightRadius;

        public bool IsSolid => solid != 0;
        public bool IsTerrain => kind == (byte)TileKind.Terrain;
    }
}
