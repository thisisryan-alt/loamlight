using System.Collections.Generic;
using System.Linq;
using Loamlight.Content;
using Loamlight.Crafting;
using Loamlight.Inventory;
using Loamlight.UI;
using Loamlight.World;
using Loamlight.World.Collision;
using Loamlight.World.Rendering;
using NUnit.Framework;
using UnityEngine;

namespace Loamlight.Tests
{
    public class CraftingTests
    {
        static ContentDatabase Db => ContentDatabase.Instance;

        static ItemContainer Inv(params (string id, int n)[] items)
        {
            var inv = ItemContainer.ForContent(50, Db);
            foreach (var (id, n) in items) inv.Add(Db.ItemId(id), n);
            return inv;
        }

        [Test]
        public void HandRecipesNeedNoStation()
        {
            var book = new CraftingBook(Db);
            var hand = book.Available(new HashSet<string>());
            Assert.That(hand.Select(r => r.id), Does.Contain("workbench"));
            Assert.That(hand.Select(r => r.id), Does.Not.Contain("table"), "tables need a workbench");
            var atBench = book.Available(new HashSet<string> { "workbench" });
            Assert.That(atBench.Select(r => r.id), Does.Contain("table"));
        }

        [Test]
        public void CraftConsumesIngredientsAndAddsResult()
        {
            var book = new CraftingBook(Db);
            var inv = Inv(("wood", 12));
            Assert.That(book.Craft(book.Find("workbench"), inv, new HashSet<string>()), Is.True);
            Assert.That(inv.Count(Db.ItemId("wood")), Is.EqualTo(2));
            Assert.That(inv.Count(Db.ItemId("workbench")), Is.EqualTo(1));
        }

        [Test]
        public void CraftIsAllOrNothing()
        {
            var book = new CraftingBook(Db);
            var inv = Inv(("wood", 8));
            Assert.That(book.Craft(book.Find("chest"), inv, new HashSet<string> { "workbench" }), Is.False, "missing copper bar");
            Assert.That(inv.Count(Db.ItemId("wood")), Is.EqualTo(8));
            Assert.That(book.Craft(book.Find("table"), inv, new HashSet<string>()), Is.False, "no workbench nearby");
            Assert.That(inv.Count(Db.ItemId("wood")), Is.EqualTo(8));
        }

        [Test]
        public void MaxCraftsAndMultiOutput()
        {
            var book = new CraftingBook(Db);
            var inv = Inv(("copper_ore", 10));
            Assert.That(book.MaxCrafts(book.Find("copper_bar"), inv), Is.EqualTo(3));
            var torches = Inv(("wood", 2));
            Assert.That(book.Craft(book.Find("torch"), torches, new HashSet<string>()), Is.True);
            Assert.That(torches.Count(Db.ItemId("torch")), Is.EqualTo(3));
        }

