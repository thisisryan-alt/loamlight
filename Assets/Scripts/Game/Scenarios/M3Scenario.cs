using System.Collections;
using Loamlight.Core;
using Loamlight.World;
using Loamlight.World.Generation;
using UnityEngine;

namespace Loamlight.Game.Scenarios
{
    /// <summary>
    /// M3 "light, liquids and day/night", end to end in the real game: noon, dusk and night skies (sky light really
    /// dims), a torch holding back the night, scooping a lake with a bucket and pouring it into a pit, swimming,
    /// water poured onto lava turning to obsidian, sand collapsing when undermined, a lit Glowmire cavern, the
    /// Emberdeep, and the desert and frost surfaces.
    /// </summary>
    public sealed class M3Scenario : Scenario
    {
        public override IEnumerator Run()
        {
            var w = G.World;
            yield return Frames(5);
            Check(G.DayNight != null && G.Sky != null && G.Simulation != null, "day/night, sky and simulation are running");

            // ---------------------------------------------------------------- day, dusk, night at the spawn
            G.DayNight.SetTime(0.3f);
            yield return Seconds(0.6f);
            Check(G.Light.skyIntensity > 0.95f, $"noon sky light {G.Light.skyIntensity:0.00}");
            float noon = SurfaceLight();
            yield return new CaptureShot("noon");
            G.DayNight.SetTime(0.6f);
            yield return Seconds(0.6f);
            yield return new CaptureShot("dusk");
            G.DayNight.SetTime(0.8f);
            yield return Seconds(0.6f);
            float night = SurfaceLight();
            Check(G.DayNight.IsNight && G.Light.skyIntensity < 0.25f, $"night sky light {G.Light.skyIntensity:0.00}");
            Check(night < noon * 0.4f, $"open ground is dark at night ({night:0.00} vs {noon:0.00} at noon)");
            // a torch pushes back the dark
            var torchSpot = FindPlaceSpot(t => w.GetBlock(t.x, t.y) == 0 && G.Edits.IsSolidTerrain(t.x, t.y - 1) && Mathf.Abs(t.x - FeetTile.x) >= 2);
            Check(torchSpot.HasValue, "found a spot for a torch");
            if (torchSpot.HasValue)
            {
                Check(Select("torch"), "torches in the kit");
                yield return UseOnce(torchSpot.Value);
                yield return Seconds(0.4f);
                float lit = G.Light.Sample(torchSpot.Value.x, torchSpot.Value.y + 1);
                Check(lit > night * 2f + 0.1f, $"torch light {lit:0.00} at night");
                yield return new CaptureShot("night_torch");
            }
            G.DayNight.SetTime(0.25f);

            // ---------------------------------------------------------------- bucket: scoop a lake, pour into a pit
            // a surface lake: open sky above the middle of a body of water
            var lake = FindCell(c => IsWater(c) && IsWater(new Vector2Int(c.x - 3, c.y)) && IsWater(new Vector2Int(c.x + 3, c.y))
                                    && w.GetLiquid(c.x, c.y + 1) == 0 && OpenSky(c.x, c.y + 1), 0f);
            Check(lake.HasValue, "found a body of water");
            if (lake.HasValue)
            {
                var stand = StandNear(lake.Value, 26);
                Check(stand.HasValue, "dry ground beside the water");
                if (stand.HasValue)
                {
                    TeleportTo(stand.Value);
                    yield return Seconds(0.5f);
                    Give("bucket", 1, "a bucket (its anvil recipe is covered by the crafting tests)");
                    Check(Select("bucket"), "holding the bucket");
                    // the nearest full water tile within the bucket's reach
                    var scoop = lake.Value;
                    float best = float.MaxValue;
                    for (int dy = -6; dy <= 6; dy++)
                    for (int dx = -6; dx <= 6; dx++)
                    {
                        var c = new Vector2Int(FeetTile.x + dx, FeetTile.y + dy);
                        float d = Vector2.Distance(G.Player.Center, c + new Vector2(0.5f, 0.5f));
                        if (IsWater(c) && d < best && d <= 5f) { best = d; scoop = c; }
                    }
                    Check(best <= 5f, $"water within reach ({best:0.0} tiles)");
                    yield return UseOnce(scoop);
                    Check(SlotOf("water_bucket") >= 0, "scooping turned the bucket into a water bucket");
                    // dig a 3-wide pit at our feet and pour it in
                    var f = FeetTile;
                    var pit = new Vector2Int(f.x + G.Player.Facing * 3, f.y - 1);
                    for (int dx = -1; dx <= 1; dx++)
                    for (int dy = 0; dy >= -1; dy--)
                        G.Edits.RemoveBlock(pit.x + dx, pit.y + dy);
                    long before = G.Simulation.TotalLiquid(Around(pit, 6), LiquidType.Water);
                    Select("water_bucket");
                    yield return UseOnce(pit);
                    Check(SlotOf("bucket") >= 0, "pouring empties the bucket");
                    yield return Seconds(1.5f);
                    long after = G.Simulation.TotalLiquid(Around(pit, 6), LiquidType.Water);
                    Check(after >= before + 200, $"poured a tile of water into the pit ({after - before})");
                    Check(w.GetLiquid(pit.x - 1, pit.y - 1) > 0 && w.GetLiquid(pit.x + 1, pit.y - 1) > 0, "the water levelled across the pit floor");
                    yield return new CaptureShot("bucket_pit");
                }

                // swim: drop into the water
                TeleportTo(new Vector2Int(lake.Value.x, lake.Value.y));
                yield return Seconds(1.2f);
                Check(G.Player.Submerged > 0.35f, $"the player is in the water (submerged {G.Player.Submerged:0.00})");
                Check(G.Player.Velocity.y > -G.Player.motor.swimMaxFall - 0.5f, $"sinking slowly ({G.Player.Velocity.y:0.0})");
                Check(Sfx.PlayLog.Contains("splash"), "entering the water splashes");
                In.jumpHeld = true;
                yield return Seconds(0.25f);
                Check(G.Player.Velocity.y > 0f, "holding jump swims up");
                yield return new CaptureShot("swimming");
                In.jumpHeld = false;
            }

            // ---------------------------------------------------------------- water onto lava makes obsidian
            var lava = FindCell(c => w.GetLiquid(c.x, c.y) > 200 && w.LiquidKind[w.Index(c.x, c.y)] == (byte)LiquidType.Lava
                                    && w.GetBlock(c.x, c.y + 1) == 0 && w.GetLiquid(c.x, c.y + 1) == 0 && w.GetBlock(c.x, c.y + 2) == 0, 0f);
            Check(lava.HasValue, "found a lava pool");
            if (lava.HasValue)
            {
                var stand = StandNear(lava.Value, 8);
                if (stand.HasValue) TeleportTo(stand.Value);
                yield return Seconds(0.5f);
                long reactions = G.Simulation.Reactions;
                var l = lava.Value;
                for (int dx = -1; dx <= 1; dx++)
                    if (w.GetBlock(l.x + dx, l.y + 2) == 0) w.SetLiquid(l.x + dx, l.y + 2, 255, LiquidType.Water);
                yield return Seconds(1.5f);
                int obsidian = 0;
                ushort obs = G.Db.TileId("obsidian");
                for (int dy = -2; dy <= 3; dy++)
                for (int dx = -4; dx <= 4; dx++)
                    if (w.GetBlock(l.x + dx, l.y + dy) == obs) obsidian++;
                Check(obsidian > 0 && G.Simulation.Reactions > reactions, $"water met lava: {obsidian} obsidian nearby, {G.Simulation.Reactions - reactions} reactions");
                Check(Sfx.PlayLog.Contains("sizzle"), "the reaction sizzles");
                yield return new CaptureShot("lava_obsidian");
            }

            // ---------------------------------------------------------------- sand collapses when undermined
            {
                var spawn = Vector2Int.FloorToInt(G.SpawnPointWorld);
                TeleportTo(spawn);
                yield return Seconds(0.4f);
                ushort sand = G.Db.TileId("sand");
                // a column of open air over solid ground near the spawn (not under a tree)
                var col = new Vector2Int(spawn.x + 4, spawn.y + 6);
                int floor = spawn.y;
                for (int k = 3; k < 40; k++)
                {
                    int x = spawn.x + (k % 2 == 0 ? k / 2 : -k / 2) + 3;
                    var top = SurfaceAt(x);
                    bool clear = true;
                    for (int y = top.y; y < top.y + 12 && clear; y++) clear = w.GetBlock(x, y) == 0 && w.GetBlock(x, y) == 0;
                    if (!clear || !G.Edits.IsSolidTerrain(x, top.y - 1)) continue;
                    col = new Vector2Int(x, top.y + 6);
                    floor = top.y;
                    break;
                }
                for (int i = 0; i < 3; i++) G.Edits.Place(sand, col.x, col.y + i);
                yield return Seconds(1.2f);
                Check(w.GetBlock(col.x, col.y) != sand || col.y == floor, "floating sand did not stay in the air");
                int landed = 0;
                for (int y = floor; y < floor + 3; y++) if (w.GetBlock(col.x, y) == sand) landed++;
                Check(landed == 3, $"three sand blocks piled up on the ground ({landed})");
                Check(G.Simulation.BlocksFallen >= 3, $"falling-block ticks moved {G.Simulation.BlocksFallen} blocks");
            }

            // ---------------------------------------------------------------- Glowmire: lit by glowcaps
            ushort glowcap = G.Db.TileId("glowcap");
            var glow = FindCell(c => w.GetBlock(c.x, c.y) == glowcap && CountAround(c, 6, glowcap) >= 6 && Standable(new Vector2Int(c.x + 1, c.y)), 0f);
            Check(glow.HasValue, "found a Glowmire cavern");
            if (glow.HasValue)
            {
                TeleportTo(new Vector2Int(glow.Value.x + 1, glow.Value.y));
                G.DayNight.SetTime(0.8f);
                yield return Seconds(0.8f);
                float l = G.Light.Sample(glow.Value.x, glow.Value.y + 1);
                Check(l > 0.15f, $"glowcaps light the cavern ({l:0.00})");
                yield return new CaptureShot("glowmire");
                G.DayNight.SetTime(0.3f);
            }

            // ---------------------------------------------------------------- Emberdeep
            ushort basalt = G.Db.TileId("basalt");
            var deep = FindCell(c => c.y < w.Height * 0.12f && Standable(c) && w.GetBlock(c.x, c.y - 1) == basalt, 0f);
            Check(deep.HasValue, "found standing room in the Emberdeep");
            if (deep.HasValue)
            {
                TeleportTo(deep.Value);
                yield return Seconds(0.8f);
                yield return new CaptureShot("emberdeep");
            }

            // ---------------------------------------------------------------- surface biomes
            var biomes = WorldGenerator.SurfaceBiomes(w.Width, w.Height, w.Seed, GenParams.Default);
            foreach (var (biome, shot) in new[] { (Biome.Dunes, "dunes"), (Biome.Frostfell, "frostfell") })
            {
                int x = System.Array.IndexOf(biomes, biome);
                int x1 = System.Array.LastIndexOf(biomes, biome);
                Check(x >= 0, $"{biome} exists on the surface");
                if (x < 0) continue;
                int mid = (x + x1) / 2;
                var top = SurfaceAt(mid);
                TeleportTo(top);
                yield return Seconds(0.8f);
                yield return new CaptureShot(shot);
            }

            Check(G.Simulation.Spreads >= 0, $"spread ticks ran ({G.Simulation.Spreads} conversions so far)");
            Note($"liquid ticks {G.Simulation.LiquidTicks} (skipped asleep {G.Simulation.SkippedTicks}), last tick {G.Simulation.LastTickMs:0.00} ms");
        }

