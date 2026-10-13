using System;
using UnityEngine;

namespace Loamlight.Content
{
    /// <summary>A block, platform, object or torch that occupies the foreground tile layer.</summary>
    public sealed class TileDef : ScriptableObject
    {
        public string id;
        public string displayName;
        public TileKind kind;
        public bool solid;
        [Tooltip("Seconds to mine with a pickaxe of power 1 at matching tier.")]
        public float hardness = 0.5f;
        [Tooltip("Minimum pickaxe tier needed to mine this tile.")]
        public int tier;
        public ToolType tool = ToolType.Pickaxe;
        public string drop;
        public int dropCount = 1;
        public Color mapColor = Color.gray;
        public Color light = Color.black;
        public float lightRadius;
        public bool falls;
        public string spreadsOnto;
        public Vector2Int size = Vector2Int.one;
        public Placement placement = Placement.Any;
        public string interact;
        public string sound = "dig";
        public bool isStation;
        public OreSpawn ore;

        public bool EmitsLight => light.maxColorComponent > 0.001f;
    }
}
