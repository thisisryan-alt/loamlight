using System.Diagnostics;
using Loamlight.Content;
using Loamlight.Game;
using Loamlight.World;
using Loamlight.World.Generation;
using NUnit.Framework;
using Unity.Mathematics;

namespace Loamlight.Tests
{
    public class WorldLayoutTests
    {
        [Test]
        public void IndexAndCoordRoundTrip()
        {
            var l = new WorldLayout(100, 70);
            Assert.That(l.ChunksX, Is.EqualTo(4));
            Assert.That(l.ChunksY, Is.EqualTo(3));
            var seen = new System.Collections.Generic.HashSet<int>();
            for (int y = 0; y < 70; y++)
            for (int x = 0; x < 100; x++)
            {
                int i = l.Index(x, y);
                Assert.That(seen.Add(i), $"index collision at {x},{y}");
                Assert.That(l.Coord(i), Is.EqualTo(new int2(x, y)));
                Assert.That(i, Is.LessThan(l.StorageLength));
            }
        }

        [Test]
        public void ChunksAreContiguousBlocksOf1024()
        {
            var l = new WorldLayout(64, 64);
            Assert.That(l.Index(0, 0), Is.EqualTo(0));
            Assert.That(l.Index(31, 31), Is.EqualTo(1023));
            Assert.That(l.Index(32, 0), Is.EqualTo(1024));     // next chunk to the right
            Assert.That(l.Index(0, 32), Is.EqualTo(2048));     // chunk row above
        }

        [Test]
        public void EditsMarkOwnAndNeighbourChunksDirty()
        {
            using var w = new WorldData(96, 64, 1);
            for (int i = 0; i < w.ChunkFlags.Length; i++) w.ChunkFlags[i] = 0;
            w.SetBlock(32, 10, 1);   // left edge of chunk 1 -> chunk 0 reads it for autotiling
            Assert.That(w.ChunkFlags[1] & WorldData.FlagMeshDirty, Is.Not.Zero);
            Assert.That(w.ChunkFlags[0] & WorldData.FlagMeshDirty, Is.Not.Zero);
            Assert.That(w.ChunkFlags[2] & WorldData.FlagMeshDirty, Is.Zero);
        }
    }

    public class WorldGenTests
    {
        static GenIds Ids => GameBootstrap.GenIdsFor(ContentDatabase.Instance);

        [Test]
        public void SameSeedGivesIdenticalWorld()
        {
            using var a = WorldGenerator.Generate(512, 256, 42, Ids, GenParams.Default, out _);
            using var b = WorldGenerator.Generate(512, 256, 42, Ids, GenParams.Default, out _);
            Assert.That(WorldHash.Compute(a), Is.EqualTo(WorldHash.Compute(b)));
        }

        [Test]
        public void DifferentSeedsDiffer()
        {
            using var a = WorldGenerator.Generate(512, 256, 1, Ids, GenParams.Default, out _);
            using var b = WorldGenerator.Generate(512, 256, 2, Ids, GenParams.Default, out _);
            Assert.That(WorldHash.Compute(a), Is.Not.EqualTo(WorldHash.Compute(b)));
        }

        [Test]
        public void LayersSkyDirtStoneAndCaves()
        {
            var ids = Ids;
            using var w = WorldGenerator.Generate(1024, 600, 7, ids, GenParams.Default, out _);
            int sky = 0, grass = 0, stone = 0, open = 0, deep = 0;
            for (int x = 0; x < w.Width; x++)
            {
                if (w.GetBlock(x, w.Height - 1) == 0) sky++;
                for (int y = 0; y < w.Height; y++)
                {
                    ushort b = w.GetBlock(x, y);
                    if (b == ids.grass) grass++;
                    if (y < w.Height * 0.5f)
                    {
                        deep++;
                        if (b == ids.stone) stone++;
                        if (b == 0) open++;
                    }
                }
            }
            Assert.That(sky, Is.EqualTo(w.Width), "top row is open sky");
            Assert.That(grass, Is.GreaterThan(w.Width / 2), "most columns have grass on top");
            Assert.That((float)stone / deep, Is.GreaterThan(0.4f), "deep layers are mostly stone");
            float caves = (float)open / deep;
            Assert.That(caves, Is.InRange(0.05f, 0.5f), $"cave fraction {caves:P0} underground");
        }

        [Test]
        public void FullSizeWorldGeneratesUnder15Seconds()
        {
            var sw = Stopwatch.StartNew();
            using var w = WorldGenerator.Generate(4200, 1200, 20261012, Ids, GenParams.Default, out var report);
            sw.Stop();
            UnityEngine.Debug.Log($"[WorldGenTests] 4200x1200: {report} (wall {sw.ElapsedMilliseconds} ms), hash {WorldHash.Compute(w):x16}");
            Assert.That(sw.Elapsed.TotalSeconds, Is.LessThan(15));
        }

        [Test]
        public void BurstIsCompilingJobsInTheEditor() => Assert.That(BurstProbe.IsBurstCompiled(), Is.True);
    }
}
