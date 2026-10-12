using System;
using UnityEngine;

namespace Loamlight.Content
{
    public sealed class LootTableDef : ScriptableObject
    {
        public string id;
        public LootEntry[] entries = Array.Empty<LootEntry>();
    }
}