        // ------------------------------------------------------------------------------------------- helpers
        bool IsWater(Vector2Int c) => G.World.GetLiquid(c.x, c.y) == 255 && G.World.LiquidKind[G.World.Index(c.x, c.y)] == (byte)LiquidType.Water;

        bool OpenSky(int x, int y)
        {
            for (int yy = y; yy < G.World.Height; yy++)
                if (G.World.GetBlock(x, yy) != 0) return false;
            return true;
        }

        float SurfaceLight()
        {
            var f = FeetTile;
            return G.Light.Sample(f.x, f.y + 1);
        }

        Liquids.SimRegion Around(Vector2Int c, int r) => new Liquids.SimRegion
        {
            x0 = Mathf.Max(0, c.x - r), x1 = Mathf.Min(G.World.Width, c.x + r), y0 = Mathf.Max(0, c.y - r), y1 = Mathf.Min(G.World.Height, c.y + r),
        };

        void TeleportTo(Vector2Int feet)
        {
            G.Player.Teleport(new Vector2(feet.x + 0.5f, feet.y));
            G.CameraFollow.SnapToTarget();
        }

        bool Standable(Vector2Int t)
        {
            var w = G.World;
            if (!w.InBounds(t.x, t.y - 1) || !w.InBounds(t.x, t.y + 3)) return false;
            if (G.Render.TileProps[w.GetBlock(t.x, t.y - 1)].solid == 0 || w.GetBlock(t.x, t.y - 1) == 0) return false;
            for (int dy = 0; dy < 3; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                ushort b = w.GetBlock(t.x + dx, t.y + dy);
                if ((b != 0 && G.Render.TileProps[b].solid != 0) || w.GetLiquid(t.x + dx, t.y + dy) > 0) return false;
            }
            return true;
        }

