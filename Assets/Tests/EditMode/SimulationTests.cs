using System.Linq;
using Loamlight.Content;
using Loamlight.Lighting;
using Loamlight.Liquids;
using Loamlight.Player;
using Loamlight.World;
using Loamlight.World.Collision;
using Loamlight.World.Generation;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace Loamlight.Tests
{
    /// <summary>Pillar 2 (liquids, falling blocks, spread) on small hand-built worlds, plus the day curve and swimming.</summary>
    public class SimulationTests
    {
        ContentDatabase _db;
        NativeArray<TileProps> _props;
        WorldData _w;
        SimulationRunner _sim;
        ushort _stone, _sand, _dirt, _grass, _obsidian;

        [SetUp]
        public void SetUp()
        {
            _db = ContentDatabase.Instance;
            _props = new NativeArray<TileProps>(_db.BuildTileProps(), Allocator.Persistent);
            _stone = _db.TileId("stone");
            _sand = _db.TileId("sand");
            _dirt = _db.TileId("dirt");
            _grass = _db.TileId("grass");
            _obsidian = _db.TileId("obsidian");
            _w = new WorldData(64, 64, 1);
            _sim = new GameObject("sim").AddComponent<SimulationRunner>();
            _sim.Init(_w, _props, _db, null);
        }

        [TearDown]
        public void TearDown()
        {
            _sim.Dispose();   // edit mode never ran Awake, so OnDestroy will not fire
            Object.DestroyImmediate(_sim.gameObject);
            _w.Dispose();
            _props.Dispose();
        }

        void Set(int x, int y, ushort id) => _w.Blocks[_w.Index(x, y)] = id;
        ushort Get(int x, int y) => _w.Blocks[_w.Index(x, y)];
        SimRegion All => new SimRegion { x0 = 0, x1 = _w.Width, y0 = 0, y1 = _w.Height };

        /// <summary>A stone cup: floor at y = 10 from x0..x1, walls up to y = 30.</summary>
        void Cup(int x0, int x1)
        {
            for (int x = x0; x <= x1; x++) Set(x, 10, _stone);
            for (int y = 10; y <= 30; y++) { Set(x0, y, _stone); Set(x1, y, _stone); }
        }

        void Wake() => _w.MarkAllChunks(WorldData.FlagLiquidActive | WorldData.FlagSimActive);

        [Test]
        public void WaterLevelsOutAndIsConserved()
        {
            Cup(20, 27);
            for (int y = 11; y < 21; y++) _w.SetLiquid(21, y, 255, LiquidType.Water);   // a 10-tall column at the left
            long before = _sim.TotalLiquid(All, LiquidType.Water);
            Wake();
            for (int i = 0; i < 400; i++) _sim.TickLiquids(All);
            long after = _sim.TotalLiquid(All, LiquidType.Water);
            Assert.That(after, Is.InRange(before * 0.97, before), "liquid is conserved (only thin films may evaporate)");
            // levelled: the bottom row is full everywhere inside the cup, the column no longer towers
            for (int x = 21; x <= 26; x++) Assert.That(_w.GetLiquid(x, 11), Is.GreaterThan(200), $"bottom row filled at x={x}");
            Assert.That(_w.GetLiquid(21, 15), Is.EqualTo(0), "the original column has spread out");
        }

        [Test]
        public void WaterMeetingLavaMakesObsidian()
        {
            Cup(20, 27);
            for (int x = 21; x <= 26; x++) _w.SetLiquid(x, 11, 255, LiquidType.Lava);
            for (int x = 21; x <= 26; x++) _w.SetLiquid(x, 14, 255, LiquidType.Water);
            Wake();
            for (int i = 0; i < 60; i++) _sim.TickLiquids(All);
            int obsidian = 0;
            for (int y = 11; y < 16; y++)
            for (int x = 21; x <= 26; x++)
                if (Get(x, y) == _obsidian) obsidian++;
            Assert.That(obsidian, Is.GreaterThan(0));
            Assert.That(_sim.Reactions, Is.GreaterThan(0));
        }

        [Test]
        public void SandFallsOntoTheFloor()
        {
            for (int x = 10; x < 20; x++) Set(x, 5, _stone);
            Set(14, 20, _sand);
            Set(14, 21, _sand);
            Wake();
            for (int i = 0; i < 40; i++) _sim.TickFalling(All);
            Assert.That(Get(14, 6), Is.EqualTo(_sand));
            Assert.That(Get(14, 7), Is.EqualTo(_sand));
            Assert.That(Get(14, 20), Is.EqualTo(0));
            Assert.That(_sim.BlocksFallen, Is.GreaterThan(0));
        }

        [Test]
        public void GrassSpreadsOverExposedDirt()
        {
            for (int x = 0; x < 16; x++) Set(x, 8, x == 0 ? _grass : _dirt);
            var r = new SimRegion { x0 = 0, x1 = 16, y0 = 0, y1 = 16 };
            for (int i = 0; i < 300; i++) _sim.TickSpread(r);
            int grass = Enumerable.Range(0, 16).Count(x => Get(x, 8) == _grass);
            Assert.That(grass, Is.GreaterThan(3), "grass crept along the dirt");
        }

        [Test]
        public void BuriedGrassIsNotSpreadTarget()
        {
            for (int x = 0; x < 16; x++) { Set(x, 8, x == 0 ? _grass : _dirt); Set(x, 9, x == 0 ? (ushort)0 : _stone); }
            var r = new SimRegion { x0 = 0, x1 = 16, y0 = 0, y1 = 16 };
            for (int i = 0; i < 200; i++) _sim.TickSpread(r);
            Assert.That(Enumerable.Range(2, 14).Count(x => Get(x, 8) == _grass), Is.EqualTo(0), "covered dirt stays dirt");
        }
    }

    public class DayNightTests
    {
        [Test]
        public void NoonIsBrightAndMidnightIsDark()
        {
            Assert.That(DayNightCycle.Sample(0.3f).intensity, Is.GreaterThan(0.95f));
            Assert.That(DayNightCycle.Sample(0.8f).intensity, Is.LessThan(0.25f));
            Assert.That(DayNightCycle.Sample(0.8f).night, Is.EqualTo(1f).Within(1e-4));
            Assert.That(DayNightCycle.Sample(0.3f).night, Is.EqualTo(0f).Within(1e-4));
        }

        [Test]
        public void CycleIsContinuousAcrossMidnightWrap()
        {
            var a = DayNightCycle.Sample(0.99999f);
            var b = DayNightCycle.Sample(0f);
            Assert.That(a.intensity, Is.EqualTo(b.intensity).Within(0.01f));
            Assert.That(((Vector4)a.top - (Vector4)b.top).magnitude, Is.LessThan(0.02f));
            // no jumps anywhere along the loop
            float prev = DayNightCycle.Sample(0f).intensity;
            for (int i = 1; i <= 1000; i++)
            {
                float v = DayNightCycle.Sample(i / 1000f).intensity;
                Assert.That(Mathf.Abs(v - prev), Is.LessThan(0.05f), $"step at t={i / 1000f}");
                prev = v;
            }
        }
    }

    public class SwimTests
    {
        [Test]
        public void SubmergedBodiesSinkSlowlyAndSwimUp()
        {
            var p = MotorParams.Default;
            var dry = MotorState.At(new float2(5f, 50f), PlayerController.Size);
            var wet = dry;
            var grid = new FakeGrid();
            for (int i = 0; i < 60; i++)
            {
                PlayerMotor.Step(ref dry, new MotorInput(), p, grid, 1f / 60f);
                PlayerMotor.Step(ref wet, new MotorInput { submerged = 1f }, p, grid, 1f / 60f);
            }
            Assert.That(-wet.body.velocity.y, Is.LessThanOrEqualTo(p.swimMaxFall + 0.01f), "terminal sink speed in water");
            Assert.That(wet.body.position.y, Is.GreaterThan(dry.body.position.y + 10f), "water slows the fall");
            for (int i = 0; i < 30; i++) PlayerMotor.Step(ref wet, new MotorInput { submerged = 1f, jumpHeld = true }, p, grid, 1f / 60f);
            Assert.That(wet.body.velocity.y, Is.GreaterThan(0f), "holding jump swims upward");
        }

        [Test]
        public void SubmersionMeasuresTheWetFractionOfTheBody()
        {
            var w = new WorldData(16, 16, 1);
            try
            {
                using var props = new NativeArray<TileProps>(ContentDatabase.Instance.BuildTileProps(), Allocator.Temp);
                var grid = new WorldCollisionGrid(w, props);
                w.SetLiquid(5, 3, 255, LiquidType.Water);
                w.SetLiquid(5, 4, 128, LiquidType.Water);
                var body = new Body { position = new float2(5.5f, 3f), size = new float2(1.25f, 2.6f) };
                float wet = TileCollision.Submersion(body, grid, out var type);
                Assert.That(type, Is.EqualTo(LiquidType.Water));
                Assert.That(wet, Is.EqualTo((1f + 128f / 255f) / 2.6f).Within(0.01f));
            }
            finally { w.Dispose(); }
        }
    }

    public class BiomeGenTests
    {
        [Test]
        public void OreSpawnRulesComeFromContentData()
        {
            var db = ContentDatabase.Instance;
            var ids = GenIds.From(db);
            int withOre = db.tiles.Count(t => t.ore.enabled);
            Assert.That(withOre, Is.GreaterThanOrEqualTo(4), "copper, iron, lumite, cinderite carry ore rules in tiles.json");
            Assert.That(ids.ores.Length, Is.EqualTo(withOre));
            for (int i = 0; i < ids.ores.Length; i++)
            {
                Assert.That(ids.ores[i].host0, Is.Not.EqualTo(0));
                Assert.That(ids.ores[i].depthMax, Is.GreaterThan(ids.ores[i].depthMin));
            }
        }

        [Test]
        public void EveryBiomeLiquidAndOreTierAppears()
        {
            var db = ContentDatabase.Instance;
            var ids = GenIds.From(db);
            using var w = WorldGenerator.Generate(2100, 800, 4242, ids, GenParams.Default, out var report);
            var count = new System.Collections.Generic.Dictionary<ushort, int>();
            int water = 0, lava = 0;
            for (int y = 0; y < w.Height; y++)
            for (int x = 0; x < w.Width; x++)
            {
                int i = w.Index(x, y);
                ushort b = w.Blocks[i];
                count[b] = count.TryGetValue(b, out var c) ? c + 1 : 1;
                if (w.Liquid[i] > 0) { if (w.LiquidKind[i] == (byte)LiquidType.Water) water++; else lava++; }
            }
            int N(string id) => count.TryGetValue(db.TileId(id), out var c) ? c : 0;
            foreach (var id in new[] { "grass", "sand", "sandstone", "snow", "ice", "mossy_stone", "glowcap", "ash", "basalt",
                                       "copper_ore", "iron_ore", "lumite_ore", "cinderite_ore", "cactus", "pine_top", "tree_top", "gravel" })
                Assert.That(N(id), Is.GreaterThan(0), $"{id} generated");
            Assert.That(water, Is.GreaterThan(500), "water pools and lakes");
            Assert.That(lava, Is.GreaterThan(500), "lava pools");
            Assert.That(report.WaterPools + report.Lakes, Is.GreaterThan(10));
            Debug.Log($"[BiomeGenTests] {report}");
        }

        [Test]
        public void SurfaceBiomesMatchTheGenerator()
        {
            var a = WorldGenerator.SurfaceBiomes(1000, 400, 9, GenParams.Default);
            var b = WorldGenerator.SurfaceBiomes(1000, 400, 9, GenParams.Default);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a.Distinct().Count(), Is.EqualTo(3), "meadow, dunes and frost all on the surface");
            Assert.That(a[500], Is.EqualTo(Biome.Meadow), "spawn is in the meadow");
        }
    }
}
