using System;
using UnityEngine;

namespace Loamlight.Core
{
    /// <summary>
    /// Every generated sprite sheet's animation set, by sheet name. Lives in Resources (built by the art importer)
    /// so runtime code can find sheets without each one needing its own scene reference.
    /// </summary>
    public sealed class SpriteSheetIndex : ScriptableObject
    {
        public const string ResourcePath = "Sprites/SpriteSheetIndex";

        public SpriteAnimationSet[] sheets = Array.Empty<SpriteAnimationSet>();

        static SpriteSheetIndex _instance;

        public static SpriteSheetIndex Instance
        {
            get
            {
                if (_instance == null) _instance = Resources.Load<SpriteSheetIndex>(ResourcePath);
                return _instance;
            }
        }

        public static SpriteAnimationSet Get(string sheetName)
        {
            var idx = Instance;
            if (idx == null) return null;
            foreach (var s in idx.sheets)
                if (s != null && s.sheetName == sheetName) return s;
            return null;
        }

        public static Sprite Frame(string sheetName, string clip, int frame = 0)
        {
            var c = Get(sheetName)?.Find(clip);
            return c != null && c.frames.Length > 0 ? c.frames[Mathf.Clamp(frame, 0, c.frames.Length - 1)] : null;
        }
    }
}