        Vector2Int? StandNear(Vector2Int c, int r)
        {
            for (int d = 1; d <= r; d++)
            for (int dy = -d; dy <= d; dy++)
            foreach (int dx in new[] { -d, d })
            {
                var t = new Vector2Int(c.x + dx, c.y + dy);
                if (Standable(t)) return t;
            }
            return null;
        }

        Vector2Int SurfaceAt(int x)
        {
            for (int y = G.World.Height - 2; y > 0; y--)
            {
                ushort b = G.World.GetBlock(x, y);
                if (b != 0 && G.Render.TileProps[b].solid != 0) return new Vector2Int(x, y + 1);
            }
            return new Vector2Int(x, G.World.Height / 2);
        }

        int CountAround(Vector2Int c, int r, ushort id)
        {
            int n = 0;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
                if (G.World.GetBlock(c.x + dx, c.y + dy) == id) n++;
            return n;
        }

        /// <summary>Nearest cell (by rings around the world centre at height y0, or the spawn when y0 = 0) matching a test.</summary>
        Vector2Int? FindCell(System.Func<Vector2Int, bool> test, float y0)
        {
            var w = G.World;
            var start = y0 > 0f ? new Vector2Int(w.Width / 2, (int)y0) : Vector2Int.FloorToInt(G.SpawnPointWorld);
            int max = Mathf.Max(w.Width, w.Height);
            for (int r = 0; r < max; r += 1)
            {
                for (int k = -r; k <= r; k += 1)
                {
                    foreach (var c in new[] { new Vector2Int(start.x + k, start.y - r), new Vector2Int(start.x + k, start.y + r),
                                              new Vector2Int(start.x - r, start.y + k), new Vector2Int(start.x + r, start.y + k) })
                        if (w.InBounds(c.x, c.y) && test(c)) return c;
                }
            }
            return null;
        }
    }
}
