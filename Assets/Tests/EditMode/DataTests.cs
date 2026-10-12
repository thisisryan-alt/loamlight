using System.Collections.Generic;
using System.IO;
using System.Linq;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.EditorTools;
using Loamlight.Game;
using Loamlight.Inventory;
using Loamlight.World;
using Loamlight.World.Generation;
using Loamlight.World.Rendering;
using Loamlight.World.Saving;
using NUnit.Framework;
using UnityEngine;

namespace Loamlight.Tests
{
    public class InventoryTests
    {
        static ItemContainer Make(int size = 12, int max = 10) => new ItemContainer(size, _ => max);

        [Test]
        public void StacksFillExistingFirstThenEmptySlots()
        {
            var inv = Make();
            Assert.That(inv.Add(1, 7), Is.Zero);
            Assert.That(inv.Add(1, 7), Is.Zero);
            Assert.That(inv[0].count, Is.EqualTo(10));
            Assert.That(inv[1].count, Is.EqualTo(4));
            Assert.That(inv.Count(1), Is.EqualTo(14));
        }

        [Test]
        public void ReturnsLeftoverWhenFull()
        {
            var inv = Make(2, 5);
            Assert.That(inv.Add(3, 12), Is.EqualTo(2));
            Assert.That(inv.CanAdd(3, 1), Is.False);
            Assert.That(inv.CanAdd(4, 1), Is.False);
        }

        [Test]
        public void RemoveIsAllOrNothing()
        {
            var inv = Make();
            inv.Add(2, 5);
            Assert.That(inv.Remove(2, 6), Is.False);
            Assert.That(inv.Count(2), Is.EqualTo(5));
            Assert.That(inv.Remove(2, 5), Is.True);
            Assert.That(inv[0].IsEmpty, Is.True);
        }

        [Test]
        public void MoveSlotMergesAndSwaps()
        {
            var inv = Make();
            inv.Set(0, new ItemStack(1, 8));
            inv.Set(1, new ItemStack(1, 6));
            inv.MoveSlot(1, 0);
            Assert.That(inv[0].count, Is.EqualTo(10));
            Assert.That(inv[1].count, Is.EqualTo(4));
            inv.Set(2, new ItemStack(2, 1));
            inv.MoveSlot(2, 1);
            Assert.That(inv[1].item, Is.EqualTo(2));
            Assert.That(inv[2].item, Is.EqualTo(1));
        }

        [Test]
        public void HotbarSelectionWraps()
        {
            var inv = Make();
            inv.Selected = 9;
            inv.Selected = inv.Selected + 1;
            Assert.That(inv.Selected, Is.EqualTo(0));
            inv.Selected = -1;
            Assert.That(inv.Selected, Is.EqualTo(9));
        }
    }

    public class SaveTests
    {
        public const string FixtureV1 = "Assets/Tests/Fixtures/world_v1.lmw";
        public const string FixtureV1Hash = "Assets/Tests/Fixtures/world_v1.hash.txt";

        [Test]
        public void RoundTripPreservesEveryArray()
        {
            var db = ContentDatabase.Instance;
            using var w = WorldGenerator.Generate(300, 200, 99, GameBootstrap.GenIdsFor(db), GenParams.Default, out _);
            w.SetLiquid(10, 150, 200, LiquidType.Water);
            w.SetShape(20, 100, TileShape.SlopeUpLeft);
            var path = Path.Combine(Application.temporaryCachePath, "roundtrip.lmw");
            WorldSave.Save(path, w, new SaveMeta { name = "Round Trip", gameJson = "{\"x\":1}" }, db);
            using var loaded = WorldSave.Load(path, db, out var meta);
            Assert.That(WorldHash.Compute(loaded), Is.EqualTo(WorldHash.Compute(w)));
            Assert.That(meta.name, Is.EqualTo("Round Trip"));
            Assert.That(meta.seed, Is.EqualTo(99u));
            Assert.That(meta.gameJson, Is.EqualTo("{\"x\":1}"));
            File.Delete(path);
        }

