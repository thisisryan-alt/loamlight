using System;
using UnityEngine;

namespace Loamlight.Content
{
    public sealed class WallDef : ScriptableObject
    {
        public string id;
        public string displayName;
        public string drop;
        public float hardness = 0.3f;
        public Color mapColor = Color.gray;
    }
}
