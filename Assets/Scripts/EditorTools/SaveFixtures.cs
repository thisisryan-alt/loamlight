using System.IO;
using Loamlight.Content;
using Loamlight.Game;
using Loamlight.World.Generation;
using Loamlight.World.Saving;
using UnityEditor;
using UnityEngine;

namespace Loamlight.EditorTools
{
    /// <summary>
    /// Writes the per-version save fixtures that tests load forever after. Run once when a save version is
    /// introduced, then commit the file; never regenerate an old version's fixture.
    /// </summary>
    public static class SaveFixtures
    {
        public const string Folder = "Assets/Tests/Fixtures";

        /// <summary>Records the content-independent name hash of a fixture (derived from the file alone).</summary>
        public static void WriteNameHash(string path)
        {
            using var raw = WorldSave.Load(path, ContentDatabase.Instance, out _, out var tiles, out var walls, false);
            ulong h = 14695981039346656037UL;
            void Mix(string s) { foreach (char c in s) { h ^= c; h *= 1099511628211UL; } h ^= 0xff; h *= 1099511628211UL; }
            for (int y = 0; y < raw.Height; y++)
            for (int x = 0; x < raw.Width; x++)
            {
                Mix(tiles[raw.GetBlock(x, y)]);
                Mix(walls[raw.GetWall(x, y)]);
            }
            File.WriteAllText(Path.ChangeExtension(path, null) + ".hash.txt", h.ToString("x16") + "\n");
        }

        [MenuItem("Loamlight/Tests/Rehash Save Fixtures (name hash)")]
        public static void RehashAll()
        {
            foreach (var f in Directory.GetFiles(Folder, "*.lmw")) WriteNameHash(f.Replace('\\', '/'));
            AssetDatabase.Refresh();
        }

        [MenuItem("Loamlight/Tests/Write Save Fixture (current version)")]
        public static void WriteCurrent()
        {
            var db = ContentDatabase.Instance;
            var path = $"{Folder}/world_v{WorldSave.Version}.lmw";
            if (File.Exists(path))
            {
                Debug.Log($"[SaveFixtures] {path} already exists; fixtures are write-once");
                return;
            }
            Directory.CreateDirectory(Folder);
            using var w = WorldGenerator.Generate(128, 128, 7, GameBootstrap.GenIdsFor(db), GenParams.Default, out _);
            w.SetBlock(64, 2, db.TileId("stone"));
            w.SetBlock(65, 100, db.TileId("torch"));
            WorldSave.Save(path, w, new SaveMeta
            {
                name = $"Fixture v{WorldSave.Version}",
                createdUtc = "2026-10-12T00:00:00.0000000Z",
                gameJson = "{\"player\":{\"x\":64.5,\"y\":100,\"selected\":0,\"inventory\":[\"copper_pickaxe*1\",\"torch*10\"]}}",
            }, db);
            WriteNameHash(path);
            AssetDatabase.Refresh();
            Debug.Log($"[SaveFixtures] wrote {path}");
        }
    }
}
