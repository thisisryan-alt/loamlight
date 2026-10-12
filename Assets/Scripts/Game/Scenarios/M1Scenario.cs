using System.Collections;
using System.IO;
using Loamlight.Core;
using Loamlight.World.Generation;
using Loamlight.World.Saving;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>M1 "dig and move": walking, jump heights, mining, digging down, placing blocks and a torch, save/load.</summary>
    public sealed class M1Scenario : Scenario
    {
        public override IEnumerator Run()
        {
            yield return Frames(10);
            Check(G.Player.Grounded, $"player spawns standing on the surface at {Feet}");
            Note($"world {G.World.Width}x{G.World.Height}, setup {G.LoadMs:0} ms ({G.GenReport})");
            yield return new CaptureShot("spawn_surface");

            // --- walking: acceleration to full speed, step-up over bumps, ground contact
            float x0 = Feet.x;
            In.moveX = 1f;
            yield return Seconds(0.25f);
            Check(G.Player.Velocity.x > 6f, $"accelerates to running speed within 0.25 s (vx {G.Player.Velocity.x:0.0})");
            yield return Seconds(1.75f);
            In.moveX = 0f;
            yield return Seconds(0.4f);
            Check(Feet.x - x0 > 8f, $"walked right {Feet.x - x0:0.0} tiles in 2 s (step-up keeps it moving over terrain)");
            Check(Mathf.Abs(G.Player.Velocity.x) < 0.5f, $"stops when input is released (vx {G.Player.Velocity.x:0.00})");

            // --- full jump vs short hop (variable jump height)
            yield return Seconds(0.2f);
            float full = 0f;
            yield return MeasureJump(0.6f, h => full = h);
            yield return Seconds(0.3f);
            float hop = 0f;
            yield return MeasureJump(0.05f, h => hop = h);
            Check(full > 4.5f && full < 7.5f, $"held jump reaches {full:0.00} tiles (expected ~5.5)");
            Check(hop > 0.8f && hop < full * 0.6f, $"tapped jump is a short hop: {hop:0.00} tiles");

            // --- mining the block under our feet, item goes to the inventory
            Check(Select("copper_pickaxe"), "pickaxe is in the inventory");
            yield return Frames(2);
            var below = new Vector2Int(FeetTile.x, FeetTile.y - 1);
            ushort mined = G.World.GetBlock(below.x, below.y);
            var minedDef = G.Db.Tile(mined);
            string dropId = minedDef != null ? minedDef.drop : "";
            int before = string.IsNullOrEmpty(dropId) ? 0 : G.Inventory.Count(G.Db.ItemId(dropId));
            yield return MineTile(below);
            Check(G.World.GetBlock(below.x, below.y) == 0, $"mined {minedDef?.id ?? "nothing"} at {below}");
            yield return Seconds(1.0f);   // the drop pops out as a pickup and flies into the inventory
            if (!string.IsNullOrEmpty(dropId))
                Check(G.Inventory.Count(G.Db.ItemId(dropId)) == before + 1, $"{dropId} added to inventory ({before} -> {G.Inventory.Count(G.Db.ItemId(dropId))})");
            Check(Sfx.PlayLog.Contains(minedDef?.sound ?? "dig"), "mining played its dig sound");

            // --- dig straight down: clear every column under the body each time, and fall
            float startY = Feet.y;
            for (int i = 0; i < 10; i++)
            {
                yield return Seconds(0.3f);
                int row = FeetTile.y - 1;
                foreach (int x in new System.Collections.Generic.List<int>(BodyColumns()))
                    if (G.World.GetBlock(x, row) != 0) yield return MineTile(new Vector2Int(x, row));
            }
            yield return Seconds(0.6f);
            Check(startY - Feet.y >= 7f, $"dug down and fell {startY - Feet.y:0.0} tiles");
            yield return new CaptureShot("dug_down");

            // --- placing a block (consumes one from the stack) on a legal, anchored spot
            string block = G.Inventory.Count(G.Db.ItemId("dirt")) > 0 ? "dirt" : "stone";
            Check(Select(block), $"have {block} to place");
            yield return Frames(2);
            int countBefore = G.Inventory.Count(G.Db.ItemId(block));
            var spot = FindPlaceSpot();
            Check(spot.HasValue, "found an anchored empty tile next to the shaft");
            if (spot.HasValue)
            {
                yield return UseOnce(spot.Value);
                Check(G.World.GetBlock(spot.Value.x, spot.Value.y) == G.Db.TileId(block), $"placed {block} at {spot.Value}");
                Check(G.Inventory.Count(G.Db.ItemId(block)) == countBefore - 1, $"placing consumed one {block}");
                // unanchored placement is refused (floating in the middle of the shaft, no wall)
            }

            // --- a torch lights the dark shaft
            Check(Select("torch"), "torches in the inventory");
            yield return Frames(2);
            G.Light.Tick();
            // torches are not solid, so they may go on the shaft wall right behind the player
            var torchSpot = FindPlaceSpot(t => G.Light.Sample(t.x, t.y) < 0.75f && G.World.GetWall(t.x, t.y) != 0, allowBodyOverlap: true);
            Check(torchSpot.HasValue, "found a dark, supported spot for a torch");
            if (torchSpot.HasValue)
            {
                var torchTile = torchSpot.Value;
                float darkBefore = G.Light.Sample(torchTile.x, torchTile.y);
                yield return UseOnce(torchTile);
                Check(G.World.GetBlock(torchTile.x, torchTile.y) == G.Db.TileId("torch"), $"torch placed at {torchTile}");
                G.Light.Tick();
                float litAfter = G.Light.Sample(torchTile.x, torchTile.y);
                Check(litAfter > darkBefore + 0.2f, $"torch raises light at its tile from {darkBefore:0.00} to {litAfter:0.00}");
            }
            yield return Seconds(0.3f);
            yield return new CaptureShot("torch_in_shaft");

            // --- save, then load into a second world and compare every tile
            var path = Path.Combine(Application.temporaryCachePath, "scenario_m1" + WorldSave.Extension);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            G.SaveTo(path);
            double saveMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var loaded = WorldSave.Load(path, G.Db, out var meta);
            double loadMs = sw.Elapsed.TotalMilliseconds;
            ulong a = WorldHash.Compute(G.World), b = WorldHash.Compute(loaded);
            loaded.Dispose();
            long bytes = new FileInfo(path).Length;
            Check(a == b, $"save/load round trip preserves every tile (hash {a:x16})");
            Check(meta.gameJson.Contains("torch"), "player inventory stored in the save");
            Note($"save {saveMs:0} ms, load {loadMs:0} ms, {bytes / 1024} KB on disk");
            File.Delete(path);
            yield return new CaptureShot("after_save_load");
        }

        IEnumerator MeasureJump(float hold, System.Action<float> result)
        {
            // wait for ground
            float until = Time.time + 2f;
            while (!G.Player.Grounded && Time.time < until) yield return null;
            float y0 = Feet.y, peak = y0;
            In.PressJump();
            float release = Time.time + hold;
            float end = Time.time + 1.6f;
            while (Time.time < end)
            {
                if (Time.time >= release) In.jumpHeld = false;
                peak = Mathf.Max(peak, Feet.y);
                if (Time.time > release + 0.1f && G.Player.Grounded) break;
                yield return null;
            }
            In.jumpHeld = false;
            result(peak - y0);
        }
    }
}
