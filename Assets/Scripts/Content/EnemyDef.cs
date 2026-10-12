using System;
using UnityEngine;

namespace Loamlight.Content
{
    public sealed class EnemyDef : ScriptableObject
    {
        public string id;
        public string displayName;
        public AiKind ai;
        public int health = 20;
        public int damage = 10;
        public int defense;
        public float knockbackResist;
        public float speed = 4f;
        public Vector2 size = new Vector2(1.5f, 1.5f);
        public string sheet;
        public string loot;
        public string projectile;

        [Header("Spawning")]
        public string[] biomes = Array.Empty<string>();
        public float depthMin;         // 0 = top of world, 1 = bottom
        public float depthMax = 1f;
        public bool nightOnly;
        public float spawnWeight = 1f;
        public bool boss;
    }
}
