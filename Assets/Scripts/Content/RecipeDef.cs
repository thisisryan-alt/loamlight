using System;
using UnityEngine;

namespace Loamlight.Content
{
    public sealed class RecipeDef : ScriptableObject
    {
        public string id;
        public string result;
        public int resultCount = 1;
        public ItemAmount[] ingredients = Array.Empty<ItemAmount>();
        [Tooltip("Tile id of the crafting station required nearby; empty = craftable by hand.")]
        public string station;
    }
}
