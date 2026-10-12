using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loamlight.Content;
using UnityEditor;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Converts the content JSON under Assets/Content/Data into ScriptableObjects (one asset per definition) plus the
    /// ContentDatabase in Resources. Assets are updated in place so their GUIDs, and every reference to them, survive.
    /// Validation runs first: a dangling reference anywhere fails the import.
    /// </summary>
    public static class ContentImport
    {
        public const string DataFolder = "Assets/Content/Data";
        public const string OutFolder = "Assets/Generated/Content";
        public const string DatabasePath = "Assets/Resources/Content/ContentDatabase.asset";

#pragma warning disable 0649
        [Serializable] public class TilesFile { public int version; public TileJson[] tiles; }
        [Serializable] public class TileJson
        {
            public string id, name, kind = "Terrain", drop, mapColor, spreadsOnto, placement = "Any", interact, sound = "dig", tool = "Pickaxe";
            public bool solid, falls, isStation;
            public float hardness = 0.5f, lightRadius;
            public int tier, dropCount = 1;
            public float[] light;
            public int[] size;
        }
        [Serializable] public class WallsFile { public int version; public WallJson[] walls; }
        [Serializable] public class WallJson { public string id, name, drop, mapColor; public float hardness = 0.3f; }
        [Serializable] public class ItemsFile { public int version; public ItemJson[] items; }
        [Serializable] public class ItemJson
        {
            public string id, name, description, category = "Material", placesTile, placesWall, toolType = "None", weaponKind = "None",
                projectile, ammo, summons, bucket;
            public int maxStack = 999, toolPower, damage, manaCost, heal, restoreMana;
            public float toolSpeed = 1f, useTime = 0.25f, reach = 5.5f, knockback, hitstop = 1f, shake = 1f, projectileSpeed;
        }
        [Serializable] public class RecipesFile { public int version; public RecipeJson[] recipes; }
        [Serializable] public class RecipeJson { public string id, result, station; public int resultCount = 1; public ItemAmount[] ingredients; }
        [Serializable] public class LootFile { public int version; public LootJson[] tables; }
        [Serializable] public class LootJson { public string id; public LootEntry[] entries; }
        [Serializable] public class EnemiesFile { public int version; public EnemyJson[] enemies; }
        [Serializable] public class EnemyJson
        {
            public string id, name, ai = "Hopper", sheet, loot, projectile;
            public int health = 20, damage = 10, defense;
            public float knockbackResist, speed = 4f, depthMin, depthMax = 1f, spawnWeight = 1f;
            public float[] size;
            public string[] biomes;
            public bool nightOnly, boss;
        }
#pragma warning restore 0649

        static T Read<T>(string file) where T : class, new()
        {
            var path = Path.Combine(DataFolder, file);
            return File.Exists(path) ? JsonUtility.FromJson<T>(File.ReadAllText(path)) : new T();
        }

        [MenuItem("Loamlight/Setup/Import Content")]
        public static void ImportAll()
        {
            var tilesJ = (Read<TilesFile>("tiles.json").tiles ?? Array.Empty<TileJson>()).OrderBy(t => t.id, StringComparer.Ordinal).ToArray();
            var wallsJ = (Read<WallsFile>("walls.json").walls ?? Array.Empty<WallJson>()).OrderBy(t => t.id, StringComparer.Ordinal).ToArray();
            var itemsJ = (Read<ItemsFile>("items.json").items ?? Array.Empty<ItemJson>()).OrderBy(t => t.id, StringComparer.Ordinal).ToArray();
            var recipesJ = (Read<RecipesFile>("recipes.json").recipes ?? Array.Empty<RecipeJson>()).OrderBy(t => t.id, StringComparer.Ordinal).ToArray();
            var lootJ = (Read<LootFile>("loot.json").tables ?? Array.Empty<LootJson>()).OrderBy(t => t.id, StringComparer.Ordinal).ToArray();
            var enemiesJ = (Read<EnemiesFile>("enemies.json").enemies ?? Array.Empty<EnemyJson>()).OrderBy(t => t.id, StringComparer.Ordinal).ToArray();

            var errors = Validate(tilesJ, wallsJ, itemsJ, recipesJ, lootJ, enemiesJ);
            if (errors.Count > 0)
                throw new InvalidDataException("content validation failed:\n  " + string.Join("\n  ", errors));

            var tiles = tilesJ.Select(j => Upsert<TileDef>("Tiles", j.id, d =>
            {
                d.id = j.id; d.displayName = j.name ?? j.id; d.kind = Parse<TileKind>(j.kind);
                d.solid = j.solid; d.hardness = j.hardness; d.tier = j.tier; d.tool = Parse<ToolType>(j.tool);
                d.drop = j.drop ?? ""; d.dropCount = j.dropCount; d.mapColor = Hex(j.mapColor, Color.gray);
                d.light = j.light != null && j.light.Length >= 3 ? new Color(j.light[0], j.light[1], j.light[2], 1f) : Color.black;
                d.lightRadius = j.lightRadius; d.falls = j.falls; d.spreadsOnto = j.spreadsOnto ?? "";
                d.size = j.size != null && j.size.Length >= 2 ? new Vector2Int(j.size[0], j.size[1]) : Vector2Int.one;
                d.placement = Parse<Placement>(j.placement); d.interact = j.interact ?? ""; d.sound = j.sound ?? "dig";
                d.isStation = j.isStation;
            })).ToArray();
            var walls = wallsJ.Select(j => Upsert<WallDef>("Walls", j.id, d =>
            {
                d.id = j.id; d.displayName = j.name ?? j.id; d.drop = j.drop ?? ""; d.hardness = j.hardness; d.mapColor = Hex(j.mapColor, Color.gray);
            })).ToArray();
            var icons = LoadIcons();
            var items = itemsJ.Select(j => Upsert<ItemDef>("Items", j.id, d =>
            {
                d.icon = icons.TryGetValue("icon_" + j.id, out var icon) ? icon : null;
                d.id = j.id; d.displayName = j.name ?? j.id; d.description = j.description ?? ""; d.category = Parse<ItemCategory>(j.category);
                d.maxStack = Mathf.Max(1, j.maxStack); d.placesTile = j.placesTile ?? ""; d.placesWall = j.placesWall ?? "";
                d.toolType = Parse<ToolType>(j.toolType); d.toolPower = j.toolPower; d.toolSpeed = j.toolSpeed; d.useTime = j.useTime;
                d.reach = j.reach; d.weaponKind = Parse<WeaponKind>(j.weaponKind); d.damage = j.damage; d.knockback = j.knockback;
                d.hitstop = j.hitstop; d.shake = j.shake; d.manaCost = j.manaCost; d.projectile = j.projectile ?? ""; d.ammo = j.ammo ?? "";
                d.projectileSpeed = j.projectileSpeed; d.heal = j.heal; d.restoreMana = j.restoreMana; d.summons = j.summons ?? "";
                d.bucket = j.bucket ?? "";
            })).ToArray();
            var recipes = recipesJ.Select(j => Upsert<RecipeDef>("Recipes", j.id, d =>
            {
                d.id = j.id; d.result = j.result; d.resultCount = Mathf.Max(1, j.resultCount);
                d.ingredients = j.ingredients ?? Array.Empty<ItemAmount>(); d.station = j.station ?? "";
            })).ToArray();
            var loot = lootJ.Select(j => Upsert<LootTableDef>("Loot", j.id, d =>
            {
                d.id = j.id; d.entries = j.entries ?? Array.Empty<LootEntry>();
            })).ToArray();
            var enemies = enemiesJ.Select(j => Upsert<EnemyDef>("Enemies", j.id, d =>
            {
                d.id = j.id; d.displayName = j.name ?? j.id; d.ai = Parse<AiKind>(j.ai); d.health = j.health; d.damage = j.damage;
                d.defense = j.defense; d.knockbackResist = j.knockbackResist; d.speed = j.speed;
                d.size = j.size != null && j.size.Length >= 2 ? new Vector2(j.size[0], j.size[1]) : new Vector2(1.5f, 1.5f);
                d.sheet = j.sheet ?? ""; d.loot = j.loot ?? ""; d.projectile = j.projectile ?? ""; d.biomes = j.biomes ?? Array.Empty<string>();
                d.depthMin = j.depthMin; d.depthMax = j.depthMax; d.nightOnly = j.nightOnly; d.spawnWeight = j.spawnWeight; d.boss = j.boss;
            })).ToArray();

            RemoveStale("Tiles", tiles.Select(t => t.id));
            RemoveStale("Walls", walls.Select(t => t.id));
            RemoveStale("Items", items.Select(t => t.id));
            RemoveStale("Recipes", recipes.Select(t => t.id));
            RemoveStale("Loot", loot.Select(t => t.id));
            RemoveStale("Enemies", enemies.Select(t => t.id));

            AssetFolders.Ensure(Path.GetDirectoryName(DatabasePath).Replace('\\', '/'));
            var db = AssetDatabase.LoadAssetAtPath<ContentDatabase>(DatabasePath);
            bool created = db == null;
            if (created) db = ScriptableObject.CreateInstance<ContentDatabase>();
            var before = created ? null : EditorJsonUtility.ToJson(db);
            db.tiles = tiles; db.walls = walls; db.items = items; db.recipes = recipes; db.lootTables = loot; db.enemies = enemies;
            if (created) AssetDatabase.CreateAsset(db, DatabasePath);
            else if (EditorJsonUtility.ToJson(db) != before) EditorUtility.SetDirty(db);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Content] {tiles.Length} tiles, {walls.Length} walls, {items.Length} items, {recipes.Length} recipes, " +
                      $"{loot.Length} loot tables, {enemies.Length} enemies");
        }

        public static List<string> Validate(TileJson[] tiles, WallJson[] walls, ItemJson[] items, RecipeJson[] recipes, LootJson[] loot, EnemyJson[] enemies)
        {
            var errors = new List<string>();
            var tileIds = new HashSet<string>(tiles.Select(t => t.id));
            var wallIds = new HashSet<string>(walls.Select(t => t.id));
            var itemIds = new HashSet<string>(items.Select(t => t.id));
            var lootIds = new HashSet<string>(loot.Select(t => t.id));
            var enemyIds = new HashSet<string>(enemies.Select(t => t.id));

            void Dupes(IEnumerable<string> ids, string what)
            {
                foreach (var g in ids.GroupBy(i => i).Where(g => g.Count() > 1)) errors.Add($"duplicate {what} id '{g.Key}'");
            }
            Dupes(tiles.Select(t => t.id), "tile"); Dupes(walls.Select(t => t.id), "wall"); Dupes(items.Select(t => t.id), "item");
            Dupes(recipes.Select(t => t.id), "recipe"); Dupes(loot.Select(t => t.id), "loot"); Dupes(enemies.Select(t => t.id), "enemy");

            void Ref(string value, HashSet<string> set, string where)
            {
                if (!string.IsNullOrEmpty(value) && !set.Contains(value)) errors.Add($"{where}: unknown reference '{value}'");
            }
            void Enum<T>(string value, string where) where T : struct
            {
                if (!string.IsNullOrEmpty(value) && !System.Enum.TryParse<T>(value, out _)) errors.Add($"{where}: bad {typeof(T).Name} '{value}'");
            }

            foreach (var t in tiles)
            {
                if (string.IsNullOrEmpty(t.id)) { errors.Add("tile with empty id"); continue; }
                Ref(t.drop, itemIds, $"tile {t.id}.drop");
                Ref(t.spreadsOnto, tileIds, $"tile {t.id}.spreadsOnto");
                Enum<TileKind>(t.kind, $"tile {t.id}.kind"); Enum<Placement>(t.placement, $"tile {t.id}.placement"); Enum<ToolType>(t.tool, $"tile {t.id}.tool");
            }
            foreach (var w in walls) Ref(w.drop, itemIds, $"wall {w.id}.drop");
            foreach (var i in items)
            {
                Ref(i.placesTile, tileIds, $"item {i.id}.placesTile"); Ref(i.placesWall, wallIds, $"item {i.id}.placesWall");
                Ref(i.ammo, itemIds, $"item {i.id}.ammo"); Ref(i.summons, enemyIds, $"item {i.id}.summons");
                Enum<ItemCategory>(i.category, $"item {i.id}.category"); Enum<ToolType>(i.toolType, $"item {i.id}.toolType");
                Enum<WeaponKind>(i.weaponKind, $"item {i.id}.weaponKind");
            }
            foreach (var r in recipes)
            {
                Ref(r.result, itemIds, $"recipe {r.id}.result"); Ref(r.station, tileIds, $"recipe {r.id}.station");
                if (r.ingredients == null || r.ingredients.Length == 0) errors.Add($"recipe {r.id} has no ingredients");
                else foreach (var ing in r.ingredients)
                {
                    Ref(ing.item, itemIds, $"recipe {r.id}.ingredient");
                    if (ing.count <= 0) errors.Add($"recipe {r.id}: ingredient {ing.item} count {ing.count}");
                }
            }
            foreach (var l in loot)
                if (l.entries != null) foreach (var e in l.entries) Ref(e.item, itemIds, $"loot {l.id}");
            foreach (var e in enemies) { Ref(e.loot, lootIds, $"enemy {e.id}.loot"); Enum<AiKind>(e.ai, $"enemy {e.id}.ai"); }
            return errors;
        }

        /// <summary>Icon sprites from every generated icons_*.png sheet, by sprite name ("icon_&lt;item id&gt;").</summary>
        static Dictionary<string, Sprite> LoadIcons()
        {
            var map = new Dictionary<string, Sprite>(StringComparer.Ordinal);
            if (!Directory.Exists(ArtImport.SpriteRoot)) return map;
            foreach (var png in Directory.GetFiles(ArtImport.SpriteRoot, "icons_*.png").OrderBy(p => p, StringComparer.Ordinal))
                foreach (var sp in AssetDatabase.LoadAllAssetsAtPath(png.Replace('\\', '/')).OfType<Sprite>())
                    map[sp.name] = sp;
            return map;
        }

        static T Upsert<T>(string folder, string id, Action<T> fill) where T : ScriptableObject
        {
            var dir = $"{OutFolder}/{folder}";
            AssetFolders.Ensure(dir);
            var path = $"{dir}/{id}.asset";
            var asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<T>();
                fill(asset);
                asset.name = id;
                AssetDatabase.CreateAsset(asset, path);
                return asset;
            }
            var before = EditorJsonUtility.ToJson(asset);
            fill(asset);
            if (EditorJsonUtility.ToJson(asset) != before) EditorUtility.SetDirty(asset);
            return asset;
        }

        static void RemoveStale(string folder, IEnumerable<string> keep)
        {
            var dir = $"{OutFolder}/{folder}";
            if (!AssetDatabase.IsValidFolder(dir)) return;
            var set = new HashSet<string>(keep);
            foreach (var guid in AssetDatabase.FindAssets("", new[] { dir }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!set.Contains(Path.GetFileNameWithoutExtension(path))) AssetDatabase.DeleteAsset(path);
            }
        }

        static T Parse<T>(string value) where T : struct => string.IsNullOrEmpty(value) ? default : (T)System.Enum.Parse(typeof(T), value);

        public static Color Hex(string hex, Color fallback) =>
            !string.IsNullOrEmpty(hex) && ColorUtility.TryParseHtmlString(hex, out var c) ? c : fallback;
    }
}
