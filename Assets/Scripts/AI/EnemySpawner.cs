using System;
using System.Collections.Generic;
using Loamlight.Content;
using Loamlight.Core;
using Loamlight.World;
using Loamlight.World.Collision;
using UnityEngine;

namespace Loamlight.AI
{
    /// <summary>
    /// Spawns enemies just off screen around the player, weighted by each <see cref="EnemyDef"/>'s spawn rules (biome,
    /// depth band, night-only, weight) with a cap that rises at night and underground. Ground walkers need standing
    /// room on solid floor; flyers need open air. Bosses are never spawned here (they are summoned).
    /// </summary>
    public sealed class EnemySpawner : MonoBehaviour
    {
        public const float Interval = 0.8f;

        public ContentDatabase Db { get; set; }
        public WorldData World { get; set; }
        public ICollisionGrid Grid { get; set; }
        public ILiquidSampler Liquids { get; set; }
        public Camera Cam { get; set; }
        public Func<Vector2> PlayerFeet { get; set; }
        public Func<bool> IsNight { get; set; }
        public Func<Vector2, string> BiomeAt { get; set; }
        public float SurfaceY { get; set; }
        /// <summary>Off in peaceful scenarios and menus.</summary>
        public bool Active { get; set; } = true;

        float _timer = 2f;
        uint _seed = 0x9e3779b9u;
        readonly List<EnemyDef> _candidates = new List<EnemyDef>();

        public int Spawned { get; private set; }

        float Rand()
        {
            _seed ^= _seed << 13; _seed ^= _seed >> 17; _seed ^= _seed << 5;
            return (_seed & 0xffffff) / (float)0x1000000;
        }

        public int CapHere(Vector2 feet, bool night)
        {
            bool underground = feet.y < SurfaceY - 25f;
            return underground ? 6 : night ? 7 : 3;
        }

        void Update()
        {
            if (!Active || Db == null || PlayerFeet == null || GameClock.Scale == 0f) return;
            _timer -= Time.deltaTime;
            if (_timer > 0f) return;
            _timer = Interval;
            var feet = PlayerFeet();
            bool night = IsNight != null && IsNight();
            int live = 0;
            foreach (var e in Enemy.Live) if (e != null && !e.Def.boss) live++;
            if (live >= CapHere(feet, night)) return;

            string biome = BiomeAt != null ? BiomeAt(feet) : "";
            float depth = 1f - feet.y / World.Height;
            _candidates.Clear();
            float total = 0f;
            foreach (var def in Db.enemies)
            {
                if (def.boss || def.spawnWeight <= 0f) continue;
                if (depth < def.depthMin || depth > def.depthMax) continue;
                if (def.nightOnly && !night) continue;
                if (def.biomes != null && def.biomes.Length > 0 && Array.IndexOf(def.biomes, biome) < 0) continue;
                _candidates.Add(def);
                total += def.spawnWeight;
            }
            if (_candidates.Count == 0) return;
            float pick = Rand() * total;
            var chosen = _candidates[_candidates.Count - 1];
            foreach (var def in _candidates)
            {
                pick -= def.spawnWeight;
                if (pick <= 0f) { chosen = def; break; }
            }
            if (FindSpot(chosen, feet, out var spot)) Spawn(chosen, spot);
        }

        bool FindSpot(EnemyDef def, Vector2 feet, out Vector2 spot)
        {
            float halfW = Cam != null ? Cam.orthographicSize * Cam.aspect : 40f;
            bool ground = def.ai == AiKind.Hopper || def.ai == AiKind.Walker;
            int w = Mathf.CeilToInt(def.size.x), h = Mathf.CeilToInt(def.size.y);
            for (int attempt = 0; attempt < 16; attempt++)
            {
                float side = Rand() < 0.5f ? -1f : 1f;
                int x = Mathf.FloorToInt(feet.x + side * (halfW + 3f + Rand() * 14f));
                int y = Mathf.FloorToInt(feet.y + (Rand() - 0.4f) * 24f);
                if (ground)
                {
                    for (int yy = y + 10; yy > y - 20; yy--)
                        if (Open(x, yy, w, h) && Solid(x, yy - 1))
                        {
                            spot = new Vector2(x + 0.5f, yy);
                            return true;
                        }
                }
                else if (Open(x - 1, y, w + 2, h + 2))
                {
                    spot = new Vector2(x + 0.5f, y + 1f);
                    return true;
                }
            }
            spot = default;
            return false;
        }

        bool Solid(int x, int y) => Grid.Cell(x, y, out _) == CellCollision.Solid;

        bool Open(int x0, int y0, int w, int h)
        {
            for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
            {
                if (!World.InBounds(x, y) || Grid.Cell(x, y, out _) != CellCollision.Empty) return false;
                if (World.GetLiquid(x, y) > 60) return false;
            }
            return true;
        }

        /// <summary>Create an enemy (also used by scenarios and boss summons).</summary>
        public Enemy Spawn(EnemyDef def, Vector2 feet)
        {
            var go = new GameObject("Enemy " + def.id);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sortingLayerName = "Entities";
            sr.sortingOrder = def.boss ? 8 : 5;
            var anim = go.AddComponent<SpriteAnimator>();
            var set = SpriteSheetIndex.Get(def.sheet);
            if (set != null && set.clips.Length > 0) anim.Configure(set, set.clips[0].name);
            var enemy = go.AddComponent<Enemy>();
            enemy.Init(def, Grid, Liquids, feet, (uint)(Spawned * 7919 + 17));
            Spawned++;
            return enemy;
        }
    }
}
