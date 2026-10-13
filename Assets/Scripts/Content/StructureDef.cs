using System;
using UnityEngine;

namespace Loamlight.Content
{
    /// <summary>One legend entry of a structure template: what a character in the rows becomes.</summary>
    [Serializable]
    public struct StructureCell
    {
        public string key;        // the single character used in the rows
        public string tile;       // tile id to place (objects: this cell is the bottom-left origin)
        public string wall;       // wall id to place behind it
        public bool clear;        // empty the cell (air) even if no tile is given
        public bool ruinable;     // may be missing (the structure's ruin chance)
    }

    /// <summary>
    /// A hand-authored building the world generator scatters (cabins, shrines, vaults), from structures.json. Rows are
    /// top to bottom; a space leaves the world as it is, so templates can have ragged outlines.
    /// </summary>
    public sealed class StructureDef : ScriptableObject
    {
        public string id;
        public string[] biomes = Array.Empty<string>();
        [Tooltip("Band, as a fraction of world height measured from the top.")]
        public float depthMin, depthMax = 1f;
        public int count = 1;
        [Range(0f, 1f)] public float ruin;
        public string loot;       // loot table rolled into every chest in the structure
        public string[] rows = Array.Empty<string>();
        public StructureCell[] legend = Array.Empty<StructureCell>();
    }
}
