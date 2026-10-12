using System;
using System.IO;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Inventory;
using Loamlight.Lighting;
using Loamlight.Player;
using Loamlight.UI;
using Loamlight.World;
using Loamlight.World.Collision;
using Loamlight.World.Generation;
using Loamlight.World.Rendering;
using Loamlight.World.Saving;
using UnityEngine;

namespace Loamlight.Game
{
    /// <summary>What the Game scene should start: a new world (seed, size, name) or a save file.</summary>
    public static class GameSession
    {
        public static string LoadPath;
        public static string WorldName = "New World";
        public static uint Seed = 20261012;
        public static int Width = 4200, Height = 1200;

        public static string SaveFolder => Path.Combine(Application.persistentDataPath, "Worlds");
        public static string PathFor(string worldName)
        {
            var safe = string.Join("_", worldName.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(SaveFolder, safe + WorldSave.Extension);
        }
    }

    /// <summary>
    /// Composition root of the Game scene: builds or loads the world, then creates and wires every system
    /// (chunk rendering, lighting, player, tools, HUD). Systems only know each other through what is passed here.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] Camera cam;

        public static GameBootstrap Current { get; private set; }

        public ContentDatabase Db { get; private set; }
        public WorldData World { get; private set; }
        public WorldRenderData Render { get; private set; }
        public WorldEdits Edits { get; private set; }
        public ChunkRenderer Chunks { get; private set; }
        public LightMapSystem Light { get; private set; }
        public PlayerController Player { get; private set; }
        public ToolUser Tools { get; private set; }
        public ItemContainer Inventory { get; private set; }
        public InputSystemPlayerInput HumanInput { get; private set; }
        public CameraFollow CameraFollow { get; private set; }
        public Hud Hud { get; private set; }
        public Camera Camera => cam;
        public GenReport GenReport { get; private set; }
        public double LoadMs { get; private set; }
        public SaveMeta Meta { get; private set; }
        public bool Ready { get; private set; }

        public event Action WorldReady;

        public void Configure(Camera camera) => cam = camera;

        void Awake() => Current = this;

        void Start()
        {
            ApplyLaunchArgs();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Db = ContentDatabase.Instance;
            Render = new WorldRenderData(Db, WorldAtlas.Load());

            if (!string.IsNullOrEmpty(GameSession.LoadPath) && File.Exists(GameSession.LoadPath))
            {
                World = WorldSave.Load(GameSession.LoadPath, Db, out var meta);
                Meta = meta;
                Debug.Log($"[Game] loaded '{meta.name}' {World.Width}x{World.Height} from {GameSession.LoadPath} in {sw.ElapsedMilliseconds} ms");
            }
            else
            {
                World = WorldGenerator.Generate(GameSession.Width, GameSession.Height, GameSession.Seed, GenIdsFor(Db), GenParams.Default, out var report);
                GenReport = report;
                Meta = new SaveMeta { name = GameSession.WorldName };
                Debug.Log($"[Game] generated {World.Width}x{World.Height} seed {GameSession.Seed}: {report}");
            }
            LoadMs = sw.Elapsed.TotalMilliseconds;

            Edits = new WorldEdits(World, Db, Render.TileProps);
            var grid = new WorldCollisionGrid(World, Render.TileProps);

            Chunks = new GameObject("World Chunks").AddComponent<ChunkRenderer>();
            Chunks.Init(World, Render, cam);

            // player
            Inventory = ItemContainer.ForContent(50, Db);
            HumanInput = new InputSystemPlayerInput(() => cam);
            var playerGo = new GameObject("Player");
            var sr = playerGo.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Entities";
            var anim = playerGo.AddComponent<SpriteAnimator>();
            anim.Configure(SpriteSheetIndex.Get("miner"), "idle");
            Player = playerGo.AddComponent<PlayerController>();
            Player.Grid = grid;
            Player.Input = HumanInput;
            Tools = playerGo.AddComponent<ToolUser>();
            Tools.Input = HumanInput;
            Tools.Player = Player;
            Tools.Edits = Edits;
            Tools.Inventory = Inventory;
            Tools.Db = Db;
            Tools.Drops = new InventoryDropSink(Inventory, Db);
            Tools.Crack = CreateCrackOverlay();
            Tools.CrackFrames = SpriteSheetIndex.Get("cracks")?.Find("crack")?.frames;
            var visual = playerGo.AddComponent<PlayerVisual>();
            visual.Controller = Player;
            visual.Tools = Tools;
            Player.Stepped += ev =>
            {
                if (ev.jumped) Sfx.Play("jump", Player.Feet, 0.6f);
                if (ev.landed && ev.landingSpeed > 12f) Sfx.Play("land", Player.Feet, Mathf.Clamp01(ev.landingSpeed / 40f));
            };

            Player.Teleport(SpawnPoint());
            GiveStartingItems();
            if (!string.IsNullOrEmpty(GameSession.LoadPath)) PlayerState.Restore(this, Meta.gameJson);

            CameraFollow = cam.gameObject.GetComponent<CameraFollow>() ?? cam.gameObject.AddComponent<CameraFollow>();
            CameraFollow.target = Player;
            CameraFollow.worldSize = new Vector2(World.Width, World.Height);
            CameraFollow.SnapToTarget();

            Light = new GameObject("Light Map").AddComponent<LightMapSystem>();
            Light.Init(World, Render.TileProps, cam);

            Hud = Hud.Create(Inventory, Db);
            Chunks.BuildAllVisibleNow();
            Ready = true;
            WorldReady?.Invoke();
        }