        [Test]
        public void RejectsGarbage()
        {
            var path = Path.Combine(Application.temporaryCachePath, "garbage.lmw");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 });
            Assert.Throws<InvalidDataException>(() => WorldSave.ReadMeta(path));
            File.Delete(path);
        }

        /// <summary>Saves written by version 1 must load in every later version (fixture committed in M1).</summary>
        [Test]
        public void LoadsCommittedVersion1Fixture()
        {
            Assert.That(File.Exists(FixtureV1), "fixture missing: run Loamlight.EditorTools.SaveFixtures.WriteV1 once");
            using var w = WorldSave.Load(FixtureV1, ContentDatabase.Instance, out var meta);
            Assert.That(meta.name, Is.EqualTo("Fixture v1"));
            Assert.That(w.Width, Is.EqualTo(128));
            var expected = File.ReadAllText(FixtureV1Hash).Trim();
            Assert.That(WorldHash.Compute(w).ToString("x16"), Is.EqualTo(expected));
            Assert.That(w.GetBlock(64, 2), Is.EqualTo(ContentDatabase.Instance.TileId("stone")), "known tile survives palette remap");
        }
    }

    public class MiniJsonTests
    {
        [Test]
        public void ParsesAndSerializesNestedData()
        {
            var json = "{\"a\": [1, 2.5, -3e2], \"b\": {\"c\": \"x\\\"y\\n\", \"d\": true, \"e\": null}}";
            var root = (Dictionary<string, object>)MiniJson.Parse(json);
            var a = (List<object>)root["a"];
            Assert.That(a.Select(v => (double)v), Is.EqualTo(new[] { 1.0, 2.5, -300.0 }));
            var b = (Dictionary<string, object>)root["b"];
            Assert.That(b["c"], Is.EqualTo("x\"y\n"));
            Assert.That(b["d"], Is.EqualTo(true));
            Assert.That(b["e"], Is.Null);
            var again = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Serialize(root));
            Assert.That(((Dictionary<string, object>)again["b"])["c"], Is.EqualTo("x\"y\n"));
        }
    }

    public class ContentTests
    {
        [Test]
        public void JsonValidates()
        {
            T Read<T>(string f) where T : class, new() =>
                File.Exists(Path.Combine(ContentImport.DataFolder, f)) ? JsonUtility.FromJson<T>(File.ReadAllText(Path.Combine(ContentImport.DataFolder, f))) : new T();
            var errors = ContentImport.Validate(
                Read<ContentImport.TilesFile>("tiles.json").tiles ?? new ContentImport.TileJson[0],
                Read<ContentImport.WallsFile>("walls.json").walls ?? new ContentImport.WallJson[0],
                Read<ContentImport.ItemsFile>("items.json").items ?? new ContentImport.ItemJson[0],
                Read<ContentImport.RecipesFile>("recipes.json").recipes ?? new ContentImport.RecipeJson[0],
                Read<ContentImport.LootFile>("loot.json").tables ?? new ContentImport.LootJson[0],
                Read<ContentImport.EnemiesFile>("enemies.json").enemies ?? new ContentImport.EnemyJson[0]);
            Assert.That(errors, Is.Empty, string.Join("\n", errors));
        }

        [Test]
        public void EveryTileAndWallHasAtlasArt()
        {
            var db = ContentDatabase.Instance;
            var atlas = WorldAtlas.Load();
            foreach (var t in db.tiles)
                Assert.That(atlas.tiles.Any(e => e.id == t.id && e.layout != AtlasLayout.None), $"tile {t.id} has no atlas frames");
            foreach (var w in db.walls)
                Assert.That(atlas.walls.Any(e => e.id == w.id), $"wall {w.id} has no atlas frames");
            Assert.That(atlas.material.shader.name, Is.EqualTo("Loamlight/Tile Lit"));
        }

        [Test]
        public void PlaceableItemsHaveIcons()
        {
            foreach (var item in ContentDatabase.Instance.items)
                if (!string.IsNullOrEmpty(item.placesTile) || !string.IsNullOrEmpty(item.placesWall))
                    Assert.That(item.icon, Is.Not.Null, $"item {item.id} has no icon");
        }

        [Test]
        public void NumericIdsFollowSortedNames()
        {
            var db = ContentDatabase.Instance;
            var sorted = db.tiles.Select(t => t.id).OrderBy(i => i, System.StringComparer.Ordinal).ToArray();
            Assert.That(db.tiles.Select(t => t.id).ToArray(), Is.EqualTo(sorted));
            Assert.That(db.TileId(sorted[0]), Is.EqualTo((ushort)1));
        }
    }

    /// <summary>"Each system in its own assembly, decoupled": enforce the allowed reference graph from DESIGN.md.</summary>
    public class AssemblyGraphTests
    {
        static readonly Dictionary<string, string[]> Allowed = new Dictionary<string, string[]>
        {
            ["Loamlight.Core"] = new string[0],
            ["Loamlight.Content"] = new[] { "Loamlight.Core" },
            ["Loamlight.World"] = new[] { "Loamlight.Core", "Loamlight.Content" },
            ["Loamlight.Lighting"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.World" },
            ["Loamlight.Liquids"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.World" },
            ["Loamlight.Inventory"] = new[] { "Loamlight.Core", "Loamlight.Content" },
            ["Loamlight.Crafting"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.Inventory" },
            ["Loamlight.Combat"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.World" },
            ["Loamlight.Player"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.World", "Loamlight.Inventory", "Loamlight.Combat" },
            ["Loamlight.AI"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.World", "Loamlight.Combat" },
            ["Loamlight.UI"] = new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.Inventory", "Loamlight.Crafting", "Loamlight.Player", "Loamlight.Combat" },
        };

        [Test]
        public void AssembliesOnlyReferenceAllowedSystems()
        {
            var problems = new List<string>();
            foreach (var path in Directory.GetFiles("Assets/Scripts", "*.asmdef", SearchOption.AllDirectories))
            {
                var root = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(path));
                var name = (string)root["name"];
                if (!Allowed.TryGetValue(name, out var allowed)) continue;   // Game / EditorTools may reference everything
                foreach (var r in ((List<object>)root["references"]).Cast<string>().Where(r => r.StartsWith("Loamlight.")))
                    if (!allowed.Contains(r)) problems.Add($"{name} -> {r}");
            }
            Assert.That(problems, Is.Empty, string.Join(", ", problems));
        }

        [Test]
        public void EverySystemAssemblyExistsOnceItsMilestoneArrived()
        {
            var names = Directory.GetFiles("Assets/Scripts", "*.asmdef", SearchOption.AllDirectories)
                .Select(p => (string)((Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(p)))["name"]).ToList();
            foreach (var required in new[] { "Loamlight.Core", "Loamlight.Content", "Loamlight.World", "Loamlight.Lighting", "Loamlight.Inventory", "Loamlight.Player", "Loamlight.UI" })
                Assert.That(names, Does.Contain(required));
        }
    }
}
