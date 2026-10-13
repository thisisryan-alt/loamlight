using System;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using Loamlight.AI;
using Loamlight.Combat;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.Inventory;
using Loamlight.Lighting;
using Loamlight.Liquids;
using Loamlight.Player;
using Loamlight.UI;
using Loamlight.World;
using Loamlight.World.Collision;
using Loamlight.World.Generation;
using Loamlight.World.Rendering;
using Loamlight.World.Saving;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;

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
    /// (chunk rendering, lighting, player, tools, pickups, interaction, UI). Systems only know each other through
    /// what is passed here.
    /// </summary>
    public sealed class GameBootstrap : MonoBehaviour
    {
        [SerializeField] Camera cam;

        public static GameBootstrap Current { get; private set; }

        public ContentDatabase Db { get; private set; }
        public WorldData World { get; private set; }
        public WorldRenderData Render { get; private set; }
        public WorldEdits Edits { get; private set; }
        public WorldCollisionGrid Grid { get; private set; }
        public ChunkRenderer Chunks { get; private set; }
        public LightMapSystem Light { get; private set; }
        public SimulationRunner Simulation { get; private set; }
        public DayNightCycle DayNight { get; private set; }
        public PlayerHealth Health { get; private set; }
        public WeaponUser Weapons { get; private set; }
        public ProjectileSystem Projectiles { get; private set; }
        public EnemySpawner Spawner { get; private set; }
        public CombatFx Fx { get; private set; }
        public HealthHud HealthUi { get; private set; }
        public BiomeSensor Biomes { get; private set; }
        /// <summary>Peaceful: no spawns and nothing hurts the player (older scenarios, perf runs, -llPeaceful).</summary>
        public bool Peaceful { get; private set; }
        public int Kills { get; private set; }
        public int Deaths { get; private set; }
        float _brewReady;
        public SkyBackdrop Sky { get; private set; }
        public PlayerController Player { get; private set; }
        public ToolUser Tools { get; private set; }
        public Interactor Interactor { get; private set; }
        public PickupSystem Pickups { get; private set; }
        public ItemContainer Inventory { get; private set; }
        public ChestStore Chests { get; private set; }
        public InputSystemPlayerInput HumanInput { get; private set; }
        public CameraFollow CameraFollow { get; private set; }
        public Hud Hud { get; private set; }
        public InventoryScreen InventoryUi { get; private set; }
        public Camera Camera => cam;
        public GenReport GenReport { get; private set; }
        public double LoadMs { get; private set; }
        public SaveMeta Meta { get; private set; }
        public Vector2 SpawnPointWorld { get; set; }
        public bool HasBedSpawn { get; set; }
        public bool Ready { get; private set; }

        public event Action WorldReady;

        public void Configure(Camera camera) => cam = camera;

        void Awake() => Current = this;

        void Start()
        {
            ApplyLaunchArgs();
            // static combat state must not leak between scene loads
            GameClock.Reset();
            CombatRegistry.Clear();
            CombatEvents.ClearListeners();
            Enemy.Live.Clear();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Db = ContentDatabase.Instance;
            var atlas = WorldAtlas.Load();
            Render = new WorldRenderData(Db, atlas);

            if (!string.IsNullOrEmpty(GameSession.LoadPath) && File.Exists(GameSession.LoadPath))
            {
                World = WorldSave.Load(GameSession.LoadPath, Db, out var meta);
                Meta = meta;
                Debug.Log($"[Game] loaded '{meta.name}' {World.Width}x{World.Height} from {GameSession.LoadPath} in {sw.ElapsedMilliseconds} ms");
            }
            else
            {
                GameSession.LoadPath = null;
                World = WorldGenerator.Generate(GameSession.Width, GameSession.Height, GameSession.Seed, GenIdsFor(Db), GenParams.Default, out var report);
                GenReport = report;
                Meta = new SaveMeta { name = GameSession.WorldName };
                Debug.Log($"[Game] generated {World.Width}x{World.Height} seed {GameSession.Seed}: {report}");
            }
            LoadMs = sw.Elapsed.TotalMilliseconds;

            Edits = new WorldEdits(World, Db, Render.TileProps);
            Grid = new WorldCollisionGrid(World, Render.TileProps);
            Chests = new ChestStore(Db);

            Chunks = new GameObject("World Chunks").AddComponent<ChunkRenderer>();
            Chunks.Init(World, Render, cam);

            // player
            Inventory = ItemContainer.ForContent(50, Db);
            HumanInput = new InputSystemPlayerInput(() => cam);
            var playerGo = new GameObject("Player");
            var sr = playerGo.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Entities";
            sr.sortingOrder = 10;
            playerGo.AddComponent<SpriteAnimator>().Configure(SpriteSheetIndex.Get("miner"), "idle");
            Player = playerGo.AddComponent<PlayerController>();
            Player.Grid = Grid;
            Player.Liquids = Grid;
            Player.Input = HumanInput;

            Pickups = new GameObject("Pickups").AddComponent<PickupSystem>();
            Pickups.Db = Db;
            Pickups.Grid = Grid;
            Pickups.Player = Player;
            Pickups.Inventory = Inventory;

            Tools = playerGo.AddComponent<ToolUser>();
            Tools.Input = HumanInput;
            Tools.Player = Player;
            Tools.Edits = Edits;
            Tools.Inventory = Inventory;
            Tools.Db = Db;
            Tools.Drops = Pickups;
            Tools.Crack = CreateOverlay("Mining crack");
            Tools.CrackFrames = SpriteSheetIndex.Get("cracks")?.Find("crack")?.frames;
            Tools.Atlas = new AtlasSprites(atlas, Db);
            Tools.GhostMaterial = Resources.Load<Material>("Materials/SpriteUnlit");
            Tools.CanRemove = origin =>
            {
                var def = Db.Tile(World.GetBlock(origin.x, origin.y));
                return def == null || def.interact != "chest" || Chests.IsEmpty(origin);
            };
            Tools.Mined += (tile, id) =>
            {
                if (Db.Tile(id)?.interact == "chest") Chests.Remove(tile);
            };

            Interactor = playerGo.AddComponent<Interactor>();
            Interactor.Input = HumanInput;
            Interactor.Player = Player;
            Interactor.Edits = Edits;
            Interactor.Db = Db;

            Health = playerGo.AddComponent<PlayerHealth>();
            Health.Init(Player, sr);
            Weapons = playerGo.AddComponent<WeaponUser>();
            Weapons.Player = Player;
            Weapons.Db = Db;
            Weapons.Inventory = Inventory;
            Weapons.HeldSprite = item => SpriteSheetIndex.Frame("held_" + item.id, "all") ?? item.icon;
            Tools.Weapons = Weapons;
            Tools.Consume = UseConsumable;

            var visual = playerGo.AddComponent<PlayerVisual>();
            visual.Controller = Player;
            visual.Tools = Tools;
            Player.Stepped += ev =>
            {
                if (ev.jumped) Sfx.Play("jump", Player.Feet, 0.6f);
                if (ev.landed && ev.landingSpeed > 12f) Sfx.Play("land", Player.Feet, Mathf.Clamp01(ev.landingSpeed / 40f));
            };

            SpawnPointWorld = SpawnPoint();
            Player.Teleport(SpawnPointWorld);
            GiveStartingItems();
            if (!string.IsNullOrEmpty(GameSession.LoadPath)) GameState.Restore(this, Meta.gameJson);

            CameraFollow = cam.gameObject.GetComponent<CameraFollow>() ?? cam.gameObject.AddComponent<CameraFollow>();
            CameraFollow.target = Player;
            CameraFollow.worldSize = new Vector2(World.Width, World.Height);
            CameraFollow.SnapToTarget();

            Light = new GameObject("Light Map").AddComponent<LightMapSystem>();
            Light.Init(World, Render.TileProps, cam);
            Simulation = new GameObject("Simulation").AddComponent<SimulationRunner>();
            Simulation.Init(World, Render.TileProps, Db, cam);
            Simulation.Reacted += n => Sfx.Play("sizzle", cam.transform.position, Mathf.Clamp01(0.25f + n * 0.05f));
            // the miner's headlamp: a small warm light that matters at night and underground
            var lamp = LightMapSystem.CreatePointLight("Headlamp", playerGo.transform, new Color(1f, 0.86f, 0.62f), 6.5f, 0.75f);
            lamp.transform.localPosition = new Vector3(0.4f, 2.1f, 0f);
            DayNight = new GameObject("Day Night").AddComponent<DayNightCycle>();
            DayNight.Light = Light;
            if (!string.IsNullOrEmpty(GameSession.LoadPath)) GameState.RestoreTime(this, Meta.gameJson);
            DayNight.SetTime(DayNight.time);
            float surfaceY = WorldGenerator.SurfaceLevel(World.Height, GenParams.Default);
            Sky = SkyBackdrop.Create(cam, DayNight, surfaceY);

            // combat
            Projectiles = new GameObject("Projectiles").AddComponent<ProjectileSystem>();
            Projectiles.Grid = Grid;
            Projectiles.MakeLight = c => LightMapSystem.CreatePointLight("Projectile light", Projectiles.transform, c, 5f, 0.9f);
            RegisterProjectiles();
            Weapons.FireProjectile = (id, pos, vel, dmg, kb, w) => Projectiles.Fire(id, pos, vel, dmg, kb, Team.Player);
            Enemy.ProjectileRequested += OnEnemyProjectile;
            Enemy.Died += OnEnemyDied;
            Fx = CombatFx.Create();
            Projectiles.Impact += (spec, at, exploded) => { if (exploded) Fx.Explosion(at); };
            CombatEvents.Shake += t => CameraFollow.AddTrauma(t);
            Biomes = new BiomeSensor(World, Db, surfaceY);
            Spawner = new GameObject("Enemy Spawner").AddComponent<EnemySpawner>();
            Spawner.Db = Db;
            Spawner.World = World;
            Spawner.Grid = Grid;
            Spawner.Liquids = Grid;
            Spawner.Cam = cam;
            Spawner.PlayerFeet = () => Player.Feet;
            Spawner.IsNight = () => DayNight.IsNight;
            Spawner.BiomeAt = Biomes.At;
            Spawner.SurfaceY = surfaceY;
            Health.Died += _ => { Deaths++; StartCoroutine(RespawnAfter(4f)); };
            SetPeaceful(LaunchArgs.Has("-llPeaceful"));
            Player.Splashed += (under, kind) =>
            {
                if (kind == LiquidType.Lava) Sfx.Play("sizzle", Player.Center, 0.6f);
                else Sfx.Play("splash", Player.Feet, under ? 0.7f : 0.45f);
            };

            // UI
            if (FindAnyObjectByType<EventSystem>() == null)
            {
                var es = new GameObject("EventSystem");
                es.AddComponent<EventSystem>();
                es.AddComponent<InputSystemUIInputModule>();
            }
            Hud = Hud.Create(Inventory, Db);
            HealthUi = HealthHud.Create(Hud.transform, Health);
            InventoryUi = InventoryScreen.Create(Inventory, Db, Hud);
            InventoryUi.NearbyStations = () => Edits.StationsNear(Player.Center, 4);
            InventoryUi.PlayerPosition = () => Player.Center;
            InventoryUi.ChestFor = origin => Chests.Get(origin);
            InventoryUi.DropOverflow = s => Pickups.Spawn(s.item, s.count, Player.Center, new Vector2(Player.Facing * 4f, 4f));
            InventoryUi.OpenChanged += open => Tools.Enabled = !open;
            Interactor.ChestOpened += origin => InventoryUi.OpenChest(origin);
            Interactor.SpawnSet += origin =>
            {
                SpawnPointWorld = new Vector2(origin.x + 2f, origin.y);
                HasBedSpawn = true;
                Hud.Toast("Spawn point set");
            };

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

        public static GenIds GenIdsFor(ContentDatabase db) => GenIds.From(db);

        SpriteRenderer CreateOverlay(string name)
        {
            var go = new GameObject(name);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Foreground";
            sr.enabled = false;
            return sr;
        }

        /// <summary>Middle of the world, standing on solid surface ground with head room (not on a tree).</summary>
        public Vector2 SpawnPoint()
        {
            for (int dx = 0; dx < 200; dx++)
            {
                int x = World.Width / 2 + (dx % 2 == 0 ? dx / 2 : -(dx / 2 + 1));
                for (int y = World.Height - 1; y > 0; y--)
                {
                    ushort b = World.GetBlock(x, y);
                    if (b == 0) continue;
                    if (Render.TileProps[b].solid == 0) break;   // tree / decor column: try the next one
                    if (TileCollision.Free(Grid, x + 0.5f - 0.7f, x + 0.5f + 0.7f, y + 1, y + 4)) return new Vector2(x + 0.5f, y + 1);
                    break;
                }
            }
            return new Vector2(World.Width / 2 + 0.5f, World.Height * 0.75f);
        }

        public void SetPeaceful(bool peaceful)
        {
            Peaceful = peaceful;
            if (Spawner != null) Spawner.Active = !peaceful;
            if (Health != null) Health.Vulnerable = !peaceful;
        }

        void RegisterProjectiles()
        {
            Projectiles.Register(new ProjectileSpec
            {
                id = "arrow", sprite = SpriteSheetIndex.Frame("projectiles", "arrow"), gravity = 22f, life = 3f, radius = 0.3f, impactSound = "hit",
            });
            Projectiles.Register(new ProjectileSpec
            {
                id = "fireball", sprite = SpriteSheetIndex.Frame("projectiles", "fireball"), gravity = 0f, life = 2.2f, radius = 0.35f,
                explodeRadius = 2.2f, glow = new Color(1f, 0.55f, 0.2f, 1f),
            });
            Projectiles.Register(new ProjectileSpec
            {
                id = "imp_bolt", sprite = SpriteSheetIndex.Frame("projectiles", "imp_bolt"), gravity = 0f, life = 3f, radius = 0.35f,
                glow = new Color(0.8f, 0.3f, 1f, 1f), impactSound = "hit",
            });
        }

        void OnEnemyProjectile(Enemy e, string id, Vector2 pos, Vector2 vel) => Projectiles.Fire(id, pos, vel, e.Def.damage, 4f, Team.Enemy);

        void OnEnemyDied(Enemy e)
        {
            Kills++;
            var table = Db.Loot(e.Def.loot);
            if (table == null) return;
            foreach (var entry in table.entries)
            {
                if (UnityEngine.Random.value > entry.chance) continue;
                int n = UnityEngine.Random.Range(entry.min, entry.max + 1);
                if (n > 0 && Db.TryItemId(entry.item, out var id))
                    Pickups.Spawn(id, n, e.Center, new Vector2(UnityEngine.Random.Range(-3f, 3f), 6f));
            }
        }

        IEnumerator RespawnAfter(float seconds)
        {
            float t = seconds;
            while (t > 0f)
            {
                HealthUi.RespawnCountdown = t;
                t -= Time.deltaTime;
                yield return null;
            }
            HealthUi.RespawnCountdown = 0f;
            Health.Respawn(SpawnPointWorld);
            CameraFollow.SnapToTarget();
        }

        /// <summary>Drink, eat or use a consumable; returns true if it was used up.</summary>
        bool UseConsumable(ItemDef item)
        {
            if (item.maxLifeUp > 0)
            {
                if (Health.MaxLife >= 200) { Hud.Toast("Your heart can grow no stronger"); return false; }
                Health.SetMaxLife(Mathf.Min(200, Health.MaxLife + item.maxLifeUp), false);
                Health.Heal(item.maxLifeUp);
                Hud.Toast($"Maximum life is now {Health.MaxLife}");
                return true;
            }
            if (item.heal > 0)
            {
                if (Health.Life >= Health.MaxLife) return false;
                if (Time.time < _brewReady) { Hud.Toast("Too soon for another"); return false; }
                _brewReady = Time.time + 20f;
                Health.Heal(item.heal);
                return true;
            }
            if (!string.IsNullOrEmpty(item.summons)) return Summon(item.summons);
            return false;
        }

        /// <summary>Boss summons (M5).</summary>
        bool Summon(string enemyId) => false;

        void GiveStartingItems()
        {
            Inventory.Add(Db.ItemId("copper_pickaxe"), 1);
            Inventory.Add(Db.ItemId("copper_axe"), 1);
            if (Db.TryItemId("copper_shortsword", out var sword)) Inventory.Add(sword, 1);
            Inventory.Add(Db.ItemId("torch"), 25);
        }

        public void SaveTo(string path)
        {
            Meta.gameJson = MiniJson.Serialize(GameState.Capture(this));
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

    /// <summary>Player, inventory, chests and spawn point, stored in the save's game JSON.</summary>
    public static class GameState
    {
        public static Dictionary<string, object> Capture(GameBootstrap g) => new Dictionary<string, object>
        {
            ["player"] = new Dictionary<string, object>
            {
                ["x"] = (double)g.Player.Feet.x, ["y"] = (double)g.Player.Feet.y, ["selected"] = (double)g.Inventory.Selected,
                ["inventory"] = ContainerCodec.Capture(g.Inventory, g.Db),
            },
            ["spawn"] = new List<object> { (double)g.SpawnPointWorld.x, (double)g.SpawnPointWorld.y, g.HasBedSpawn },
            ["chests"] = g.Chests.Capture(),
            ["time"] = g.DayNight != null ? (double)g.DayNight.time : 0.08,
            ["life"] = g.Health != null ? (double)g.Health.Life : 100.0,
            ["maxLife"] = g.Health != null ? (double)g.Health.MaxLife : 100.0,
            ["day"] = g.DayNight != null ? (double)g.DayNight.day : 1.0,
        };

        public static void RestoreTime(GameBootstrap g, string json)
        {
            if (string.IsNullOrEmpty(json) || !(MiniJson.Parse(json) is Dictionary<string, object> root) || g.DayNight == null) return;
            if (root.TryGetValue("time", out var t)) g.DayNight.time = Mathf.Repeat((float)Convert.ToDouble(t), 1f);
            if (root.TryGetValue("day", out var d)) g.DayNight.day = (int)Convert.ToDouble(d);
        }

        public static void Restore(GameBootstrap g, string json)
        {
            if (string.IsNullOrEmpty(json) || !(MiniJson.Parse(json) is Dictionary<string, object> root)) return;
            if (root.TryGetValue("player", out var p) && p is Dictionary<string, object> player)
            {
                g.Player.Teleport(new Vector2((float)Convert.ToDouble(player["x"]), (float)Convert.ToDouble(player["y"])));
                if (player.TryGetValue("inventory", out var inv)) ContainerCodec.Restore(g.Inventory, inv as List<object>, g.Db);
                if (player.TryGetValue("selected", out var sel)) g.Inventory.Selected = (int)Convert.ToDouble(sel);
            }
            if (root.TryGetValue("spawn", out var sp) && sp is List<object> spawn && spawn.Count >= 3)
            {
                g.SpawnPointWorld = new Vector2((float)Convert.ToDouble(spawn[0]), (float)Convert.ToDouble(spawn[1]));
                g.HasBedSpawn = spawn[2] is bool b && b;
            }
            if (root.TryGetValue("chests", out var c)) g.Chests.Restore(c as List<object>);
            if (g.Health != null && root.TryGetValue("life", out var life))
                g.Health.Restore((int)Convert.ToDouble(life), root.TryGetValue("maxLife", out var max) ? (int)Convert.ToDouble(max) : 100);
        }
    }
}