        void ApplyLaunchArgs()
        {
            if (LaunchArgs.TryGet("-llSeed", out var seed) && uint.TryParse(seed, out var s)) GameSession.Seed = s;
            if (LaunchArgs.TryGet("-llWorldSize", out var size))
            {
                var parts = size.ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                {
                    GameSession.Width = w;
                    GameSession.Height = h;
                }
            }
            if (LaunchArgs.TryGet("-llLoad", out var load)) GameSession.LoadPath = load;
        }

        public static GenIds GenIdsFor(ContentDatabase db) => new GenIds
        {
            dirt = db.TileId("dirt"), grass = db.TileId("grass"), stone = db.TileId("stone"), sand = db.TileId("sand"),
            clay = db.TileId("clay"), dirtWall = db.WallId("dirt_wall"), stoneWall = db.WallId("stone_wall"),
        };

        SpriteRenderer CreateCrackOverlay()
        {
            var go = new GameObject("Mining crack");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Foreground";
            sr.enabled = false;
            return sr;
        }

        /// <summary>Middle of the world, standing on the surface.</summary>
        public Vector2 SpawnPoint()
        {
            int x = World.Width / 2;
            for (int y = World.Height - 1; y > 0; y--)
            {
                ushort b = World.GetBlock(x, y);
                if (b != 0 && Render.TileProps[b].solid != 0) return new Vector2(x + 0.5f, y + 1);
            }
            return new Vector2(x + 0.5f, World.Height * 0.75f);
        }

        void GiveStartingItems()
        {
            Inventory.Add(Db.ItemId("copper_pickaxe"), 1);
            Inventory.Add(Db.ItemId("torch"), 25);
        }

        public void SaveTo(string path)
        {
            Meta.gameJson = MiniJson.Serialize(PlayerState.Capture(this));
            WorldSave.Save(path, World, Meta, Db);
        }

        void OnDestroy()
        {
            HumanInput?.Dispose();
            World?.Dispose();
            Render?.Dispose();
            if (Current == this) Current = null;
        }
    }

    /// <summary>Player + inventory snapshot stored in the save's game JSON.</summary>
    public static class PlayerState
    {
        public static System.Collections.Generic.Dictionary<string, object> Capture(GameBootstrap g)
        {
            var slots = new System.Collections.Generic.List<object>();
            for (int i = 0; i < g.Inventory.Size; i++)
            {
                var s = g.Inventory[i];
                slots.Add(s.IsEmpty ? "" : $"{g.Db.Item(s.item).id}*{s.count}");
            }
            return new System.Collections.Generic.Dictionary<string, object>
            {
                ["player"] = new System.Collections.Generic.Dictionary<string, object>
                {
                    ["x"] = (double)g.Player.Feet.x, ["y"] = (double)g.Player.Feet.y, ["selected"] = (double)g.Inventory.Selected,
                    ["inventory"] = slots,
                },
            };
        }

        public static void Restore(GameBootstrap g, string json)
        {
            if (string.IsNullOrEmpty(json) || !(MiniJson.Parse(json) is System.Collections.Generic.Dictionary<string, object> root)) return;
            if (!root.TryGetValue("player", out var p) || !(p is System.Collections.Generic.Dictionary<string, object> player)) return;
            g.Player.Teleport(new Vector2((float)Convert.ToDouble(player["x"]), (float)Convert.ToDouble(player["y"])));
            if (player.TryGetValue("inventory", out var inv) && inv is System.Collections.Generic.List<object> slots)
            {
                g.Inventory.Clear();
                for (int i = 0; i < slots.Count && i < g.Inventory.Size; i++)
                {
                    var s = (string)slots[i];
                    if (string.IsNullOrEmpty(s)) continue;
                    var parts = s.Split('*');
                    if (parts.Length == 2 && g.Db.TryItemId(parts[0], out var id) && int.TryParse(parts[1], out var count))
                        g.Inventory.Set(i, new ItemStack(id, count));
                }
            }
            if (player.TryGetValue("selected", out var sel)) g.Inventory.Selected = (int)Convert.ToDouble(sel);
        }
    }
}
