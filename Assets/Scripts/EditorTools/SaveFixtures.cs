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
            File.WriteAllText($"{Folder}/world_v{WorldSave.Version}.hash.txt", WorldHash.Compute(w).ToString("x16") + "\n");
            AssetDatabase.Refresh();
            Debug.Log($"[SaveFixtures] wrote {path}");
        }
    }
}
