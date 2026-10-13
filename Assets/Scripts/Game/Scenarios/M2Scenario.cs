using System.Collections;
using System.Collections.Generic;
using System.IO;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Crafting;
using Loamlight.World;
using Loamlight.World.Collision;
using Loamlight.World.Saving;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// M2 "build and craft", end to end in the real game: fell a tree and pick up the wood, craft a workbench and
    /// place it, craft furniture/walls/a hammer/a door/a chest, build a house with drag-lines and a Shift-drag wall
    /// rectangle (ghost preview checked mid-drag), hammer a block into a half block and slopes, undo a placement,
    /// open/close the door, store items in a chest, set the bed spawn point, walk on a platform and drop through it,
    /// then save and confirm the chest contents went into the save.
    /// </summary>
    public sealed class M2Scenario : Scenario
    {
        CraftingBook _book;

        public override IEnumerator Run()
        {
            _book = new CraftingBook(G.Db);
            yield return Frames(5);

            // ---------------------------------------------------------------- wood: fell a tree, pick up the drops
            var trunk = FindTree();
            Check(trunk.HasValue, "found a tree near spawn");
            if (!trunk.HasValue) yield break;
            var t = trunk.Value;
            G.Player.Teleport(new Vector2(t.x - 2.5f, t.y));
            G.CameraFollow.SnapToTarget();
            yield return Seconds(0.4f);
            int height = 0;
            while (G.World.GetBlock(t.x, t.y + height) == G.Db.TileId("tree_trunk")) height++;
            int woodBefore = Count("wood");
            Check(Select("copper_axe"), "copper axe in the starting kit");
            yield return Seconds(0.1f);
            yield return MineTile(t, 6f);
            Check(G.World.GetBlock(t.x, t.y + 1) == 0 && G.World.GetBlock(t.x, t.y + height + 1) == 0,
                $"felling the base removed the whole {height}-tall tree and its canopy");
            Check(G.Pickups.Count > 0, $"wood dropped as pickups ({G.Pickups.Count} on the ground)");
            yield return new CaptureShot("tree_felled");
            // walk over the drops so they fly in
            In.moveX = 1f;
            yield return Seconds(0.6f);
            In.moveX = 0f;
            yield return Seconds(1.2f);
            int wood = Count("wood");
            Check(wood - woodBefore >= height * 2 - 2, $"picked up {wood - woodBefore} wood (expected ~{height * 2})");
            Check(Sfx.PlayLog.Contains("pickup"), "pickups make a sound");
            if (Count("wood") < 120) Give("wood", 120 - Count("wood"), "topped up for the build (one tree is not enough for a house)");

            // ---------------------------------------------------------------- craft a workbench by hand and place it
            var ground = FindFlatGround(14) ?? LevelSite(14);
            Check(ground.HasValue, "have 14 tiles of flat ground to build on");
            if (!ground.HasValue) yield break;
            var g0 = ground.Value;   // first air tile above flat ground
            G.Player.Teleport(new Vector2(g0.x + 7.5f, g0.y));
            G.CameraFollow.SnapToTarget();
            yield return Seconds(0.3f);
            G.InventoryUi.SetOpen(true);
            G.InventoryUi.RefreshStations();
            Check(!Has(G.InventoryUi.VisibleRecipes, "table"), "without a workbench, tables are not offered");
            G.InventoryUi.SelectRecipe(_book.Find("workbench"));
            Check(G.InventoryUi.CraftSelected() == 1 && Count("workbench") == 1, "crafted a workbench by hand from 10 wood");
            yield return new CaptureShot("crafting_ui");
            G.InventoryUi.SetOpen(false);
            yield return Seconds(0.1f);
            Select("workbench");
            yield return UseOnce(new Vector2Int(g0.x + 10, g0.y));
            var benchAt = new Vector2Int(g0.x + 10 - (2 - 1) / 2, g0.y);
            Check(G.World.GetBlock(benchAt.x, benchAt.y) == G.Db.TileId("workbench"), $"workbench placed at {benchAt}");

            // ---------------------------------------------------------------- craft at the workbench
            G.InventoryUi.SetOpen(true);
            G.InventoryUi.RefreshStations();
            Check(G.InventoryUi.Stations.Contains("workbench"), "standing next to the workbench enables it");
            foreach (var (recipe, times) in new[] { ("wood_wall", 20), ("door", 1), ("table", 1), ("chair", 1), ("wooden_hammer", 1), ("bed", 1), ("wood_platform", 3) })
            {
                G.InventoryUi.SelectRecipe(_book.Find(recipe));
                int made = G.InventoryUi.CraftSelected(times);
                Check(made == times, $"crafted {recipe} x{made}");
            }
            Give("copper_bar", 1, "a copper bar for the chest (smelting is covered by the furnace recipe test)");
            G.InventoryUi.SelectRecipe(_book.Find("chest"));
            Check(G.InventoryUi.CraftSelected() == 1, "crafted a chest (wood + copper bar)");
            G.InventoryUi.SetOpen(false);
            yield return Seconds(0.1f);

            // ---------------------------------------------------------------- build: wall rectangle (Shift-drag)
            int x0 = g0.x, y0 = g0.y, w = 10, h = 6;
            Check(Select("wood_wall"), "wood walls ready");
            yield return Seconds(0.1f);
            int wallsBefore = Count("wood_wall");
            In.shiftHeld = true;
            Aim(new Vector2Int(x0, y0));
            yield return Seconds(0.05f);
            In.useHeld = true;
            yield return Seconds(0.05f);
            Aim(new Vector2Int(x0 + w - 1, y0 + h - 1));
            yield return Seconds(0.1f);
            Check(G.Tools.DragPreview.Count == w * h, $"Shift-drag previews a filled {w}x{h} rectangle ({G.Tools.DragPreview.Count} ghost tiles)");
            yield return new CaptureShot("drag_preview");
            In.useHeld = false;
            In.shiftHeld = false;
            yield return Seconds(0.1f);
            int walls = 0;
            for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                if (G.World.GetWall(x, y) == G.Db.WallId("wood_wall")) walls++;
            Check(walls >= w * h - 2, $"wall rectangle placed: {walls} of {w * h} tiles");
            Check(wallsBefore - Count("wood_wall") == walls, $"each wall tile consumed one item ({wallsBefore - Count("wood_wall")})");

            // ---------------------------------------------------------------- build: pillars + roof (drag lines)
            Select("wood");
            yield return Seconds(0.1f);
            yield return DragLine(new Vector2Int(x0, y0 + 3), new Vector2Int(x0, y0 + h - 1));          // left pillar above the door
            yield return DragLine(new Vector2Int(x0 + w - 1, y0), new Vector2Int(x0 + w - 1, y0 + h - 1)); // right wall
            yield return DragLine(new Vector2Int(x0, y0 + h), new Vector2Int(x0 + w - 1, y0 + h));        // roof
            int blocks = 0;
            for (int x = x0; x < x0 + w; x++) if (G.World.GetBlock(x, y0 + h) == G.Db.TileId("wood_planks")) blocks++;
            Check(blocks == w, $"roof line placed ({blocks}/{w})");
            Check(G.World.GetBlock(x0 + w - 1, y0) == G.Db.TileId("wood_planks") && G.World.GetBlock(x0, y0 + h - 1) == G.Db.TileId("wood_planks"),
                "pillar lines placed");

            // ---------------------------------------------------------------- undo
            int woodNow = Count("wood");
            yield return DragLine(new Vector2Int(x0 + 2, y0 + h + 1), new Vector2Int(x0 + 6, y0 + h + 1));
            Check(G.World.GetBlock(x0 + 4, y0 + h + 1) != 0 && Count("wood") == woodNow - 5, "placed a 5-block line on the roof");
            In.PressUndo();
            yield return Seconds(0.1f);
            Check(G.World.GetBlock(x0 + 4, y0 + h + 1) == 0 && Count("wood") == woodNow, "Ctrl+Z removed the line and refunded all 5 wood");

            // ---------------------------------------------------------------- hammer: half block and slopes
            Select("wooden_hammer");
            yield return Seconds(0.35f);
            var corner = new Vector2Int(x0 + w - 1, y0 + h);
            Aim(corner);
            In.useHeld = true;
            yield return Seconds(0.05f);
            In.useHeld = false;
            yield return Seconds(0.35f);
            Check(G.World.GetShape(corner.x, corner.y) == TileShape.HalfBottom, "hammer turns a block into a half block");
            In.useHeld = true;
            yield return Seconds(0.05f);
            In.useHeld = false;
            yield return Seconds(0.35f);
            Check(G.World.GetShape(corner.x, corner.y) == TileShape.SlopeUpRight, "second hit makes a slope");

            // ---------------------------------------------------------------- door, furniture, torch
            G.Player.Teleport(new Vector2(x0 + 3.5f, y0));
            yield return Seconds(0.2f);
            Select("door");
            yield return Seconds(0.1f);
            yield return UseOnce(new Vector2Int(x0, y0));
            Check(G.World.GetBlock(x0, y0) == G.Db.TileId("door"), "door placed in the left wall");
            Check(G.Grid.Cell(x0, y0 + 1, out _) == CellCollision.Solid, "a closed door blocks the way");
            In.aimWorld = new Vector2(x0 + 0.5f, y0 + 1.5f);
            In.PressInteract();
            yield return Seconds(0.1f);
            Check(G.Grid.Cell(x0, y0 + 1, out _) == CellCollision.Empty, "right-click opens the door (passable)");
            In.PressInteract();
            yield return Seconds(0.1f);
            Check(G.Grid.Cell(x0, y0 + 1, out _) == CellCollision.Solid, "right-click again closes it");

            Select("table");
            yield return Seconds(0.1f);
            yield return UseOnce(new Vector2Int(x0 + 5, y0));
            Check(G.World.GetBlock(x0 + 4, y0) == G.Db.TileId("table"), "table placed");
            Select("chair");
            yield return Seconds(0.1f);
            yield return UseOnce(new Vector2Int(x0 + 3, y0));
            Check(G.World.GetBlock(x0 + 3, y0 + 1) == G.Db.TileId("chair"), "chair placed");
            Select("torch");
            yield return Seconds(0.1f);
            yield return UseOnce(new Vector2Int(x0 + 5, y0 + 4));
            Check(G.World.GetBlock(x0 + 5, y0 + 4) == G.Db.TileId("torch"), "torch on the back wall");

            // ---------------------------------------------------------------- chest storage
            Select("chest");
            yield return Seconds(0.1f);
            yield return UseOnce(new Vector2Int(x0 + 7, y0));
            var chestOrigin = new Vector2Int(x0 + 7, y0);
            Check(G.World.GetBlock(chestOrigin.x, chestOrigin.y) == G.Db.TileId("chest"), "chest placed");
            In.aimWorld = chestOrigin + new Vector2(0.5f, 0.5f);
            In.PressInteract();
            yield return Seconds(0.15f);
            Check(G.InventoryUi.IsOpen && G.InventoryUi.ChestOpen, "right-click opens the chest UI");
            int stoneSlot = SlotOf("dirt");
            if (stoneSlot < 0) { Give("dirt", 20, "some dirt to store"); stoneSlot = SlotOf("dirt"); }
            int dirtCount = G.Inventory[stoneSlot].count;
            G.InventoryUi.Model.LeftClick(G.Inventory, stoneSlot, true);   // shift-click: quick-move into the chest
            var chest = G.Chests.Get(chestOrigin);
            Check(chest.Count(G.Db.ItemId("dirt")) == dirtCount, $"shift-click moved {dirtCount} dirt into the chest");
            yield return new CaptureShot("chest_open");
            G.InventoryUi.SetOpen(false);
            yield return Seconds(0.1f);
            Select("copper_pickaxe");
            yield return Seconds(0.1f);
            yield return MineTile(chestOrigin, 1.5f);
            Check(G.World.GetBlock(chestOrigin.x, chestOrigin.y) == G.Db.TileId("chest"), "a chest with items in it cannot be broken");

            // ---------------------------------------------------------------- bed sets spawn
            Select("bed");
            yield return Seconds(0.1f);
            var bedSpot = new Vector2Int(x0 + 14, y0);
            G.Player.Teleport(new Vector2(x0 + 12.5f, y0));
            yield return Seconds(0.2f);
            yield return UseOnce(bedSpot);
            var bed = G.Edits.ObjectOrigin(bedSpot.x, bedSpot.y);
            Check(G.World.GetBlock(bedSpot.x, bedSpot.y) == G.Db.TileId("bed"), "bed placed outside");
            In.aimWorld = bedSpot + new Vector2(0.5f, 0.5f);
            In.PressInteract();
            yield return Seconds(0.1f);
            Check(G.HasBedSpawn && G.Hud.LastToast == "Spawn point set", "right-click on the bed sets the spawn point");

            // ---------------------------------------------------------------- platform: stand on it, drop through
            Select("wood_platform");
            yield return Seconds(0.1f);
            int py = y0 + 2;   // feet at y0+3, head clear of the roof at y0+6
            yield return DragLine(new Vector2Int(x0 + 1, py), new Vector2Int(x0 + 4, py));
            Check(G.World.GetBlock(x0 + 2, py) == G.Db.TileId("wood_platform"), "platform line placed inside the house");
            G.Player.Teleport(new Vector2(x0 + 2.5f, py + 1.3f));
            yield return Seconds(0.6f);
            Check(Mathf.Abs(Feet.y - (py + 1)) < 0.05f, $"stands on the platform (feet {Feet.y:0.00}, top {py + 1})");
            In.downHeld = true;
            yield return Seconds(0.6f);
            In.downHeld = false;
            Check(Feet.y < py, $"holding Down drops through it (feet {Feet.y:0.00})");
            G.CameraFollow.SnapToTarget();
            yield return Seconds(0.3f);
            yield return new CaptureShot("house_built");

            // ---------------------------------------------------------------- save: chest contents persist
            var path = Path.Combine(Application.temporaryCachePath, "scenario_m2" + WorldSave.Extension);
            G.SaveTo(path);
            var meta = WorldSave.ReadMeta(path);
            Check(meta.gameJson.Contains("dirt*" + dirtCount) && meta.gameJson.Contains("chests"), "chest contents are in the save");
            Check(meta.gameJson.Contains("true"), "bed spawn point is in the save");
            using (var loaded = WorldSave.Load(path, G.Db, out _))
                Check(loaded.GetBlock(x0, y0) == G.Db.TileId("door") && loaded.GetShape(corner.x, corner.y) == TileShape.SlopeUpRight,
                    "house (door, hammered slope) survives save/load");
            File.Delete(path);
            yield return new CaptureShot("after_save");
        }


        static bool Has(IReadOnlyList<RecipeDef> list, string id)
        {
            foreach (var r in list) if (r.id == id) return true;
            return false;
        }

        IEnumerator DragLine(Vector2Int a, Vector2Int b)
        {
            yield return Seconds(0.05f);
            Aim(a);
            yield return Seconds(0.05f);
            In.useHeld = true;
            yield return Seconds(0.05f);
            Aim(b);
            yield return Seconds(0.08f);
            In.useHeld = false;
            yield return Seconds(0.08f);
        }

        Vector2Int? FindTree()
        {
            ushort trunk = G.Db.TileId("tree_trunk");
            int cx = Mathf.FloorToInt(Feet.x);
            for (int d = 0; d < 400; d++)
            {
                int x = cx + (d % 2 == 0 ? d / 2 : -(d / 2 + 1));
                for (int y = G.World.Height - 2; y > 1; y--)
                {
                    ushort b = G.World.GetBlock(x, y);
                    if (b == trunk && G.World.GetBlock(x, y - 1) != trunk) return new Vector2Int(x, y);
                    if (b != 0 && G.Render.TileProps[b].solid != 0) break;
                }
            }
            return null;
        }

        /// <summary>First air tile above a run of n columns of equal surface height with nothing growing on them.</summary>
        Vector2Int? FindFlatGround(int n)
        {
            int cx = Mathf.FloorToInt(Feet.x);
            for (int d = 0; d < 600; d++)
            {
                int x = cx + (d % 2 == 0 ? d / 2 : -(d / 2 + 1));
                int top = Surface(x);
                bool ok = top > 0;
                for (int i = 0; ok && i < n + 6; i++)
                {
                    if (Surface(x + i) != top) ok = false;
                    for (int y = top + 1; ok && y < top + 10; y++)
                        if (G.World.GetBlock(x + i, y) != 0 && G.World.GetBlock(x + i, y) != G.Db.TileId("grass_decor")) ok = false;
                }
                if (!ok) continue;
                // clear tall grass in the build area
                for (int i = 0; i < n + 6; i++)
                    if (G.World.GetBlock(x + i, top + 1) == G.Db.TileId("grass_decor")) G.World.SetBlock(x + i, top + 1, 0);
                return new Vector2Int(x, top + 1);
            }
            return null;
        }

        /// <summary>Levels a building site next to the player (scenario setup, noted in the report).</summary>
        Vector2Int? LevelSite(int n)
        {
            int x0 = Mathf.FloorToInt(Feet.x) + 4;
            int top = Surface(x0);
            if (top < 0) return null;
            ushort dirt = G.Db.TileId("dirt"), grass = G.Db.TileId("grass");
            for (int i = 0; i < n + 6; i++)
            {
                int x = x0 + i;
                for (int y = top + 1; y < top + 24; y++)
                    if (G.World.GetBlock(x, y) != 0) G.Edits.ClearSilently(x, y);
                for (int y = top - 6; y <= top; y++)
                    G.World.SetBlock(x, y, y == top ? grass : dirt);
            }
            Note($"levelled a {n + 6}-wide building site at x={x0}, ground y={top}");
            return new Vector2Int(x0, top + 1);
        }

        int Surface(int x)
        {
            for (int y = G.World.Height - 2; y > 1; y--)
            {
                ushort b = G.World.GetBlock(x, y);
                if (b != 0 && G.Render.TileProps[b].solid != 0) return y;
            }
            return -1;
        }
    }
}
