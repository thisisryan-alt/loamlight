using System.Collections;
using System.Diagnostics;
using Loamlight.Content;
using Loamlight.Game;
using Loamlight.World;
using Loamlight.World.Generation;
using Loamlight.World.Rendering;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.Tilemaps;

namespace Loamlight.Tests
{
    public class GameSceneTests
    {
        [UnityTest]
        public IEnumerator GameSceneBootsAndPlayerStands()
        {
            GameSession.Width = 512;
            GameSession.Height = 400;
            GameSession.LoadPath = null;
            yield return SceneManager.LoadSceneAsync("Game");
            float until = Time.realtimeSinceStartup + 10f;
            while ((GameBootstrap.Current == null || !GameBootstrap.Current.Ready) && Time.realtimeSinceStartup < until) yield return null;
            var g = GameBootstrap.Current;
            Assert.That(g != null && g.Ready, "game bootstrapped");
            Assert.That(g.Chunks.ActiveChunks, Is.GreaterThan(0), "chunks streamed in");
            yield return new WaitForSeconds(0.5f);
            Assert.That(g.Player.Grounded, Is.True, "player stands on generated ground");
            Assert.That(g.Inventory.Count(g.Db.ItemId("copper_pickaxe")), Is.EqualTo(1));
            Assert.That(g.Light.MapLight.lightCookieSprite, Is.Not.Null, "light map cookie assigned");
            GameSession.Width = 4200;
            GameSession.Height = 1200;
        }
    }

    /// <summary>
    /// The brief: "a generated mesh per chunk (or a Tilemap, if profiling shows it keeps up with frequent edits)".
    /// Measures rebuilding 16 dirty chunks both ways; the numbers are logged and recorded in DESIGN.md.
    /// </summary>
    public class ChunkRenderBenchmark
    {
        [UnityTest]
        public IEnumerator MeshRebuildVersusTilemap()
        {
            var db = ContentDatabase.Instance;
            using var world = WorldGenerator.Generate(256, 256, 3, GameBootstrap.GenIdsFor(db), GenParams.Default, out _);
            using var render = new WorldRenderData(db, WorldAtlas.Load());
            var layout = ChunkMeshLayout.Create(Allocator.Persistent);
            var chunks = new NativeArray<int>(16, Allocator.Persistent);
            for (int i = 0; i < 16; i++) chunks[i] = i + 8;
            var meshes = new Mesh[16 * ChunkMeshJob.Layers];
            for (int i = 0; i < meshes.Length; i++) meshes[i] = new Mesh();

            // warm up both paths once
            RunMesh(world, render, layout, chunks, meshes);
            var tilemap = new GameObject("Grid", typeof(Grid)).AddComponent<Tilemap>();
            tilemap.gameObject.AddComponent<TilemapRenderer>();
            var tiles = new TileBase[1024];
            var tileAssets = new Tile[8];
            for (int i = 0; i < tileAssets.Length; i++) { tileAssets[i] = ScriptableObject.CreateInstance<Tile>(); }
            RunTilemap(world, tilemap, tiles, tileAssets);

            const int reps = 20;
            var sw = Stopwatch.StartNew();
            for (int r = 0; r < reps; r++) RunMesh(world, render, layout, chunks, meshes);
            double meshMs = sw.Elapsed.TotalMilliseconds / reps;
            sw.Restart();
            for (int r = 0; r < reps; r++) RunTilemap(world, tilemap, tiles, tileAssets);
            double tilemapMs = sw.Elapsed.TotalMilliseconds / reps;
            UnityEngine.Debug.Log($"[ChunkRenderBenchmark] rebuild 16 chunks: mesh job {meshMs:0.00} ms, Tilemap.SetTilesBlock {tilemapMs:0.00} ms");

            chunks.Dispose();
            layout.Dispose();
            foreach (var m in meshes) Object.Destroy(m);
            Object.Destroy(tilemap.transform.parent != null ? tilemap.transform.parent.gameObject : tilemap.gameObject);
            Assert.That(meshMs, Is.LessThan(8.0), "mesh rebuild of 16 chunks fits comfortably in a frame");
            yield return null;
        }

        static void RunMesh(WorldData world, WorldRenderData render, NativeArray<UnityEngine.Rendering.VertexAttributeDescriptor> layout,
            NativeArray<int> chunks, Mesh[] meshes)
        {
            var data = Mesh.AllocateWritableMeshData(chunks.Length * ChunkMeshJob.Layers);
            new ChunkMeshJob
            {
                layout = world.Layout, chunks = chunks, blocks = world.Blocks, walls = world.Walls, shapes = world.Shapes,
                frames = world.Frames, tileInfo = render.Tiles, wallInfo = render.Walls, tileProps = render.TileProps, uvs = render.Uvs,
                vertexLayout = layout, meshes = data, wallTint = new Color32(150, 150, 160, 255),
                liquid = world.Liquid, liquidKind = world.LiquidKind, waterFrame = render.WaterFrame, lavaFrame = render.LavaFrame,
            }.Schedule(chunks.Length * ChunkMeshJob.Layers, 1).Complete();
            Mesh.ApplyAndDisposeWritableMeshData(data, meshes);
        }

        static void RunTilemap(WorldData world, Tilemap map, TileBase[] tiles, Tile[] assets)
        {
            for (int c = 0; c < 16; c++)
            {
                int chunk = c + 8, cx = chunk % world.Layout.ChunksX, cy = chunk / world.Layout.ChunksX;
                for (int i = 0; i < 1024; i++)
                {
                    ushort b = world.Blocks[(chunk << 10) | i];
                    tiles[i] = b == 0 ? null : assets[b % assets.Length];
                }
                map.SetTilesBlock(new BoundsInt(cx * 32, cy * 32, 0, 32, 32, 1), tiles);
            }
        }
    }
}