        [Test]
        public void EveryRecipeIsReachableFromTheStartingKit()
        {
            // progression check: starting with world resources (wood, stone, ores, sand, clay) and nothing else,
            // walk recipes and stations until nothing new can be made; every recipe result must become obtainable.
            var book = new CraftingBook(Db);
            var have = new HashSet<string> { "wood", "stone", "dirt", "sand", "clay", "copper_ore", "iron_ore", "copper_pickaxe", "copper_axe", "torch",
                "gravel", "obsidian", "sandstone", "snow", "ice", "ash", "basalt", "lumite_ore", "cinderite_ore", "cactus", "glowcap" };   // starting kit + natural world drops
            var stations = new HashSet<string>();
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (var r in book.All)
                {
                    if (!string.IsNullOrEmpty(r.station) && !stations.Contains(r.station)) continue;
                    if (!r.ingredients.All(i => have.Contains(i.item))) continue;
                    if (have.Add(r.result)) changed = true;
                    var placed = Db.Item(r.result).placesTile;
                    if (!string.IsNullOrEmpty(placed) && Db.Tile(Db.TileId(placed)).isStation && stations.Add(placed)) changed = true;
                }
            }
            var unreachable = book.All.Where(r => !have.Contains(r.result)).Select(r => r.id).ToList();
            Assert.That(unreachable, Is.Empty, "unreachable recipes: " + string.Join(", ", unreachable));
        }
    }

    public class InventoryModelTests
    {
        static (InventoryModel model, ItemContainer inv) Make()
        {
            var inv = new ItemContainer(20, _ => 10);
            return (new InventoryModel(inv, _ => 10), inv);
        }

        [Test]
        public void PickUpPutDownSwapAndMerge()
        {
            var (m, inv) = Make();
            inv.Set(0, new ItemStack(1, 4));
            inv.Set(1, new ItemStack(2, 3));
            inv.Set(2, new ItemStack(1, 8));
            m.LeftClick(inv, 0, false);                      // pick up 4 of item 1
            Assert.That(m.Cursor.count, Is.EqualTo(4));
            Assert.That(inv[0].IsEmpty, Is.True);
            m.LeftClick(inv, 2, false);                      // merge into 8 -> 10, 2 stay on cursor
            Assert.That(inv[2].count, Is.EqualTo(10));
            Assert.That(m.Cursor.count, Is.EqualTo(2));
            m.LeftClick(inv, 1, false);                      // swap with item 2
            Assert.That(inv[1].item, Is.EqualTo(1));
            Assert.That(m.Cursor.item, Is.EqualTo(2));
            m.LeftClick(inv, 5, false);                      // put down
            Assert.That(m.Cursor.IsEmpty, Is.True);
            Assert.That(inv[5].item, Is.EqualTo(2));
        }

        [Test]
        public void RightClickSplitsAndPlacesOne()
        {
            var (m, inv) = Make();
            inv.Set(0, new ItemStack(1, 7));
            m.RightClick(inv, 0);
            Assert.That(m.Cursor.count, Is.EqualTo(4));
            Assert.That(inv[0].count, Is.EqualTo(3));
            m.RightClick(inv, 3);
            Assert.That(inv[3].count, Is.EqualTo(1));
            Assert.That(m.Cursor.count, Is.EqualTo(3));
        }

        [Test]
        public void ShiftClickMovesBetweenInventoryAndChest()
        {
            var (m, inv) = Make();
            var chest = new ItemContainer(10, _ => 10);
            m.Chest = chest;
            inv.Set(12, new ItemStack(5, 6));
            m.LeftClick(inv, 12, true);
            Assert.That(chest[0].item, Is.EqualTo(5));
            Assert.That(inv[12].IsEmpty, Is.True);
            m.LeftClick(chest, 0, true);
            Assert.That(inv.Count(5), Is.EqualTo(6));
        }

        [Test]
        public void ReturnCursorPutsItemsBack()
        {
            var (m, inv) = Make();
            inv.Set(3, new ItemStack(9, 5));
            m.LeftClick(inv, 3, false);
            var rest = m.ReturnCursor();
            Assert.That(rest.IsEmpty, Is.True);
            Assert.That(inv.Count(9), Is.EqualTo(5));
        }
    }

    public class WorldEditTests
    {
        static ContentDatabase Db => ContentDatabase.Instance;

        static (WorldData w, WorldEdits e, WorldRenderData r) Flat(int width = 40, int height = 40)
        {
            var r = new WorldRenderData(Db, WorldAtlas.Load());
            var w = new WorldData(width, height, 1);
            for (int x = 0; x < width; x++)
            for (int y = 0; y < 10; y++) w.SetBlock(x, y, Db.TileId(y == 9 ? "grass" : "dirt"));
            return (w, new WorldEdits(w, Db, r.TileProps), r);
        }

        [Test]
        public void FellingATreeRemovesTrunkAndCanopyAndDropsWood()
        {
            var (w, e, r) = Flat();
            ushort trunk = Db.TileId("tree_trunk"), top = Db.TileId("tree_top");
            for (int y = 10; y < 18; y++) w.SetBlock(20, y, trunk);
            e.Place(top, 18, 18);
            var drops = e.RemoveBlock(20, 10).ToList();
            for (int y = 10; y < 22; y++)
            for (int x = 16; x < 25; x++)
                Assert.That(w.GetBlock(x, y), Is.Zero, $"({x},{y}) still has a tree part");
            int wood = drops.Where(d => d.item == "wood").Sum(d => d.count);
            Assert.That(wood, Is.EqualTo(16), "8 trunk cells x 2 wood");
            w.Dispose(); r.Dispose();
        }

        [Test]
        public void ObjectsNeedFloorAndBreakWhenItIsRemoved()
        {
            var (w, e, r) = Flat();
            var table = Db.Tile(Db.TileId("table"));
            Assert.That(e.CanPlace(table, 5, 10), Is.True);
            Assert.That(e.CanPlace(table, 5, 14), Is.False, "floating");
            e.Place(Db.TileId("table"), 5, 10);
            Assert.That(e.ObjectOrigin(7, 11), Is.EqualTo(new Vector2Int(5, 10)));
            var drops = e.RemoveBlock(6, 9).ToList();     // pull a block out from under it
            Assert.That(w.GetBlock(5, 10), Is.Zero, "table broke");
            Assert.That(drops.Any(d => d.item == "table"), Is.True);
            w.Dispose(); r.Dispose();
        }

        [Test]
        public void DoorsToggleAndBlockOnlyWhenClosed()
        {
            var (w, e, r) = Flat();
            e.Place(Db.TileId("door"), 10, 10);
            var grid = new WorldCollisionGrid(w, r.TileProps);
            Assert.That(grid.Cell(10, 11, out _), Is.EqualTo(CellCollision.Solid));
            Assert.That(e.ToggleDoor(10, 12, null), Is.True);
            Assert.That(e.IsDoorOpen(10, 10), Is.True);
            Assert.That(grid.Cell(10, 11, out _), Is.EqualTo(CellCollision.Empty));
            Assert.That(e.ToggleDoor(10, 10, (x, y) => y == 11), Is.False, "will not close on a blocked cell");
            Assert.That(e.ToggleDoor(10, 10, null), Is.True);
            Assert.That(grid.Cell(10, 12, out _), Is.EqualTo(CellCollision.Solid));
            w.Dispose(); r.Dispose();
        }

        [Test]
        public void StationsNearbyAreFound()
        {
            var (w, e, r) = Flat();
            e.Place(Db.TileId("workbench"), 10, 10);
            Assert.That(e.StationsNear(new Vector2(12, 11), 4), Does.Contain("workbench"));
            Assert.That(e.StationsNear(new Vector2(30, 11), 4), Does.Not.Contain("workbench"));
            w.Dispose(); r.Dispose();
        }

        [Test]
        public void TorchesNeedSupport()
        {
            var (w, e, r) = Flat();
            var torch = Db.Tile(Db.TileId("torch"));
            Assert.That(e.CanPlace(torch, 5, 10), Is.True, "on the ground");
            Assert.That(e.CanPlace(torch, 5, 20), Is.False, "in mid-air");
            w.SetWall(5, 20, Db.WallId("wood_wall"));
            Assert.That(e.CanPlace(torch, 5, 20), Is.True, "on a wall");
            w.Dispose(); r.Dispose();
        }
    }
}
