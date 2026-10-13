using System;
using UnityEngine;

namespace Loamlight.Content
{
    public enum TileKind : byte
    {
        Terrain = 0,   // solid, autotiled, minable with a pickaxe
        Platform = 1,  // one-way, walk through from below, drop through with Down
        Object = 2,    // multi-tile furniture/station/chest; non-solid unless a door
        Torch = 3,     // 1x1 light source attached to a wall or block
        Door = 4,      // 1x3, solid when closed
        Tree = 5,      // trunk cells (axe), canopy is decoration on top
        Decor = 6,     // grass tufts, mushrooms, flowers: break instantly, non-solid
    }

    public enum ToolType : byte { None = 0, Pickaxe = 1, Axe = 2, Hammer = 3 }

    public enum Placement : byte { Any = 0, Floor = 1, WallOrSolid = 2, Ceiling = 3, Trunk = 4 }

    public enum ItemCategory : byte { Material = 0, Block = 1, Wall = 2, Tool = 3, Weapon = 4, Placeable = 5, Consumable = 6, Ammo = 7, Summon = 8 }

    public enum WeaponKind : byte { None = 0, Shortsword = 1, Broadsword = 2, Bow = 3, Staff = 4, Thrown = 5 }

    public enum AiKind : byte { Hopper = 0, Flyer = 1, Thrower = 2, Caster = 3, Boss = 4 }

    [Serializable]
    public struct ItemAmount
    {
        public string item;
        public int count;
    }

    /// <summary>Where the world generator scatters an ore: noise blobs inside host tiles within a depth band.</summary>
    [Serializable]
    public struct OreSpawn
    {
        public bool enabled;
        public string[] hosts;
        [Tooltip("Band, as a fraction of world height measured from the top (0 = sky, 1 = bedrock).")]
        public float depthMin, depthMax;
        public float scale, threshold;
    }

    [Serializable]
    public struct LootEntry
    {
        public string item;
        public int min;
        public int max;
        public float chance;
    }
}
